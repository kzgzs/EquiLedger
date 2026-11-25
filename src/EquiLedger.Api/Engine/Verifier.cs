// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>全量复算校验器：哈希链连续性 + 签名验证 + 事件重放重建投影 + 逐表比对（A2/A3）。</summary>
public class Verifier
{
    private readonly Db _db;
    private readonly EventStore _store;
    public Verifier(Db db, EventStore store) { _db = db; _store = store; }

    // 参与比对的确定性投影表（sessions/accounts 为运营身份数据，notifications 只比内容）
    private static readonly string[] Tables =
    {
        "company", "parties", "relations", "resolutions", "rule_versions", "cap_versions", "cap_positions",
        "ledger", "period_locks", "calc_results", "dividend_plans", "employees", "payroll_runs", "payroll_lines",
        "fixed_assets", "depreciation_runs", "materials", "warehouses", "stock_balances", "stock_moves", "equipment", "production_orders",
        "purchase_requests", "purchase_orders", "purchase_deliveries", "purchase_inspections", "purchase_payments", "loans", "loan_interest", "tax_returns", "notify_groups", "notify_bindings",
        "reports",
        "contracts", "module_states", "todos", "bank_flows", "whitelist_accounts", "investors",
        "evidence_files", "routine_usage", "impact_lists", "sales_orders", "commissions", "bonuses", "access_logs", "legal_library", "external_investments",
        "ip_rights", "ip_events_log", "intangible_assets", "amort_runs", "esop_plans", "esop_virtual_dividends",
        "devices", "scan_packets", "scan_reviews", "piece_records", "quality_records", "logistics_scans"
    };

    public record TableDiff(string Table, long Missing, long Extra);
    public record Report(bool ChainOk, bool SignaturesOk, int EventsChecked, List<string> ChainErrors,
        List<TableDiff> Diffs, bool Ok, DateTimeOffset At);

    public async Task<Report> Verify()
    {
        return await _store.WithLock(async () =>
        {
            var events = await _store.ReadAll();
            var chainErrors = new List<string>();
            var prev = HashChain.Genesis;
            var sigOk = true;

            await using var app = _db.App();
            var pubKeys = (await app.Rows("SELECT user_id,pub_key FROM proj.accounts")).ToDictionary(r => Pg.S(r["user_id"]), r => Pg.S(r["pub_key"]));

            foreach (var e in events)
            {
                var expect = HashChain.ComputeEventHash(prev, e.EventId.ToString(), e.Type, e.ActorId, e.OccurredAt, e.ResolutionNo, e.Evidence, e.Payload, e.RuleRefs);
                if (expect != e.Hash || e.PrevHash != prev)
                    chainErrors.Add($"seq={e.Seq} 哈希链断裂（存储哈希 {e.Hash[..12]}… ≠ 重算 {expect[..12]}…）");
                if (pubKeys.TryGetValue(e.ActorId, out var pk))
                {
                    if (!Crypto.Verify(pk, e.Hash, e.Signature)) { sigOk = false; chainErrors.Add($"seq={e.Seq} 签名验证失败（actor={e.ActorId}）"); }
                }
                prev = e.Hash;
            }

            // 快照当前投影 → 销毁重建 → 比对
            await using (var owner = _db.Owner())
            {
                await owner.Exec("DROP SCHEMA IF EXISTS verify_rebuild CASCADE");
                await owner.Exec("CREATE SCHEMA verify_rebuild");
                foreach (var t in Tables)
                    await owner.Exec($"CREATE TABLE verify_rebuild.{t} AS TABLE proj.{t}");
                await owner.Exec("CREATE TABLE verify_rebuild.notifications AS SELECT channel,summary,source_event_id FROM proj.notifications");
                await owner.Exec($"TRUNCATE {string.Join(",", Tables.Select(t => "proj." + t))} RESTART IDENTITY CASCADE");
                await owner.Exec("TRUNCATE proj.notifications RESTART IDENTITY");
            }

            var diffs = new List<TableDiff>();
            await using (var app2 = _db.App())
            {
                await using var tx = await app2.BeginTransactionAsync();
                foreach (var e in events)
                {
                    var ctx = new ApplyCtx(app2, tx, e);
                    await Projection.Apply(ctx);
                    await Propagation.Run(ctx);
                }
                await tx.CommitAsync();
            }
            // 系统预置法务知识为确定性参考数据：重建后同样注入，再参与比对
            await LegalSeed.EnsureAsync(_db);

            await using (var owner2 = _db.Owner())
            {
                foreach (var t in Tables)
                {
                    var missing = Convert.ToInt64(await owner2.Scalar($"SELECT COUNT(*) FROM (SELECT * FROM verify_rebuild.{t} EXCEPT SELECT * FROM proj.{t}) x"));
                    var extra = Convert.ToInt64(await owner2.Scalar($"SELECT COUNT(*) FROM (SELECT * FROM proj.{t} EXCEPT SELECT * FROM verify_rebuild.{t}) x"));
                    if (missing > 0 || extra > 0) diffs.Add(new TableDiff(t, missing, extra));
                }
                // notifications 内容比对（推送状态为运营数据，不参与）
                var nMissing = Convert.ToInt64(await owner2.Scalar("SELECT COUNT(*) FROM (SELECT channel,summary,source_event_id FROM verify_rebuild.notifications EXCEPT SELECT channel,summary,source_event_id FROM proj.notifications) x"));
                await owner2.Exec("DROP SCHEMA verify_rebuild CASCADE");
                if (nMissing > 0) diffs.Add(new TableDiff("notifications", nMissing, 0));
            }

            var ok = chainErrors.Count == 0 && sigOk && diffs.Count == 0;
            return new Report(chainErrors.Count == 0, sigOk, events.Count, chainErrors, diffs, ok, DateTimeOffset.UtcNow);
        });
    }

    /// <summary>司法友好证据包：事件链 + 公钥与签名 + 锚定记录 + 复算说明。</summary>
    public async Task<JsonObject> EvidencePackage()
    {
        var events = await _store.ReadAll();
        await using var conn = _db.App();
        var accounts = await conn.Rows("SELECT user_id,display_name,role,pub_key FROM proj.accounts");
        var anchors = await conn.Rows("SELECT upto_seq,head_hash,anchored_at,anchor_ref FROM events.anchors ORDER BY id");
        var arr = new JsonArray();
        foreach (var e in events)
            arr.Add(new JsonObject
            {
                ["seq"] = e.Seq, ["eventId"] = e.EventId.ToString(), ["type"] = e.Type, ["actor"] = e.ActorId,
                ["occurredAt"] = e.OccurredAt.ToString("O"), ["resolutionNo"] = e.ResolutionNo,
                ["evidence"] = e.Evidence.DeepClone(), ["payload"] = e.Payload.DeepClone(),
                ["ruleRefs"] = e.RuleRefs.DeepClone(), ["prevHash"] = e.PrevHash, ["hash"] = e.Hash, ["signature"] = e.Signature
            });
        return new JsonObject
        {
            ["attribution"] = HashChain.Attribution,
            ["exportedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["chainAlgorithm"] = "SHA-256(prevHash | canonicalJSON(core))，签名 ECDSA P-256 / SHA-256",
            ["replayInstructions"] = "按 seq 顺序重放 payload 至投影即可复现全部报表；每条事件哈希可由 prevHash 与核心字段独立重算验证",
            ["accounts"] = new JsonArray(accounts.Select(a => (JsonNode)new JsonObject
            { ["userId"] = Pg.S(a["user_id"]), ["displayName"] = Pg.S(a["display_name"]), ["role"] = Pg.S(a["role"]), ["publicKey"] = Pg.S(a["pub_key"]) }).ToArray()),
            ["anchors"] = new JsonArray(anchors.Select(a => (JsonNode)new JsonObject
            { ["uptoSeq"] = Convert.ToInt64(a["upto_seq"]), ["headHash"] = Pg.S(a["head_hash"]), ["anchoredAt"] = ((DateTime)a["anchored_at"]!).ToString("O"), ["ref"] = Pg.S(a["anchor_ref"]) }).ToArray()),
            ["events"] = arr
        };
    }
}
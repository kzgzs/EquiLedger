// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Engine;
using Npgsql;

namespace EquiLedger.Storage;

/// <summary>Append-Only 事件存储：哈希链 + 操作人签名，事务内完成投影与联动。</summary>
public class EventStore
{
    private readonly Db _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public EventStore(Db db) => _db = db;

    public async Task<T> WithLock<T>(Func<Task<T>> f)
    {
        await _lock.WaitAsync();
        try { return await f(); }
        finally { _lock.Release(); }
    }

    public record Draft(string Type, string ActorId, string? ResolutionNo, JsonNode? Evidence, JsonObject Payload, JsonNode? RuleRefs = null);

    public async Task<EventRow> Append(Draft draft)
    {
        await _lock.WaitAsync();
        try
        {
            await using var conn = _db.App();
            await using var tx = await conn.BeginTransactionAsync();

            var prev = (string?)await conn.ScalarTx("SELECT hash FROM events.event_log ORDER BY seq DESC LIMIT 1", tx) ?? HashChain.Genesis;
            var eventId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var evidence = draft.Evidence ?? new JsonArray();
            var ruleRefs = draft.RuleRefs ?? new JsonArray();
            var hash = HashChain.ComputeEventHash(prev, eventId.ToString(), draft.Type, draft.ActorId, now, draft.ResolutionNo, evidence, draft.Payload, ruleRefs);

            var priv = (string?)await conn.ScalarTx("SELECT priv_key FROM proj.accounts WHERE user_id=@u", tx, ("u", draft.ActorId));
            var signature = priv is null ? "SYSTEM" : Crypto.Sign(priv, hash);

            await using (var cmd = conn.Cmd(@"INSERT INTO events.event_log
                (event_id,event_type,actor_id,occurred_at,resolution_no,evidence,payload,rule_refs,prev_hash,hash,signature)
                VALUES (@id,@t,@a,@at,@r,@e,@p,@rr,@ph,@h,@s) RETURNING seq", tx,
                ("id", eventId), ("t", draft.Type), ("a", draft.ActorId), ("at", now.UtcDateTime), ("r", (object?)draft.ResolutionNo ?? DBNull.Value),
                ("ph", prev), ("h", hash), ("s", signature)))
            {
                cmd.Parameters.Add(Pg.Json("e", evidence));
                cmd.Parameters.Add(Pg.Json("p", draft.Payload));
                cmd.Parameters.Add(Pg.Json("rr", ruleRefs));
                var seq = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                var row = new EventRow(seq, eventId, draft.Type, draft.ActorId, now, draft.ResolutionNo, evidence, draft.Payload, ruleRefs, prev, hash, signature);
                var ctx = new ApplyCtx(conn, tx, row);
                await Projection.Apply(ctx);
                await Propagation.Run(ctx);
                await tx.CommitAsync();
                return row;
            }
        }
        finally { _lock.Release(); }
    }

    public async Task<List<EventRow>> ReadAll()
    {
        await using var conn = _db.App();
        var rows = await conn.Rows("SELECT * FROM events.event_log ORDER BY seq");
        return rows.Select(ToRow).ToList();
    }

    public static EventRow ToRow(Dictionary<string, object?> d) => new(
        Convert.ToInt64(d["seq"]), (Guid)d["event_id"]!, Pg.S(d["event_type"]), Pg.S(d["actor_id"]),
        new DateTimeOffset((DateTime)d["occurred_at"]!, TimeSpan.Zero),
        d["resolution_no"] as string, Pg.J(d["evidence"]), Pg.J(d["payload"]), Pg.J(d["rule_refs"]),
        Pg.S(d["prev_hash"]), Pg.S(d["hash"]), Pg.S(d["signature"]));

    /// <summary>链头锚定（MVP：本地锚定记录；生产接 TSA/司法存证 API）。</summary>
    public async Task Anchor(string anchorRef)
    {
        await using var conn = _db.App();
        var head = await conn.Rows("SELECT seq,hash FROM events.event_log ORDER BY seq DESC LIMIT 1");
        if (head.Count == 0) return;
        await conn.Exec(@"INSERT INTO events.anchors(upto_seq,head_hash,anchored_at,anchor_ref) VALUES(@s,@h,@t,@r)",
            ("s", head[0]["seq"]), ("h", head[0]["hash"]), ("t", DateTime.UtcNow), ("r", anchorRef));
    }
}
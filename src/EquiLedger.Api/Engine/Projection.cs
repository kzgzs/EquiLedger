// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Storage;
using Npgsql;

namespace EquiLedger.Engine;

/// <summary>投影应用上下文：所有投影写入必须经由事件应用（可重放重建）。</summary>
public class ApplyCtx
{
    public NpgsqlConnection Conn;
    public NpgsqlTransaction Tx;
    public EventRow Evt;
    public List<string> Impacts = new();
    public JsonNode P => Evt.Payload;
    public string Eid => Evt.EventId.ToString();

    public ApplyCtx(NpgsqlConnection c, NpgsqlTransaction t, EventRow e) { Conn = c; Tx = t; Evt = e; }

    public string Ps(string k) => P[k]?.GetValue<string>() ?? "";
    public decimal Pd(string k) => JsonNum(P[k]);
    public int Pi(string k) => (int)JsonNum(P[k]);
    public bool Pb(string k) => P[k] is JsonValue v && v.TryGetValue(out bool b) && b;

    public static decimal JsonNum(JsonNode? n)
    {
        if (n is not JsonValue v) return 0m;
        if (v.TryGetValue(out decimal d)) return d;
        return decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2) ? d2 : 0m;
    }

    public Task Exec(string sql, params (string, object?)[] ps) => Conn.ExecTx(sql, Tx, ps);
    public Task<object?> Scalar(string sql, params (string, object?)[] ps) => Conn.ScalarTx(sql, Tx, ps);
    public Task<List<Dictionary<string, object?>>> Rows(string sql, params (string, object?)[] ps) => Conn.RowsTx(sql, Tx, ps);

    public async Task ExecJ(string sql, (string, JsonNode?)[] jps, params (string, object?)[] ps)
    {
        await using var cmd = Conn.Cmd(sql, Tx, ps);
        foreach (var (k, v) in jps) cmd.Parameters.Add(Pg.Json(k, v));
        await cmd.ExecuteNonQueryAsync();
    }

    public void Impact(string s) => Impacts.Add(s);

    public async Task Todo(string kind, string title, JsonNode detail, string? assignee = null, string? idSuffix = null)
    {
        var id = $"TD-{kind}-{idSuffix ?? Eid}";
        await ExecJ("INSERT INTO proj.todos(todo_id,kind,title,detail,assignee,created_at,source_event_id) VALUES(@id,@k,@t,@d,@a,@at,@e) ON CONFLICT DO NOTHING",
            new[] { ("d", detail) },
            ("id", id), ("k", kind), ("t", title), ("a", (object?)assignee ?? DBNull.Value), ("at", Evt.OccurredAt.UtcDateTime), ("e", Evt.EventId));
        Impact($"待办：{title}");
    }

    public async Task Ledger(string period, string account, decimal debit, decimal credit, string memo)
    {
        await Exec("INSERT INTO proj.ledger(period,account,debit,credit,memo,source_event_id) VALUES(@p,@a,@d,@c,@m,@e)",
            ("p", period), ("a", account), ("d", debit), ("c", credit), ("m", memo), ("e", Evt.EventId));
    }

    public async Task Calc(string kind, string? subject, CalcOutcome outcome, string status = "Current")
    {
        await ExecJ(@"INSERT INTO proj.calc_results(calc_id,kind,subject,amount,derivation,rule_refs,input_events,status,created_at,source_event_id)
            VALUES(@id,@k,@s,@a,@d,@r,@i,@st,@at,@e) ON CONFLICT (calc_id) DO UPDATE SET amount=@a,derivation=@d,status=@st",
            new[] { ("d", (JsonNode?)outcome.RowsJson()), ("r", (JsonNode?)outcome.RuleRefsJson()), ("i", (JsonNode?)new JsonArray(Evt.EventId.ToString())) },
            ("id", $"{kind}-{subject ?? "global"}-{Eid}"), ("k", kind), ("s", (object?)subject ?? DBNull.Value), ("a", outcome.Amount),
            ("st", status), ("at", Evt.OccurredAt.UtcDateTime), ("e", Evt.EventId));
        Impact($"计算单：{kind} {subject} = {outcome.Amount}");
    }

    public async Task Notify(string channel, JsonNode summary)
    {
        await ExecJ("INSERT INTO proj.notifications(channel,summary,pushed_at,push_status,source_event_id) VALUES(@c,@s,@t,'Pending',@e)",
            new[] { ("s", summary) }, ("c", channel), ("t", Evt.OccurredAt.UtcDateTime), ("e", Evt.EventId));
    }

    public string Period => Evt.OccurredAt.ToString("yyyy-MM");

    // ---------- Cap Table ----------
    public class Pos
    {
        public string PartyId = "";
        public decimal Subscribed, PaidIn, Ratio, VotingRatio;
        public string Status = "Normal";
        public JsonObject ToJson() => new()
        { ["partyId"] = PartyId, ["subscribed"] = Subscribed, ["paidIn"] = PaidIn, ["ratio"] = Ratio, ["votingRatio"] = VotingRatio, ["status"] = Status };
    }

    public async Task<List<Pos>> CurrentCap()
    {
        var v = await Scalar("SELECT COALESCE(MAX(version),0) FROM proj.cap_versions");
        var ver = Convert.ToInt32(v);
        if (ver == 0) return new List<Pos>();
        var rows = await Rows("SELECT * FROM proj.cap_positions WHERE cap_version=@v ORDER BY party_id", ("v", ver));
        return rows.Select(r => new Pos
        {
            PartyId = Pg.S(r["party_id"]), Subscribed = Pg.D(r["subscribed"]), PaidIn = Pg.D(r["paid_in"]),
            Ratio = Pg.D(r["ratio"]), VotingRatio = Pg.D(r["voting_ratio"]), Status = Pg.S(r["status"])
        }).ToList();
    }

    public async Task<string> VotingBasis()
    {
        var r = await Rows("SELECT params FROM proj.rule_versions WHERE kind='Charter.Voting' AND status='Active' ORDER BY version DESC LIMIT 1");
        if (r.Count == 0) return "Subscribed";
        return Pg.J(r[0]["params"])["basis"]?.GetValue<string>() ?? "Subscribed";
    }

    public async Task<int> SaveCap(List<Pos> positions)
    {
        var totalSub = positions.Where(p => p.Status is "Normal" or "Deceased").Sum(p => p.Subscribed);
        var basis = await VotingBasis();
        var activeVoting = positions.Where(p => p.Status == "Normal");
        var totalVote = basis == "PaidIn" ? activeVoting.Sum(p => p.PaidIn) : activeVoting.Sum(p => p.Subscribed);
        foreach (var p in positions)
        {
            p.Ratio = totalSub > 0 ? Math.Round(p.Subscribed / totalSub, 6) : 0m;
            var vb = basis == "PaidIn" ? p.PaidIn : p.Subscribed;
            p.VotingRatio = p.Status == "Normal" && totalVote > 0 ? Math.Round(vb / totalVote, 6) : 0m;
        }
        var ver = Convert.ToInt32(await Scalar("SELECT COALESCE(MAX(version),0)+1 FROM proj.cap_versions"));
        await Exec("INSERT INTO proj.cap_versions(version,effective_at,source_event_id) VALUES(@v,@t,@e)",
            ("v", ver), ("t", Evt.OccurredAt.UtcDateTime), ("e", Evt.EventId));
        foreach (var p in positions)
            await Exec("INSERT INTO proj.cap_positions(cap_version,party_id,subscribed,paid_in,ratio,voting_ratio,status) VALUES(@v,@p,@s,@pi,@r,@vr,@st)",
                ("v", ver), ("p", p.PartyId), ("s", p.Subscribed), ("pi", p.PaidIn), ("r", p.Ratio), ("vr", p.VotingRatio), ("st", p.Status));
        Impact($"股权结构表 v{ver}：" + string.Join("；", positions.Select(p => $"{p.PartyId} {p.Ratio:P2}({p.Status})")));
        Impact("表决权基数已按最新股权结构重算");
        return ver;
    }

    public async Task EnsureParty(string partyId, string name, string kind = "Natural")
    {
        await Exec("INSERT INTO proj.parties(party_id,name,kind,source_event_id) VALUES(@p,@n,@k,@e) ON CONFLICT DO NOTHING",
            ("p", partyId), ("n", name), ("k", kind), ("e", Evt.EventId));
    }

    /// <summary>期间损益（投影自 ledger）。</summary>
    public async Task<decimal> ProfitBeforeTax(int year)
    {
        var rev = Pg.D(await Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='Revenue' AND period LIKE @y", ("y", $"{year}-%")));
        var exp = Pg.D(await Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account IN ('Expense','PayrollExpense','SICompanyExpense') AND period LIKE @y", ("y", $"{year}-%")));
        return rev - exp;
    }

    public async Task<decimal> NetAssets()
    {
        var assets = Pg.D(await Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account IN ('Bank','AR','Inventory','Materials','FixedAssets','ExternalInvestment')"));
        var liab = Pg.D(await Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account IN ('DividendPayable','TaxPayable','SIPayable','ExitPayable','LoanPayable','DividendSuspense')"));
        return assets - liab;
    }

    public async Task<JsonObject?> ActiveRule(string kind, string? subject = null)
    {
        var rows = subject is null
            ? await Rows("SELECT rule_id,version,params FROM proj.rule_versions WHERE kind=@k AND status='Active' ORDER BY version DESC LIMIT 1", ("k", kind))
            : await Rows("SELECT rule_id,version,params FROM proj.rule_versions WHERE kind=@k AND subject=@s AND status='Active' ORDER BY version DESC LIMIT 1", ("k", kind), ("s", subject));
        if (rows.Count == 0) return null;
        var o = (JsonObject)Pg.J(rows[0]["params"]);
        o["__ruleId"] = Pg.S(rows[0]["rule_id"]);
        o["__version"] = Convert.ToInt32(rows[0]["version"]);
        return o;
    }

    public static string RuleRef(JsonObject? rule) => rule is null ? "default" : $"{rule["__ruleId"]}v{rule["__version"]}";
}

public static partial class Projection
{
    public static async Task Apply(ApplyCtx c)
    {
        switch (c.Evt.Type)
        {
            case "CompanyFounded": await CompanyFounded(c); break;
            case "AccountStatusChanged": await AccountStatusChanged(c); break;
            case "OperatorHandover": await OperatorHandover(c); break;
            case "RelationDeclared": await RelationDeclared(c); break;
            case "ResolutionProposed": await ResolutionProposed(c); break;
            case "ResolutionVoted": await ResolutionVoted(c); break;
            case "ResolutionRecorded": await ResolutionRecorded(c); break;
            case "RuleActivated": await RuleActivated(c); break;
            case "ContractSigned": await ContractSigned(c); break;
            case "ContractTerminated": await ContractTerminated(c); break;
            case "ContractAccessed": await ContractAccessed(c); break;
            case "EvidenceUploaded": await EvidenceUploaded(c); break;
            case "ShareSubscription": await ShareSubscription(c); break;
            case "CapitalPaidIn": await CapitalPaidIn(c); break;
            case "InvestmentMade": await InvestmentMade(c); break;
            case "InvestmentReturnReceived": await InvestmentReturnReceived(c); break;
            case "PayrollPaid": await PayrollPaid(c); break;
            case "FixedAssetAcquired": await FixedAssetAcquired(c); break;
            case "FixedAssetDepreciationRun": await FixedAssetDepreciationRun(c); break;
            case "FixedAssetDisposed": await FixedAssetDisposed(c); break;
            case "MaterialDefined": await MaterialDefined(c); break;
            case "WarehouseDefined": await WarehouseDefined(c); break;
            case "StockReceived": await StockReceived(c); break;
            case "StockIssued": await StockIssued(c); break;
            case "StockCounted": await StockCounted(c); break;
            case "EquipmentRegistered": await EquipmentRegistered(c); break;
            case "ProductionOrderCreated": await ProductionOrderCreated(c); break;
            case "ProductionCompleted": await ProductionCompleted(c); break;
            case "ShareTransfer": await ShareTransfer(c); break;
            case "ShareExit": await ShareExit(c); break;
            case "ShareholderDeceased": await ShareholderDeceased(c); break;
            case "DividendApproved": await DividendApproved(c); break;
            case "DividendPaid": await DividendPaid(c); break;
            case "RevenueRecorded": await RevenueRecorded(c); break;
            case "ExpenseRecorded": await ExpenseRecorded(c); break;
            case "SalesOrderConfirmed": await SalesOrderConfirmed(c); break;
            case "CollectionReceived": await CollectionReceived(c); break;
            case "BankFlowImported": await BankFlowImported(c); break;
            case "PeriodClosed": await PeriodClosed(c); break;
            case "CorrectionRecorded": await CorrectionRecorded(c); break;
            case "EmployeeHired": await EmployeeHired(c); break;
            case "BonusGranted": await BonusGranted(c); break;
            case "PayrollRun": await PayrollRun(c); break;
            case "EmployeeLeft": await EmployeeLeft(c); break;
            case "ModuleSwitchChanged": await ModuleSwitchChanged(c); break;
            case "InvestorQualified": await InvestorQualified(c); break;
            case "InvestmentReceived": await InvestmentReceived(c); break;
            case "SecurityAlert": await SecurityAlert(c); break;
            case "TodoCompleted": await TodoCompleted(c); break;
            case "CompanyInfoChanged": await CompanyInfoChanged(c); break;
            case "LegalEntryAdded": await LegalEntryAdded(c); break;
            case "AccountAdded": await AccountAdded(c); break;
            default: throw new InvalidOperationException($"未知事件类型 {c.Evt.Type}");
        }

        // 决议一次性消费（表决/记录类除外）
        if (c.Evt.ResolutionNo is string rn && c.Evt.Type is not ("ResolutionVoted" or "ResolutionRecorded" or "ResolutionProposed"))
            await c.Exec("UPDATE proj.resolutions SET consumed=TRUE WHERE resolution_no=@r", ("r", rn));
    }
}
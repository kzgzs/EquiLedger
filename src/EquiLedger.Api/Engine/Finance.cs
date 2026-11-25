// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>模块三聚合层：凭证=账本按源事件归集（不重复建账）；三表=科目聚合；全部纯函数、可单测、与事件链同源。</summary>
public static class Finance
{
    public static Dictionary<string, (decimal d, decimal c)> Sums(IEnumerable<Dictionary<string, object?>> rows, Func<Dictionary<string, object?>, bool>? filter = null)
    {
        var m = new Dictionary<string, (decimal, decimal)>();
        foreach (var r in rows)
        {
            if (filter != null && !filter(r)) continue;
            var k = Pg.S(r["account"]);
            m.TryGetValue(k, out var t);
            m[k] = (t.Item1 + Pg.D(r["debit"]), t.Item2 + Pg.D(r["credit"]));
        }
        return m;
    }

    private static JsonObject Line(Dictionary<string, object?> ch, decimal bal) => new()
    { ["code"] = Pg.S(ch["code"]), ["name"] = Pg.S(ch["name"]), ["balance"] = bal };

    /// <summary>资产负债表（累计至 period）：资产=借方余额；负债/权益=贷方余额；未分配利润=累计损益插入权益侧。</summary>
    public static JsonObject BalanceSheet(Dictionary<string, (decimal d, decimal c)> cum, List<Dictionary<string, object?>> chart)
    {
        var assets = new JsonArray(); var liabs = new JsonArray(); var eq = new JsonArray();
        decimal a = 0, l = 0, e = 0, pl = 0;
        foreach (var ch in chart)
        {
            cum.TryGetValue(Pg.S(ch["account_key"]), out var t);
            var bal = t.d - t.c;
            switch (Pg.S(ch["category"]))
            {
                case "asset": a += bal; assets.Add(Line(ch, bal)); break;
                case "cost": a += bal; assets.Add(Line(ch, bal)); break; // 生产成本余额=在产品，列资产侧
                case "liability": l += -bal; liabs.Add(Line(ch, -bal)); break;
                case "equity": e += -bal; eq.Add(Line(ch, -bal)); break;
                case "pl_revenue": pl += t.c - t.d; break;
                case "pl_expense": pl -= t.d - t.c; break;
            }
        }
        eq.Add(new JsonObject { ["code"] = "4104-UR", ["name"] = "未分配利润（累计损益）", ["balance"] = pl });
        e += pl;
        return new JsonObject
        {
            ["assets"] = assets, ["liabilities"] = liabs, ["equity"] = eq,
            ["totalAssets"] = a, ["totalLiabilities"] = l, ["totalEquity"] = e, ["balanced"] = a == l + e
        };
    }

    /// <summary>利润表：本期与本年累计（收入=贷方发生，费用=借方发生）。</summary>
    public static JsonObject Income(Dictionary<string, (decimal d, decimal c)> period, Dictionary<string, (decimal d, decimal c)> ytd, List<Dictionary<string, object?>> chart)
    {
        JsonObject Side(Dictionary<string, (decimal d, decimal c)> s)
        {
            var rev = new JsonArray(); var exp = new JsonArray();
            decimal r = 0, x = 0;
            foreach (var ch in chart)
            {
                s.TryGetValue(Pg.S(ch["account_key"]), out var t);
                if (Pg.S(ch["category"]) == "pl_revenue") { r += t.c - t.d; if (t.c != 0 || t.d != 0) rev.Add(Line(ch, t.c - t.d)); }
                else if (Pg.S(ch["category"]) == "pl_expense") { x += t.d - t.c; if (t.c != 0 || t.d != 0) exp.Add(Line(ch, t.d - t.c)); }
            }
            return new JsonObject { ["revenue"] = rev, ["expenses"] = exp, ["totalRevenue"] = r, ["totalExpense"] = x, ["netProfit"] = r - x };
        }
        return new JsonObject { ["period"] = Side(period), ["ytd"] = Side(ytd) };
    }

    private static readonly HashSet<string> FinancingIn = new() { "CapitalPaidIn", "InvestmentReceived" };
    private static readonly HashSet<string> FinancingOut = new() { "DividendPaid", "ShareExit" };
    private static readonly HashSet<string> InvestingOut = new() { "InvestmentMade" };
    private static readonly HashSet<string> InvestingIn = new() { "InvestmentReturnReceived" };

    /// <summary>现金流量表：现金/银行科目发生额按源事件类型归类（经营/投资/筹资）。</summary>
    public static JsonObject CashFlow(List<Dictionary<string, object?>> periodRows, Dictionary<Guid, string> evType)
    {
        var cats = new Dictionary<string, (decimal i, decimal o)>
        { ["operating"] = (0, 0), ["investing"] = (0, 0), ["financing"] = (0, 0) };
        foreach (var r in periodRows)
        {
            var acc = Pg.S(r["account"]);
            if (acc != "Bank" && acc != "Cash") continue;
            var amt = Pg.D(r["debit"]) > 0 ? Pg.D(r["debit"]) : -Pg.D(r["credit"]);
            evType.TryGetValue((Guid)r["source_event_id"], out var t);
            var cat = FinancingIn.Contains(t!) || FinancingOut.Contains(t!) ? "financing"
                : InvestingIn.Contains(t!) || InvestingOut.Contains(t!) ? "investing" : "operating";
            var cur = cats[cat];
            cats[cat] = amt > 0 ? (cur.i + amt, cur.o) : (cur.i, cur.o - amt);
        }
        var obj = new JsonObject();
        decimal net = 0;
        foreach (var kv in cats)
        {
            obj[kv.Key] = new JsonObject { ["inflow"] = kv.Value.i, ["outflow"] = kv.Value.o, ["net"] = kv.Value.i - kv.Value.o };
            net += kv.Value.i - kv.Value.o;
        }
        obj["netIncrease"] = net;
        return obj;
    }

    /// <summary>科目余额表：本期发生 + 累计余额（按科目方向列示）。</summary>
    public static JsonArray Trial(Dictionary<string, (decimal d, decimal c)> period, Dictionary<string, (decimal d, decimal c)> cum, List<Dictionary<string, object?>> chart)
    {
        var arr = new JsonArray();
        foreach (var ch in chart)
        {
            var k = Pg.S(ch["account_key"]);
            period.TryGetValue(k, out var p); cum.TryGetValue(k, out var t);
            var bal = Pg.S(ch["normal_balance"]) == "debit" ? t.d - t.c : t.c - t.d;
            arr.Add(new JsonObject
            {
                ["code"] = Pg.S(ch["code"]), ["name"] = Pg.S(ch["name"]), ["key"] = k,
                ["periodDebit"] = p.d, ["periodCredit"] = p.c, ["balance"] = bal
            });
        }
        return arr;
    }

    /// <summary>记账凭证视图：ledger 行按源事件归集，凭证号=PZ-期间-事件序号；借贷合计必须相等。</summary>
    public static JsonArray Vouchers(string period, List<Dictionary<string, object?>> periodRows, Dictionary<Guid, Dictionary<string, object?>> evById, List<Dictionary<string, object?>> chart)
    {
        var map = chart.ToDictionary(c => Pg.S(c["account_key"]), c => c);
        var groups = new Dictionary<Guid, List<Dictionary<string, object?>>>();
        foreach (var r in periodRows)
        {
            var eid = (Guid)r["source_event_id"];
            if (!groups.TryGetValue(eid, out var g)) groups[eid] = g = new List<Dictionary<string, object?>>();
            g.Add(r);
        }
        var arr = new JsonArray();
        foreach (var kv in groups.OrderBy(g => evById.TryGetValue(g.Key, out var e) ? Convert.ToInt64(e["seq"]) : 0))
        {
            evById.TryGetValue(kv.Key, out var ev);
            var lines = new JsonArray();
            decimal td = 0, tc = 0;
            foreach (var r in kv.Value)
            {
                var k = Pg.S(r["account"]);
                map.TryGetValue(k, out var ch);
                td += Pg.D(r["debit"]); tc += Pg.D(r["credit"]);
                lines.Add(new JsonObject
                {
                    ["account"] = k, ["code"] = ch is null ? k : Pg.S(ch["code"]), ["name"] = ch is null ? k : Pg.S(ch["name"]),
                    ["debit"] = Pg.D(r["debit"]), ["credit"] = Pg.D(r["credit"]), ["memo"] = Pg.S(r["memo"]),
                    ["reversedBy"] = r["reversed_by"] is null or DBNull ? null : Convert.ToInt64(r["reversed_by"])
                });
            }
            var evidence = new JsonArray();
            if (ev?["evidence"] is string es && es.Length > 0)
            {
                try { foreach (var n in JsonNode.Parse(es) as JsonArray ?? new JsonArray()) evidence.Add(n?["name"]?.GetValue<string>() ?? ""); } catch { }
            }
            arr.Add(new JsonObject
            {
                ["voucherNo"] = $"PZ-{period}-{(ev is null ? 0 : Convert.ToInt64(ev["seq"])):D4}",
                ["eventId"] = kv.Key, ["type"] = ev is null ? "" : Pg.S(ev["type"]),
                ["resolutionNo"] = ev?["resolution_no"] as string,
                ["lines"] = lines, ["totalDebit"] = td, ["totalCredit"] = tc, ["balanced"] = td == tc,
                ["evidence"] = evidence
            });
        }
        return arr;
    }
}
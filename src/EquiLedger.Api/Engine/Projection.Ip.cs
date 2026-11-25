// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>知识产权双账簿：产权档案（商业价值/全生命周期记录）与无形资产会计账簿（CAS6 确认闸门+摊销+减值）分离又关联。
/// 产权价值≠会计账面价值：自研软件市场值数百万不等于可直接登记无形资产；确认须可辨认性+经济利益流入+成本可靠计量。</summary>
public static partial class Projection
{
    private static decimal IpR2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);
    private static object IpDate(string s) => string.IsNullOrEmpty(s) ? DBNull.Value : DateOnly.Parse(s).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private static async Task IpHistory(ApplyCtx c, string ipId, string kind, JsonNode detail)
    {
        var d = detail.DeepClone();
        await c.ExecJ("UPDATE proj.ip_rights SET history = history || @h WHERE ip_id=@i",
            new[] { ("h", (JsonNode)new JsonArray(new JsonObject { ["kind"] = kind, ["at"] = c.Evt.OccurredAt.ToString("yyyy-MM-dd"), ["detail"] = d, ["eventId"] = c.Eid })) },
            ("i", ipId));
        await c.ExecJ("INSERT INTO proj.ip_events_log(log_id,ip_id,kind,detail,memo,source_event_id) VALUES(@l,@i,@k,@d,@m,@e)",
            new[] { ("d", d) }, ("l", $"IPL-{c.Eid}"), ("i", ipId), ("k", kind), ("m", c.Ps("memo")), ("e", c.Evt.EventId));
    }

    private static async Task IpRenewalTodo(ApplyCtx c, string ipId, string name, string? validUntil, string? renewDate)
    {
        foreach (var (label, date) in new[] { ("有效期届满", validUntil), ("续费日", renewDate) })
        {
            if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out var dt)) continue;
            var ds = dt.ToString("yyyy-MM-dd");
            var days = (dt.Date - c.Evt.OccurredAt.UtcDateTime.Date).TotalDays;
            if (days < 0 || days > 90) continue;
            await c.Todo("IpRenewal", $"产权{label}提醒：{name}（{ds}，剩 {days:0} 天）",
                new JsonObject { ["ipId"] = ipId, ["date"] = ds, ["label"] = label }, null, $"{ipId}-{ds}");
        }
    }

    private static async Task IpRegistered(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("ipId")) ? $"IP-{c.Eid}" : c.Ps("ipId");
        await c.ExecJ(@"INSERT INTO proj.ip_rights(ip_id,name,category,reg_no,owner_party,controller,acquire_mode,acquire_date,cert_refs,cost,fees,valid_until,renew_date,status,history,source_event_id)
            VALUES(@i,@n,@cat,@no,@owner,@ctrl,@mode,@ad,@certs,@cost,@fees,@vu,@rd,'Active','[]',@e)",
            new[] { ("certs", (JsonNode)(c.P["certRefs"] as JsonArray ?? new JsonArray())) },
            ("i", id), ("n", c.Ps("name")), ("cat", c.Ps("category")), ("no", c.Ps("regNo")), ("owner", c.Ps("ownerParty")),
            ("ctrl", c.Ps("controller")), ("mode", c.Ps("acquireMode")), ("ad", IpDate(c.Ps("acquireDate"))),
            ("cost", c.Pd("cost")), ("fees", c.Pd("fees")),
            ("vu", IpDate(c.Ps("validUntil"))),
            ("rd", IpDate(c.Ps("renewDate"))), ("e", c.Evt.EventId));
        await IpHistory(c, id, "Registered", new JsonObject { ["name"] = c.Ps("name"), ["category"] = c.Ps("category"), ["cost"] = c.Pd("cost") });
        await IpRenewalTodo(c, id, c.Ps("name"), c.Ps("validUntil"), c.Ps("renewDate"));
        c.Impact($"产权档案建立 {id}（{c.Ps("category")}·{c.Ps("name")}）：商业价值与会计账面分离，未满足 CAS6 确认条件前不入财务资产");
    }

    private static async Task IpRecordAdded(ApplyCtx c)
    {
        var id = c.Ps("ipId"); var kind = c.Ps("kind");
        var detail = c.P["detail"] as JsonObject ?? new JsonObject();
        await IpHistory(c, id, kind, detail);
        if (kind == "Renewal")
        {
            if (detail["validUntil"] != null) await c.Exec("UPDATE proj.ip_rights SET valid_until=@d WHERE ip_id=@i", ("d", IpDate(detail["validUntil"]!.ToString())), ("i", id));
            if (detail["renewDate"] != null) await c.Exec("UPDATE proj.ip_rights SET renew_date=@d WHERE ip_id=@i", ("d", IpDate(detail["renewDate"]!.ToString())), ("i", id));
        }
        if (kind == "StatusChange" && detail["status"] != null)
            await c.Exec("UPDATE proj.ip_rights SET status=@s WHERE ip_id=@i", ("s", detail["status"]!.ToString()), ("i", id));
        if (kind == "Transfer" && detail["ownerParty"] != null)
            await c.Exec("UPDATE proj.ip_rights SET owner_party=@o WHERE ip_id=@i", ("o", detail["ownerParty"]!.ToString()), ("i", id));
        var row = (await c.Rows("SELECT name,valid_until,renew_date FROM proj.ip_rights WHERE ip_id=@i", ("i", id)))[0];
        await IpRenewalTodo(c, id, Pg.S(row["name"]), Pg.S(row["valid_until"]), Pg.S(row["renew_date"]));
        c.Impact($"产权 {id} 记录 {kind}：转让/授权/许可/质押/争议/诉讼/续费全生命周期留痕");
    }

    private static async Task IpValued(ApplyCtx c)
    {
        var id = c.Ps("ipId");
        await c.Exec("UPDATE proj.ip_rights SET appraised_value=@v, appraisal_report=@r WHERE ip_id=@i", ("v", c.Pd("appraisedValue")), ("r", c.Ps("appraisalReport")), ("i", id));
        await IpHistory(c, id, "Valued", new JsonObject { ["appraisedValue"] = c.Pd("appraisedValue"), ["report"] = c.Ps("appraisalReport") });
        c.Impact($"产权 {id} 评估价值 {c.Pd("appraisedValue")}（评估报告 {c.Ps("appraisalReport")}）：仅档案商业价值，不自动改动会计账面");
    }

    private static async Task IpCapitalized(ApplyCtx c)
    {
        var id = c.Ps("ipId");
        var assetId = string.IsNullOrEmpty(c.Ps("assetId")) ? $"IA-{c.Eid}" : c.Ps("assetId");
        var cost = c.Pd("cost");
        var ip = (await c.Rows("SELECT name FROM proj.ip_rights WHERE ip_id=@i", ("i", id)))[0];
        await c.ExecJ(@"INSERT INTO proj.intangible_assets(asset_id,ip_id,name,cost,life_months,recognized_date,criteria,status,source_event_id)
            VALUES(@a,@i,@n,@cost,@life,@d,@cr,'Active',@e)",
            new[] { ("cr", (JsonNode)(c.P["criteria"] as JsonObject ?? new JsonObject())) },
            ("a", assetId), ("i", id), ("n", Pg.S(ip["name"])), ("cost", cost), ("life", c.Pi("lifeMonths")),
            ("d", c.Evt.OccurredAt.UtcDateTime.Date), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "IntangibleAssets", cost, 0, $"无形资产确认·{Pg.S(ip["name"])}（CAS6 三条件满足）");
        await c.Ledger(c.Period, c.Ps("payMode") == "AP" ? "AP" : "Bank", 0, cost, $"无形资产取得对价·{assetId}");
        await c.Exec("UPDATE proj.ip_rights SET linked_asset_id=@a WHERE ip_id=@i", ("a", assetId), ("i", id));
        await IpHistory(c, id, "Capitalized", new JsonObject { ["assetId"] = assetId, ["cost"] = cost, ["lifeMonths"] = c.Pi("lifeMonths") });
        c.Impact($"产权 {id} 满足 CAS6 确认条件→无形资产 {assetId} 入账 {cost}（寿命 {c.Pi("lifeMonths")} 月）；档案评估价值与账面价值双轨并行");
    }

    private static async Task IpAmortizationRun(ApplyCtx c)
    {
        var period = c.Ps("period");
        var rows = await c.Rows("SELECT asset_id,name,cost,life_months,accumulated_amort,impairment FROM proj.intangible_assets WHERE status='Active' AND life_months>0 ORDER BY asset_id");
        var lines = new JsonArray(); decimal total = 0;
        foreach (var r in rows)
        {
            var cost = Pg.D(r["cost"]); var accum = Pg.D(r["accumulated_amort"]); var imp = Pg.D(r["impairment"]);
            var book = cost - accum - imp;
            if (book <= 0) continue;
            var amt = IpR2(Math.Min(IpR2(cost / Pg.D(r["life_months"])), book));
            if (amt <= 0) continue;
            await c.Ledger(period, "AmortExpense", amt, 0, $"无形资产摊销·{Pg.S(r["name"])}（直线法）");
            await c.Ledger(period, "AccumAmortization", 0, amt, $"累计摊销·{Pg.S(r["asset_id"])}");
            await c.Exec("UPDATE proj.intangible_assets SET accumulated_amort=accumulated_amort+@a WHERE asset_id=@i", ("a", amt), ("i", Pg.S(r["asset_id"])));
            lines.Add(new JsonObject { ["assetId"] = Pg.S(r["asset_id"]), ["name"] = Pg.S(r["name"]), ["amount"] = amt });
            total += amt;
        }
        await c.ExecJ("INSERT INTO proj.amort_runs(period,total,lines,source_event_id) VALUES(@p,@t,@l,@e)",
            new[] { ("l", (JsonNode)lines) }, ("p", period), ("t", total), ("e", c.Evt.EventId));
        c.Impact($"无形资产月度摊销 {period}：合计 {total}（直线法·账面价值封顶·自动计算）");
    }

    private static async Task IpImpaired(ApplyCtx c)
    {
        var id = c.Ps("assetId"); var amt = c.Pd("amount");
        var r = (await c.Rows("SELECT ip_id,name FROM proj.intangible_assets WHERE asset_id=@i", ("i", id)))[0];
        await c.Ledger(c.Period, "ImpairmentLoss", amt, 0, $"无形资产减值·{Pg.S(r["name"])}");
        await c.Ledger(c.Period, "IntangibleImpairment", 0, amt, $"减值准备·{id}");
        await c.Exec("UPDATE proj.intangible_assets SET impairment=impairment+@a WHERE asset_id=@i", ("a", amt), ("i", id));
        if (Pg.S(r["ip_id"]) != "") await IpHistory(c, Pg.S(r["ip_id"]), "Impaired", new JsonObject { ["assetId"] = id, ["amount"] = amt });
        c.Impact($"无形资产 {id} 减值 {amt}：账面价值同步下调，产权档案商业价值不受会计减值自动影响");
    }

    private static async Task IpDisposed(ApplyCtx c)
    {
        var id = c.Ps("assetId"); var proceeds = c.Pd("proceeds");
        var r = (await c.Rows("SELECT ip_id,name,cost,accumulated_amort,impairment FROM proj.intangible_assets WHERE asset_id=@i", ("i", id)))[0];
        var cost = Pg.D(r["cost"]); var accum = Pg.D(r["accumulated_amort"]); var imp = Pg.D(r["impairment"]);
        var book = cost - accum - imp; var diff = proceeds - book;
        await c.Ledger(c.Period, "Bank", proceeds, 0, $"无形资产处置收入·{id}");
        await c.Ledger(c.Period, "AccumAmortization", accum, 0, $"处置转销累计摊销·{id}");
        await c.Ledger(c.Period, "IntangibleImpairment", imp, 0, $"处置转销减值准备·{id}");
        await c.Ledger(c.Period, "IntangibleAssets", 0, cost, $"处置转销无形资产原值·{id}");
        if (diff >= 0) await c.Ledger(c.Period, "AssetDisposalGainLoss", 0, diff, $"处置净收益·{id}");
        else await c.Ledger(c.Period, "AssetDisposalGainLoss", -diff, 0, $"处置净损失·{id}");
        await c.Exec("UPDATE proj.intangible_assets SET status='Disposed' WHERE asset_id=@i", ("i", id));
        if (Pg.S(r["ip_id"]) != "") await IpHistory(c, Pg.S(r["ip_id"]), "Disposed", new JsonObject { ["assetId"] = id, ["proceeds"] = proceeds, ["gainLoss"] = diff });
        c.Impact($"无形资产 {id} 处置：收入 {proceeds}·账面 {book}·损益 {diff}（自动结转）");
    }

    private static async Task IpRenewalScan(ApplyCtx c)
    {
        var rows = await c.Rows("SELECT ip_id,name,valid_until,renew_date FROM proj.ip_rights WHERE status='Active'");
        foreach (var r in rows)
            await IpRenewalTodo(c, Pg.S(r["ip_id"]), Pg.S(r["name"]), Pg.S(r["valid_until"]), Pg.S(r["renew_date"]));
        c.Impact($"产权到期/续费扫描 {c.Period}：90 天内到期自动生成提醒待办（去重）");
    }
}
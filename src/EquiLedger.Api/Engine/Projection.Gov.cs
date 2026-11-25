// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>治理补充：举报处理（监管邮箱转来→操作员铁面判官登记→监事/操作员调查取证→决议认定问责→严重者可群通告）、
/// 员工自由入股与公司分股（资本公积转增 / 股东赠与），与股权账本/会计分录/治理留痕同源贯通。</summary>
public static partial class Projection
{
    private static async Task ReportFiled(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("reportId")) ? $"RP-{c.Eid}" : c.Ps("reportId");
        var refs = new JsonArray();
        foreach (var ev in c.Evt.Evidence as JsonArray ?? new JsonArray())
            refs.Add(ev?["hash"]?.ToString() ?? ev?.ToString() ?? "");
        var src = c.Ps("source");
        await c.ExecJ("INSERT INTO proj.reports(report_id,category,target,description,anonymous,reporter,reporter_actor,status,source,severity,evidence_refs,source_event_id) VALUES(@r,@cat,@t,@d,FALSE,@rep,@actor,'Open',@src,'General',@refs,@e)",
            new[] { ("refs", (JsonNode)refs) },
            ("r", id), ("cat", c.Ps("category")), ("t", c.Ps("target")), ("d", c.Ps("description")),
            ("rep", src == "Mailbox" ? "监管邮箱转来" : c.Evt.ActorId), ("actor", c.Evt.ActorId), ("src", src), ("e", c.Evt.EventId));
        c.Impact($"举报处理受理 {id}（{c.Ps("category")}·对象 {c.Ps("target")}·来源 {(src == "Mailbox" ? "监管邮箱（高层共同监督）" : src)}）：被举报不等于违规，调查取证前不作认定；直送监事群/董事会群");
    }

    private static async Task ReportInvestigated(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.reports SET status='Investigating', findings=@f, investigated_by=@b WHERE report_id=@r", ("f", c.Ps("findings")), ("b", c.Evt.ActorId), ("r", c.Ps("reportId")));
        c.Impact($"调查取证 {c.Ps("reportId")}（{c.Evt.ActorId}）：{c.Ps("findings")}；认定办结须走 ReportHandling 决议");
    }

    private static async Task ReportClosed(ApplyCtx c)
    {
        var id = c.Ps("reportId"); var outcome = c.Ps("outcome"); var severity = c.Ps("severity");
        await c.Exec("UPDATE proj.reports SET status='Closed', outcome=@o, action=@a, severity=@s WHERE report_id=@r", ("o", outcome), ("a", c.Ps("action")), ("s", severity), ("r", id));
        if (outcome is "Substantiated" or "PartiallySubstantiated")
            await c.Todo("Compliance", $"举报属实整改：{id}（{c.Ps("action")}）", new JsonObject { ["reportId"] = id, ["action"] = c.Ps("action") });
        c.Impact($"举报办结 {id}：{outcome}·情节 {severity}；处置 {c.Ps("action")} 入链留痕" + (severity == "Serious" ? "；情节严重，通告公司群/董事会群/监事群" : ""));
    }

    private static async Task SupervisionEmailSet(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.company SET supervision_email=@e WHERE id=1", ("e", c.Ps("email")));
        c.Impact($"监管邮箱设置为 {c.Ps("email")}（多个高层共同监督登录）；各群介绍展示该邮箱，举报走邮箱而非系统自助通道");
    }

    private static async Task EmployeeShareSubscription(ApplyCtx c)
    {
        var em = (await c.Rows("SELECT party_id,name FROM proj.employees WHERE employee_id=@i", ("i", c.Ps("employeeId"))))[0];
        var partyId = Pg.S(em["party_id"]); var name = Pg.S(em["name"]);
        await c.EnsureParty(partyId, name);
        var cap = await c.CurrentCap();
        var sub = c.Pd("subscribed");
        if (sub <= 0 && c.P["targetRatio"] != null)
        {
            var r = c.Pd("targetRatio");
            var total = cap.Where(p => p.Status == "Normal").Sum(p => p.Subscribed);
            sub = Math.Round(total * r / (1 - r), 2);
        }
        var exist = cap.FirstOrDefault(p => p.PartyId == partyId);
        if (exist != null) exist.Subscribed += sub;
        else cap.Add(new ApplyCtx.Pos { PartyId = partyId, Subscribed = sub, PaidIn = 0m });
        await c.SaveCap(cap);
        await c.Exec("UPDATE proj.employees SET is_shareholder=TRUE WHERE employee_id=@i", ("i", c.Ps("employeeId")));
        await c.Todo("BusinessRegistry", "工商变更待办：员工入股登记", new JsonObject { ["employeeId"] = c.Ps("employeeId"), ["partyId"] = partyId, ["subscribed"] = sub });
        c.Impact($"员工 {name} 自由入股：认缴 {sub}（双身份分账：工资走薪酬、持股走股权）；实缴走 CapitalPaidIn");
    }

    private static async Task EmployeeShareGranted(ApplyCtx c)
    {
        var em = (await c.Rows("SELECT party_id,name FROM proj.employees WHERE employee_id=@i", ("i", c.Ps("employeeId"))))[0];
        var partyId = Pg.S(em["party_id"]); var name = Pg.S(em["name"]);
        var capital = c.Pd("capitalAmount");
        await c.EnsureParty(partyId, name);
        if (c.Ps("source") == "ShareholderGift")
        {
            // 股东赠与：零对价转让，复用股权转让引擎（个税/印花税按 0 对价计算留痕）
            c.P["fromParty"] = c.Ps("fromParty");
            c.P["toParty"] = partyId;
            c.P["toName"] = name;
            c.P["price"] = 0m;
            await ShareTransfer(c);
        }
        else
        {
            var cap = await c.CurrentCap();
            var exist = cap.FirstOrDefault(p => p.PartyId == partyId);
            if (exist != null) { exist.Subscribed += capital; exist.PaidIn += capital; }
            else cap.Add(new ApplyCtx.Pos { PartyId = partyId, Subscribed = capital, PaidIn = capital });
            await c.SaveCap(cap);
            await c.Ledger(c.Period, "CapitalReserve", capital, 0, $"资本公积转增股本·员工分股 {name}");
            await c.Ledger(c.Period, "PaidInCapital", 0, capital, $"员工分股 {name}（公积转增·视同实缴）");
        }
        await c.Exec("UPDATE proj.employees SET is_shareholder=TRUE WHERE employee_id=@i", ("i", c.Ps("employeeId")));
        await c.Todo("BusinessRegistry", "工商变更待办：员工分股登记", new JsonObject { ["employeeId"] = c.Ps("employeeId"), ["partyId"] = partyId, ["capital"] = capital });
        c.Impact($"公司分股给 {name}：{capital}（{c.Ps("source")}）；股权账本+会计分录+工商待办同步");
    }
}
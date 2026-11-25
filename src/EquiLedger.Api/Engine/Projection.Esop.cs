// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>员工持股与股权激励中心：五形式分离管理（直接受让/增资认购/持股平台间接/期权/虚拟分红权）。
/// 自愿申请≠不经公司程序取得股权；公司授予≠自动完成法律变更。治理事件账本+经济事项账本+财务会计三层贯通。
/// 股权权利、员工岗位权限、系统操作权限分别管理；股东知情查阅凭股东角色账号依法保障。</summary>
public static partial class Projection
{
    private static decimal EsopR2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    private static async Task EsopApplied(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("planId")) ? $"ESOP-{c.Eid}" : c.Ps("planId");
        await c.ExecJ(@"INSERT INTO proj.esop_plans(plan_id,employee_id,form,status,quantity,ratio,price,platform_party,vesting,source_event_id)
            VALUES(@i,@em,@form,'Applied',@q,@r,@price,@pp,@v,@e)",
            new[] { ("v", (JsonNode)(c.P["vesting"] as JsonObject ?? new JsonObject())) },
            ("i", id), ("em", c.Ps("employeeId")), ("form", c.Ps("form")), ("q", c.Pd("quantity")), ("r", c.Pd("ratio")),
            ("price", c.Pd("price")), ("pp", c.Ps("platformParty")), ("e", c.Evt.EventId));
        c.Impact($"持股计划申请 {id}（{c.Ps("form")}·员工 {c.Ps("employeeId")}·来源 {(c.Ps("origin") == "CompanyGrant" ? "公司主动激励" : "员工自愿")}）：申请不等于取得股权，须资格审核+决议+确认");
    }

    private static async Task EsopReviewed(ApplyCtx c)
    {
        var id = c.Ps("planId"); var ok = c.Ps("result") == "Approved";
        await c.Exec("UPDATE proj.esop_plans SET status=@s, review=@r WHERE plan_id=@i", ("s", ok ? "Reviewed" : "Rejected"), ("r", c.Ps("review")), ("i", id));
        c.Impact($"持股计划 {id} 资格审核：{(ok ? "通过" : "驳回")}（{c.Ps("review")}）；章程/股东决议/股权结构/法律程序合规性留痕");
    }

    private static async Task EsopGranted(ApplyCtx c)
    {
        var id = c.Ps("planId");
        var pl = (await c.Rows("SELECT * FROM proj.esop_plans WHERE plan_id=@i", ("i", id)))[0];
        var form = Pg.S(pl["form"]); var emId = Pg.S(pl["employee_id"]);
        var em = (await c.Rows("SELECT party_id,name FROM proj.employees WHERE employee_id=@i", ("i", emId)))[0];
        var partyId = Pg.S(em["party_id"]); var name = Pg.S(em["name"]);
        var qty = Pg.D(pl["quantity"]);
        await c.Exec("UPDATE proj.esop_plans SET status='Granted', granted_qty=@q, confirm_ref=@cr WHERE plan_id=@i", ("q", qty), ("cr", c.Ps("confirmRef")), ("i", id));
        if (form == "DirectTransfer")
        {
            // 员工直接购买现有股东转让的股权：复用股权转让引擎（优先购买权/个税/印花税同源）
            c.P["fromParty"] = c.Ps("fromParty");
            c.P["toParty"] = partyId;
            c.P["toName"] = name;
            c.P["price"] = c.Pd("price");
            c.P["capitalAmount"] = qty;
            await ShareTransfer(c);
        }
        else if (form == "CapitalIncrease")
        {
            // 公司增资员工认购新增注册资本：原股东优先认缴权由决议载明放弃或行使
            await c.EnsureParty(partyId, name);
            var cap = await c.CurrentCap();
            var exist = cap.FirstOrDefault(p => p.PartyId == partyId);
            if (exist != null) exist.Subscribed += qty;
            else cap.Add(new ApplyCtx.Pos { PartyId = partyId, Subscribed = qty, PaidIn = 0m });
            await c.SaveCap(cap);
            await c.Todo("BusinessRegistry", "工商变更待办：员工增资认购登记", new JsonObject { ["employeeId"] = emId, ["partyId"] = partyId, ["subscribed"] = qty });
        }
        else if (form == "Platform")
        {
            await c.Exec("UPDATE proj.esop_plans SET platform_party=@pp WHERE plan_id=@i", ("pp", c.Ps("platformParty") == "" ? Pg.S(pl["platform_party"]) : c.Ps("platformParty")), ("i", id));
            c.Impact($"持股平台间接持有：员工 {name} 通过平台 {c.Ps("platformParty")} 持有权益，不直接登记为公司股东");
        }
        if (form is "DirectTransfer" or "CapitalIncrease")
            await c.Exec("UPDATE proj.employees SET is_shareholder=TRUE WHERE employee_id=@i", ("i", emId));
        c.Impact($"持股计划 {id} 授予（{form}·{name}·数量 {qty}）：员工确认凭据 {c.Ps("confirmRef")} 入链；法律变更以工商/股东名册登记为准");
    }

    private static async Task EsopVested(ApplyCtx c)
    {
        var id = c.Ps("planId"); var qty = c.Pd("quantity"); var fv = c.Pd("fairValue");
        var expense = EsopR2(qty * fv);
        await c.Exec("UPDATE proj.esop_plans SET vested_qty=vested_qty+@q, sbc_expense=sbc_expense+@e WHERE plan_id=@i", ("q", qty), ("e", expense), ("i", id));
        if (expense > 0)
        {
            // 权益结算股份支付：授予日公允价值×归属数量，等待期费用化
            await c.Ledger(c.Period, "ShareBasedExpense", expense, 0, $"股份支付费用·{id} 归属 {qty}×公允 {fv}");
            await c.Ledger(c.Period, "OtherCapitalReserve", 0, expense, $"其他资本公积·股份支付 {id}");
        }
        c.Impact($"期权计划 {id} 归属 {qty}（条件满足）：股份支付费用 {expense} 自动计提（CAS11 权益结算）");
    }

    private static async Task EsopExercised(ApplyCtx c)
    {
        var id = c.Ps("planId"); var qty = c.Pd("quantity");
        var pl = (await c.Rows("SELECT * FROM proj.esop_plans WHERE plan_id=@i", ("i", id)))[0];
        var emId = Pg.S(pl["employee_id"]); var price = Pg.D(pl["price"]); var granted = Pg.D(pl["granted_qty"]);
        var sbc = Pg.D(pl["sbc_expense"]);
        var em = (await c.Rows("SELECT party_id,name FROM proj.employees WHERE employee_id=@i", ("i", emId)))[0];
        var partyId = Pg.S(em["party_id"]); var name = Pg.S(em["name"]);
        var bank = EsopR2(qty * price);
        var sbcPortion = granted > 0 ? EsopR2(sbc * qty / granted) : 0m;
        var premium = bank + sbcPortion - qty;
        await c.Ledger(c.Period, "Bank", bank, 0, $"期权行权收款·{id} {qty}×{price}");
        await c.Ledger(c.Period, "OtherCapitalReserve", sbcPortion, 0, $"行权结转其他资本公积·{id}");
        await c.Ledger(c.Period, "PaidInCapital", 0, qty, $"行权转实收资本·{name}");
        if (premium >= 0) await c.Ledger(c.Period, "CapitalReserve", 0, premium, $"行权溢价·{id}");
        else await c.Ledger(c.Period, "CapitalReserve", -premium, 0, $"行权折价冲公积·{id}");
        await c.EnsureParty(partyId, name);
        var cap = await c.CurrentCap();
        var exist = cap.FirstOrDefault(p => p.PartyId == partyId);
        if (exist != null) { exist.Subscribed += qty; exist.PaidIn += qty; }
        else cap.Add(new ApplyCtx.Pos { PartyId = partyId, Subscribed = qty, PaidIn = qty });
        await c.SaveCap(cap);
        await c.Exec("UPDATE proj.employees SET is_shareholder=TRUE WHERE employee_id=@i", ("i", emId));
        var exercised = Pg.D(pl["exercised_qty"]) + qty;
        await c.Exec("UPDATE proj.esop_plans SET exercised_qty=exercised_qty+@q, status=@s WHERE plan_id=@i",
            ("q", qty), ("s", exercised >= granted ? "Exercised" : "Granted"), ("i", id));
        await c.Todo("BusinessRegistry", "工商变更待办：期权行权股东登记", new JsonObject { ["employeeId"] = emId, ["partyId"] = partyId, ["capital"] = qty });
        c.Impact($"期权行权 {id}：{qty} 股·收款 {bank}·结转公积 {sbcPortion}·溢价 {premium}；{name} 登记为股东（行权后才取得实股）");
    }

    private static async Task EsopExited(ApplyCtx c)
    {
        var id = c.Ps("planId");
        var pl = (await c.Rows("SELECT * FROM proj.esop_plans WHERE plan_id=@i", ("i", id)))[0];
        var form = Pg.S(pl["form"]); var emId = Pg.S(pl["employee_id"]);
        await c.Exec("UPDATE proj.esop_plans SET status='Exited' WHERE plan_id=@i", ("i", id));
        if (form is "DirectTransfer" or "CapitalIncrease")
        {
            var em = (await c.Rows("SELECT party_id FROM proj.employees WHERE employee_id=@i", ("i", emId)))[0];
            c.P["partyId"] = Pg.S(em["party_id"]);
            c.P["capitalAmount"] = c.Pd("capitalAmount");
            await ShareExit(c);
        }
        c.Impact($"持股计划 {id} 退出（{form}）：未归属/未行权部分作废；实股走退股引擎估值与会计处理；离职退出管理留痕");
    }

    private static async Task VirtualDividendDeclared(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("dividendId")) ? $"VD-{c.Eid}" : c.Ps("dividendId");
        var amt = c.Pd("amount");
        await c.Exec("INSERT INTO proj.esop_virtual_dividends(id,plan_id,period,amount,status,source_event_id) VALUES(@i,@p,@per,@a,'Declared',@e)",
            ("i", id), ("p", c.Ps("planId")), ("per", c.Period), ("a", amt), ("e", c.Evt.EventId));
        // 虚拟分红权=约定经济权益（奖金性质），不当然成为股东：费用化而非利润分配
        await c.Ledger(c.Period, "Expense", amt, 0, $"虚拟分红权权益·{c.Ps("planId")}（奖金性质费用化）");
        await c.Ledger(c.Period, "PayrollPayable", 0, amt, $"应付虚拟分红·{id}");
        c.Impact($"虚拟分红宣告 {id}：{amt}（仅经济权益·非股东分红·费用化计提）");
    }

    private static async Task VirtualDividendPaid(ApplyCtx c)
    {
        var id = c.Ps("dividendId"); var amt = c.Pd("amount");
        await c.Ledger(c.Period, "PayrollPayable", amt, 0, $"虚拟分红支付·{id}");
        await c.Ledger(c.Period, "Bank", 0, amt, $"虚拟分红付讫·{id}");
        await c.Exec("UPDATE proj.esop_virtual_dividends SET status='Paid' WHERE id=@i", ("i", id));
        c.Impact($"虚拟分红支付 {id}：{amt}（回单入链）");
    }
}
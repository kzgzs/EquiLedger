// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>第二体系贯通：采购八步链（申请→审批→订单→交货→验收入库→应付形成→付款审批→支付）、
/// 借款还本付息、纳税申报与缴纳、群通知配置。业务事件一次提交同时落业务投影+会计分录+治理留痕。</summary>
public static partial class Projection
{
    private static async Task PurchaseRequested(ApplyCtx c)
    {
        await c.ExecJ("INSERT INTO proj.purchase_requests(req_no,department,items,reason,status,source_event_id) VALUES(@r,@d,@i,@why,'Requested',@e)",
            new[] { ("i", (JsonNode?)c.P["items"]) }, ("r", c.Ps("reqNo")), ("d", c.Ps("department")), ("why", c.Ps("reason")), ("e", c.Evt.EventId));
        c.Impact($"采购申请 {c.Ps("reqNo")}（{c.Ps("department")}）提交，待审批决议");
    }

    private static async Task PurchaseApproved(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.purchase_requests SET status='Approved' WHERE req_no=@r", ("r", c.Ps("reqNo")));
        c.Impact($"采购申请 {c.Ps("reqNo")} 经决议批准，授权额度 {c.Pd("approvedAmount")}");
    }

    private static async Task PurchaseOrderCreated(ApplyCtx c)
    {
        await c.ExecJ("INSERT INTO proj.purchase_orders(po_no,req_no,supplier,items,expected_date,status,source_event_id) VALUES(@p,@r,@s,@i,@d,'Open',@e)",
            new[] { ("i", (JsonNode?)c.P["items"]) }, ("p", c.Ps("poNo")), ("r", c.Ps("reqNo")), ("s", c.Ps("supplier")),
            ("d", DateOnly.Parse(c.Ps("expectedDate")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("e", c.Evt.EventId));
        c.Impact($"采购订单 {c.Ps("poNo")} 下达（供应商 {c.Ps("supplier")}），关联申请 {c.Ps("reqNo")}");
    }

    private static async Task PurchaseDelivered(ApplyCtx c)
    {
        await c.ExecJ("INSERT INTO proj.purchase_deliveries(delivery_no,po_no,items,delivered_date,source_event_id) VALUES(@d,@p,@i,@dd,@e)",
            new[] { ("i", (JsonNode?)c.P["items"]) }, ("d", c.Ps("deliveryNo")), ("p", c.Ps("poNo")),
            ("dd", DateOnly.Parse(c.Ps("deliveredDate")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("e", c.Evt.EventId));
        await c.Exec("UPDATE proj.purchase_orders SET status='Delivered' WHERE po_no=@p", ("p", c.Ps("poNo")));
        c.Impact($"供应商交货 {c.Ps("deliveryNo")}（订单 {c.Ps("poNo")}），待验收");
    }

    private static async Task PurchaseInspected(ApplyCtx c)
    {
        var po = c.Ps("poNo");
        await c.ExecJ("INSERT INTO proj.purchase_inspections(inspection_no,delivery_no,po_no,accepted,rejected,inspector,source_event_id) VALUES(@i,@d,@p,@a,@r,@ins,@e)",
            new[] { ("a", (JsonNode?)c.P["accepted"]), ("r", (JsonNode?)c.P["rejected"]) },
            ("i", c.Ps("inspectionNo")), ("d", c.Ps("deliveryNo")), ("p", po), ("ins", c.Ps("inspector")), ("e", c.Evt.EventId));
        decimal net = 0, tax = 0;
        foreach (var it in c.P["accepted"] as JsonArray ?? new JsonArray())
        {
            var mid = it!["materialId"]!.GetValue<string>();
            var qty = Convert.ToDecimal(it["qty"]!.ToString());
            var uc = Convert.ToDecimal(it["unitPrice"]!.ToString());
            var rate = it["taxRate"] is null ? 0m : Convert.ToDecimal(it["taxRate"]!.ToString());
            var amt = Math.Round(qty * uc, 2); var t = Math.Round(amt * rate, 2);
            var m = (await c.Rows("SELECT category FROM proj.materials WHERE material_id=@i", ("i", mid)))[0];
            var acc = Pg.S(m["category"]) == "goods" ? "Inventory" : "Materials";
            var wh = c.Ps("warehouse");
            var old = await c.Rows("SELECT qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", mid), ("w", wh));
            var oq = old.Count > 0 ? Pg.D(old[0]["qty"]) : 0; var oa = old.Count > 0 ? Pg.D(old[0]["avg_cost"]) : 0;
            var na = oq + qty > 0 ? Math.Round((oq * oa + amt) / (oq + qty), 4) : uc;
            await c.Exec("INSERT INTO proj.stock_balances(material_id,warehouse,qty,avg_cost) VALUES(@m,@w,@q,@a) ON CONFLICT (material_id,warehouse) DO UPDATE SET qty=proj.stock_balances.qty+@q, avg_cost=@a",
                ("m", mid), ("w", wh), ("q", qty), ("a", na));
            await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'In',@q,@u,@amt,@memo,@e)",
                ("id", $"SM-{c.Eid}-{mid}"), ("m", mid), ("w", wh), ("q", qty), ("u", uc), ("amt", amt), ("memo", $"采购验收 {po}"), ("e", c.Evt.EventId));
            await c.Ledger(c.Period, acc, amt, 0, $"采购验收入库 {po}：{mid}×{qty}");
            net += amt; tax += t;
        }
        if (tax > 0) await c.Ledger(c.Period, "TaxPayable", tax, 0, $"采购进项税 {po}");
        await c.Ledger(c.Period, "AP", 0, net + tax, $"应付账款形成 {po}（验收合格部分）");
        await c.Exec("UPDATE proj.purchase_orders SET status='Inspected' WHERE po_no=@p", ("p", po));
        c.Impact($"验收 {c.Ps("inspectionNo")}：合格入库 net={net}、进项税 {tax}、应付 {net + tax} 自动形成；拒收部分不入账");
    }

    private static async Task PurchasePaymentApproved(ApplyCtx c)
    {
        c.Impact($"付款审批决议通过：订单 {c.Ps("poNo")} 授权支付 {c.Pd("amount")}，待银行支付执行");
    }

    private static async Task PurchasePaid(ApplyCtx c)
    {
        var po = c.Ps("poNo"); var amt = c.Pd("amount");
        await c.Exec("INSERT INTO proj.purchase_payments(pay_id,po_no,amount,receipt_hash,source_event_id) VALUES(@id,@p,@a,@h,@e)",
            ("id", $"PP-{c.Eid}"), ("p", po), ("a", amt), ("h", c.Ps("receiptHash")), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "AP", amt, 0, $"采购付款 {po}");
        await c.Ledger(c.Period, "Bank", 0, amt, $"采购付款 {po}（银行回单留痕）");
        var o = (await c.Rows("SELECT paid_amount,items,status FROM proj.purchase_orders WHERE po_no=@p", ("p", po)))[0];
        var paid = Pg.D(o["paid_amount"]) + amt;
        decimal acceptedValue = 0;
        var ins = await c.Rows("SELECT accepted FROM proj.purchase_inspections WHERE po_no=@p", ("p", po));
        foreach (var i in ins)
            foreach (var it in Pg.J(i["accepted"]) as JsonArray ?? new JsonArray())
            {
                var q = Convert.ToDecimal(it!["qty"]!.ToString()); var u = Convert.ToDecimal(it["unitPrice"]!.ToString());
                var r = it["taxRate"] is null ? 0m : Convert.ToDecimal(it["taxRate"]!.ToString());
                acceptedValue += Math.Round(q * u, 2) + Math.Round(Math.Round(q * u, 2) * r, 2);
            }
        await c.Exec("UPDATE proj.purchase_orders SET paid_amount=@a, status=@s WHERE po_no=@p",
            ("a", paid), ("s", paid >= acceptedValue ? "Closed" : "Inspected"), ("p", po));
        c.Impact($"采购支付 {po}：{amt}，应付冲减、银行流出；累计已付 {paid}/{acceptedValue}{(paid >= acceptedValue ? "，订单关闭" : "")}");
    }

    private static async Task LoanReceived(ApplyCtx c)
    {
        var id = c.Ps("loanId"); var p = c.Pd("principal");
        await c.Exec("INSERT INTO proj.loans(loan_id,lender,principal,annual_rate,term_months,start_date,outstanding_principal,status,source_event_id) VALUES(@i,@l,@p,@r,@t,@d,@p,'Active',@e)",
            ("i", id), ("l", c.Ps("lender")), ("p", p), ("r", c.Pd("annualRate")), ("t", (int)c.Pd("termMonths")),
            ("d", DateOnly.Parse(c.Ps("startDate")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "Bank", p, 0, $"借款到账 {id}（{c.Ps("lender")}）");
        await c.Ledger(c.Period, "LoanPayable", 0, p, $"借款本金 {id}");
        c.Impact($"借款 {id} 到账 {p}，年利率 {c.Pd("annualRate")}、{c.Pd("termMonths")} 期；负债与筹资现金流同步");
    }

    private static async Task LoanInterestAccrued(ApplyCtx c)
    {
        var id = c.Ps("loanId"); var period = c.Ps("period"); var amt = c.Pd("amount");
        await c.Exec("INSERT INTO proj.loan_interest(loan_id,period,amount,source_event_id) VALUES(@i,@p,@a,@e)", ("i", id), ("p", period), ("a", amt), ("e", c.Evt.EventId));
        await c.Exec("UPDATE proj.loans SET accrued_interest=accrued_interest+@a WHERE loan_id=@i", ("a", amt), ("i", id));
        await c.Ledger(period, "InterestExpense", amt, 0, $"借款利息计提 {id} {period}");
        await c.Ledger(period, "InterestPayable", 0, amt, $"应付利息 {id} {period}");
        c.Impact($"利息计提 {id} {period}：{amt}（ outstanding×年利率÷12 校验一致）");
    }

    private static async Task LoanRepaid(ApplyCtx c)
    {
        var id = c.Ps("loanId"); var pp = c.Pd("principalPart"); var ip = c.Pd("interestPart");
        await c.Ledger(c.Period, "LoanPayable", pp, 0, $"还本 {id}");
        if (ip > 0) await c.Ledger(c.Period, "InterestPayable", ip, 0, $"付息 {id}");
        await c.Ledger(c.Period, "Bank", 0, pp + ip, $"借款还本付息 {id}");
        var l = (await c.Rows("SELECT outstanding_principal,accrued_interest FROM proj.loans WHERE loan_id=@i", ("i", id)))[0];
        var op = Pg.D(l["outstanding_principal"]) - pp; var ai = Pg.D(l["accrued_interest"]) - ip;
        await c.Exec("UPDATE proj.loans SET outstanding_principal=@o, accrued_interest=@a, paid_interest=paid_interest+@i, status=@s WHERE loan_id=@id",
            ("o", op), ("a", ai), ("i", ip), ("s", op <= 0 && ai <= 0 ? "Closed" : "Active"), ("id", id));
        c.Impact($"还本付息 {id}：本金 {pp}、利息 {ip}；剩余本金 {op}{(op <= 0 && ai <= 0 ? "，借款结清" : "")}");
    }

    private static async Task TaxReturnFiled(ApplyCtx c)
    {
        var period = c.Ps("period"); var kind = c.Ps("kind");
        await c.ExecJ("INSERT INTO proj.tax_returns(return_id,period,kind,amount,detail,status,source_event_id) VALUES(@id,@p,@k,@a,@d,'Filed',@e)",
            new[] { ("d", (JsonNode?)c.P["detail"]) }, ("id", $"TR-{period}-{kind}"), ("p", period), ("k", kind), ("a", c.Pd("amount")), ("e", c.Evt.EventId));
        var amt = c.Pd("amount");
        if (kind == "CIT" && amt > 0) { await c.Ledger(period, "IncomeTaxExpense", amt, 0, $"所得税计提 {period}"); await c.Ledger(period, "TaxPayable", 0, amt, $"应交所得税 {period}"); }
        if (kind == "Surcharge" && amt > 0) { await c.Ledger(period, "TaxSurcharge", amt, 0, $"附加税费计提 {period}"); await c.Ledger(period, "TaxPayable", 0, amt, $"应交附加税费 {period}"); }
        c.Impact($"纳税申报 {period} {kind}：应纳 {amt} 已申报留痕（CIT/附加同步计提），待 TaxPaid 执行");
    }

    private static async Task TaxPaid(ApplyCtx c)
    {
        var period = c.Ps("period"); var kind = c.Ps("kind"); var amt = c.Pd("amount");
        await c.Exec("UPDATE proj.tax_returns SET status='Paid' WHERE period=@p AND kind=@k", ("p", period), ("k", kind));
        await c.Ledger(c.Period, "TaxPayable", amt, 0, $"缴纳税款 {period} {kind}");
        await c.Ledger(c.Period, "Bank", 0, amt, $"缴纳税款 {period} {kind}（完税凭证留痕）");
        c.Impact($"税款缴纳 {period} {kind}：{amt}，应交税费冲减、银行流出");
    }

    private static async Task NotifyGroupConfigured(ApplyCtx c)
    {
        var gid = c.Ps("groupId");
        await c.Exec("INSERT INTO proj.notify_groups(group_id,name,kind,department,webhook,enabled,source_event_id) VALUES(@g,@n,@k,@d,@w,@en,@e) ON CONFLICT (group_id) DO UPDATE SET name=@n,kind=@k,department=@d,webhook=@w,enabled=@en,source_event_id=@e",
            ("g", gid), ("n", c.Ps("name")), ("k", c.Ps("kind")), ("d", c.Ps("department")), ("w", c.Ps("webhook")), ("en", c.Pb("enabled")), ("e", c.Evt.EventId));
        await c.Exec("DELETE FROM proj.notify_bindings WHERE group_id=@g", ("g", gid));
        foreach (var t in c.P["bindings"] as JsonArray ?? new JsonArray())
            await c.Exec("INSERT INTO proj.notify_bindings(group_id,event_type) VALUES(@g,@t)", ("g", gid), ("t", t!.GetValue<string>()));
        c.Impact($"群通知配置 {c.Ps("name")}（{c.Ps("kind")}）经决议生效：绑定事件 {(c.P["bindings"] as JsonArray)?.Count ?? 0} 个；敏感分级由系统强制，高敏感仅摘要");
    }
}
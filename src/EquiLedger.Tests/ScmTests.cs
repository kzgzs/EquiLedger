// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>第二体系贯通：采购八步链、借款还本付息、纳税申报与缴纳；每步哈希链重放验真。</summary>
public class ScmTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 采购八步贯通与超付拦截()
    {
        await _f.Found();
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "M1", ["name"] = "钢材", ["unit"] = "件", ["category"] = "raw", ["safetyStock"] = 0 });
        await _f.Submit("WarehouseDefined", "op", null, new JsonObject { ["warehouseId"] = "WH1", ["name"] = "主仓", ["location"] = "一号库" });
        await _f.Submit("PurchaseRequested", "op", null, new JsonObject
        {
            ["reqNo"] = "PR-1", ["department"] = "生产部",
            ["items"] = new JsonArray(new JsonObject { ["materialId"] = "M1", ["qty"] = 100 }), ["reason"] = "生产备料"
        });
        var orderPayload = new JsonObject
        {
            ["poNo"] = "PO-B1", ["reqNo"] = "PR-1", ["supplier"] = "供应商A",
            ["items"] = new JsonArray(new JsonObject { ["materialId"] = "M1", ["qty"] = 100, ["unitPrice"] = 10, ["taxRate"] = 0.13 }),
            ["expectedDate"] = "2026-12-10"
        };
        var noRes = await _f.SubmitFails("PurchaseOrderCreated", "op", null, orderPayload);
        Assert.Equal("NotApproved", noRes.Kind);
        await _f.RecordResolution("R-PA", "PurchasePlan", new JsonObject { ["reqNo"] = "PR-1", ["approvedAmount"] = 1200 });
        await _f.Submit("PurchaseApproved", "op", "R-PA", new JsonObject { ["reqNo"] = "PR-1", ["approvedAmount"] = 1200 });
        await _f.Submit("PurchaseOrderCreated", "op", null, orderPayload);
        await _f.Submit("PurchaseDelivered", "op", null, new JsonObject
        {
            ["deliveryNo"] = "DL-1", ["poNo"] = "PO-B1",
            ["items"] = new JsonArray(new JsonObject { ["materialId"] = "M1", ["qty"] = 100 }), ["deliveredDate"] = "2026-12-08"
        });
        await _f.Submit("PurchaseInspected", "op", null, new JsonObject
        {
            ["inspectionNo"] = "IN-1", ["deliveryNo"] = "DL-1", ["poNo"] = "PO-B1", ["warehouse"] = "WH1", ["inspector"] = "操建国",
            ["accepted"] = new JsonArray(new JsonObject { ["materialId"] = "M1", ["qty"] = 100, ["unitPrice"] = 10, ["taxRate"] = 0.13 }),
            ["rejected"] = new JsonArray()
        });
        Assert.Equal(1130m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='AP'"));
        Assert.Equal(130m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='TaxPayable'"));
        Assert.Equal(100m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='M1' AND warehouse='WH1'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
        await _f.RecordResolution("R-PP2", "PurchasePayment", new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 2000 });
        var over = await _f.SubmitFails("PurchasePaymentApproved", "op", "R-PP2", new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 2000 });
        Assert.Equal("AmountExceed", over.Kind);
        await _f.RecordResolution("R-PP", "PurchasePayment", new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 1130 });
        await _f.Submit("PurchasePaymentApproved", "op", "R-PP", new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 1130 });
        var noPayRes = await _f.SubmitFails("PurchasePaid", "op", null, new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 1130, ["receiptHash"] = "h" });
        Assert.Equal("NeedResolution", noPayRes.Kind);
        await _f.Submit("PurchasePaid", "op", "R-PP", new JsonObject { ["poNo"] = "PO-B1", ["amount"] = 1130, ["receiptHash"] = "h" });
        Assert.Equal(0m, await _f.ScalarD("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='AP'"));
        Assert.Equal("Closed", (await _f.Rows("SELECT status FROM proj.purchase_orders WHERE po_no='PO-B1'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 借款到账计提与还本付息()
    {
        await _f.Found();
        await _f.RecordResolution("R-LN", "Loan", new JsonObject { ["loanId"] = "LN-1", ["principal"] = 100000 });
        await _f.Submit("LoanReceived", "op", "R-LN", new JsonObject
        {
            ["loanId"] = "LN-1", ["lender"] = "某银行", ["principal"] = 100000, ["annualRate"] = 0.06, ["termMonths"] = 12, ["startDate"] = "2026-11-01"
        });
        Assert.Equal(100000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='LoanPayable'"));
        await _f.Submit("LoanInterestAccrued", "op", null, new JsonObject { ["loanId"] = "LN-1", ["period"] = "2026-11", ["amount"] = 500 });
        var dup = await _f.SubmitFails("LoanInterestAccrued", "op", null, new JsonObject { ["loanId"] = "LN-1", ["period"] = "2026-11", ["amount"] = 500 });
        Assert.Equal("AlreadyAccrued", dup.Kind);
        var wrong = await _f.SubmitFails("LoanInterestAccrued", "op", null, new JsonObject { ["loanId"] = "LN-1", ["period"] = "2026-12", ["amount"] = 600 });
        Assert.Equal("AmountMismatch", wrong.Kind);
        await _f.RecordResolution("R-LR", "LoanRepayment", new JsonObject { ["loanId"] = "LN-1", ["principalPart"] = 20000, ["interestPart"] = 500 });
        await _f.Submit("LoanRepaid", "op", "R-LR", new JsonObject { ["loanId"] = "LN-1", ["principalPart"] = 20000, ["interestPart"] = 500 });
        Assert.Equal(80000m, await _f.ScalarD("SELECT outstanding_principal FROM proj.loans WHERE loan_id='LN-1'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT accrued_interest FROM proj.loans WHERE loan_id='LN-1'"));
        Assert.Equal(500m, await _f.ScalarD("SELECT paid_interest FROM proj.loans WHERE loan_id='LN-1'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 纳税申报计提与缴纳()
    {
        await _f.Found();
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 500000, ["customer"] = "X" }, Fixture.Ev("发票"));
        await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 2000, ["category"] = "办公", ["payee"] = "A" });
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var vatCredit = await _f.ScalarD("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='TaxPayable'");
        var profit = await _f.ScalarD("SELECT COALESCE(SUM(CASE WHEN account='Revenue' THEN credit-debit WHEN account='Expense' THEN debit-credit ELSE 0 END),0) FROM proj.ledger");
        var cit = Math.Round(profit * 0.25m, 2);
        await _f.RecordResolution("R-TX", "TaxFiling", new JsonObject { ["period"] = period, ["kind"] = "CIT", ["amount"] = cit });
        await _f.Submit("TaxReturnFiled", "op", "R-TX", new JsonObject { ["period"] = period, ["kind"] = "CIT", ["amount"] = cit, ["detail"] = new JsonObject { ["profit"] = profit, ["rate"] = 0.25 } });
        Assert.Equal(vatCredit + cit, await _f.ScalarD("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='TaxPayable'"));
        Assert.Equal(cit, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='IncomeTaxExpense'"));
        var mismatch = await _f.SubmitFails("TaxPaid", "op", null, new JsonObject { ["period"] = period, ["kind"] = "CIT", ["amount"] = 100 });
        Assert.Equal("AmountMismatch", mismatch.Kind);
        await _f.Submit("TaxPaid", "op", null, new JsonObject { ["period"] = period, ["kind"] = "CIT", ["amount"] = cit });
        Assert.Equal(vatCredit, await _f.ScalarD("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='TaxPayable'"));
        Assert.Equal("Paid", (await _f.Rows($"SELECT status FROM proj.tax_returns WHERE period='{period}' AND kind='CIT'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }
}
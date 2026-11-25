// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>员工持股与股权激励中心：五形式分离；申请-审核-决议-确认-授予-归属-行权-退出；股份支付会计；每步重放验真。</summary>
public class EsopTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task Hire(string id)
    {
        await _f.RecordResolution($"RES-H-{id}", "Hire", new JsonObject { ["employeeId"] = id });
        await _f.Submit("EmployeeHired", "op", $"RES-H-{id}", new JsonObject
        {
            ["employeeId"] = id, ["partyId"] = id, ["name"] = $"员工{id}", ["position"] = "工程师",
            ["hireDate"] = "2026-11-01", ["contractId"] = $"CT-L-{id}", ["bankAccount"] = "6222000011112222", ["bankName"] = "测试银行"
        }, Fixture.Ev("劳动合同+入职登记"));
    }

    [Fact]
    public async Task 期权授予归属行权退出全链路()
    {
        await _f.Found();
        await Hire("E1");
        await _f.Submit("EsopApplied", "op", null, new JsonObject
        {
            ["planId"] = "ES-1", ["employeeId"] = "E1", ["form"] = "Option", ["origin"] = "CompanyGrant",
            ["quantity"] = 10000, ["price"] = 1, ["vesting"] = new JsonObject { ["serviceYears"] = 3 }
        }, Fixture.Ev("激励方案草案"));
        await _f.Submit("EsopReviewed", "op", null, new JsonObject { ["planId"] = "ES-1", ["result"] = "Approved", ["review"] = "章程与决议合规" }, Fixture.Ev("审核意见"));
        var noRes = await _f.SubmitFails("EsopGranted", "op", null, new JsonObject { ["planId"] = "ES-1", ["confirmRef"] = "CONF-1" }, Fixture.Ev("员工确认"));
        Assert.Equal("NoResolution", noRes.Kind);
        await _f.RecordResolution("R-EG", "EsopPlan", new JsonObject { ["planId"] = "ES-1" });
        await _f.Submit("EsopGranted", "op", "R-EG", new JsonObject { ["planId"] = "ES-1", ["confirmRef"] = "CONF-1" }, Fixture.Ev("员工确认书"));
        Assert.Equal("Granted", (await _f.Rows("SELECT status FROM proj.esop_plans WHERE plan_id='ES-1'"))[0]["status"]);
        Assert.Equal(false, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E1'"))[0]["is_shareholder"]);
        var notMet = await _f.SubmitFails("EsopVested", "op", null, new JsonObject { ["planId"] = "ES-1", ["quantity"] = 4000, ["fairValue"] = 2, ["conditionsMet"] = false }, Fixture.Ev("x"));
        Assert.Equal("ConditionsNotMet", notMet.Kind);
        await _f.Submit("EsopVested", "op", null, new JsonObject { ["planId"] = "ES-1", ["quantity"] = 4000, ["fairValue"] = 2, ["conditionsMet"] = true }, Fixture.Ev("归属条件达成证明"));
        Assert.Equal(8000m, await _f.ScalarD("SELECT sbc_expense FROM proj.esop_plans WHERE plan_id='ES-1'"));
        Assert.Equal(8000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='ShareBasedExpense'"));
        await _f.RecordResolution("R-EX", "EsopExercise", new JsonObject { ["planId"] = "ES-1" });
        var over = await _f.SubmitFails("EsopExercised", "op", "R-EX", new JsonObject { ["planId"] = "ES-1", ["quantity"] = 5000 }, Fixture.Ev("x"));
        Assert.Equal("AmountExceed", over.Kind);
        await _f.Submit("EsopExercised", "op", "R-EX", new JsonObject { ["planId"] = "ES-1", ["quantity"] = 4000 }, Fixture.Ev("行权申请+缴款回单"));
        Assert.Equal(4000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='Bank' AND memo LIKE '期权行权收款%'"));
        Assert.Equal(4000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='E1' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(true, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E1'"))[0]["is_shareholder"]);
        await _f.RecordResolution("R-EE", "EsopExit", new JsonObject { ["planId"] = "ES-1", ["capitalAmount"] = 4000 });
        await _f.Submit("EsopExited", "op", "R-EE", new JsonObject { ["planId"] = "ES-1", ["capitalAmount"] = 4000 }, Fixture.Ev("离职协议"));
        Assert.Equal("Exited", (await _f.Rows("SELECT status FROM proj.esop_plans WHERE plan_id='ES-1'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 虚拟分红权与持股平台与增资认购分离()
    {
        await _f.Found();
        await Hire("E2");
        await _f.Submit("EsopApplied", "op", null, new JsonObject { ["planId"] = "ES-V", ["employeeId"] = "E2", ["form"] = "Virtual", ["ratio"] = 0.05, ["origin"] = "CompanyGrant" }, Fixture.Ev("虚拟分红方案"));
        await _f.Submit("EsopReviewed", "op", null, new JsonObject { ["planId"] = "ES-V", ["result"] = "Approved", ["review"] = "合规" }, Fixture.Ev("审核"));
        await _f.RecordResolution("R-EV", "EsopPlan", new JsonObject { ["planId"] = "ES-V" });
        await _f.Submit("EsopGranted", "op", "R-EV", new JsonObject { ["planId"] = "ES-V", ["confirmRef"] = "CONF-V" }, Fixture.Ev("确认书"));
        await _f.RecordResolution("R-VD", "VirtualDividend", new JsonObject { ["planId"] = "ES-V", ["amount"] = 20000 });
        await _f.Submit("VirtualDividendDeclared", "op", "R-VD", new JsonObject { ["dividendId"] = "VD-1", ["planId"] = "ES-V", ["amount"] = 20000 }, Fixture.Ev("宣告决议执行"));
        Assert.Equal(20000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='Expense' AND memo LIKE '虚拟分红权权益%'"));
        var mismatch = await _f.SubmitFails("VirtualDividendPaid", "op", null, new JsonObject { ["dividendId"] = "VD-1", ["amount"] = 1 }, Fixture.Ev("x"));
        Assert.Equal("AmountMismatch", mismatch.Kind);
        await _f.Submit("VirtualDividendPaid", "op", null, new JsonObject { ["dividendId"] = "VD-1", ["amount"] = 20000 }, Fixture.Ev("银行回单"));
        Assert.Equal("Paid", (await _f.Rows("SELECT status FROM proj.esop_virtual_dividends WHERE id='VD-1'"))[0]["status"]);
        Assert.Equal(false, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E2'"))[0]["is_shareholder"]);
        await _f.Submit("EsopApplied", "op", null, new JsonObject { ["planId"] = "ES-P", ["employeeId"] = "E2", ["form"] = "Platform", ["quantity"] = 1000, ["platformParty"] = "alice", ["origin"] = "Voluntary" }, Fixture.Ev("入股申请"));
        await _f.Submit("EsopReviewed", "op", null, new JsonObject { ["planId"] = "ES-P", ["result"] = "Approved", ["review"] = "合规" }, Fixture.Ev("审核"));
        await _f.RecordResolution("R-EP", "EsopPlan", new JsonObject { ["planId"] = "ES-P" });
        await _f.Submit("EsopGranted", "op", "R-EP", new JsonObject { ["planId"] = "ES-P", ["confirmRef"] = "CONF-P" }, Fixture.Ev("确认书"));
        Assert.Equal(false, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E2'"))[0]["is_shareholder"]);
        await _f.Submit("EsopApplied", "op", null, new JsonObject { ["planId"] = "ES-C", ["employeeId"] = "E2", ["form"] = "CapitalIncrease", ["quantity"] = 50000, ["origin"] = "Voluntary" }, Fixture.Ev("增资认购申请"));
        await _f.Submit("EsopReviewed", "op", null, new JsonObject { ["planId"] = "ES-C", ["result"] = "Approved", ["review"] = "原股东优先认缴权已决议放弃" }, Fixture.Ev("审核"));
        await _f.RecordResolution("R-EC", "EsopPlan", new JsonObject { ["planId"] = "ES-C" });
        await _f.Submit("EsopGranted", "op", "R-EC", new JsonObject { ["planId"] = "ES-C", ["confirmRef"] = "CONF-C" }, Fixture.Ev("确认书"));
        Assert.Equal(50000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='E2' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(true, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E2'"))[0]["is_shareholder"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }
}
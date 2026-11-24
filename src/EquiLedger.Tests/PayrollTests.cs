// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A10/A12/A15：薪酬锁定、一次性奖金、离职结算、社保合规、合同守卫。</summary>
public class PayrollTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task Hire(string empId = "e1", string hireDate = "2026-11-01", decimal monthly = 8000)
    {
        await _f.Found();
        await _f.SignContract("CT-L-" + empId, "人事类", "劳动合同", new JsonArray(empId));
        await _f.RecordResolution("RES-H-" + empId, "Hire", new JsonObject { ["employeeId"] = empId });
        await _f.Submit("EmployeeHired", "op", "RES-H-" + empId, new JsonObject
        {
            ["employeeId"] = empId, ["partyId"] = empId, ["name"] = "员工" + empId,
            ["position"] = "工程师", ["hireDate"] = hireDate, ["contractId"] = "CT-L-" + empId,
            ["bankAccount"] = "6222000011112222", ["bankName"] = "测试银行"
        }, Fixture.Ev("劳动合同+入职登记"));
        await _f.RecordResolution("RES-W-" + empId, "PayrollChange", new JsonObject
        { ["ruleId"] = "wage-" + empId, ["kind"] = "Payroll.Standard", ["subject"] = empId, ["params"] = new JsonObject { ["monthly"] = monthly } });
        await _f.Submit("RuleActivated", "op", "RES-W-" + empId, new JsonObject
        { ["ruleId"] = "wage-" + empId, ["kind"] = "Payroll.Standard", ["subject"] = empId, ["params"] = new JsonObject { ["monthly"] = monthly } });
    }

    [Fact]
    public async Task Payroll_Cumulative_Withholding()
    {
        await Hire();
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("考勤表"));
        var line = (await _f.Rows("SELECT * FROM proj.payroll_lines WHERE period='2026-11'"))[0];
        Assert.Equal(8000m, Storage.Pg.D(line["gross"]));
        Assert.Equal(1400m, Storage.Pg.D(line["si_personal"]));  // 8000×17.5%
        Assert.Equal(48m, Storage.Pg.D(line["iit"]));            // (8000−5000−1400)×3%
        Assert.Equal(6552m, Storage.Pg.D(line["net"]));
        // 次月累计预扣
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-12" }, Fixture.Ev("考勤表"));
        Assert.Equal(48m, await _f.ScalarD("SELECT iit FROM proj.payroll_lines WHERE period='2026-12'"));
        // 参保待办自动生成
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='SocialInsurance'") > 0);
    }

    [Fact]
    public async Task A10_Bonus_Is_Event_Not_RuleChange()
    {
        await Hire();
        var rulesBefore = await _f.ScalarD("SELECT COUNT(*) FROM proj.rule_versions");
        await _f.RecordResolution("RES-B1", "Bonus", new JsonObject { ["employeeId"] = "e1", ["amount"] = 2000 });
        await _f.Submit("BonusGranted", "op", "RES-B1", new JsonObject
        { ["employeeId"] = "e1", ["amount"] = 2000, ["reason"] = "月度突出贡献", ["period"] = "2026-11" }, Fixture.Ev("奖金决议纪要"));
        Assert.Equal(rulesBefore, await _f.ScalarD("SELECT COUNT(*) FROM proj.rule_versions"));
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("考勤表"));
        Assert.Equal(10000m, await _f.ScalarD("SELECT gross FROM proj.payroll_lines WHERE period='2026-11'"));
        // 奖金并入后工资标准仍是 8000（规则未变）
        Assert.Equal(8000m, await _f.ScalarD("SELECT (params->>'monthly')::numeric FROM proj.rule_versions WHERE rule_id='wage-e1' AND status='Active'"));
    }

    [Fact]
    public async Task A10_Leave_Settlement_SameDay()
    {
        await Hire("e1", "2024-01-01");
        await _f.RecordResolution("RES-LV", "EmployeeLeave", new JsonObject { ["employeeId"] = "e1" });
        // 未本人确认 → 拒绝
        var ex = await _f.SubmitFails("EmployeeLeft", "op", "RES-LV", new JsonObject
        { ["employeeId"] = "e1", ["leaveDate"] = "2026-11-23", ["reason"] = "TerminatedNoNotice", ["remainingLeaveDays"] = 5 }, Fixture.Ev("交接清单"));
        Assert.Equal("SelfConfirm", ex.Kind);

        await _f.RecordResolution("RES-LV2", "EmployeeLeave", new JsonObject { ["employeeId"] = "e1" });
        var e = await _f.Submit("EmployeeLeft", "op", "RES-LV2", new JsonObject
        { ["employeeId"] = "e1", ["leaveDate"] = "2026-11-23", ["reason"] = "TerminatedNoNotice", ["remainingLeaveDays"] = 5, ["selfConfirmed"] = true }, Fixture.Ev("本人实名确认+交接清单"));
        var settlement = (await _f.Rows($"SELECT amount,derivation FROM proj.calc_results WHERE kind='LeaveSettlement' AND source_event_id='{e.EventId}'"))[0];
        // 6133.33(工资) + 3678.16(年假) + 0(提成) + 32000(N+1=4个月，工龄2年10.7月→N=3) = 41811.49
        Assert.Equal(41811.49m, Storage.Pg.D(settlement["amount"]));
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='SettlementReview'") > 0);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='SocialInsurance' AND title LIKE '%减员%'") > 0);
    }

    [Fact]
    public async Task A12_SiWaiver_Rejected()
    {
        await _f.Found();
        await _f.RecordResolution("RES-SW", "ContractSign", new JsonObject { ["contractId"] = "CT-SW" });
        var ex = await _f.SubmitFails("ContractSigned", "op", "RES-SW", new JsonObject
        {
            ["contractId"] = "CT-SW", ["category"] = "人事类", ["contractType"] = "自愿放弃社保协议",
            ["parties"] = new JsonArray("e1"), ["secrecy"] = "Restricted", ["docHash"] = "abc", ["clauses"] = new JsonArray()
        }, Fixture.Ev("协议文本"));
        Assert.Equal("SiWaiverInvalid", ex.Kind);
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.contracts WHERE contract_id='CT-SW'"));
    }

    [Fact]
    public async Task NonCompete_InvalidClauses_Rejected()
    {
        await _f.Found();
        await _f.RecordResolution("RES-NC1", "ContractSign", new JsonObject { ["contractId"] = "CT-NC1" });
        var ex1 = await _f.SubmitFails("ContractSigned", "op", "RES-NC1", new JsonObject
        {
            ["contractId"] = "CT-NC1", ["category"] = "人事约束类", ["contractType"] = "竞业限制协议",
            ["parties"] = new JsonArray("e1"), ["secrecy"] = "Restricted", ["docHash"] = "abc",
            ["clauses"] = new JsonArray(new JsonObject { ["kind"] = "NonCompete", ["months"] = 36, ["monthlyCompensation"] = 2000 })
        }, Fixture.Ev("协议文本"));
        Assert.Equal("InvalidClause", ex1.Kind);

        await _f.RecordResolution("RES-NC2", "ContractSign", new JsonObject { ["contractId"] = "CT-NC2" });
        var ex2 = await _f.SubmitFails("ContractSigned", "op", "RES-NC2", new JsonObject
        {
            ["contractId"] = "CT-NC2", ["category"] = "人事约束类", ["contractType"] = "竞业限制协议",
            ["parties"] = new JsonArray("e1"), ["secrecy"] = "Restricted", ["docHash"] = "abc",
            ["clauses"] = new JsonArray(new JsonObject { ["kind"] = "NonCompete", ["months"] = 12 })
        }, Fixture.Ev("协议文本"));
        Assert.Equal("InvalidClause", ex2.Kind);
    }

    [Fact]
    public async Task Commission_Locked_At_Order_Time()
    {
        await Hire();
        // 提成规则 5%（按回款）
        await _f.RecordResolution("RES-CM", "CommissionChange", new JsonObject
        { ["ruleId"] = "com-e1", ["kind"] = "Commission.Rate", ["subject"] = "e1", ["params"] = new JsonObject { ["rate"] = 0.05, ["basis"] = "Collection" } });
        await _f.Submit("RuleActivated", "op", "RES-CM", new JsonObject
        { ["ruleId"] = "com-e1", ["kind"] = "Commission.Rate", ["subject"] = "e1", ["params"] = new JsonObject { ["rate"] = 0.05, ["basis"] = "Collection" } });
        await _f.RecordResolution("RES-SO", "SalesOrder", new JsonObject { ["orderId"] = "SO-1" });
        await _f.Submit("SalesOrderConfirmed", "op", "RES-SO", new JsonObject
        { ["orderId"] = "SO-1", ["customer"] = "客户Y", ["amount"] = 200000, ["employeeId"] = "e1" }, Fixture.Ev("销售合同"));
        // 年中偷偷下调到 2% → 新版本，但历史订单仍按 5%
        await _f.RecordResolution("RES-CM2", "CommissionChange", new JsonObject
        { ["ruleId"] = "com-e1", ["kind"] = "Commission.Rate", ["subject"] = "e1", ["params"] = new JsonObject { ["rate"] = 0.02, ["basis"] = "Collection" } });
        await _f.Submit("RuleActivated", "op", "RES-CM2", new JsonObject
        { ["ruleId"] = "com-e1", ["kind"] = "Commission.Rate", ["subject"] = "e1", ["params"] = new JsonObject { ["rate"] = 0.02, ["basis"] = "Collection" } });
        await _f.Submit("CollectionReceived", "op", null, new JsonObject { ["orderId"] = "SO-1", ["amount"] = 200000 }, Fixture.Ev("回款回单"));
        Assert.Equal(10000m, await _f.ScalarD("SELECT amount FROM proj.commissions WHERE order_id='SO-1'")); // 200000×5% 而非 2%
    }

    [Fact]
    public async Task 发放闭环_回单确认金额一致与防重复()
    {
        await Hire();
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11", ["auto"] = true }, Fixture.Ev("考勤表"));
        Assert.Equal("Calculated", (await _f.Rows("SELECT status FROM proj.payroll_runs WHERE period='2026-11'"))[0]["status"]);
        Assert.Equal(true, (await _f.Rows("SELECT auto FROM proj.payroll_runs WHERE period='2026-11'"))[0]["auto"]);
        var net = await _f.ScalarD("SELECT net FROM proj.payroll_lines WHERE period='2026-11'");
        // 金额与实发合计不一致 → 拒绝
        var ex = await _f.SubmitFails("PayrollPaid", "op", null, new JsonObject { ["period"] = "2026-11", ["totalAmount"] = net + 1 }, Fixture.Ev("回单"));
        Assert.Equal("AmountMismatch", ex.Kind);
        // 未算薪期间 → 拒绝
        var ex0 = await _f.SubmitFails("PayrollPaid", "op", null, new JsonObject { ["period"] = "2026-10", ["totalAmount"] = 0 }, Fixture.Ev("回单"));
        Assert.Equal("NoRun", ex0.Kind);
        // 一致 → 闭环
        await _f.Submit("PayrollPaid", "op", null, new JsonObject { ["period"] = "2026-11", ["totalAmount"] = net }, Fixture.Ev("银行代发回单"));
        Assert.Equal("Paid", (await _f.Rows("SELECT status FROM proj.payroll_runs WHERE period='2026-11'"))[0]["status"]);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.notifications WHERE summary->>'kind'='PayslipPaid'") > 0);
        // 重复确认 → 拒绝
        var ex2 = await _f.SubmitFails("PayrollPaid", "op", null, new JsonObject { ["period"] = "2026-11", ["totalAmount"] = net }, Fixture.Ev("回单"));
        Assert.Equal("AlreadyPaid", ex2.Kind);
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " " + string.Join(";", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }
}
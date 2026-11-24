// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A11/A13/A14：企业类型规则包、模块开关先决条件、在线投资合规闭环。</summary>
public class ModuleInvestmentTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task A13_M14_Requires_JointStock_Company()
    {
        await _f.Found(); // 有限责任公司
        await _f.RecordResolution("RES-MS1", "ModuleSwitch", new JsonObject { ["moduleId"] = "M14", ["targetState"] = "Enabled" });
        var ex = await _f.SubmitFails("ModuleSwitchChanged", "op", "RES-MS1", new JsonObject { ["moduleId"] = "M14", ["targetState"] = "Enabled" });
        Assert.Equal("PrereqUnmet", ex.Kind);
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.module_states WHERE module_id='M14' AND state='Disabled'"));
    }

    [Fact]
    public async Task A13_Mandatory_Module_Cannot_Close()
    {
        await _f.Found();
        await _f.RecordResolution("RES-MS2", "ModuleSwitch", new JsonObject { ["moduleId"] = "M1", ["targetState"] = "Disabled" });
        var ex = await _f.SubmitFails("ModuleSwitchChanged", "op", "RES-MS2", new JsonObject { ["moduleId"] = "M1", ["targetState"] = "Disabled" });
        Assert.Equal("MandatoryModule", ex.Kind);
    }

    [Fact]
    public async Task A13_Switch_Needs_Resolution()
    {
        await _f.Found();
        var ex = await _f.SubmitFails("ModuleSwitchChanged", "op", null, new JsonObject { ["moduleId"] = "M16", ["targetState"] = "Enabled" });
        Assert.Equal("NoResolution", ex.Kind);
    }

    [Fact]
    public async Task A13_Disable_Keeps_History_And_Notifies_Downstream()
    {
        await _f.Found();
        await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 2000, ["category"] = "采购", ["payee"] = "供应商A" });
        var ledgerBefore = await _f.ScalarD("SELECT COUNT(*) FROM proj.ledger");
        await _f.RecordResolution("RES-MS3", "ModuleSwitch", new JsonObject { ["moduleId"] = "M5", ["targetState"] = "Suspended" });
        await _f.Submit("ModuleSwitchChanged", "op", "RES-MS3", new JsonObject { ["moduleId"] = "M5", ["targetState"] = "Suspended" });
        Assert.Equal(ledgerBefore, await _f.ScalarD("SELECT COUNT(*) FROM proj.ledger")); // 关闭不删数据
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.notifications WHERE summary::text LIKE '%UpstreamDisabled%'") > 0);
    }

    [Fact]
    public async Task A11_Partnership_PayEvenIf_Retained()
    {
        await _f.Found("合伙企业", 1_000_000m, new[]
        {
            new JsonObject { ["kind"] = "Partnership.Allocation", ["params"] = new JsonObject
            { ["partners"] = new JsonArray(
                new JsonObject { ["partyId"] = "alice", ["ratio"] = 0.6, ["isNatural"] = true },
                new JsonObject { ["partyId"] = "bob", ["ratio"] = 0.4, ["isNatural"] = true }) } }
        });
        // 收入 1,010,000 含税 → 净收入 1,000,000（VAT 10000）
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 1_010_000m, ["customer"] = "客户Z" }, Fixture.Ev("合同+发票"));
        Assert.Equal(144_500m, await _f.ScalarD("SELECT MAX(amount) FROM proj.calc_results WHERE kind='PartnerIIT' AND subject='alice'"));
        Assert.Equal(79_500m, await _f.ScalarD("SELECT MAX(amount) FROM proj.calc_results WHERE kind='PartnerIIT' AND subject='bob'"));
        // 留存未分配 → 现金流预警；合伙企业不产生企业所得税候选分红
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='PartnerCashAlert'") > 0);
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.dividend_plans"));
    }

    [Fact]
    public async Task A11_IndividualBusiness_No_CorporateTax()
    {
        await _f.Found("个体工商户");
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 1_010_000m, ["customer"] = "客户Z" }, Fixture.Ev("合同+发票"));
        // 经营所得 100 万减半：284500/2 = 142250
        Assert.Equal(142_250m, await _f.ScalarD("SELECT MAX(amount) FROM proj.calc_results WHERE kind='IncomeTax' AND subject='个体工商户'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.dividend_plans"));
    }

    [Fact]
    public async Task A11_Partnership_AllProfitToSubset_Rejected()
    {
        await _f.Found("合伙企业");
        await _f.RecordResolution("RES-PA", "CharterAmend", new JsonObject
        {
            ["ruleId"] = "pa", ["kind"] = "Partnership.Allocation",
            ["params"] = new JsonObject { ["partners"] = new JsonArray(
                new JsonObject { ["partyId"] = "alice", ["ratio"] = 1.0 }, new JsonObject { ["partyId"] = "bob", ["ratio"] = 0.0 }) }
        });
        var ex = await _f.SubmitFails("RuleActivated", "op", "RES-PA", new JsonObject
        {
            ["ruleId"] = "pa", ["kind"] = "Partnership.Allocation",
            ["params"] = new JsonObject { ["partners"] = new JsonArray(
                new JsonObject { ["partyId"] = "alice", ["ratio"] = 1.0 }, new JsonObject { ["partyId"] = "bob", ["ratio"] = 0.0 }) }
        });
        Assert.Equal("InvalidClause", ex.Kind);
    }

    private async Task EnableM15()
    {
        await _f.RecordResolution("RES-FIN", "Financing", new JsonObject { ["note"] = "A轮融资决议" });
        await _f.RecordResolution("RES-CC", "ComplianceCheck", new JsonObject { ["note"] = "合规检查通过" });
        await _f.RecordResolution("RES-MS15", "ModuleSwitch", new JsonObject { ["moduleId"] = "M15", ["targetState"] = "Enabled" });
        await _f.Submit("ModuleSwitchChanged", "op", "RES-MS15", new JsonObject { ["moduleId"] = "M15", ["targetState"] = "Enabled" });
    }

    [Fact]
    public async Task A14_Investment_Full_ClosedLoop()
    {
        await _f.Found();
        await EnableM15();
        await _f.Submit("InvestorQualified", "op", null, new JsonObject
        { ["investorId"] = "inv1", ["name"] = "投资人甲", ["proofHash"] = "proof-hash" }, Fixture.Ev("实名+风险测评+资产证明"));
        await _f.SignContract("CT-INV", "融资类", "在线投资入股合同", new JsonArray("inv1"), "Private",
            new JsonArray(new JsonObject { ["kind"] = "Investment", ["amount"] = 500000, ["subscribed"] = 100000 }));
        await _f.RecordResolution("RES-INV", "Financing", new JsonObject { ["investorId"] = "inv1", ["amount"] = 500000, ["subscribed"] = 100000, ["contractId"] = "CT-INV" });
        var e = await _f.Submit("InvestmentReceived", "op", "RES-INV", new JsonObject
        { ["investorId"] = "inv1", ["amount"] = 500000, ["subscribed"] = 100000, ["contractId"] = "CT-INV", ["contractAmount"] = 500000 }, Fixture.Ev("监管账户到账回单"));
        // Cap Table 更新 + 印花税 + 工商待办
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.cap_positions WHERE party_id='inv1' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(62.5m, await _f.ScalarD($"SELECT amount FROM proj.calc_results WHERE kind='BooksStamp' AND source_event_id='{e.EventId}'"));
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='DilutionConfirm'") > 0);
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok);
    }

    [Fact]
    public async Task A14_NoContract_NoConfirmation()
    {
        await _f.Found();
        await EnableM15();
        await _f.Submit("InvestorQualified", "op", null, new JsonObject { ["investorId"] = "inv1", ["name"] = "投资人甲", ["proofHash"] = "p" }, Fixture.Ev("认定材料"));
        await _f.RecordResolution("RES-INV2", "Financing", new JsonObject { ["investorId"] = "inv1", ["amount"] = 500000 });
        var ex = await _f.SubmitFails("InvestmentReceived", "op", "RES-INV2", new JsonObject
        { ["investorId"] = "inv1", ["amount"] = 500000, ["subscribed"] = 100000, ["contractAmount"] = 500000 }, Fixture.Ev("到账回单"));
        Assert.Equal("NoContract", ex.Kind);
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.cap_positions WHERE party_id='inv1'"));
    }

    [Fact]
    public async Task A14_GuaranteedReturn_Blocked()
    {
        await _f.Found();
        await _f.RecordResolution("RES-GR", "ContractSign", new JsonObject { ["contractId"] = "CT-GR" });
        var ex = await _f.SubmitFails("ContractSigned", "op", "RES-GR", new JsonObject
        {
            ["contractId"] = "CT-GR", ["category"] = "融资类", ["contractType"] = "在线投资入股合同",
            ["parties"] = new JsonArray("inv1"), ["secrecy"] = "Private", ["docHash"] = "x",
            ["clauses"] = new JsonArray(new JsonObject { ["kind"] = "Return", ["text"] = "承诺保本保收益，年化固定回报12%" })
        }, Fixture.Ev("合同文本"));
        Assert.Equal("IllegalFundraising", ex.Kind);
    }

    [Fact]
    public async Task A14_ModuleDisabled_Blocks_Investment()
    {
        await _f.Found();
        var ex = await _f.SubmitFails("InvestmentReceived", "op", "RES-X", new JsonObject
        { ["investorId"] = "inv1", ["amount"] = 100, ["subscribed"] = 100, ["contractAmount"] = 100 }, Fixture.Ev("回单"));
        // 无决议先被拦（治理层在业务层上游）
        Assert.Equal("NoResolution", ex.Kind);
    }

    [Fact]
    public async Task Guarantee_Without_Resolution_Blocked()
    {
        await _f.Found();
        await _f.RecordResolution("RES-G1", "ContractSign", new JsonObject { ["contractId"] = "CT-G1" });
        var ex = await _f.SubmitFails("ContractSigned", "op", "RES-G1", new JsonObject
        {
            ["contractId"] = "CT-G1", ["category"] = "融资类", ["contractType"] = "担保合同",
            ["parties"] = new JsonArray("bank"), ["secrecy"] = "Internal", ["docHash"] = "x", ["clauses"] = new JsonArray()
        }, Fixture.Ev("担保合同文本"));
        Assert.Equal("GuaranteeBlocked", ex.Kind);
    }
}
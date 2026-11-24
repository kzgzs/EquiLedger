// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A4/A5/A6/A9：治理守卫验收。</summary>
public class GovernanceTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task EnsureFound()
    {
        if (await _f.ScalarD("SELECT COUNT(*) FROM proj.company") == 0) await _f.Found();
    }

    [Fact]
    public async Task A9_NonOperator_Write_Rejected_And_Alerted()
    {
        await EnsureFound();
        var ex = await _f.SubmitFails("ExpenseRecorded", "alice", null, new JsonObject { ["amount"] = 100, ["category"] = "办公", ["payee"] = "供应商A" });
        Assert.Equal("NotOperator", ex.Kind);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='SecurityAlert'") > 0);
    }

    [Fact]
    public async Task A9_NoResolution_Rejected()
    {
        await EnsureFound();
        var ex = await _f.SubmitFails("RuleActivated", "op", null, new JsonObject
        { ["ruleId"] = "wage-e1", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = new JsonObject { ["monthly"] = 8000 } });
        Assert.Equal("NoResolution", ex.Kind);
    }

    [Fact]
    public async Task A9_A10_ResolutionConsistency_8000_vs_9000()
    {
        await EnsureFound();
        await _f.RecordResolution("RES-W1", "PayrollChange", new JsonObject
        { ["ruleId"] = "wage-e1", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = new JsonObject { ["monthly"] = 8000 } });
        var ex = await _f.SubmitFails("RuleActivated", "op", "RES-W1", new JsonObject
        { ["ruleId"] = "wage-e1", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = new JsonObject { ["monthly"] = 9000 } });
        Assert.Equal("ResolutionMismatch", ex.Kind);
        // 决议未被消费，按决议金额录入成功
        await _f.Submit("RuleActivated", "op", "RES-W1", new JsonObject
        { ["ruleId"] = "wage-e1", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = new JsonObject { ["monthly"] = 8000 } });
        Assert.Equal(8000m, await _f.ScalarD("SELECT (params->>'monthly')::numeric FROM proj.rule_versions WHERE rule_id='wage-e1' AND status='Active'"));
    }

    [Fact]
    public async Task A5_NoEvidence_Rejected()
    {
        await EnsureFound();
        var ex = await _f.SubmitFails("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 100000 }, new JsonArray());
        Assert.Equal("NoEvidence", ex.Kind);
    }

    [Fact]
    public async Task A4_Spouse_Avoidance_In_Voting()
    {
        await EnsureFound();
        await _f.Submit("RelationDeclared", "op", null, new JsonObject { ["fromParty"] = "alice", ["toParty"] = "bob", ["relation"] = "Spouse" });
        await _f.Submit("ResolutionProposed", "alice", null, new JsonObject
        { ["resolutionNo"] = "R-AV1", ["title"] = "甲的薪酬议案", ["kind"] = "PayrollChange", ["params"] = new JsonObject { ["subjectParty"] = "alice" } });
        await _f.Submit("ResolutionVoted", "bob", null, new JsonObject { ["resolutionNo"] = "R-AV1", ["partyId"] = "bob", ["approve"] = true }, new JsonArray());
        var votes = (JsonArray)System.Text.Json.Nodes.JsonNode.Parse((await _f.Rows("SELECT votes FROM proj.resolutions WHERE resolution_no='R-AV1'"))[0]["votes"]!.ToString()!)!;
        Assert.True(votes.Any(v => v!["avoided"]?.GetValue<bool>() == true && v["weight"]!.GetValue<decimal>() == 0m));
    }

    [Fact]
    public async Task A6_Admin_No_Business_Write()
    {
        await EnsureFound();
        var ex = await _f.SubmitFails("ExpenseRecorded", "adm", null, new JsonObject { ["amount"] = 100, ["category"] = "x", ["payee"] = "y" });
        Assert.Equal("NotOperator", ex.Kind);
        // 管理员只能管账号可用性
        await _f.Submit("AccountStatusChanged", "adm", null, new JsonObject { ["userId"] = "bob", ["enabled"] = false });
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.accounts WHERE user_id='bob' AND enabled=TRUE"));
    }

    [Fact]
    public async Task RoutineAuth_Limits()
    {
        await EnsureFound();
        // 额度内凭凭证直录
        await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 3000, ["category"] = "办公", ["payee"] = "供应商A", ["payeeAccount"] = "6222000011112222" });
        // 超单笔限额 → 回到开会通道
        var ex1 = await _f.SubmitFails("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 6000, ["category"] = "办公", ["payee"] = "供应商A" });
        Assert.Equal("NoResolution", ex1.Kind);
        // 月度总额 20000：再录 5 笔 3000（累计 18000），第 7 笔超限
        for (var i = 0; i < 5; i++)
            await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 3000, ["category"] = "办公", ["payee"] = "供应商A" });
        var ex2 = await _f.SubmitFails("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 3000, ["category"] = "办公", ["payee"] = "供应商A" });
        Assert.Equal("NoResolution", ex2.Kind);
    }

    [Fact]
    public async Task Whititelist_Blocks_Unknown_Account()
    {
        await EnsureFound();
        var ex = await _f.SubmitFails("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 1000, ["category"] = "办公", ["payee"] = "不明户", ["payeeAccount"] = "9999888877776666" });
        Assert.Equal("Whitelist", ex.Kind);
    }

    [Fact]
    public async Task PeriodLock_Blocks_Backdated_Entry()
    {
        await EnsureFound();
        await _f.RecordResolution("RES-PC1", "PeriodClose", new JsonObject { ["period"] = "2026-10" });
        await _f.Submit("PeriodClosed", "op", "RES-PC1", new JsonObject { ["period"] = "2026-10" });
        var ex = await _f.SubmitFails("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 1000, ["category"] = "办公", ["payee"] = "供应商A", ["period"] = "2026-10" });
        Assert.Equal("PeriodLocked", ex.Kind);
    }

    [Fact]
    public async Task Deceased_Shareholder_Frozen()
    {
        await EnsureFound();
        await _f.Submit("ShareholderDeceased", "op", null, new JsonObject { ["partyId"] = "bob" }, Fixture.Ev("死亡证明"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.accounts WHERE user_id='bob' AND frozen=TRUE"));
        var ex = await _f.SubmitFails("ResolutionVoted", "bob", null, new JsonObject { ["resolutionNo"] = "X", ["partyId"] = "bob", ["approve"] = true }, new JsonArray());
        Assert.Equal("AccountDisabled", ex.Kind);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='Inheritance'") > 0);
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.cap_positions WHERE party_id='bob' AND status='Deceased' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
    }

    [Fact]
    public async Task InvalidClause_ExemptLoss_Rejected()
    {
        await EnsureFound();
        await _f.RecordResolution("RES-IV1", "CharterAmend", new JsonObject { ["ruleId"] = "bad", ["kind"] = "Charter.ExemptLoss", ["params"] = new JsonObject() });
        var ex = await _f.SubmitFails("RuleActivated", "op", "RES-IV1", new JsonObject { ["ruleId"] = "bad", ["kind"] = "Charter.ExemptLoss", ["params"] = new JsonObject() });
        Assert.Equal("InvalidClause", ex.Kind);
    }

    [Fact]
    public async Task CompanyRename_ResolutionDriven_KeepsFoundingEvent()
    {
        await EnsureFound();
        await _f.RecordResolution("RES-CN1", "CompanyChange", new JsonObject { ["name"] = "新名字公司" });
        await _f.Submit("CompanyInfoChanged", "op", "RES-CN1", new JsonObject { ["name"] = "新名字公司" });
        Assert.Equal(1m, await _f.ScalarD("SELECT CASE WHEN name='新名字公司' THEN 1 ELSE 0 END FROM proj.company WHERE id=1"));
        await _f.RecordResolution("RES-CN2", "CompanyChange", new JsonObject { ["name"] = "另一个名字" });
        var ex = await _f.SubmitFails("CompanyInfoChanged", "op", "RES-CN2", new JsonObject { ["name"] = "乱改" });
        Assert.Equal("ResolutionMismatch", ex.Kind);
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM events.event_log WHERE event_type='CompanyFounded'"));
    }
}
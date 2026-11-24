// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A1/A3：股权全生命周期 + 分红闭环 + 全量复算。</summary>
public class EquityDividendTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task Setup(decimal revenue = 1_000_000m)
    {
        await _f.Found();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 600000 }, Fixture.Ev("银行回单"));
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "bob", ["amount"] = 400000 }, Fixture.Ev("银行回单"));
        if (revenue > 0)
            await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = revenue, ["customer"] = "客户X" }, Fixture.Ev("销售合同+发票"));
    }

    [Fact]
    public async Task A1_Exit_Propagates_Everything()
    {
        await Setup();
        await _f.RecordResolution("RES-EXIT", "ShareExit", new JsonObject { ["partyId"] = "bob", ["capitalAmount"] = 100000 });
        var e = await _f.Submit("ShareExit", "op", "RES-EXIT", new JsonObject { ["partyId"] = "bob", ["capitalAmount"] = 100000 }, Fixture.Ev("退股申请+本人签署"));

        // 其余股东比例被动上升
        Assert.Equal(0.666667m, await _f.ScalarD("SELECT ratio FROM proj.cap_positions WHERE party_id='alice' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(0.333333m, await _f.ScalarD("SELECT ratio FROM proj.cap_positions WHERE party_id='bob' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        // 估值单：净资产 1,990,099.01 × 10% = 199,009.90
        Assert.Equal(199009.90m, await _f.ScalarD($"SELECT amount FROM proj.calc_results WHERE kind='ExitValuation' AND source_event_id='{e.EventId}'"));
        // 税务待办 / 工商待办 / 分期支付计划
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='TaxFiling'") >= 2);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='BusinessRegistry'") > 0);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='ExitPayment'") > 0);
        // 分红候选稿按新实缴结构重算：alice 600000/900000 = 2/3
        var plan = (await _f.Rows("SELECT allocations FROM proj.dividend_plans WHERE status='Candidate' ORDER BY plan_id DESC LIMIT 1"))[0];
        var allocs = (JsonArray)Pg.J(plan["allocations"]);
        var alice = allocs.First(a => a!["partyId"]!.GetValue<string>() == "alice")!;
        Assert.Equal(0.666667m, alice["ratio"]!.GetValue<decimal>());
        // 影响清单完整
        var impacts = (JsonArray)Pg.J((await _f.Rows($"SELECT impacts FROM proj.impact_lists WHERE source_event_id='{e.EventId}'"))[0]["impacts"]);
        Assert.Contains(impacts, x => x!.GetValue<string>().Contains("表决权"));
        Assert.Contains(impacts, x => x!.GetValue<string>().Contains("股权结构表"));
        // A3：全量复算零误差
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " | " + string.Join(",", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }

    [Fact]
    public async Task Dividend_Full_ClosedLoop()
    {
        await Setup();
        // 候选稿：可分配 846,534.65（11.4 口径推导）
        var planRow = (await _f.Rows("SELECT plan_id,distributable FROM proj.dividend_plans WHERE status='Candidate'"))[0];
        Assert.Equal(846534.65m, Pg.D(planRow["distributable"]));
        var planId = Pg.S(planRow["plan_id"]);

        await _f.RecordResolution("RES-DIV", "Dividend", new JsonObject { ["planId"] = planId });
        await _f.Submit("DividendApproved", "op", "RES-DIV", new JsonObject { ["planId"] = planId }, Fixture.Ev("股东会决议"));
        // 个税代扣 20%：合计 169,306.93
        Assert.Equal(169306.93m, await _f.ScalarD("SELECT COALESCE(SUM(amount),0) FROM proj.calc_results WHERE kind='DividendIIT'"));

        // 未申报不得标记分红完成
        var ex = await _f.SubmitFails("DividendPaid", "op", null, new JsonObject { ["planId"] = planId }, Fixture.Ev("付款回单"));
        Assert.Equal("TaxNotFiled", ex.Kind);
        await _f.Submit("TodoCompleted", "op", null, new JsonObject { ["todoId"] = $"TD-TaxFiling-{planId}" }, Fixture.Ev("个税申报回执"));
        await _f.Submit("DividendPaid", "op", null, new JsonObject { ["planId"] = planId }, Fixture.Ev("付款回单"));
        Assert.Equal(1m, await _f.ScalarD($"SELECT COUNT(*) FROM proj.dividend_plans WHERE plan_id='{planId}' AND status='Paid'"));

        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " | " + string.Join(",", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }

    [Fact]
    public async Task Transfer_Requires_Preemption_Waiver_And_Contract()
    {
        await Setup(0);
        // 无合同 → 拒绝
        await _f.RecordResolution("RES-T1", "ShareTransfer", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000, ["preemptionWaived"] = true });
        var ex1 = await _f.SubmitFails("ShareTransfer", "op", "RES-T1", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000 });
        Assert.Equal("NoContract", ex1.Kind);

        await _f.SignContract("CT-T1", "股权类", "股权转让协议", new JsonArray("bob", "carol"));
        // 未走优先购买权流程 → 拒绝
        await _f.RecordResolution("RES-T2", "ShareTransfer", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000, ["contractId"] = "CT-T1" });
        var ex2 = await _f.SubmitFails("ShareTransfer", "op", "RES-T2", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000, ["contractId"] = "CT-T1" });
        Assert.Equal("Preemption", ex2.Kind);

        await _f.RecordResolution("RES-T3", "ShareTransfer", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000, ["contractId"] = "CT-T1", ["preemptionWaived"] = true });
        await _f.Submit("ShareTransfer", "op", "RES-T3", new JsonObject { ["fromParty"] = "bob", ["toParty"] = "carol", ["capitalAmount"] = 100000, ["price"] = 150000, ["contractId"] = "CT-T1" });
        // 溢价 5 万 → 个税 1 万；印花税 150000×0.05%×50% = 37.5
        Assert.Equal(10000m, await _f.ScalarD("SELECT amount FROM proj.calc_results WHERE kind='TransferIIT'"));
        Assert.Equal(37.5m, await _f.ScalarD("SELECT amount FROM proj.calc_results WHERE kind='StampDuty'"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.cap_positions WHERE party_id='carol' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
    }

    [Fact]
    public async Task PaidIn_Over_Subscription_Rejected()
    {
        await _f.Found();
        var ex = await _f.SubmitFails("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 700000 }, Fixture.Ev("银行回单"));
        Assert.Equal("AmountMismatch", ex.Kind);
    }

    [Fact]
    public async Task Correction_Reverses_Not_Deletes()
    {
        await _f.Found();
        var pay = await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 600000 }, Fixture.Ev("银行回单"));
        await _f.RecordResolution("RES-COR", "Correction", new JsonObject { ["targetEventId"] = pay.EventId.ToString() });
        await _f.Submit("CorrectionRecorded", "op", "RES-COR", new JsonObject { ["targetEventId"] = pay.EventId.ToString(), ["reason"] = "金额录入错误" }, Fixture.Ev("更正依据+双人复核"));
        // 原分录永久保留，冲销分录存在
        Assert.Equal(2m, await _f.ScalarD("SELECT COUNT(*) FROM proj.ledger WHERE account='Bank'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='Bank'"));
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.ledger WHERE reversed_by IS NOT NULL") > 0);
    }

    [Fact]
    public async Task BankFlow_Unmatched_Goes_Exception()
    {
        await _f.Found();
        await _f.Submit("BankFlowImported", "op", null, new JsonObject
        {
            ["flows"] = new JsonArray(new JsonObject { ["flowId"] = "F1", ["direction"] = "In", ["counterparty"] = "陌生账户", ["amount"] = 50000, ["flowDate"] = "2026-11-20" })
        }, Fixture.Ev("银行流水单"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.bank_flows WHERE status='UnmatchedException'"));
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='BankException'") > 0);
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>个体户业主工资调增 / 员工档案 / 法务库。</summary>
public class MiscTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task FoundIndividual()
    {
        await _f.Submit("CompanyFounded", "founder", null, new JsonObject
        {
            ["company"] = new JsonObject { ["name"] = "测试经营部", ["type"] = "个体工商户", ["registeredCapital"] = 100000, ["industryTags"] = new JsonArray("零售") },
            ["accounts"] = new JsonArray(
                new JsonObject { ["username"] = "op", ["displayName"] = "经营者", ["role"] = "Operator", ["partyId"] = "boss", ["password"] = "Op#12345", ["subscribed"] = 100000, ["realName"] = "经营者甲", ["idNumber"] = "110101198001011234" }),
            ["charterRules"] = new JsonArray(),
            ["whitelist"] = new JsonArray()
        }, Fixture.Ev("设立材料"));
    }

    [Fact]
    public async Task IndividualBusiness_OwnerWage_TaxAddBack()
    {
        await FoundIndividual();
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='OwnerSocialInsurance'"));
        await _f.RecordResolution("RES-H1", "Hire", new JsonObject { ["employeeId"] = "e1" });
        await _f.Submit("EmployeeHired", "op", "RES-H1", new JsonObject
        {
            ["employeeId"] = "e1", ["partyId"] = "boss", ["name"] = "老板本人", ["hireDate"] = "2026-11-01", ["contractId"] = "CT-X"
        }, Fixture.Ev("入职登记"));
        var wageParams = new JsonObject { ["monthly"] = 8000 };
        await _f.RecordResolution("RES-W1", "PayrollChange", new JsonObject { ["ruleId"] = "wage-boss", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = wageParams });
        await _f.Submit("RuleActivated", "op", "RES-W1", new JsonObject { ["ruleId"] = "wage-boss", ["kind"] = "Payroll.Standard", ["subject"] = "e1", ["params"] = wageParams.DeepClone() });
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" });
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 100000, ["customer"] = "客户" }, Fixture.Ev("发票"));
        // 业主工资 8000+ 不得税前扣除：推导单必须含纳税调增行
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.calc_results WHERE kind='IncomeTax' AND derivation::text LIKE '%业主工资纳税调增%'") >= 1);
        // 工资条通知已生成
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.notifications WHERE channel='Employee' AND summary->>'kind'='Payslip'") >= 1);
    }

    [Fact]
    public async Task EmployeeProfile_Stored_And_Queryable()
    {
        await _f.Found();
        await _f.RecordResolution("RES-H2", "Hire", new JsonObject { ["employeeId"] = "e2" });
        await _f.Submit("EmployeeHired", "op", "RES-H2", new JsonObject
        {
            ["employeeId"] = "e2", ["partyId"] = "e2", ["name"] = "李四", ["position"] = "销售", ["hireDate"] = "2026-11-01", ["contractId"] = "CT-Y",
            ["gender"] = "女", ["age"] = 26, ["department"] = "销售部", ["skills"] = "大客户销售", ["salaryType"] = "BasePlusCommission", ["baseSalary"] = 5000, ["siStatus"] = "办理中"
        }, Fixture.Ev("入职登记表"));
        var rows = await _f.Rows("SELECT profile->>'gender' AS g, profile->>'department' AS d, profile->>'salaryType' AS s FROM proj.employees WHERE employee_id='e2'");
        Assert.Equal("女", rows[0]["g"]);
        Assert.Equal("销售部", rows[0]["d"]);
        Assert.Equal("BasePlusCommission", rows[0]["s"]);
    }

    [Fact]
    public async Task AccountAdded_ResolutionDriven_RealName_Required()
    {
        await _f.Found();
        await _f.RecordResolution("RES-A1", "AccountManage", new JsonObject { ["username"] = "carol" });
        await _f.Submit("AccountAdded", "op", "RES-A1", new JsonObject
        {
            ["username"] = "carol", ["displayName"] = "股东丙", ["role"] = "Shareholder", ["partyId"] = "carol",
            ["password"] = "C#12345", ["realName"] = "卡罗尔", ["idNumber"] = "110101199201016789"
        }, Fixture.Ev("股东会决议+身份证明"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.accounts WHERE username='carol' AND real_name='卡罗尔'"));
        // 缺身份证 → 拒绝
        await _f.RecordResolution("RES-A2", "AccountManage", new JsonObject { ["username"] = "dave" });
        var ex = await _f.SubmitFails("AccountAdded", "op", "RES-A2", new JsonObject
        {
            ["username"] = "dave", ["role"] = "Supervisor", ["password"] = "D#12345", ["realName"] = "戴夫"
        }, Fixture.Ev("材料"));
        Assert.Equal("InvalidIdentity", ex.Kind);
    }

    [Fact]
    public async Task Founding_Requires_RealName_And_IdNumber()
    {
        var ex = await _f.SubmitFails("CompanyFounded", "founder", null, new JsonObject
        {
            ["company"] = new JsonObject { ["name"] = "无证公司", ["type"] = "有限责任公司", ["registeredCapital"] = 100000, ["industryTags"] = new JsonArray() },
            ["accounts"] = new JsonArray(new JsonObject { ["username"] = "op", ["displayName"] = "op", ["role"] = "Operator", ["password"] = "Op#12345" }),
            ["charterRules"] = new JsonArray(), ["whitelist"] = new JsonArray()
        }, Fixture.Ev("材料"));
        Assert.Equal("InvalidIdentity", ex.Kind);
    }

    [Fact]
    public async Task Reset_WithinWindow_RebuildsCleanSlate()
    {
        await _f.Found();
        var foundedAt = await EquiLedger.Storage.Maintenance.FoundedAt(_f.Db);
        Assert.True(EquiLedger.Storage.Maintenance.WithinGraceWindow(foundedAt));
        await EquiLedger.Storage.Maintenance.ResetAll(_f.Db, Path.Combine(Path.GetTempPath(), "equi-reset-test-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM proj.company"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COUNT(*) FROM events.event_log"));
        await _f.Found(); // 清空后可重新设立
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.company"));
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok);
    }

    [Fact]
    public async Task LegalLibrary_Seeded_EventAdded_And_Guarded()
    {
        await _f.Found();
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.legal_library WHERE origin='System'") >= 10);
        await _f.Submit("LegalEntryAdded", "op", null, new JsonObject
        {
            ["title"] = "停车场收费纠纷应对", ["keywords"] = "停车场 收费 纠纷", ["scenario"] = "客户拒付停车费",
            ["options"] = new JsonArray(new JsonObject { ["label"] = "协商", ["action"] = "出示收费公示与记录", ["basis"] = "价格法第13条" }),
            ["clauseTemplate"] = "停车服务按公示标准计费。"
        }, Fixture.Ev("法务整理依据"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.legal_library WHERE origin='Event' AND title='停车场收费纠纷应对'"));
        var ex = await _f.SubmitFails("LegalEntryAdded", "op", null, new JsonObject { ["title"] = "无依据条目", ["keywords"] = "x", ["scenario"] = "x" }, new JsonArray());
        Assert.Equal("NoEvidence", ex.Kind);
        // 全量验真：法务库（含系统预置+事件条目）参与重建比对
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " " + string.Join(";", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }

    [Fact]
    public async Task 增资扩股_目标比例换算与溢价入资本公积()
    {
        await _f.Found();
        await _f.SignContract("CT-ZZ1", "股权类", "增资协议", new JsonArray("carol"));
        await _f.RecordResolution("RES-ZZ1", "CapitalIncrease", new JsonObject { ["partyId"] = "carol", ["targetRatio"] = 0.2 });
        await _f.Submit("ShareSubscription", "op", "RES-ZZ1", new JsonObject
        {
            ["partyId"] = "carol", ["partyName"] = "股东丙", ["targetRatio"] = 0.2, ["contractId"] = "CT-ZZ1"
        }, Fixture.Ev("增资协议"));
        // 原注册资本 100 万，目标 20% → 新增认缴 25 万；老股东 alice 60/125=48%
        Assert.Equal(250000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='carol' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(0.48m, await _f.ScalarD("SELECT ratio FROM proj.cap_positions WHERE party_id='alice' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        // 实缴 25 万 + 溢价 35 万：银行借 60 万，实收资本贷 25 万，资本公积贷 35 万
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "carol", ["amount"] = 250000, ["premium"] = 350000 }, Fixture.Ev("银行回单"));
        Assert.Equal(350000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='CapitalReserve'"));
        Assert.Equal(600000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='Bank' AND memo LIKE '%carol%'"));
        Assert.Equal(250000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='PaidInCapital' AND memo LIKE '%carol%'"));
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " " + string.Join(";", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }

    [Fact]
    public async Task 对外投资_成本法权益法与免税股息()
    {
        await _f.Found();
        await _f.RecordResolution("RES-INV1", "ExternalInvestment", new JsonObject { ["target"] = "子公司A", ["amount"] = 200000, ["method"] = "cost" });
        await _f.Submit("InvestmentMade", "op", "RES-INV1", new JsonObject { ["target"] = "子公司A", ["amount"] = 200000, ["method"] = "cost", ["shareRatio"] = 0.6 }, Fixture.Ev("投资协议+付款回单"));
        Assert.Equal(200000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='LongTermEquityInvestment'"));
        await _f.Submit("InvestmentReturnReceived", "op", null, new JsonObject { ["target"] = "子公司A", ["amount"] = 30000 }, Fixture.Ev("分红回单"));
        Assert.Equal(30000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='InvestmentIncome'"));
        // 权益法：联营企业，分红冲减账面
        await _f.RecordResolution("RES-INV2", "ExternalInvestment", new JsonObject { ["target"] = "联营B", ["amount"] = 100000, ["method"] = "equity" });
        await _f.Submit("InvestmentMade", "op", "RES-INV2", new JsonObject { ["target"] = "联营B", ["amount"] = 100000, ["method"] = "equity", ["shareRatio"] = 0.3 }, Fixture.Ev("投资协议"));
        await _f.Submit("InvestmentReturnReceived", "op", null, new JsonObject { ["target"] = "联营B", ["amount"] = 10000 }, Fixture.Ev("分红回单"));
        Assert.Equal(90000m, await _f.ScalarD("SELECT carrying FROM proj.external_investments WHERE target='联营B'"));
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " " + string.Join(";", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }

    [Fact]
    public async Task WooCommerce订单完成_自动入账与幂等去重()
    {
        await _f.Found();
        var body = "{\"id\":1001,\"status\":\"completed\",\"total\":\"599.00\",\"billing\":{\"first_name\":\"张\",\"last_name\":\"三\"},\"line_items\":[{\"name\":\"产品A\",\"quantity\":2,\"total\":\"599.00\"}]}";
        var secret = "test-woo-secret";
        var (ok, msg) = await EquiLedger.Engine.WooCommerce.Ingest(_f.Db, _f.Store, body, EquiLedger.Engine.WooCommerce.Sign(body, secret), secret);
        Assert.True(ok, msg);
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM events.event_log WHERE payload->>'externalId'='woo-1001'"));
        Assert.Equal(599m, await _f.ScalarD("SELECT (payload->>'amount')::numeric FROM events.event_log WHERE payload->>'externalId'='woo-1001'"));
        // 重复推送幂等
        var (ok2, msg2) = await EquiLedger.Engine.WooCommerce.Ingest(_f.Db, _f.Store, body, EquiLedger.Engine.WooCommerce.Sign(body, secret), secret);
        Assert.True(ok2); Assert.Contains("重复", msg2);
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM events.event_log WHERE payload->>'externalId'='woo-1001'"));
        // 签名错误拒绝
        var (ok3, _) = await EquiLedger.Engine.WooCommerce.Ingest(_f.Db, _f.Store, body, "badsignature", secret);
        Assert.False(ok3);
        // 未完成订单拒绝
        var pending = body.Replace("\"completed\"", "\"processing\"");
        var (ok4, _) = await EquiLedger.Engine.WooCommerce.Ingest(_f.Db, _f.Store, pending, EquiLedger.Engine.WooCommerce.Sign(pending, secret), secret);
        Assert.False(ok4);
        var report = await _f.Verifier.Verify();
        Assert.True(report.Ok, string.Join(";", report.ChainErrors) + " " + string.Join(";", report.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
    }
}
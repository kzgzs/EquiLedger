// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>知识产权双账簿：档案≠会计账面；CAS6 确认闸门、直线摊销、减值、处置、到期提醒；每步重放验真。</summary>
public class IpTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 产权档案与会计确认闸门及摊销减值处置()
    {
        await _f.Found();
        await _f.Submit("IpRegistered", "op", null, new JsonObject
        {
            ["ipId"] = "IP-1", ["name"] = "自研治理软件", ["category"] = "Copyright", ["regNo"] = "软著X",
            ["ownerParty"] = "company", ["controller"] = "本公司", ["acquireMode"] = "自研", ["acquireDate"] = "2026-01-01",
            ["cost"] = 80000, ["fees"] = 3000, ["validUntil"] = "2026-12-31"
        }, Fixture.Ev("软著证书"));
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.ip_rights WHERE ip_id='IP-1'"));
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='IpRenewal'"));
        await _f.Submit("IpValued", "op", null, new JsonObject { ["ipId"] = "IP-1", ["appraisedValue"] = 3000000, ["appraisalReport"] = "EV-1" }, Fixture.Ev("评估报告"));
        Assert.Equal(3000000m, await _f.ScalarD("SELECT appraised_value FROM proj.ip_rights WHERE ip_id='IP-1'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='IntangibleAssets'"));
        var gate = await _f.SubmitFails("IpCapitalized", "op", null, new JsonObject
        {
            ["ipId"] = "IP-1", ["cost"] = 80000, ["lifeMonths"] = 120, ["payMode"] = "Bank",
            ["criteria"] = new JsonObject { ["identifiable"] = true, ["economicBenefits"] = true, ["costReliable"] = false }
        }, Fixture.Ev("成本凭证"));
        Assert.Equal("NotRecognizable", gate.Kind);
        await _f.Submit("IpCapitalized", "op", null, new JsonObject
        {
            ["ipId"] = "IP-1", ["assetId"] = "IA-1", ["cost"] = 80000, ["lifeMonths"] = 120, ["payMode"] = "Bank",
            ["criteria"] = new JsonObject { ["identifiable"] = true, ["economicBenefits"] = true, ["costReliable"] = true }
        }, Fixture.Ev("成本凭证+确认判断"));
        Assert.Equal("IA-1", (await _f.Rows("SELECT linked_asset_id FROM proj.ip_rights WHERE ip_id='IP-1'"))[0]["linked_asset_id"]);
        Assert.Equal(80000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='IntangibleAssets'"));
        await _f.Submit("IpAmortizationRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("摊销计算表"));
        Assert.Equal(666.67m, await _f.ScalarD("SELECT accumulated_amort FROM proj.intangible_assets WHERE asset_id='IA-1'"));
        Assert.Equal(666.67m, await _f.ScalarD("SELECT total FROM proj.amort_runs WHERE period='2026-11'"));
        var dup = await _f.SubmitFails("IpAmortizationRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("x"));
        Assert.Equal("AlreadyRun", dup.Kind);
        await _f.Submit("IpImpaired", "op", null, new JsonObject { ["assetId"] = "IA-1", ["amount"] = 10000 }, Fixture.Ev("减值测试"));
        Assert.Equal(69333.33m, await _f.ScalarD("SELECT cost-accumulated_amort-impairment FROM proj.intangible_assets WHERE asset_id='IA-1'"));
        var over = await _f.SubmitFails("IpImpaired", "op", null, new JsonObject { ["assetId"] = "IA-1", ["amount"] = 999999 }, Fixture.Ev("x"));
        Assert.Equal("AmountExceed", over.Kind);
        await _f.Submit("IpDisposed", "op", null, new JsonObject { ["assetId"] = "IA-1", ["proceeds"] = 50000 }, Fixture.Ev("处置合同+回单"));
        Assert.Equal("Disposed", (await _f.Rows("SELECT status FROM proj.intangible_assets WHERE asset_id='IA-1'"))[0]["status"]);
        Assert.Equal(19333.33m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='AssetDisposalGainLoss'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 产权流转争议续费记录全生命周期()
    {
        await _f.Found();
        await _f.Submit("IpRegistered", "op", null, new JsonObject
        {
            ["ipId"] = "IP-2", ["name"] = "注册商标玄铁", ["category"] = "Trademark", ["ownerParty"] = "company",
            ["cost"] = 3000, ["validUntil"] = "2027-06-30", ["renewDate"] = "2027-01-31"
        }, Fixture.Ev("商标注册证"));
        await _f.Submit("IpRecordAdded", "op", null, new JsonObject
        {
            ["ipId"] = "IP-2", ["kind"] = "License", ["detail"] = new JsonObject { ["counterparty"] = "p-client", ["amount"] = 50000 }
        }, Fixture.Ev("许可合同"));
        await _f.Submit("IpRecordAdded", "op", null, new JsonObject
        {
            ["ipId"] = "IP-2", ["kind"] = "Litigation", ["detail"] = new JsonObject { ["caseNo"] = "民初X", ["claim"] = "侵权索赔" }
        }, Fixture.Ev("起诉状"));
        await _f.Submit("IpRecordAdded", "op", null, new JsonObject
        {
            ["ipId"] = "IP-2", ["kind"] = "Renewal", ["detail"] = new JsonObject { ["validUntil"] = "2037-06-30", ["renewDate"] = "2037-01-31" }
        }, Fixture.Ev("续展证明"));
        Assert.Equal(4, await _f.ScalarD("SELECT COUNT(*) FROM proj.ip_events_log WHERE ip_id='IP-2'"));
        var vu = (await _f.Rows("SELECT valid_until FROM proj.ip_rights WHERE ip_id='IP-2'"))[0]["valid_until"]!;
        var vuDate = vu is DateOnly dOnly ? dOnly : DateOnly.FromDateTime((DateTime)vu);
        Assert.Equal("2037-06-30", vuDate.ToString("yyyy-MM-dd"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }
}
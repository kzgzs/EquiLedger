// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>一人有限责任公司：老板 op 兼唯一股东，决议/执行/查账一体，法定年审待办自动生成。</summary>
public class OnePersonTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task FoundOnePerson()
    {
        await _f.Submit("CompanyFounded", "founder", null, new JsonObject
        {
            ["company"] = new JsonObject { ["name"] = "一人有限公司", ["type"] = "有限责任公司", ["registeredCapital"] = 1000000, ["industryTags"] = new JsonArray("软件") },
            ["accounts"] = new JsonArray(
                new JsonObject { ["username"] = "op", ["displayName"] = "老板", ["role"] = "Operator", ["partyId"] = "boss", ["password"] = "Op#12345", ["subscribed"] = 1000000, ["realName"] = "老板甲", ["idNumber"] = "110101198001011234" },
                new JsonObject { ["username"] = "sup", ["displayName"] = "监事", ["role"] = "Supervisor", ["password"] = "S#12345", ["realName"] = "监督明", ["idNumber"] = "110101197501014567" }),
            ["charterRules"] = new JsonArray(
                new JsonObject { ["kind"] = "Charter.Voting", ["params"] = new JsonObject { ["basis"] = "Subscribed", ["threshold"] = 0.5 } },
                new JsonObject { ["kind"] = "Charter.Dividend", ["params"] = new JsonObject { ["payoutRatio"] = 1.0, ["forceMinRatio"] = 0.6 } }),
            ["whitelist"] = new JsonArray()
        }, Fixture.Ev("章程+股东决定"));
    }

    [Fact]
    public async Task OnePerson_Founding_AnnualAuditTodo_And_SolePosition()
    {
        await FoundOnePerson();
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='AnnualAudit'"));
        Assert.Equal(1000000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='boss' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.cap_positions WHERE cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
    }

    [Fact]
    public async Task OnePerson_OpDualIdentity_ProposeVoteExecute()
    {
        await FoundOnePerson();
        // 老板以 op 身份发起议案并实名表决（唯一股东书面决定的在线形式）
        await _f.Submit("ResolutionProposed", "op", null, new JsonObject
        {
            ["resolutionNo"] = "RES-OP-1", ["title"] = "利润分配议案", ["kind"] = "Dividend", ["params"] = new JsonObject()
        }, Fixture.Ev("议案"));
        await _f.Submit("ResolutionVoted", "op", null, new JsonObject
        {
            ["resolutionNo"] = "RES-OP-1", ["partyId"] = "boss", ["approve"] = true
        }, new JsonArray());
        Assert.Equal(1m, await _f.ScalarD("SELECT CASE WHEN passed THEN 1 ELSE 0 END FROM proj.resolutions WHERE resolution_no='RES-OP-1'"));
        // 只能为本人持股表决，冒名他人 → 拒绝
        var ex = await _f.SubmitFails("ResolutionVoted", "op", null, new JsonObject
        {
            ["resolutionNo"] = "RES-OP-1", ["partyId"] = "other", ["approve"] = true
        }, new JsonArray());
        Assert.Equal("RoleDenied", ex.Kind);
    }

    [Fact]
    public async Task OnePerson_FullBusinessFlow_SoloOperator()
    {
        await FoundOnePerson();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "boss", ["amount"] = 1000000 }, Fixture.Ev("银行回单"));
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 1000000, ["customer"] = "客户X" }, Fixture.Ev("合同+发票"));
        // 分红候选稿自动生成（一人公司同样走决议→批准→申报→支付闭环）
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.dividend_plans WHERE status='Candidate'") >= 1);
        var planId = (string)(await _f.Rows("SELECT plan_id FROM proj.dividend_plans WHERE status='Candidate' ORDER BY plan_id LIMIT 1"))[0]["plan_id"]!;
        await _f.RecordResolution("RES-D1", "Dividend", new JsonObject { ["planId"] = planId });
        await _f.Submit("DividendApproved", "op", "RES-D1", new JsonObject { ["planId"] = planId }, Fixture.Ev("股东书面决定"));
        Assert.Equal(1m, await _f.ScalarD("SELECT COUNT(*) FROM proj.dividend_plans WHERE status='Approved'"));
        // 一人公司不缴经营所得，仍走企业所得税+股息20%（与多人有限公司同规则包，CIT 并入利润推导单）
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.calc_results WHERE kind='ProfitDerivation'") >= 1);
        Assert.True(await _f.ScalarD("SELECT COUNT(*) FROM proj.calc_results WHERE kind='ProfitDerivation' AND derivation::text LIKE '%企业所得税%'") >= 1);
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using EquiLedger.Storage;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>模块三：凭证归集借贷平衡、三表勾稽（资产=负债+权益、现金净增加=银行发生）、科目余额表借贷合计相等。</summary>
public class FinanceTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 三表与凭证_同源勾稽()
    {
        await _f.Found();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 600000 }, Fixture.Ev("回单"));
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "bob", ["amount"] = 400000 }, Fixture.Ev("回单"));
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 500000, ["customer"] = "X" }, Fixture.Ev("发票"));
        await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 2000, ["category"] = "办公", ["payee"] = "A" });
        var period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
        var all = await _f.Rows("SELECT * FROM proj.ledger ORDER BY id");
        var chart = await _f.Rows("SELECT * FROM proj.chart_of_accounts ORDER BY code");
        var evs = await _f.Rows("SELECT seq,event_id,event_type AS type,resolution_no,evidence FROM events.event_log");
        var byId = evs.ToDictionary(e => (Guid)e["event_id"]!, e => e);
        var cum = Finance.Sums(all, r => Pg.S(r["period"]).CompareTo(period) <= 0);
        var per = Finance.Sums(all, r => Pg.S(r["period"]) == period);
        var perRows = all.Where(r => Pg.S(r["period"]) == period).ToList();

        var bs = Finance.BalanceSheet(cum, chart);
        Assert.True(bs["balanced"]!.GetValue<bool>());
        Assert.Equal(1498000m, bs["totalAssets"]!.GetValue<decimal>());

        var vo = Finance.Vouchers(period, perRows, byId, chart);
        Assert.Equal(4, vo.Count);
        Assert.All(vo, v => Assert.True(v!["balanced"]!.GetValue<bool>()));

        var cf = Finance.CashFlow(perRows, evs.ToDictionary(e => (Guid)e["event_id"]!, e => Pg.S(e["type"])));
        Assert.Equal(1000000m, cf["financing"]!["inflow"]!.GetValue<decimal>());
        Assert.Equal(498000m, cf["operating"]!["net"]!.GetValue<decimal>());
        Assert.Equal(1498000m, cf["netIncrease"]!.GetValue<decimal>());

        var revNet = per.TryGetValue("Revenue", out var rv) ? rv.c - rv.d : 0;
        var inc = Finance.Income(per, cum, chart);
        Assert.Equal(revNet - 2000m, inc["period"]!["netProfit"]!.GetValue<decimal>());

        var tb = Finance.Trial(per, cum, chart);
        var sd = tb.Sum(n => n!["periodDebit"]!.GetValue<decimal>());
        var sc = tb.Sum(n => n!["periodCredit"]!.GetValue<decimal>());
        Assert.Equal(sd, sc);

        Assert.True((await _f.Verifier.Verify()).Ok); // 科目种子为参考数据，不影响重放比对
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

// 授权闸门是进程级静态状态：串行化全部测试，避免闸门测试窗口波及其他类
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EquiLedger.Tests;

/// <summary>商业授权：判定纯函数、mock 客户端、写入闸门（冻结写不冻结读）。</summary>
public class LicenseTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public void 判定_服务器明确无效不给宽限()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var s in new[] { "expired", "invalid", "tampered", "suspended" })
            Assert.Equal(s, LicensePolicy.Evaluate(s, null, now.AddDays(-1), now, 7).Status);
        var ok = LicensePolicy.Evaluate("valid", now.AddDays(30), null, now, 7);
        Assert.Equal("valid", ok.Status);
        Assert.Equal(now, ok.LastOkAt);
    }

    [Fact]
    public void 判定_网络失败先宽限后冻结()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("grace", LicensePolicy.Evaluate(null, null, now.AddDays(-3), now, 7).Status);
        Assert.Equal("offline_expired", LicensePolicy.Evaluate(null, null, now.AddDays(-8), now, 7).Status);
        Assert.Equal("offline_expired", LicensePolicy.Evaluate(null, null, null, now, 7).Status);
    }

    [Fact]
    public async Task 客户端_mock解析状态与到期()
    {
        var client = new LicenseClient((req, ct) =>
        {
            Assert.EndsWith("/api/v2/license/check", req.RequestUri!.ToString());
            Assert.True(req.Headers.Contains("X-API-Key"));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":\"valid\",\"expires_at\":\"2027-11-24T00:00:00Z\"}") });
        });
        var (st, exp) = await client.CheckAsync("https://example.test/", "k", "lic", "d");
        Assert.Equal("valid", st);
        Assert.Equal(new DateTimeOffset(2027, 11, 24, 0, 0, 0, TimeSpan.Zero), exp);
    }

    [Fact]
    public async Task 闸门_强制开且过期_冻结写入但验真可用()
    {
        await _f.Found();
        LicenseGate.Enforce = true;
        LicenseGate.Current = new LicenseState("expired", null, null, "授权已过期");
        var ex = await _f.SubmitFails("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("考勤表"));
        Assert.Equal("LicenseInvalid", ex.Kind);
        Assert.True((await _f.Verifier.Verify()).Ok); // 读与验真不受影响
        LicenseGate.Enforce = false;
        LicenseGate.Current = new LicenseState("unconfigured", null, null, "");
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("考勤表"));
    }
}
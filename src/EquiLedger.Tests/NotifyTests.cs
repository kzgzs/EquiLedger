// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>群通知治理：工资账本禁推、默认拒绝、部门匹配、分群模板、高敏感摘要、配置决议化+重放验真。</summary>
public class NotifyTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public void 工资账本任何群不得接收明细()
    {
        var (lvl, _) = NotifyPolicy.Classify("PayrollRun", new JsonObject { ["totals"] = new JsonObject { ["net"] = 6552 } });
        Assert.Equal(NotifyPolicy.Level.High, lvl);
        var filtered = NotifyPolicy.Filter(new JsonObject
        {
            ["period"] = "2026-11", ["totals"] = new JsonObject { ["net"] = 6552 },
            ["bankAccount"] = "6222000011112222", ["lines"] = new JsonArray(), ["idNumber"] = "110101198001011234"
        });
        Assert.False(filtered.ContainsKey("totals"));
        Assert.False(filtered.ContainsKey("bankAccount"));
        Assert.False(filtered.ContainsKey("lines"));
        Assert.False(filtered.ContainsKey("idNumber"));
        Assert.True(filtered.ContainsKey("period"));
        var text = NotifyPolicy.Render("company", "月度算薪", "2026-11", filtered, true, "abcd1234");
        Assert.DoesNotContain("6552", text);
        Assert.Contains("授权登录", text);
        Assert.Contains("撤回不等于数据收回", text);
        var groups = new List<(string, string, string)> { ("G-STAFF", "staff", ""), ("G-CO", "company", "") };
        var bindings = new List<(string, string)> { ("G-STAFF", "PayrollRun"), ("G-CO", "PayrollRun") };
        var route = NotifyPolicy.Route(groups, bindings, "PayrollRun", new JsonObject());
        Assert.DoesNotContain("G-STAFF", route); // 员工群不在工资事件接收范围
        Assert.Contains("G-CO", route);          // 公司群仅摘要
    }

    [Fact]
    public void 未绑定默认拒绝与分群模板()
    {
        var groups = new List<(string, string, string)> { ("G-CO", "company", ""), ("G-BOARD", "board", ""), ("G-DEP", "department", "生产部") };
        var bindings = new List<(string, string)> { ("G-BOARD", "PurchasePaid") };
        Assert.Empty(NotifyPolicy.Route(groups, bindings, "RevenueRecorded", new JsonObject())); // 未绑定=不推
        var route = NotifyPolicy.Route(groups, bindings, "PurchasePaid", new JsonObject { ["department"] = "生产部" });
        Assert.Single(route);
        Assert.Equal("G-BOARD", route[0]);
        var depMismatch = NotifyPolicy.Route(groups, new List<(string, string)> { ("G-DEP", "StockIssued") }, "StockIssued", new JsonObject { ["department"] = "财务部" });
        Assert.Empty(depMismatch); // 部门不匹配不推
        var depMatch = NotifyPolicy.Route(groups, new List<(string, string)> { ("G-DEP", "StockIssued") }, "StockIssued", new JsonObject { ["department"] = "生产部" });
        Assert.Single(depMatch);
        var f = NotifyPolicy.Filter(new JsonObject { ["poNo"] = "PO-1", ["amount"] = 1130 });
        var b = NotifyPolicy.Render("board", "采购支付", "R-1", f, false, "h8");
        var co = NotifyPolicy.Render("company", "采购支付", "R-1", f, false, "h8");
        Assert.NotEqual(b, co);
        Assert.Contains("董事会群", b);
        Assert.Contains("公司群", co);
        var (lvlSecret, kindsSecret) = NotifyPolicy.Classify("ContractSigned", new JsonObject { ["secrecy"] = "Secret" });
        Assert.Equal(NotifyPolicy.Level.High, lvlSecret);
        Assert.Equal(new[] { "board" }, kindsSecret);
    }

    [Fact]
    public async Task 群配置决议化与重放验真()
    {
        await _f.Found();
        await _f.RecordResolution("R-NG", "NotifyPolicy", new JsonObject { ["groupId"] = "G-BOARD" });
        await _f.Submit("NotifyGroupConfigured", "op", "R-NG", new JsonObject
        {
            ["groupId"] = "G-BOARD", ["name"] = "董事会群", ["kind"] = "board", ["department"] = "",
            ["webhook"] = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=x", ["enabled"] = true,
            ["bindings"] = new JsonArray("PurchasePaid", "PeriodClosed")
        });
        Assert.Equal(2, (await _f.Rows("SELECT * FROM proj.notify_bindings WHERE group_id='G-BOARD'")).Count);
        Assert.True((await _f.Verifier.Verify()).Ok);
        await _f.RecordResolution("R-NG2", "NotifyPolicy", new JsonObject { ["groupId"] = "G-BOARD" });
        var bad = await _f.SubmitFails("NotifyGroupConfigured", "op", "R-NG2", new JsonObject
        {
            ["groupId"] = "G-BOARD", ["name"] = "x", ["kind"] = "staff", ["department"] = "",
            ["webhook"] = "http://insecure.example", ["enabled"] = true, ["bindings"] = new JsonArray()
        });
        Assert.Equal("BadParams", bad.Kind);
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>举报处理（监管邮箱转来→操作员登记→监事/操作员调查→决议认定→严重群通告）与员工入股/公司分股；每步重放验真。</summary>
public class ReportShareTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 举报处理操作员登记与监事闭环()
    {
        await _f.Found();
        var notOp = await _f.SubmitFails("ReportFiled", "alice", null, new JsonObject
        {
            ["reportId"] = "RP-0", ["category"] = "Embezzlement", ["target"] = "张三", ["description"] = "x", ["source"] = "Mailbox"
        }, Fixture.Ev("线索"));
        Assert.Equal("NotOperator", notOp.Kind);
        await _f.Submit("ReportFiled", "op", null, new JsonObject
        {
            ["reportId"] = "RP-1", ["category"] = "Embezzlement", ["target"] = "张三",
            ["description"] = "监管邮箱转来：疑似挪用备用金", ["source"] = "Mailbox"
        }, Fixture.Ev("监管邮箱邮件打印件"));
        var r = (await _f.Rows("SELECT reporter,reporter_actor,status,source FROM proj.reports WHERE report_id='RP-1'"))[0];
        Assert.Equal("监管邮箱转来", r["reporter"]);
        Assert.Equal("op", r["reporter_actor"]);
        Assert.Equal("Mailbox", r["source"]);
        await _f.Submit("ReportInvestigated", "sup", null, new JsonObject { ["reportId"] = "RP-1", ["findings"] = "流水核对属实" }, Fixture.Ev("银行流水"));
        Assert.Equal("sup", (await _f.Rows("SELECT investigated_by FROM proj.reports WHERE report_id='RP-1'"))[0]["investigated_by"]);
        await _f.RecordResolution("R-RP", "ReportHandling", new JsonObject { ["reportId"] = "RP-1", ["outcome"] = "Substantiated", ["severity"] = "Serious" });
        await _f.Submit("ReportClosed", "op", "R-RP", new JsonObject { ["reportId"] = "RP-1", ["outcome"] = "Substantiated", ["severity"] = "Serious", ["action"] = "责令退还并移送司法" });
        var closed = (await _f.Rows("SELECT status,severity FROM proj.reports WHERE report_id='RP-1'"))[0];
        Assert.Equal("Closed", closed["status"]);
        Assert.Equal("Serious", closed["severity"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
        var (lvl, kinds) = NotifyPolicy.Classify("ReportFiled", new JsonObject());
        Assert.Contains("supervisor", kinds);
        Assert.DoesNotContain("staff", kinds);
        var (_, closedKinds) = NotifyPolicy.Classify("ReportClosed", new JsonObject { ["severity"] = "Serious" });
        Assert.Contains("company", closedKinds);
    }

    [Fact]
    public async Task 群设置操作员直设与监管邮箱()
    {
        await _f.Found();
        await _f.Submit("NotifyGroupConfigured", "op", null, new JsonObject
        {
            ["groupId"] = "G-COMP", ["name"] = "公司群", ["kind"] = "company", ["department"] = "",
            ["webhook"] = "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=t", ["enabled"] = true,
            ["bindings"] = new JsonArray("ResolutionRecorded")
        }, Fixture.Ev("群配置留痕"));
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.notify_groups WHERE group_id='G-COMP'"));
        await _f.Submit("SupervisionEmailSet", "op", null, new JsonObject { ["email"] = "supervision@company.com" }, Fixture.Ev("高层共管授权"));
        Assert.Equal("supervision@company.com", (await _f.Rows("SELECT supervision_email FROM proj.company WHERE id=1"))[0]["supervision_email"]);
        var bad = await _f.SubmitFails("SupervisionEmailSet", "op", null, new JsonObject { ["email"] = "not-an-email" }, Fixture.Ev("x"));
        Assert.Equal("BadParams", bad.Kind);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 员工自由入股与公司分股()
    {
        await _f.Found();
        await _f.RecordResolution("RES-H-E1", "Hire", new JsonObject { ["employeeId"] = "E1" });
        await _f.Submit("EmployeeHired", "op", "RES-H-E1", new JsonObject
        {
            ["employeeId"] = "E1", ["partyId"] = "E1", ["name"] = "员工E1", ["position"] = "工程师",
            ["hireDate"] = "2026-11-01", ["contractId"] = "CT-L-E1", ["bankAccount"] = "6222000011112222", ["bankName"] = "测试银行"
        }, Fixture.Ev("劳动合同+入职登记"));
        await _f.RecordResolution("R-ES", "EmployeeShare", new JsonObject { ["employeeId"] = "E1", ["subscribed"] = 100000 });
        await _f.Submit("EmployeeShareSubscription", "op", "R-ES", new JsonObject { ["employeeId"] = "E1", ["subscribed"] = 100000 });
        Assert.Equal(true, (await _f.Rows("SELECT is_shareholder FROM proj.employees WHERE employee_id='E1'"))[0]["is_shareholder"]);
        Assert.Equal(100000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='E1' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "E1", ["amount"] = 100000 }, Fixture.Ev("回单"));
        Assert.Equal(100000m, await _f.ScalarD("SELECT paid_in FROM proj.cap_positions WHERE party_id='E1' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        await _f.RecordResolution("R-EG", "EmployeeShareGrant", new JsonObject { ["employeeId"] = "E1", ["capitalAmount"] = 50000, ["source"] = "ShareholderGift", ["fromParty"] = "alice" });
        await _f.Submit("EmployeeShareGranted", "op", "R-EG", new JsonObject { ["employeeId"] = "E1", ["capitalAmount"] = 50000, ["source"] = "ShareholderGift", ["fromParty"] = "alice" });
        Assert.Equal(550000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='alice' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.Equal(150000m, await _f.ScalarD("SELECT subscribed FROM proj.cap_positions WHERE party_id='E1' AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)"));
        Assert.True((await _f.Verifier.Verify()).Ok);
        await _f.RecordResolution("R-EG2", "EmployeeShareGrant", new JsonObject { ["employeeId"] = "E1", ["capitalAmount"] = 1000, ["source"] = "CapitalReserve" });
        var noReserve = await _f.SubmitFails("EmployeeShareGranted", "op", "R-EG2", new JsonObject { ["employeeId"] = "E1", ["capitalAmount"] = 1000, ["source"] = "CapitalReserve" });
        Assert.Equal("InsufficientReserve", noReserve.Kind);
    }
}
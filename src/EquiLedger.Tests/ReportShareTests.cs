// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>举报通道（任何角色含匿名/监事调查/决议办结）与员工入股/公司分股；每步重放验真。</summary>
public class ReportShareTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 举报匿名任何角色与监事闭环()
    {
        await _f.Found();
        await _f.Submit("ReportFiled", "alice", null, new JsonObject
        {
            ["reportId"] = "RP-1", ["category"] = "Embezzlement", ["target"] = "张三",
            ["description"] = "疑似挪用备用金", ["anonymous"] = true
        }, Fixture.Ev("线索材料"));
        var r = (await _f.Rows("SELECT reporter,reporter_actor,status FROM proj.reports WHERE report_id='RP-1'"))[0];
        Assert.Equal("匿名", r["reporter"]);
        Assert.Equal("alice", r["reporter_actor"]);
        await _f.Submit("ReportFiled", "sup", null, new JsonObject
        {
            ["reportId"] = "RP-2", ["category"] = "AbuseOfPower", ["target"] = "李四",
            ["description"] = "违规审批", ["anonymous"] = false
        });
        Assert.Equal("sup", (await _f.Rows("SELECT reporter FROM proj.reports WHERE report_id='RP-2'"))[0]["reporter"]);
        var denied = await _f.SubmitFails("ReportInvestigated", "op", null, new JsonObject { ["reportId"] = "RP-1", ["findings"] = "x" });
        Assert.Equal("RoleDenied", denied.Kind);
        await _f.Submit("ReportInvestigated", "sup", null, new JsonObject { ["reportId"] = "RP-1", ["findings"] = "流水核对属实" });
        await _f.RecordResolution("R-RP", "ReportHandling", new JsonObject { ["reportId"] = "RP-1", ["outcome"] = "Substantiated" });
        await _f.Submit("ReportClosed", "op", "R-RP", new JsonObject { ["reportId"] = "RP-1", ["outcome"] = "Substantiated", ["action"] = "责令退还并移送司法" });
        Assert.Equal("Closed", (await _f.Rows("SELECT status FROM proj.reports WHERE report_id='RP-1'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
        var (lvl, kinds) = NotifyPolicy.Classify("ReportFiled", new JsonObject());
        Assert.Contains("supervisor", kinds);
        Assert.DoesNotContain("staff", kinds);
        Assert.DoesNotContain("company", kinds);
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
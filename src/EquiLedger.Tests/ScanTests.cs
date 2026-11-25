// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>扫码网关：设备认证/验真/查重/隔离/复核 + 生产计件进薪酬 + 仓库/质检/物流/销售自动化；每步重放验真。</summary>
public class ScanTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    private async Task Base()
    {
        await _f.Found();
        await _f.RecordResolution("RES-H-E1", "Hire", new JsonObject { ["employeeId"] = "E1" });
        await _f.Submit("EmployeeHired", "op", "RES-H-E1", new JsonObject
        {
            ["employeeId"] = "E1", ["partyId"] = "E1", ["name"] = "工人E1", ["position"] = "操作工",
            ["hireDate"] = "2026-11-01", ["contractId"] = "CT-L-E1", ["bankAccount"] = "6222000011112222", ["bankName"] = "测试银行"
        }, Fixture.Ev("劳动合同"));
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "m1", ["name"] = "原料", ["unit"] = "件", ["category"] = "raw" }, Fixture.Ev("物料主数据"));
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "p1", ["name"] = "成品", ["unit"] = "件", ["category"] = "goods" }, Fixture.Ev("物料主数据"));
        await _f.Submit("WarehouseDefined", "op", null, new JsonObject { ["warehouseId"] = "w1", ["name"] = "主仓" }, Fixture.Ev("仓库建档"));
        await _f.Submit("StockReceived", "op", null, new JsonObject { ["materialId"] = "m1", ["warehouse"] = "w1", ["qty"] = 1000, ["unitCost"] = 10 }, Fixture.Ev("入库单"));
        await _f.Submit("DeviceRegistered", "op", null, new JsonObject { ["deviceId"] = "D1", ["name"] = "车间扫码枪", ["adapter"] = "usb", ["department"] = "生产部" }, Fixture.Ev("设备登记"));
    }

    private async Task<(string barcode, string secret)> Packet(string id, string kind, string refId, JsonObject payload)
    {
        await _f.Submit("ScanPacketCreated", "op", null, new JsonObject { ["packetId"] = id, ["kind"] = kind, ["refId"] = refId, ["payload"] = payload }, Fixture.Ev("贴码依据"));
        var row = (await _f.Rows($"SELECT barcode FROM proj.scan_packets WHERE packet_id='{id}'"))[0];
        var secret = (await _f.Rows("SELECT secret FROM proj.devices WHERE device_id='D1'"))[0]["secret"]!.ToString()!;
        return (row["barcode"]!.ToString()!, secret);
    }

    private Task<ScanGateway.Outcome> Scan(string barcode, string secret, string device = "D1")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        return ScanGateway.Process(_f.Db, _f.Store, device, ts, ScanGateway.Sign(secret, ts, barcode), barcode, "op");
    }

    [Fact]
    public async Task 生产扫码计件与查重隔离复核()
    {
        await Base();
        await _f.RecordResolution("R-PO", "ProductionPlan", new JsonObject { ["orderNo"] = "PO-1" });
        await _f.Submit("ProductionOrderCreated", "op", "R-PO", new JsonObject
        {
            ["orderNo"] = "PO-1", ["productMaterialId"] = "p1", ["qty"] = 100,
            ["bom"] = new JsonArray(new JsonObject { ["materialId"] = "m1", ["qtyPer"] = 1 }),
            ["plannedStart"] = "2026-11-01", ["plannedEnd"] = "2026-12-31"
        }, Fixture.Ev("生产计划"));
        await _f.RecordResolution("R-PR", "RuleChange", new JsonObject { ["kind"] = "PieceRate", ["subject"] = "PR-A" });
        await _f.Submit("RuleActivated", "op", "R-PR", new JsonObject
        {
            ["ruleId"] = "piece-PR-A", ["kind"] = "PieceRate", ["subject"] = "PR-A",
            ["params"] = new JsonObject { ["unitPrice"] = 2 }, ["effectiveFrom"] = "2026-12-01"
        }, Fixture.Ev("计件单价决议"));
        var (bc, sec) = await Packet("PK-1", "ProductionPack", "PO-1", new JsonObject
        {
            ["orderNo"] = "PO-1", ["completedQty"] = 10, ["warehouse"] = "w1", ["employeeId"] = "E1", ["process"] = "PR-A"
        });
        var ok = await Scan(bc, sec);
        Assert.Equal("Accepted", ok.Result);
        Assert.Equal(10m, await _f.ScalarD("SELECT qty FROM proj.piece_records WHERE packet_id='PK-1'"));
        Assert.Equal(10m, await _f.ScalarD("SELECT completed_qty FROM proj.production_orders WHERE order_no='PO-1'"));
        Assert.Equal("Duplicate", (await Scan(bc, sec)).Result);
        Assert.Equal("BadSign", (await Scan(bc, "wrong-secret")).Result);
        var tampered = bc[..^1] + (bc[^1] == '0' ? '1' : '0');
        Assert.Equal("InvalidCheck", (await Scan(tampered, sec)).Result);
        var (bc2, sec2) = await Packet("PK-2", "ProductionPack", "PO-1", new JsonObject
        {
            ["orderNo"] = "PO-1", ["completedQty"] = 10, ["warehouse"] = "w1", ["process"] = "PR-A"
        });
        var q = await Scan(bc2, sec2);
        Assert.Equal("Quarantined", q.Result);
        var rv = (await _f.Rows("SELECT review_id FROM proj.scan_reviews WHERE packet_id='PK-2'"))[0]["review_id"]!.ToString()!;
        var rel = await ScanGateway.ResolveReview(_f.Db, _f.Store, rv, "Approved", "op");
        Assert.Equal("Approved", rel.Result);
        Assert.Equal(20m, await _f.ScalarD("SELECT completed_qty FROM proj.production_orders WHERE order_no='PO-1'"));
        Assert.Equal("Consumed", (await _f.Rows("SELECT status FROM proj.scan_packets WHERE packet_id='PK-2'"))[0]["status"]);
        await _f.Submit("PayrollRun", "op", null, new JsonObject { ["period"] = "2026-11" }, Fixture.Ev("算薪"));
        Assert.Equal(20m, await _f.ScalarD("SELECT gross FROM proj.payroll_lines WHERE employee_id='E1' AND period='2026-11'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 仓库质检物流销售扫码自动化()
    {
        await Base();
        await _f.Submit("StockReceived", "op", null, new JsonObject { ["materialId"] = "p1", ["warehouse"] = "w1", ["qty"] = 50, ["unitCost"] = 20 }, Fixture.Ev("入库单"));
        var (bIn, sIn) = await Packet("PK-IN", "WarehouseIn", "m1", new JsonObject { ["materialId"] = "m1", ["warehouse"] = "w1", ["qty"] = 100, ["unitCost"] = 10 });
        Assert.Equal("Accepted", (await Scan(bIn, sIn)).Result);
        Assert.Equal(1100m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='m1' AND warehouse='w1'"));
        var (bOut, sOut) = await Packet("PK-OUT", "WarehouseOut", "m1", new JsonObject { ["materialId"] = "m1", ["warehouse"] = "w1", ["qty"] = 50, ["purpose"] = "领用" });
        Assert.Equal("Accepted", (await Scan(bOut, sOut)).Result);
        Assert.Equal(1050m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='m1' AND warehouse='w1'"));
        var (bCnt, sCnt) = await Packet("PK-CNT", "WarehouseCount", "m1", new JsonObject { ["materialId"] = "m1", ["warehouse"] = "w1", ["countedQty"] = 1050 });
        Assert.Equal("Accepted", (await Scan(bCnt, sCnt)).Result);
        var (bQc, sQc) = await Packet("PK-QC", "QualityCheck", "B1", new JsonObject
        {
            ["batch"] = "B1", ["product"] = "p1", ["inspector"] = "E1", ["standard"] = "GB/T-X", ["result"] = "Pass", ["qty"] = 5,
            ["orderNo"] = "", ["warehouse"] = "w1", ["employeeId"] = "E1", ["process"] = "PR-A"
        });
        Assert.Equal("Accepted", (await Scan(bQc, sQc)).Result);
        Assert.Equal("Pass", (await _f.Rows("SELECT result FROM proj.quality_records"))[0]["result"]);
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.scan_packets WHERE kind='ProductionPack' AND status='Ready'"));
        var (bQf, sQf) = await Packet("PK-QF", "QualityCheck", "B2", new JsonObject
        {
            ["batch"] = "B2", ["product"] = "p1", ["inspector"] = "E1", ["standard"] = "GB/T-X", ["result"] = "Fail", ["qty"] = 3
        });
        Assert.Equal("Accepted", (await Scan(bQf, sQf)).Result);
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.todos WHERE kind='QualityAnomaly'"));
        var (bLg, sLg) = await Packet("PK-LG", "Logistics", "SF1", new JsonObject { ["tracking"] = "SF1", ["stage"] = "Ship", ["batch"] = "B1" });
        Assert.Equal("Accepted", (await Scan(bLg, sLg)).Result);
        Assert.Equal("已发货", (await _f.Rows("SELECT status FROM proj.logistics_scans WHERE tracking='SF1'"))[0]["status"]);
        var (bSl, sSl) = await Packet("PK-SL", "Sales", "SO9", new JsonObject
        {
            ["orderId"] = "SO9", ["customer"] = "客户A", ["amount"] = 5000, ["materialId"] = "p1", ["warehouse"] = "w1", ["qty"] = 2, ["employeeId"] = "E1"
        });
        Assert.Equal("Accepted", (await Scan(bSl, sSl)).Result);
        Assert.Equal(1, await _f.ScalarD("SELECT COUNT(*) FROM proj.sales_orders WHERE order_id='SO9'"));
        Assert.Equal(5000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='Revenue'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='TaxPayable'"));
        Assert.Equal(5000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='AR'"));
        Assert.Equal(48m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='p1' AND warehouse='w1'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }
}
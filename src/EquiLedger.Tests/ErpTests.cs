// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>第四体系：固资折旧处置、库存移动加权/盘点、生产 BOM 成本结转；每步哈希链重放验真。</summary>
public class ErpTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task 固资购置折旧处置闭环()
    {
        await _f.Found();
        await _f.Submit("FixedAssetAcquired", "op", null, new JsonObject
        {
            ["assetId"] = "FA-01", ["name"] = "服务器", ["category"] = "电子设备", ["cost"] = 120000,
            ["residualRate"] = 0, ["lifeMonths"] = 120, ["acquiredDate"] = "2026-11-01", ["department"] = "技术部", ["location"] = "机房"
        }, Fixture.Ev("购置发票+验收单"));
        Assert.Equal(120000m, await _f.ScalarD("SELECT cost FROM proj.fixed_assets WHERE asset_id='FA-01'"));
        await _f.Submit("FixedAssetDepreciationRun", "op", null, new JsonObject { ["period"] = "2026-11" });
        Assert.Equal(1000m, await _f.ScalarD("SELECT accumulated_dep FROM proj.fixed_assets WHERE asset_id='FA-01'"));
        Assert.Equal(1000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='DepreciationExpense'"));
        Assert.Equal(1000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='AccumDepreciation'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
        var dup = await _f.SubmitFails("FixedAssetDepreciationRun", "op", null, new JsonObject { ["period"] = "2026-11" });
        Assert.Equal("AlreadyRun", dup.Kind);
        await _f.RecordResolution("R-AD", "AssetDisposal", new JsonObject { ["assetId"] = "FA-01", ["proceeds"] = 50000 });
        await _f.Submit("FixedAssetDisposed", "op", "R-AD", new JsonObject { ["assetId"] = "FA-01", ["disposalDate"] = "2026-12-01", ["proceeds"] = 50000 });
        Assert.Equal(69000m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='AssetDisposalGainLoss'"));
        Assert.Equal("Disposed", (await _f.Rows("SELECT status FROM proj.fixed_assets WHERE asset_id='FA-01'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 折旧提足自动停提()
    {
        await _f.Found();
        await _f.Submit("FixedAssetAcquired", "op", null, new JsonObject
        {
            ["assetId"] = "FA-02", ["name"] = "笔记本", ["category"] = "电子设备", ["cost"] = 1200,
            ["residualRate"] = 0.1, ["lifeMonths"] = 12, ["acquiredDate"] = "2026-11-01", ["department"] = "行政", ["location"] = "办公室"
        });
        for (var i = 0; i < 12; i++)
        {
            var period = new DateTime(2026, 11, 1).AddMonths(i).ToString("yyyy-MM");
            await _f.Submit("FixedAssetDepreciationRun", "op", null, new JsonObject { ["period"] = period });
        }
        Assert.Equal(1080m, await _f.ScalarD("SELECT accumulated_dep FROM proj.fixed_assets WHERE asset_id='FA-02'"));
        Assert.Equal("FullyDepreciated", (await _f.Rows("SELECT status FROM proj.fixed_assets WHERE asset_id='FA-02'"))[0]["status"]);
        await _f.Submit("FixedAssetDepreciationRun", "op", null, new JsonObject { ["period"] = "2027-11" });
        Assert.Equal(0m, await _f.ScalarD("SELECT total FROM proj.depreciation_runs WHERE period='2027-11'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 库存移动加权与盘点不足拦截()
    {
        await _f.Found();
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "M1", ["name"] = "钢材", ["unit"] = "件", ["spec"] = "Q235", ["category"] = "raw", ["safetyStock"] = 10 });
        await _f.Submit("WarehouseDefined", "op", null, new JsonObject { ["warehouseId"] = "WH1", ["name"] = "主仓", ["location"] = "一号库" });
        await _f.Submit("StockReceived", "op", null, new JsonObject { ["materialId"] = "M1", ["warehouse"] = "WH1", ["qty"] = 100, ["unitCost"] = 10, ["paid"] = false, ["supplier"] = "供应商A", ["memo"] = "采购入库" });
        Assert.Equal(1000m, await _f.ScalarD("SELECT COALESCE(SUM(credit),0) FROM proj.ledger WHERE account='AP'"));
        await _f.Submit("StockIssued", "op", null, new JsonObject { ["materialId"] = "M1", ["warehouse"] = "WH1", ["qty"] = 30, ["purpose"] = "sale" });
        Assert.Equal(300m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='CostOfSales'"));
        Assert.Equal(70m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='M1' AND warehouse='WH1'"));
        var lack = await _f.SubmitFails("StockIssued", "op", null, new JsonObject { ["materialId"] = "M1", ["warehouse"] = "WH1", ["qty"] = 999, ["purpose"] = "sale" });
        Assert.Equal("InsufficientStock", lack.Kind);
        await _f.Submit("StockCounted", "op", null, new JsonObject { ["materialId"] = "M1", ["warehouse"] = "WH1", ["countedQty"] = 68, ["reason"] = "月度盘点短缺" });
        Assert.Equal(68m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='M1' AND warehouse='WH1'"));
        Assert.Equal(20m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='StockAdjust'"));
        Assert.True((await _f.Verifier.Verify()).Ok);
    }

    [Fact]
    public async Task 生产订单BOM领料与完工结转()
    {
        await _f.Found();
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "M1", ["name"] = "钢材", ["unit"] = "件", ["category"] = "raw", ["safetyStock"] = 0 });
        await _f.Submit("MaterialDefined", "op", null, new JsonObject { ["materialId"] = "P1", ["name"] = "构件", ["unit"] = "件", ["category"] = "goods", ["safetyStock"] = 0 });
        await _f.Submit("WarehouseDefined", "op", null, new JsonObject { ["warehouseId"] = "WH1", ["name"] = "主仓", ["location"] = "一号库" });
        await _f.Submit("StockReceived", "op", null, new JsonObject { ["materialId"] = "M1", ["warehouse"] = "WH1", ["qty"] = 100, ["unitCost"] = 10, ["paid"] = true, ["supplier"] = "供应商A", ["memo"] = "采购入库" });
        await _f.Submit("EquipmentRegistered", "op", null, new JsonObject { ["equipId"] = "EQ1", ["name"] = "切割机", ["type"] = "加工", ["location"] = "车间", ["status"] = "Idle", ["capacityPerDay"] = 5, ["linkedAssetId"] = "" });
        await _f.RecordResolution("R-PP", "ProductionPlan", new JsonObject { ["orderNo"] = "PO-1", ["qty"] = 10 });
        await _f.Submit("ProductionOrderCreated", "op", "R-PP", new JsonObject
        {
            ["orderNo"] = "PO-1", ["productMaterialId"] = "P1", ["qty"] = 10,
            ["bom"] = new JsonArray(new JsonObject { ["materialId"] = "M1", ["qtyPer"] = 2 }),
            ["equipmentId"] = "EQ1", ["plannedStart"] = "2026-11-01", ["plannedEnd"] = "2026-11-30"
        });
        await _f.Submit("ProductionCompleted", "op", null, new JsonObject { ["orderNo"] = "PO-1", ["completedQty"] = 10, ["warehouse"] = "WH1" });
        Assert.Equal(80m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='M1' AND warehouse='WH1'"));
        Assert.Equal(10m, await _f.ScalarD("SELECT qty FROM proj.stock_balances WHERE material_id='P1' AND warehouse='WH1'"));
        Assert.Equal(20m, await _f.ScalarD("SELECT avg_cost FROM proj.stock_balances WHERE material_id='P1' AND warehouse='WH1'"));
        Assert.Equal(0m, await _f.ScalarD("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='ProductionCost'"));
        Assert.Equal(200m, await _f.ScalarD("SELECT COALESCE(SUM(debit),0) FROM proj.ledger WHERE account='Inventory'"));
        Assert.Equal("Done", (await _f.Rows("SELECT status FROM proj.production_orders WHERE order_no='PO-1'"))[0]["status"]);
        Assert.True((await _f.Verifier.Verify()).Ok);
    }
}
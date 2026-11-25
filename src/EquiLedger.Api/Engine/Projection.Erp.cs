// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>第四体系（企业资源管理）：固资卡片+折旧、物料/库存/仓储、设备/生产、成本结转。
/// 交叉不重复建设：固资/库存/生产事件一次提交同时落资源投影+会计分录+治理留痕。</summary>
public static partial class Projection
{
    private static async Task FixedAssetAcquired(ApplyCtx c)
    {
        var id = c.Ps("assetId"); var name = c.Ps("name"); var cost = c.Pd("cost");
        await c.Exec("INSERT INTO proj.fixed_assets(asset_id,name,category,cost,residual_rate,life_months,acquired_date,department,location,status,source_event_id) VALUES(@i,@n,@cat,@cost,@r,@l,@d,@dep,@loc,'Active',@e)",
            ("i", id), ("n", name), ("cat", c.Ps("category")), ("cost", cost), ("r", c.Pd("residualRate")), ("l", (int)c.Pd("lifeMonths")),
            ("d", DateOnly.Parse(c.Ps("acquiredDate")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("dep", c.Ps("department")), ("loc", c.Ps("location")), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "FixedAssets", cost, 0, $"固定资产购置 {name}");
        await c.Ledger(c.Period, "Bank", 0, cost, $"固定资产购付 {name}");
        c.Impact($"固定资产入账 {name}：原值 {cost}，折旧年限 {c.Pd("lifeMonths")} 月，残值率 {c.Pd("residualRate")}；按月自动折旧待调度");
    }

    private static async Task FixedAssetDepreciationRun(ApplyCtx c)
    {
        var period = c.Ps("period");
        var assets = await c.Rows("SELECT * FROM proj.fixed_assets WHERE status='Active' ORDER BY asset_id");
        decimal total = 0; var lines = new JsonArray();
        foreach (var a in assets)
        {
            var cost = Pg.D(a["cost"]); var depBase = cost * (1 - Pg.D(a["residual_rate"]));
            var acc = Pg.D(a["accumulated_dep"]); var remaining = depBase - acc;
            if (remaining <= 0) continue;
            var monthly = Math.Round(depBase / Pg.D(a["life_months"]), 2);
            var dep = Math.Min(monthly, remaining);
            var newAcc = acc + dep;
            await c.Exec("UPDATE proj.fixed_assets SET accumulated_dep=@a, status=@s WHERE asset_id=@i",
                ("a", newAcc), ("s", newAcc >= depBase ? "FullyDepreciated" : "Active"), ("i", Pg.S(a["asset_id"])));
            lines.Add(new JsonObject { ["assetId"] = Pg.S(a["asset_id"]), ["name"] = Pg.S(a["name"]), ["dep"] = dep });
            total += dep;
        }
        await c.ExecJ("INSERT INTO proj.depreciation_runs(period,total,lines,source_event_id) VALUES(@p,@t,@l,@e)",
            new[] { ("l", (JsonNode?)lines) }, ("p", period), ("t", total), ("e", c.Evt.EventId));
        if (total > 0)
        {
            await c.Ledger(period, "DepreciationExpense", total, 0, $"固定资产折旧 {period}");
            await c.Ledger(period, "AccumDepreciation", 0, total, $"固定资产折旧 {period}");
        }
        c.Impact($"折旧计提 {period}：合计 {total}（直线法、按月、提足自动停）；卡片累计折旧同步更新");
    }

    private static async Task FixedAssetDisposed(ApplyCtx c)
    {
        var id = c.Ps("assetId");
        var a = (await c.Rows("SELECT * FROM proj.fixed_assets WHERE asset_id=@i", ("i", id)))[0];
        var cost = Pg.D(a["cost"]); var acc = Pg.D(a["accumulated_dep"]); var proceeds = c.Pd("proceeds");
        var diff = proceeds + acc - cost;
        if (proceeds > 0) await c.Ledger(c.Period, "Bank", proceeds, 0, $"固资处置收款 {id}");
        if (acc > 0) await c.Ledger(c.Period, "AccumDepreciation", acc, 0, $"固资处置转销累计折旧 {id}");
        await c.Ledger(c.Period, "FixedAssets", 0, cost, $"固资处置转销原值 {id}");
        if (diff > 0) await c.Ledger(c.Period, "AssetDisposalGainLoss", 0, diff, $"固资处置净收益 {id}");
        if (diff < 0) await c.Ledger(c.Period, "AssetDisposalGainLoss", -diff, 0, $"固资处置净损失 {id}");
        await c.Exec("UPDATE proj.fixed_assets SET status='Disposed', disposed_date=@d WHERE asset_id=@i",
            ("d", DateOnly.Parse(c.Ps("disposalDate")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("i", id));
        c.Impact($"固定资产处置 {id}：收款 {proceeds}、转销原值 {cost}/累计折旧 {acc}、净损益 {diff} 入链");
    }

    private static async Task MaterialDefined(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.materials(material_id,name,unit,spec,category,safety_stock,source_event_id) VALUES(@i,@n,@u,@s,@cat,@ss,@e)",
            ("i", c.Ps("materialId")), ("n", c.Ps("name")), ("u", c.Ps("unit")), ("s", c.Ps("spec")), ("cat", c.Ps("category")), ("ss", c.Pd("safetyStock")), ("e", c.Evt.EventId));
        c.Impact($"物料主数据 {c.Ps("name")}（{c.Ps("category")}）建档，安全库存 {c.Pd("safetyStock")}");
    }

    private static async Task WarehouseDefined(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.warehouses(warehouse_id,name,location,source_event_id) VALUES(@i,@n,@l,@e)",
            ("i", c.Ps("warehouseId")), ("n", c.Ps("name")), ("l", c.Ps("location")), ("e", c.Evt.EventId));
        c.Impact($"仓库 {c.Ps("name")} 建档");
    }

    private static async Task StockReceived(ApplyCtx c)
    {
        var mid = c.Ps("materialId"); var wh = c.Ps("warehouse"); var qty = c.Pd("qty"); var uc = c.Pd("unitCost");
        var m = (await c.Rows("SELECT category FROM proj.materials WHERE material_id=@i", ("i", mid)))[0];
        var acc = Pg.S(m["category"]) == "goods" ? "Inventory" : "Materials";
        var old = await c.Rows("SELECT qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", mid), ("w", wh));
        var oq = old.Count > 0 ? Pg.D(old[0]["qty"]) : 0; var oa = old.Count > 0 ? Pg.D(old[0]["avg_cost"]) : 0;
        var na = oq + qty > 0 ? Math.Round((oq * oa + qty * uc) / (oq + qty), 4) : uc;
        await c.Exec("INSERT INTO proj.stock_balances(material_id,warehouse,qty,avg_cost) VALUES(@m,@w,@q,@a) ON CONFLICT (material_id,warehouse) DO UPDATE SET qty=proj.stock_balances.qty+@q, avg_cost=@a",
            ("m", mid), ("w", wh), ("q", qty), ("a", na));
        await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'In',@q,@u,@amt,@memo,@e)",
            ("id", $"SM-{c.Eid}"), ("m", mid), ("w", wh), ("q", qty), ("u", uc), ("amt", Math.Round(qty * uc, 2)), ("memo", c.Ps("memo")), ("e", c.Evt.EventId));
        var amt = Math.Round(qty * uc, 2);
        await c.Ledger(c.Period, acc, amt, 0, $"入库 {mid}×{qty}@{uc}（{wh}）");
        await c.Ledger(c.Period, c.Pb("paid") ? "Bank" : "AP", 0, amt, c.Pb("paid") ? $"入库付款 {mid}" : $"应付账款形成 {mid}（供应商 {c.Ps("supplier")}）");
        c.Impact($"入库 {mid}×{qty}，移动加权均价 {na}；{(c.Pb("paid") ? "已付款" : "应付账款形成")}");
    }

    private static async Task StockIssued(ApplyCtx c)
    {
        var mid = c.Ps("materialId"); var wh = c.Ps("warehouse"); var qty = c.Pd("qty"); var purpose = c.Ps("purpose");
        var b = (await c.Rows("SELECT qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", mid), ("w", wh)))[0];
        var avg = Pg.D(b["avg_cost"]); var amt = Math.Round(qty * avg, 2);
        var m = (await c.Rows("SELECT category FROM proj.materials WHERE material_id=@i", ("i", mid)))[0];
        var acc = Pg.S(m["category"]) == "goods" ? "Inventory" : "Materials";
        await c.Exec("UPDATE proj.stock_balances SET qty=qty-@q WHERE material_id=@m AND warehouse=@w", ("q", qty), ("m", mid), ("w", wh));
        await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'Out',@q,@u,@amt,@memo,@e)",
            ("id", $"SM-{c.Eid}"), ("m", mid), ("w", wh), ("q", qty), ("u", avg), ("amt", amt), ("memo", purpose), ("e", c.Evt.EventId));
        var dr = purpose == "sale" ? "CostOfSales" : purpose == "production" ? "ProductionCost" : "Expense";
        await c.Ledger(c.Period, dr, amt, 0, $"出库 {mid}×{qty}（{purpose}）");
        await c.Ledger(c.Period, acc, 0, amt, $"出库 {mid}×{qty}（{wh}）");
        c.Impact($"出库 {mid}×{qty}→{dr}，成本 {amt}（移动加权）");
    }

    private static async Task StockCounted(ApplyCtx c)
    {
        var mid = c.Ps("materialId"); var wh = c.Ps("warehouse"); var counted = c.Pd("countedQty");
        var b = (await c.Rows("SELECT qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", mid), ("w", wh)))[0];
        var diff = counted - Pg.D(b["qty"]); var avg = Pg.D(b["avg_cost"]);
        if (diff == 0) { c.Impact($"盘点 {mid}@{wh}：账实相符"); return; }
        var amt = Math.Round(Math.Abs(diff) * avg, 2);
        var m = (await c.Rows("SELECT category FROM proj.materials WHERE material_id=@i", ("i", mid)))[0];
        var acc = Pg.S(m["category"]) == "goods" ? "Inventory" : "Materials";
        await c.Exec("UPDATE proj.stock_balances SET qty=@q WHERE material_id=@m AND warehouse=@w", ("q", counted), ("m", mid), ("w", wh));
        await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'Count',@q,@u,@amt,@memo,@e)",
            ("id", $"SM-{c.Eid}"), ("m", mid), ("w", wh), ("q", diff), ("u", avg), ("amt", amt), ("memo", c.Ps("reason")), ("e", c.Evt.EventId));
        if (diff > 0) { await c.Ledger(c.Period, acc, amt, 0, $"盘盈 {mid}"); await c.Ledger(c.Period, "StockAdjust", 0, amt, $"盘盈 {mid}"); }
        else { await c.Ledger(c.Period, "StockAdjust", amt, 0, $"盘亏 {mid}"); await c.Ledger(c.Period, acc, 0, amt, $"盘亏 {mid}"); }
        c.Impact($"盘点 {mid}@{wh}：差异 {diff}，金额 {amt} 入链待查因");
    }

    private static async Task EquipmentRegistered(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.equipment(equip_id,name,type,location,status,capacity_per_day,linked_asset_id,source_event_id) VALUES(@i,@n,@t,@l,@s,@cap,@a,@e)",
            ("i", c.Ps("equipId")), ("n", c.Ps("name")), ("t", c.Ps("type")), ("l", c.Ps("location")), ("s", c.Ps("status")), ("cap", c.Pd("capacityPerDay")), ("a", c.Ps("linkedAssetId")), ("e", c.Evt.EventId));
        c.Impact($"设备 {c.Ps("name")} 登记（日产能 {c.Pd("capacityPerDay")}），可关联固资卡片");
    }

    private static async Task ProductionOrderCreated(ApplyCtx c)
    {
        await c.ExecJ("INSERT INTO proj.production_orders(order_no,product_material_id,qty,completed_qty,bom,equipment_id,planned_start,planned_end,status,source_event_id) VALUES(@o,@p,@q,0,@b,@eq,@s,@e,'Open',@ev)",
            new[] { ("b", (JsonNode?)c.P["bom"]) },
            ("o", c.Ps("orderNo")), ("p", c.Ps("productMaterialId")), ("q", c.Pd("qty")), ("eq", c.Ps("equipmentId")),
            ("s", DateOnly.Parse(c.Ps("plannedStart")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            ("e", DateOnly.Parse(c.Ps("plannedEnd")).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), ("ev", c.Evt.EventId));
        c.Impact($"生产订单 {c.Ps("orderNo")} 下达：{c.Pd("qty")} 件，BOM 与设备已绑定，完工时自动领料结转成本");
    }

    private static async Task ProductionCompleted(ApplyCtx c)
    {
        var ono = c.Ps("orderNo"); var cq = c.Pd("completedQty");
        var o = (await c.Rows("SELECT * FROM proj.production_orders WHERE order_no=@o", ("o", ono)))[0];
        decimal totalCost = 0;
        foreach (var bl in Pg.J(o["bom"]) as JsonArray ?? new JsonArray())
        {
            var mid = bl!["materialId"]!.GetValue<string>(); var need = Convert.ToDecimal(bl["qtyPer"]!.ToString()) * cq;
            var bals = await c.Rows("SELECT warehouse,qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND qty>0 ORDER BY warehouse", ("m", mid));
            var left = need;
            foreach (var b in bals)
            {
                if (left <= 0) break;
                var take = Math.Min(left, Pg.D(b["qty"])); var avg = Pg.D(b["avg_cost"]); var amt = Math.Round(take * avg, 2);
                await c.Exec("UPDATE proj.stock_balances SET qty=qty-@q WHERE material_id=@m AND warehouse=@w", ("q", take), ("m", mid), ("w", Pg.S(b["warehouse"])));
                await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'Out',@q,@u,@amt,@memo,@e)",
                    ("id", $"SM-{c.Eid}-{Pg.S(b["warehouse"])}"), ("m", mid), ("w", Pg.S(b["warehouse"])), ("q", take), ("u", avg), ("amt", amt), ("memo", $"生产领料 {ono}"), ("e", c.Evt.EventId));
                await c.Ledger(c.Period, "ProductionCost", amt, 0, $"生产领料 {ono}：{mid}×{take}");
                await c.Ledger(c.Period, "Materials", 0, amt, $"生产领料 {ono}：{mid}×{take}");
                totalCost += amt; left -= take;
            }
        }
        var pm = Pg.S(o["product_material_id"]);
        var unitCost = cq > 0 ? Math.Round(totalCost / cq, 4) : 0;
        var wh = c.Ps("warehouse");
        var old = await c.Rows("SELECT qty,avg_cost FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", pm), ("w", wh));
        var oq = old.Count > 0 ? Pg.D(old[0]["qty"]) : 0; var oa = old.Count > 0 ? Pg.D(old[0]["avg_cost"]) : 0;
        var na = oq + cq > 0 ? Math.Round((oq * oa + totalCost) / (oq + cq), 4) : unitCost;
        await c.Exec("INSERT INTO proj.stock_balances(material_id,warehouse,qty,avg_cost) VALUES(@m,@w,@q,@a) ON CONFLICT (material_id,warehouse) DO UPDATE SET qty=proj.stock_balances.qty+@q, avg_cost=@a",
            ("m", pm), ("w", wh), ("q", cq), ("a", na));
        await c.Exec("INSERT INTO proj.stock_moves(move_id,material_id,warehouse,direction,qty,unit_cost,amount,memo,source_event_id) VALUES(@id,@m,@w,'In',@q,@u,@amt,@memo,@e)",
            ("id", $"SM-{c.Eid}-FG"), ("m", pm), ("w", wh), ("q", cq), ("u", unitCost), ("amt", totalCost), ("memo", $"完工入库 {ono}"), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "Inventory", totalCost, 0, $"完工入库 {ono}×{cq}");
        await c.Ledger(c.Period, "ProductionCost", 0, totalCost, $"完工结转生产成本 {ono}");
        var done = Pg.D(o["completed_qty"]) + cq >= Pg.D(o["qty"]);
        await c.Exec("UPDATE proj.production_orders SET completed_qty=completed_qty+@q, status=@s WHERE order_no=@o", ("q", cq), ("s", done ? "Done" : "Open"), ("o", ono));
        c.Impact($"完工 {ono}×{cq}：领料成本 {totalCost} 结转产成品，单位成本 {unitCost}；订单{(done ? "关闭" : "部分完工")}");
    }
}
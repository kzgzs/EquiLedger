// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>扫码接入网关（统一设备适配协议的服务端）：设备 HMAC 认证→条码验真→查重→异常预检→自动事件或隔离。
/// 适配器（USB/串口/网络/厂商SDK/摄像头）在企业现场以桥接器实现本协议，系统不绑定任何品牌。</summary>
public static class ScanGateway
{
    public record Outcome(string Result, string LogId, List<string> EventIds, string? Error = null);

    public static string Sign(string secret, string ts, string barcode)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(ts + "|" + barcode))).ToLowerInvariant();
    }

    /// <summary>业务包类型→自动业务事件映射（payload 即业务包全量记录）。</summary>
    public static List<(string type, JsonObject payload)> BusinessEvents(string kind, JsonObject p)
    {
        JsonObject Pick(params string[] keys)
        {
            var o = new JsonObject();
            foreach (var k in keys) if (p[k] != null) o[k] = p[k]!.DeepClone();
            return o;
        }
        return kind switch
        {
            "ProductionPack" => new() { ("ProductionCompleted", Pick("orderNo", "completedQty", "warehouse")) },
            "WarehouseIn" => new() { ("StockReceived", Pick("materialId", "warehouse", "qty", "unitCost")) },
            "WarehouseOut" => new() { ("StockIssued", Pick("materialId", "warehouse", "qty", "purpose")) },
            "WarehouseCount" => new() { ("StockCounted", Pick("materialId", "warehouse", "countedQty")) },
            "QualityCheck" => new() { ("QualityInspected", (JsonObject)p.DeepClone()) },
            "Logistics" => new() { ("LogisticsScanned", (JsonObject)p.DeepClone()) },
            "Sales" => new() { ("SalesScanned", (JsonObject)p.DeepClone()) },
            _ => new()
        };
    }

    private static async Task<string> Log(Db db, string? device, string barcode, string result, string detail, List<string> evs)
    {
        var id = $"SL-{Guid.NewGuid():N}"[..20];
        await using var conn = db.App();
        await using var cmd = conn.Cmd("INSERT INTO proj.scan_logs(log_id,device_id,barcode,result,detail,event_ids,at) VALUES(@l,@d,@b,@r,@de,@e,@at)", null,
            ("l", id), ("d", (object?)device ?? DBNull.Value), ("b", barcode), ("r", result), ("de", detail), ("at", DateTime.UtcNow));
        cmd.Parameters.Add(Pg.Json("e", new JsonArray(evs.Select(x => (JsonNode)x).ToArray())));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public static async Task<Outcome> Process(Db db, EventStore store, string deviceId, string ts, string sign, string barcode, string actor)
    {
        await using var conn = db.App();
        var dv = (await conn.Rows("SELECT secret,enabled FROM proj.devices WHERE device_id=@d", ("d", deviceId))).FirstOrDefault();
        if (dv is null || !(bool)dv["enabled"]!)
            return new Outcome("DeviceDenied", await Log(db, deviceId, barcode, "DeviceDenied", "设备未注册或停用", new()), new(), "设备未注册或停用");
        if (!long.TryParse(ts, out var tss) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - tss) > 300)
            return new Outcome("BadTimestamp", await Log(db, deviceId, barcode, "BadTimestamp", "时间戳超窗", new()), new(), "时间戳超窗（±300s）");
        if (Sign(Pg.S(dv["secret"]), ts, barcode) != sign)
            return new Outcome("BadSign", await Log(db, deviceId, barcode, "BadSign", "HMAC 校验失败", new()), new(), "设备签名校验失败");
        var parts = barcode.Split('|');
        if (parts.Length != 4 || parts[0] != "EQL1")
            return new Outcome("InvalidBarcode", await Log(db, deviceId, barcode, "InvalidBarcode", "条码格式无效", new()), new(), "条码格式无效");
        var (kind, packetId, check) = (parts[1], parts[2], parts[3]);
        var pk = (await conn.Rows("SELECT kind,ref_id,payload,salt,check_code,status FROM proj.scan_packets WHERE packet_id=@p", ("p", packetId))).FirstOrDefault();
        if (pk is null)
            return new Outcome("Unknown", await Log(db, deviceId, barcode, "Unknown", "业务包不存在", new()), new(), "业务包不存在");
        var expect = EquiLedger.Core.HashChain.Sha256($"{Pg.S(pk["salt"])}|{Pg.S(pk["kind"])}|{Pg.S(pk["ref_id"])}|{Projection.Canon(Pg.J(pk["payload"]))}")[..8];
        if (expect != check || Pg.S(pk["kind"]) != kind)
            return new Outcome("InvalidCheck", await Log(db, deviceId, barcode, "InvalidCheck", "校验码不符（疑似伪造/篡改）", new()), new(), "数据校验码不符");
        if (Pg.S(pk["status"]) == "Consumed")
            return new Outcome("Duplicate", await Log(db, deviceId, barcode, "Duplicate", "重复扫描", new()), new(), "重复扫描：同一业务包只计一次");
        if (Pg.S(pk["status"]) != "Ready")
            return new Outcome("Quarantined", await Log(db, deviceId, barcode, "Quarantined", "业务包隔离中", new()), new(), "业务包隔离中，待人工复核");
        var payload = (JsonObject)Pg.J(pk["payload"]);
        // 异常预检：错误数据隔离，不入正式账簿
        string? reason = null;
        if (kind == "ProductionPack")
        {
            var o = (await conn.Rows("SELECT qty,completed_qty FROM proj.production_orders WHERE order_no=@o", ("o", payload["orderNo"]?.ToString() ?? ""))).FirstOrDefault();
            if (o is null) reason = "工单不存在";
            else if (ApplyCtx.JsonNum(payload["completedQty"]) <= 0 || ApplyCtx.JsonNum(payload["completedQty"]) > Pg.D(o["qty"]) - Pg.D(o["completed_qty"])) reason = "合格数量超工单剩余";
            else if (string.IsNullOrEmpty(payload["employeeId"]?.ToString())) reason = "缺员工编号";
        }
        else if (kind is "WarehouseOut" or "Sales")
        {
            var b = (await conn.Rows("SELECT qty FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", payload["materialId"]?.ToString() ?? ""), ("w", payload["warehouse"]?.ToString() ?? ""))).FirstOrDefault();
            if (b is null || ApplyCtx.JsonNum(payload["qty"]) <= 0 || ApplyCtx.JsonNum(payload["qty"]) > Pg.D(b["qty"])) reason = "出库数量超库存";
        }
        else if (kind == "WarehouseIn" && ApplyCtx.JsonNum(payload["qty"]) <= 0) reason = "入库数量非正";
        var evs = new List<string>();
        var evHash = EquiLedger.Core.HashChain.Sha256($"{deviceId}|{barcode}|{ts}");
        var evidence = new JsonArray(new JsonObject { ["name"] = "scan-gateway", ["hash"] = evHash });
        if (reason != null)
        {
            var q = await store.Append(new EventStore.Draft("ScanQuarantined", actor, null, evidence,
                new JsonObject { ["packetId"] = packetId, ["deviceId"] = deviceId, ["reason"] = reason }));
            evs.Add(q.EventId.ToString());
            return new Outcome("Quarantined", await Log(db, deviceId, barcode, "Quarantined", reason, evs), evs, reason);
        }
        var acc = await store.Append(new EventStore.Draft("ScanAccepted", actor, null, evidence,
            new JsonObject { ["packetId"] = packetId, ["deviceId"] = deviceId, ["kind"] = kind }));
        evs.Add(acc.EventId.ToString());
        foreach (var (type, pl) in BusinessEvents(kind, payload))
        {
            var e = await store.Append(new EventStore.Draft(type, actor, null, evidence, pl));
            evs.Add(e.EventId.ToString());
        }
        return new Outcome("Accepted", await Log(db, deviceId, barcode, "Accepted", "自动入账", evs), evs);
    }

    /// <summary>人工复核放行：Approved 后按业务包快照补录业务事件（隔离期间不入账）。</summary>
    public static async Task<Outcome> ResolveReview(Db db, EventStore store, string reviewId, string result, string actor)
    {
        await using var conn = db.App();
        var rv = (await conn.Rows("SELECT packet_id,kind,payload,status FROM proj.scan_reviews WHERE review_id=@r", ("r", reviewId))).FirstOrDefault();
        if (rv is null || Pg.S(rv["status"]) != "Open") return new Outcome("BadStatus", "", new(), "复核单不存在或已处理");
        var evidence = new JsonArray(new JsonObject { ["name"] = "scan-review", ["hash"] = EquiLedger.Core.HashChain.Sha256(reviewId + result) });
        var e = await store.Append(new EventStore.Draft("ScanReviewResolved", actor, null, evidence,
            new JsonObject { ["reviewId"] = reviewId, ["result"] = result }));
        var evs = new List<string> { e.EventId.ToString() };
        if (result == "Approved")
            foreach (var (type, pl) in BusinessEvents(Pg.S(rv["kind"]), (JsonObject)Pg.J(rv["payload"])))
            {
                var be = await store.Append(new EventStore.Draft(type, actor, null, evidence, pl));
                evs.Add(be.EventId.ToString());
            }
        return new Outcome(result, await Log(db, null, Pg.S(rv["packet_id"]), "Review" + result, "人工复核", evs), evs);
    }
}
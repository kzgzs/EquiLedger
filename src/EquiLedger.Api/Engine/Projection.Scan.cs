// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>扫码自动采集：业务包（条码=唯一业务标识+校验码）→设备认证扫描→自动入账；
/// 自动采集不等于自动相信：重复扫描拒绝、异常隔离、人工复核、全程留痕。
/// 部门自动化：生产计件/仓库出入盘/质检(合格自动贴码)/物流/销售扫码。</summary>
public static partial class Projection
{
    /// <summary>规范化 JSON（键按序号排序）：jsonb 会重排键序，校验码必须基于规范化串才能重放一致。</summary>
    public static string Canon(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => System.Text.Json.JsonSerializer.Serialize(kv.Key) + ":" + Canon(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        null => "null",
        _ => n.ToJsonString()
    };

    private static async Task MakePacket(ApplyCtx c, string packetId, string kind, string refId, JsonObject payload)
    {
        var salt = EquiLedger.Core.HashChain.Sha256($"{packetId}|{kind}|{refId}|{c.Eid}")[..16];
        var check = EquiLedger.Core.HashChain.Sha256($"{salt}|{kind}|{refId}|{Canon(payload)}")[..8];
        var barcode = $"EQL1|{kind}|{packetId}|{check}";
        await c.ExecJ(@"INSERT INTO proj.scan_packets(packet_id,kind,ref_id,payload,salt,check_code,barcode,status,source_event_id)
            VALUES(@p,@k,@r,@pl,@s,@c,@b,'Ready',@e)",
            new[] { ("pl", (JsonNode)payload) },
            ("p", packetId), ("k", kind), ("r", refId), ("s", salt), ("c", check), ("b", barcode), ("e", c.Evt.EventId));
    }

    private static async Task DeviceRegistered(ApplyCtx c)
    {
        // 密钥服务端生成（不入事件 payload），操作员凭 /api/device-secret 一次性领取配置桥接器
        var secret = EquiLedger.Core.HashChain.Sha256($"{c.Ps("deviceId")}|{c.Eid}|device-secret")[..32];
        await c.Exec("INSERT INTO proj.devices(device_id,name,adapter,department,secret,enabled,source_event_id) VALUES(@d,@n,@a,@dep,@s,TRUE,@e)",
            ("d", c.Ps("deviceId")), ("n", c.Ps("name")), ("a", c.Ps("adapter")), ("dep", c.Ps("department")), ("s", secret), ("e", c.Evt.EventId));
        c.Impact($"采集设备注册 {c.Ps("deviceId")}（适配器 {c.Ps("adapter")}·{c.Ps("department")}）：HMAC 身份认证，未注册设备上传一律拒绝");
    }

    private static async Task ScanPacketCreated(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("packetId")) ? $"PK-{c.Eid}" : c.Ps("packetId");
        await MakePacket(c, id, c.Ps("kind"), c.Ps("refId"), c.P["payload"] as JsonObject ?? new JsonObject());
        c.Impact($"业务包 {id}（{c.Ps("kind")}·{c.Ps("refId")}）已生成条码：扫码后系统查全量记录并验真/查重，条码本身不含明细");
    }

    private static async Task ScanAccepted(ApplyCtx c)
    {
        var id = c.Ps("packetId");
        var pk = (await c.Rows("SELECT kind,payload FROM proj.scan_packets WHERE packet_id=@p", ("p", id)))[0];
        await c.Exec("UPDATE proj.scan_packets SET status='Consumed' WHERE packet_id=@p", ("p", id));
        if (Pg.S(pk["kind"]) == "ProductionPack")
        {
            var pl = Pg.J(pk["payload"]);
            await c.Exec("INSERT INTO proj.piece_records(id,employee_id,process,qty,period,order_no,packet_id,source_event_id) VALUES(@i,@emp,@pr,@q,@per,@o,@p,@e)",
                ("i", $"PC-{c.Eid}"), ("emp", pl["employeeId"]?.ToString() ?? ""), ("pr", pl["process"]?.ToString() ?? ""),
                ("q", ApplyCtx.JsonNum(pl["completedQty"])), ("per", c.Period), ("o", pl["orderNo"]?.ToString() ?? ""), ("p", id), ("e", c.Evt.EventId));
        }
        c.Impact($"扫码受理 {id}（设备 {c.Ps("deviceId")}）：验真+查重通过，自动进入业务事件与计件台账；重复扫描将被拒绝");
    }

    private static async Task ScanQuarantined(ApplyCtx c)
    {
        var id = c.Ps("packetId");
        var pk = (await c.Rows("SELECT kind,payload FROM proj.scan_packets WHERE packet_id=@p", ("p", id)))[0];
        await c.Exec("UPDATE proj.scan_packets SET status='Quarantined' WHERE packet_id=@p", ("p", id));
        await c.ExecJ("INSERT INTO proj.scan_reviews(review_id,packet_id,kind,payload,reason,status,source_event_id) VALUES(@r,@p,@k,@pl,@reason,'Open',@e)",
            new[] { ("pl", Pg.J(pk["payload"])) },
            ("r", $"RV-{c.Eid}"), ("p", id), ("k", Pg.S(pk["kind"])), ("reason", c.Ps("reason")), ("e", c.Evt.EventId));
        await c.Todo("ScanReview", $"扫码异常复核：{id}（{c.Ps("reason")}）", new JsonObject { ["packetId"] = id, ["reason"] = c.Ps("reason") });
        c.Impact($"扫码异常隔离 {id}：{c.Ps("reason")}；错误数据不入正式账簿，待人工复核");
    }

    private static async Task ScanReviewResolved(ApplyCtx c)
    {
        var id = c.Ps("reviewId"); var ok = c.Ps("result") == "Approved";
        var rv = (await c.Rows("SELECT packet_id FROM proj.scan_reviews WHERE review_id=@r", ("r", id)))[0];
        await c.Exec("UPDATE proj.scan_reviews SET status='Resolved', result=@res, reviewer=@rev WHERE review_id=@r", ("res", c.Ps("result")), ("rev", c.Evt.ActorId), ("r", id));
        if (ok)
        {
            var pid = Pg.S(rv["packet_id"]);
            await c.Exec("UPDATE proj.scan_packets SET status='Consumed' WHERE packet_id=@p", ("p", pid));
            var pk = (await c.Rows("SELECT kind,payload FROM proj.scan_packets WHERE packet_id=@p", ("p", pid)))[0];
            if (Pg.S(pk["kind"]) == "ProductionPack")
            {
                var pl = Pg.J(pk["payload"]);
                await c.Exec("INSERT INTO proj.piece_records(id,employee_id,process,qty,period,order_no,packet_id,source_event_id) VALUES(@i,@emp,@pr,@q,@per,@o,@p,@e)",
                    ("i", $"PC-{c.Eid}"), ("emp", pl["employeeId"]?.ToString() ?? ""), ("pr", pl["process"]?.ToString() ?? ""),
                    ("q", ApplyCtx.JsonNum(pl["completedQty"])), ("per", c.Period), ("o", pl["orderNo"]?.ToString() ?? ""), ("p", pid), ("e", c.Evt.EventId));
            }
        }
        c.Impact($"扫码复核 {id}：{(ok ? "通过，放行入账" : "驳回，数据作废")}（{c.Evt.ActorId}）");
    }

    private static async Task QualityInspected(ApplyCtx c)
    {
        var id = string.IsNullOrEmpty(c.Ps("qcId")) ? $"QC-{c.Eid}" : c.Ps("qcId");
        await c.Exec("INSERT INTO proj.quality_records(qc_id,batch,product,inspector,standard,result,qty,order_no,source_event_id) VALUES(@i,@b,@p,@ins,@s,@r,@q,@o,@e)",
            ("i", id), ("b", c.Ps("batch")), ("p", c.Ps("product")), ("ins", c.Ps("inspector")), ("s", c.Ps("standard")),
            ("r", c.Ps("result")), ("q", c.Pd("qty")), ("o", c.Ps("orderNo")), ("e", c.Evt.EventId));
        if (c.Ps("result") == "Pass")
        {
            // 质检合格→自动打包贴码：生成生产业务包，条码关联员工/工单/批次/合格数/质检记录
            var pl = new JsonObject
            {
                ["orderNo"] = c.Ps("orderNo"), ["completedQty"] = c.Pd("qty"), ["warehouse"] = c.Ps("warehouse"),
                ["employeeId"] = c.Ps("employeeId"), ["process"] = c.Ps("process"), ["batch"] = c.Ps("batch"), ["qcId"] = id
            };
            await MakePacket(c, $"PK-{c.Eid}", "ProductionPack", c.Ps("orderNo"), pl);
            c.Impact($"质检合格 {id}：自动打包贴码（PK-{c.Eid}），扫码即登记产量并进入计件工资");
        }
        else
        {
            await c.Todo("QualityAnomaly", $"质检不合格异常处理：{id}（{c.Ps("product")}×{c.Pd("qty")}）", new JsonObject { ["qcId"] = id, ["batch"] = c.Ps("batch") });
            c.Impact($"质检不合格 {id}：自动进入异常处理流程，不生成贴码、不计产量");
        }
    }

    private static async Task LogisticsScanned(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.logistics_scans(id,tracking,stage,batch,status,source_event_id) VALUES(@i,@t,@s,@b,@st,@e)",
            ("i", $"LG-{c.Eid}"), ("t", c.Ps("tracking")), ("s", c.Ps("stage")), ("b", c.Ps("batch")),
            ("st", c.Ps("stage") switch { "Handover" => "已交接", "Ship" => "已发货", _ => "已收货" }), ("e", c.Evt.EventId));
        c.Impact($"物流扫码 {c.Ps("tracking")}：{c.Ps("stage")} 自动登记交接/发货/收货状态");
    }

    private static async Task SalesScanned(ApplyCtx c)
    {
        var emp = c.Ps("employeeId");
        var rule = await c.ActiveRule("Commission.Rate", emp);
        await c.Exec(@"INSERT INTO proj.sales_orders(order_id,customer,amount,employee_id,commission_rule_id,commission_rule_version,confirmed_at,source_event_id)
            VALUES(@o,@cu,@a,@emp,@rid,@rv,@t,@e)",
            ("o", c.Ps("orderId")), ("cu", c.Ps("customer")), ("a", c.Pd("amount")), ("emp", emp),
            ("rid", (object?)ApplyCtx.RuleRef(rule) ?? DBNull.Value),
            ("rv", rule?["__version"] is JsonValue vv ? vv.GetValue<int>() : (object)DBNull.Value),
            ("t", c.Evt.OccurredAt.UtcDateTime), ("e", c.Evt.EventId));
        // 赊销口径：应收+价税分离收入+销项税（与 RevenueRecorded 同算法，借方挂 AR）
        var amount = c.Pd("amount");
        var before = Pg.D(await c.Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='Revenue' AND period=@p", ("p", c.Period)));
        var at = DateOnly.FromDateTime(c.Evt.OccurredAt.UtcDateTime);
        var vatAfter = TaxCalc.VatSmallScale(before + amount, at);
        var vatDelta = vatAfter.Amount - TaxCalc.VatSmallScale(before, at).Amount;
        await c.Ledger(c.Period, "AR", amount, 0, $"销售扫码应收 {c.Ps("customer")}·{c.Ps("orderId")}");
        await c.Ledger(c.Period, "Revenue", 0, amount - vatDelta, $"销售扫码收入 {c.Ps("orderId")}（价税分离）");
        if (vatDelta != 0) await c.Ledger(c.Period, "TaxPayable", 0, vatDelta, $"增值税计提 {c.Period}");
        await c.Calc("VAT", c.Period, vatAfter);
        c.P["purpose"] = "销售扫码出库";
        await StockIssued(c);
        c.Impact($"销售扫码 {c.Ps("orderId")}：销售记录+收入/应收+库存扣减+提成规则关联一次完成");
    }
}
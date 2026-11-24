// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>
/// 外部电商集成：WooCommerce「订单完成」webhook → 自动写入收入确认事件。
/// 安全：HMAC-SHA256 签名校验（X-WC-Webhook-Signature，与 Woo webhook secret 一致）；
/// 治理：归属唯一操作员账号、凭 webhook 原文哈希为依据、按订单号幂等去重；
/// 金额/税额/分红候选稿由既有引擎自动联动。
/// </summary>
public static class WooCommerce
{
    public static string Sign(string body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private static decimal Num(JsonNode? n) => n is not null && decimal.TryParse(n.ToString(), out var d) ? d : 0m;
    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    public static async Task<(bool Ok, string Msg)> Ingest(Db db, EventStore store, string body, string? signature, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return (false, "集成未启用：请配置环境变量 EQUI_WOO_SECRET（与 Woo webhook secret 一致）");
        if (signature != Sign(body, secret)) return (false, "签名不匹配，拒绝写入");
        var j = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
        if (Str(j["status"]) != "completed") return (false, "仅接受成交完成订单（status=completed）");
        var orderId = j["id"]?.ToString() ?? "";
        if (orderId.Length == 0) return (false, "缺少订单号");
        var externalId = "woo-" + orderId;
        await using var conn = db.App();
        var dup = Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM events.event_log WHERE payload->>'externalId'=@x", ("x", externalId)));
        if (dup > 0) return (true, "重复订单，幂等忽略");
        var amount = Num(j["total"]);
        if (amount <= 0) return (false, "订单金额无效");
        var op = await conn.Scalar("SELECT username FROM proj.accounts WHERE role='Operator' AND enabled=TRUE AND frozen=FALSE ORDER BY username LIMIT 1") as string;
        if (op is null) return (false, "无可用操作员账号");
        var customer = Str(j["billing"]?["first_name"]) + Str(j["billing"]?["last_name"]);
        var items = new JsonArray();
        if (j["line_items"] is JsonArray la)
            foreach (var li in la.OfType<JsonObject>())
                items.Add(new JsonObject { ["name"] = Str(li["name"]), ["qty"] = li["quantity"]?.DeepClone(), ["total"] = Str(li["total"]) });
        var payload = new JsonObject
        {
            ["externalId"] = externalId,
            ["amount"] = amount,
            ["customer"] = customer.Length > 0 ? customer : "WooCommerce客户",
            ["channel"] = "WooCommerce",
            ["items"] = items
        };
        var evidence = new JsonArray(new JsonObject { ["name"] = "WooCommerce订单完成webhook", ["hash"] = HashChain.Sha256(body) });
        await store.Append(new EventStore.Draft("RevenueRecorded", op, null, evidence, payload));
        return (true, $"已入账：订单 {orderId}，金额 {amount}，税额与分红候选稿已自动联动");
    }
}
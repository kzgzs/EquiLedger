// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;

namespace EquiLedger.Engine;

/// <summary>商业授权状态（不入事件链、不参与验真；授权是商业层，账本可信层独立）。</summary>
public record LicenseState(string Status, DateTimeOffset? ExpiresAt, DateTimeOffset? LastOkAt, string Message);

/// <summary>授权闸门：开关默认关（社区模式零影响）；开启且无效时仅冻结新增写入，读取/验真/证据包导出始终可用——客户已购期间的账本永远可查可证。</summary>
public static class LicenseGate
{
    public static bool Enforce { get; set; } = Environment.GetEnvironmentVariable("EQUI_LICENSE_ENFORCE") == "1";
    public static LicenseState Current { get; set; } = new("unconfigured", null, null, "未配置授权校验（EQUI_LICENSE_KEY 为空），社区模式运行");
    public static bool BlocksWrites => Enforce && Current.Status is not ("valid" or "grace" or "unconfigured");
}

/// <summary>授权判定纯函数：服务器明确无效/过期/篡改/暂停不给宽限；仅网络失败享离线宽限（默认 7 天，自最近一次成功验权起算）。</summary>
public static class LicensePolicy
{
    public static LicenseState Evaluate(string? serverStatus, DateTimeOffset? expiresAt, DateTimeOffset? cachedLastOk, DateTimeOffset now, int graceDays)
    {
        if (serverStatus == "valid") return new("valid", expiresAt, now, "授权有效");
        if (serverStatus is "expired" or "invalid" or "tampered" or "suspended")
            return new(serverStatus, expiresAt, cachedLastOk, serverStatus switch
            {
                "expired" => "授权已过期，请续订后恢复写入",
                "tampered" => "检测到代码篡改，授权已被平台暂停",
                "suspended" => "平台已暂停本部署授权",
                _ => "授权无效"
            });
        if (cachedLastOk is { } t && now - t <= TimeSpan.FromDays(graceDays))
            return new("grace", expiresAt, t, $"授权服务器不可达，离线宽限中（最近成功验权 {t:yyyy-MM-dd HH:mm} UTC）");
        return new("offline_expired", expiresAt, cachedLastOk, "授权服务器不可达且超出离线宽限，新增写入已冻结");
    }
}

/// <summary>LicenseOS v2 客户端：POST /api/v2/license/check（X-API-Key 头）。HTTP 发送可注入，便于 mock 自测。</summary>
public class LicenseClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    public LicenseClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        => _send = send ?? ((m, ct) => Http.SendAsync(m, ct));

    public async Task<(string? status, DateTimeOffset? expires)> CheckAsync(string server, string apiKey, string licenseKey, string domain, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, server.TrimEnd('/') + "/api/v2/license/check");
        req.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        req.Content = new StringContent(new JsonObject { ["license_key"] = licenseKey, ["domain"] = domain }.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        var resp = await _send(req, ct);
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var status = node?["status"]?.GetValue<string>();
        DateTimeOffset? exp = null;
        foreach (var k in new[] { "expires_at", "expiry_date", "valid_until" })
        {
            var es = node?[k]?.GetValue<string>();
            if (es != null && DateTimeOffset.TryParse(es, out var e)) { exp = e; break; }
        }
        return (status, exp);
    }
}
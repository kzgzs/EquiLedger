using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EquiLedger.Core;

/// <summary>规范化 JSON（键排序、无空白），保证哈希与复算确定性。</summary>
public static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                if (val.TryGetValue(out decimal d)) sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (val.TryGetValue(out double db)) sb.Append(((decimal)db).ToString(System.Globalization.CultureInfo.InvariantCulture));
                else sb.Append(val.ToJsonString());
                break;
        }
    }

    public static JsonNode Parse(string json) => JsonNode.Parse(json) ?? new JsonObject();
}

public static class HashChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";
    /// <summary>开发者署名（防篡改层之一：写入证据包/锚定/工作群推送，随账本永久留存；移除署名违反商业授权）。</summary>
    public const string Attribution = "玄铁栈道EquiLedger © 2026 XTZDDJ | 微信 XTZDDJ | QQ 248197309/68822685 | admin@djkz.top";

    public static string Sha256(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    /// <summary>事件哈希 = SHA256(prevHash | canonical(核心字段))</summary>
    public static string ComputeEventHash(string prevHash, string eventId, string eventType,
        string actorId, DateTimeOffset occurredAt, string? resolutionNo,
        JsonNode evidence, JsonNode payload, JsonNode ruleRefs)
    {
        var core = new JsonObject
        {
            ["event_id"] = eventId,
            ["event_type"] = eventType,
            ["actor_id"] = actorId,
            ["occurred_at"] = occurredAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["resolution_no"] = resolutionNo,
            ["evidence"] = evidence?.DeepClone(),
            ["payload"] = payload?.DeepClone(),
            ["rule_refs"] = ruleRefs?.DeepClone()
        };
        return Sha256(prevHash + "|" + CanonicalJson.Serialize(core));
    }
}

public static class Crypto
{
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 2) return false;
        var salt = Convert.FromBase64String(parts[0]);
        var expect = Convert.FromBase64String(parts[1]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expect, actual);
    }

    /// <summary>生成签名密钥对（ECDSA P-256）。MVP 服务端代签；生产接硬件密钥/CA。</summary>
    public static (string pub, string priv) GenerateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()),
                Convert.ToBase64String(ec.ExportPkcs8PrivateKey()));
    }

    public static string Sign(string privKeyB64, string message)
    {
        using var ec = ECDsa.Create();
        ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privKeyB64), out _);
        return Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    public static bool Verify(string pubKeyB64, string message, string sigB64)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKeyB64), out _);
            return ec.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(sigB64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
namespace EquiLedger.Storage;

/// <summary>设立后一个月内的"推倒重来"通道（仅操作员，窗口外永久关闭）。窗口外一切变更只能走事件。</summary>
public static class Maintenance
{
    public static async Task<DateTimeOffset?> FoundedAt(Db db)
    {
        await using var conn = db.App();
        var v = await conn.Scalar("SELECT occurred_at FROM events.event_log WHERE event_type='CompanyFounded' ORDER BY seq LIMIT 1");
        return v is DateTime dt ? new DateTimeOffset(dt, TimeSpan.Zero) : null;
    }

    public static bool WithinGraceWindow(DateTimeOffset? foundedAt) =>
        foundedAt != null && DateTimeOffset.UtcNow - foundedAt.Value <= TimeSpan.FromDays(30);

    /// <summary>整库清空并重建地基（DROP SCHEMA 绕过 Append-Only 触发器，仅迁移账号可执行）。</summary>
    public static async Task ResetAll(Db db, string evidenceDir)
    {
        await using (var owner = db.Owner())
        {
            await owner.Exec("DROP SCHEMA IF EXISTS proj CASCADE");
            await owner.Exec("DROP SCHEMA IF EXISTS events CASCADE");
        }
        await db.InitAsync();
        await Engine.LegalSeed.EnsureAsync(db);
        if (Directory.Exists(evidenceDir))
            foreach (var f in Directory.GetFiles(evidenceDir)) File.Delete(f);
    }
}
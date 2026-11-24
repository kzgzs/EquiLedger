// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Storage;
using Npgsql;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A2/A8：不可篡改五层防线 + 证据包。</summary>
public class AuditTests : IAsyncLifetime
{
    private readonly Fixture _f = new();
    public Task InitializeAsync() => _f.InitializeAsync();
    public Task DisposeAsync() => _f.DisposeAsync();

    [Fact]
    public async Task A2_EventTable_Rejects_Update_Even_For_Owner()
    {
        await _f.Found();
        await using var owner = _f.Db.Owner();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            await owner.Exec("UPDATE events.event_log SET payload='{}' WHERE seq=1"));
        Assert.Contains("APPEND-ONLY", ex.Message);
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await owner.Exec("DELETE FROM events.event_log WHERE seq=1"));
    }

    [Fact]
    public async Task A2_AppRole_Has_No_Update_Grant()
    {
        await _f.Found();
        await using var app = _f.Db.App();
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await app.Exec("UPDATE events.event_log SET payload='{}' WHERE seq=1"));
    }

    [Fact]
    public async Task A2_ProjectionTamper_Detected_Then_Rebuilt()
    {
        await _f.Found();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 600000 }, Fixture.Ev("银行回单"));
        // 模拟 DBA 直改投影
        await using (var owner = _f.Db.Owner())
            await owner.Exec("UPDATE proj.ledger SET debit=debit+1 WHERE account='Bank'");
        var report = await _f.Verifier.Verify();
        Assert.False(report.Ok);
        Assert.Contains(report.Diffs, d => d.Table == "ledger");
        // 校验器已从事件流重建真值 → 再次验真通过
        var report2 = await _f.Verifier.Verify();
        Assert.True(report2.Ok, string.Join(";", report2.ChainErrors));
        Assert.Equal(600000m, await _f.ScalarD("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='Bank'"));
    }

    [Fact]
    public async Task A8_EvidencePackage_Signatures_Verify()
    {
        await _f.Found();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 100000 }, Fixture.Ev("银行回单"));
        await _f.Store.Anchor("test-anchor");
        var pkg = await _f.Verifier.EvidencePackage();
        var events = (JsonArray)pkg["events"]!;
        var accounts = (JsonArray)pkg["accounts"]!;
        var pub = accounts.First(a => a!["userId"]!.GetValue<string>() == "op")!["publicKey"]!.GetValue<string>();
        var opEvents = events.Where(e => e!["actor"]!.GetValue<string>() == "op").ToList();
        Assert.NotEmpty(opEvents);
        foreach (var e in opEvents)
            Assert.True(Crypto.Verify(pub, e!["hash"]!.GetValue<string>(), e["signature"]!.GetValue<string>()));
        Assert.NotEmpty((JsonArray)pkg["anchors"]!);
        // 哈希链独立重算
        var prev = HashChain.Genesis;
        foreach (var e in events)
        {
            var expect = HashChain.ComputeEventHash(prev, e!["eventId"]!.GetValue<string>(), e["type"]!.GetValue<string>(),
                e["actor"]!.GetValue<string>(), DateTimeOffset.Parse(e["occurredAt"]!.GetValue<string>()),
                e["resolutionNo"]?.GetValue<string>(), e["evidence"]!, e["payload"]!, e["ruleRefs"]!);
            Assert.Equal(expect, e["hash"]!.GetValue<string>());
            prev = expect;
        }
    }

    [Fact]
    public async Task Replay_Deterministic_After_Many_Events()
    {
        await _f.Found();
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "alice", ["amount"] = 600000 }, Fixture.Ev("回单"));
        await _f.Submit("CapitalPaidIn", "op", null, new JsonObject { ["partyId"] = "bob", ["amount"] = 400000 }, Fixture.Ev("回单"));
        await _f.Submit("RevenueRecorded", "op", null, new JsonObject { ["amount"] = 500000, ["customer"] = "X" }, Fixture.Ev("发票"));
        await _f.Submit("ExpenseRecorded", "op", null, new JsonObject { ["amount"] = 2000, ["category"] = "办公", ["payee"] = "供应商A" });
        var r1 = await _f.Verifier.Verify();
        Assert.True(r1.Ok, string.Join(";", r1.ChainErrors) + "|" + string.Join(",", r1.Diffs.Select(d => $"{d.Table}:{d.Missing}/{d.Extra}")));
        var r2 = await _f.Verifier.Verify();
        Assert.True(r2.Ok);
    }
}
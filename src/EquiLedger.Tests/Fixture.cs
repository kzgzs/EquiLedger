// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Engine;
using EquiLedger.Governance;
using EquiLedger.Storage;
using Npgsql;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>每个测试类独享一个 PostgreSQL 数据库（真实 Append-Only 权限与触发器）。</summary>
public class Fixture : IAsyncLifetime
{
    public Db Db = null!;
    public EventStore Store = null!;
    public Verifier Verifier = null!;
    private string _dbName = "";
    private string _host = Environment.GetEnvironmentVariable("EQUI_TEST_PG_HOST") ?? "localhost";
    private string _port = Environment.GetEnvironmentVariable("EQUI_TEST_PG_PORT") ?? "5433";
    private string _adminCs => $"Host={_host};Port={_port};Database=postgres;Username=equi;Password=equi_owner_pwd";

    public async Task InitializeAsync()
    {
        _dbName = "test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var c = new NpgsqlConnection(_adminCs))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_dbName} OWNER equi", c);
            await cmd.ExecuteNonQueryAsync();
        }
        var ownerCs = $"Host={_host};Port={_port};Database={_dbName};Username=equi;Password=equi_owner_pwd";
        var appCs = $"Host={_host};Port={_port};Database={_dbName};Username=equi_app;Password=equi_app_pwd";
        var initSql = Environment.GetEnvironmentVariable("EQUI_INIT_SQL") ?? FindInitSql();
        Db = new Db(appCs, ownerCs, initSql);
        await Db.InitAsync();
        await LegalSeed.EnsureAsync(Db);
        Store = new EventStore(Db);
        Verifier = new Verifier(Db, Store);
    }

    private static string FindInitSql()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var p = Path.Combine(dir.FullName, "db", "init.sql");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到 db/init.sql");
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        try
        {
            await using var c = new NpgsqlConnection(_adminCs);
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)", c);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* 清理尽力而为 */ }
    }

    // ----------  helpers ----------
    public static JsonArray Ev(string name = "依据文件") => new(new JsonObject { ["name"] = name, ["hash"] = HashChain.Sha256(name + Guid.NewGuid()) });

    public async Task<Caller> GetCaller(string userId)
    {
        await using var conn = Db.App();
        var r = (await conn.Rows("SELECT * FROM proj.accounts WHERE user_id=@u", ("u", userId)))[0];
        return new Caller(userId, Pg.S(r["role"]), r["party_id"] as string, (bool)r["enabled"]!, (bool)r["frozen"]!);
    }

    public async Task<EventRow> Submit(string type, string actor, string? resolutionNo, JsonObject payload, JsonArray? evidence = null)
    {
        var caller = actor == "founder" ? new Caller("founder", "Founder", null, true, false) : await GetCaller(actor);
        // 规范化：与 HTTP JSON 入口一致（JsonElement 底层），保证数值读取行为统一
        payload = (JsonObject)JsonNode.Parse(payload.ToJsonString())!;
        var ev = evidence is null ? Ev() : (JsonArray)JsonNode.Parse(evidence.ToJsonString())!;
        var draft = new EventStore.Draft(type, actor, resolutionNo, ev, payload);
        await using (var conn = Db.App()) await Guard.Validate(conn, draft, caller);
        return await Store.Append(draft);
    }

    public async Task<GuardException> SubmitFails(string type, string actor, string? resolutionNo, JsonObject payload, JsonArray? evidence = null)
    {
        var ex = await Assert.ThrowsAsync<GuardException>(async () => await Submit(type, actor, resolutionNo, payload, evidence));
        // 越权尝试告警入链（与 API 行为一致）
        await Store.Append(new EventStore.Draft("SecurityAlert", actor, null, new JsonArray(),
            new JsonObject { ["kind"] = ex.Kind, ["detail"] = new JsonObject { ["attemptedType"] = type, ["message"] = ex.Message } }));
        return ex;
    }

    public async Task RecordResolution(string no, string kind, JsonObject? parms = null, bool passed = true)
    {
        await Submit("ResolutionRecorded", "op", null, new JsonObject
        {
            ["resolutionNo"] = no, ["title"] = kind + " " + no, ["kind"] = kind,
            ["params"] = parms ?? new JsonObject(),
            ["votes"] = new JsonArray(new JsonObject { ["partyId"] = "alice", ["approve"] = true }, new JsonObject { ["partyId"] = "bob", ["approve"] = true }),
            ["passed"] = passed
        }, Ev("会议纪要+签名"));
    }

    public async Task Found(string companyType = "有限责任公司", decimal regCap = 1_000_000m, JsonObject[]? extraRules = null)
    {
        var rules = new JsonArray(
            new JsonObject { ["kind"] = "Charter.Voting", ["params"] = new JsonObject { ["basis"] = "Subscribed", ["threshold"] = 0.5 } },
            new JsonObject { ["kind"] = "Charter.ExitValuation", ["params"] = new JsonObject { ["method"] = "NetAsset", ["installments"] = 3 } },
            new JsonObject { ["kind"] = "Charter.Dividend", ["params"] = new JsonObject { ["payoutRatio"] = 1.0, ["forceMinRatio"] = 0.6 } },
            new JsonObject { ["kind"] = "RoutineAuth", ["params"] = new JsonObject { ["singleLimit"] = 5000, ["monthlyCap"] = 20000, ["categories"] = new JsonArray("Expense") } });
        if (extraRules != null) foreach (var r in extraRules) rules.Add(r);
        await Submit("CompanyFounded", "founder", null, new JsonObject
        {
            ["company"] = new JsonObject { ["name"] = "测试合伙公司", ["type"] = companyType, ["registeredCapital"] = regCap, ["industryTags"] = new JsonArray("软件") },
            ["accounts"] = new JsonArray(
                new JsonObject { ["username"] = "op", ["displayName"] = "操作员", ["role"] = "Operator", ["password"] = "Op#12345", ["realName"] = "操建国", ["idNumber"] = "110101198001011234" },
                new JsonObject { ["username"] = "alice", ["displayName"] = "股东甲", ["role"] = "Shareholder", ["partyId"] = "alice", ["password"] = "A#12345", ["subscribed"] = 600000, ["realName"] = "爱丽丝", ["idNumber"] = "110101198501012345" },
                new JsonObject { ["username"] = "bob", ["displayName"] = "股东乙", ["role"] = "Shareholder", ["partyId"] = "bob", ["password"] = "B#12345", ["subscribed"] = 400000, ["realName"] = "鲍勃", ["idNumber"] = "110101198701013456" },
                new JsonObject { ["username"] = "sup", ["displayName"] = "监事", ["role"] = "Supervisor", ["password"] = "S#12345", ["realName"] = "监督明", ["idNumber"] = "110101197501014567" },
                new JsonObject { ["username"] = "adm", ["displayName"] = "IT管理员", ["role"] = "Admin", ["password"] = "Ad#12345", ["realName"] = "管理员", ["idNumber"] = "110101199001015678" }),
            ["charterRules"] = rules,
            ["whitelist"] = new JsonArray(new JsonObject { ["accountNo"] = "6222000011112222", ["holder"] = "供应商A" })
        }, Ev("章程+股东协议全文"));
    }

    public async Task<string> SignContract(string contractId, string category, string type, JsonArray parties, string secrecy = "Internal", JsonArray? clauses = null, string? resolutionNo = null)
    {
        resolutionNo ??= "RES-C-" + contractId;
        await RecordResolution(resolutionNo, "ContractSign", new JsonObject { ["contractId"] = contractId });
        await Submit("ContractSigned", "op", resolutionNo, new JsonObject
        {
            ["contractId"] = contractId, ["category"] = category, ["contractType"] = type,
            ["parties"] = parties, ["secrecy"] = secrecy, ["docHash"] = HashChain.Sha256(contractId),
            ["clauses"] = clauses ?? new JsonArray()
        }, Ev("合同签署件"));
        return contractId;
    }

    public async Task<decimal> ScalarD(string sql)
    {
        await using var conn = Db.App();
        return Pg.D(await conn.Scalar(sql));
    }
    public async Task<List<Dictionary<string, object?>>> Rows(string sql)
    {
        await using var conn = Db.App();
        return await conn.Rows(sql);
    }
}
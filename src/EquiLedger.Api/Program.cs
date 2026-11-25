// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Api;
using EquiLedger.Engine;
using EquiLedger.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString);

var host = Environment.GetEnvironmentVariable("EQUI_PG_HOST") ?? "localhost";
var port = Environment.GetEnvironmentVariable("EQUI_PG_PORT") ?? "5433";
var database = Environment.GetEnvironmentVariable("EQUI_PG_DB") ?? "equiledger";
var ownerCs = Environment.GetEnvironmentVariable("EQUI_OWNER_CS") ?? $"Host={host};Port={port};Database={database};Username=equi;Password=equi_owner_pwd";
var appCs = Environment.GetEnvironmentVariable("EQUI_APP_CS") ?? $"Host={host};Port={port};Database={database};Username=equi_app;Password=equi_app_pwd";
var initSql = Environment.GetEnvironmentVariable("EQUI_INIT_SQL") ?? "/app/db/init.sql";

var db = new Db(appCs, ownerCs, initSql);
var store = new EventStore(db);
var verifier = new Verifier(db, store);

// 等待 PostgreSQL 就绪并初始化（迁移账号执行 DDL，运行账号最小权限）
for (var i = 0; ; i++)
{
    try { await db.InitAsync(); break; }
    catch (Exception ex)
    {
        if (i >= 30) throw;
        Console.WriteLine($"等待数据库就绪（{i + 1}/30）：{ex.Message}");
        await Task.Delay(2000);
    }
}
await LegalSeed.EnsureAsync(db);

builder.Services.AddSingleton(db);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(verifier);
builder.Services.AddHostedService<PushService>();
builder.Services.AddHostedService<PayrollAutoService>();
builder.Services.AddHostedService<LicenseService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
Endpoints.Map(app, db, store, verifier);
app.Run();

/// <summary>商业授权验权：启动即查、每 24h 复查（LicenseOS v2 license/check，出站 HTTPS）。成功验权时间落本地缓存供离线宽限；状态只影响写入闸门，不入事件链。</summary>
public class LicenseService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var server = Environment.GetEnvironmentVariable("EQUI_LICENSE_SERVER") ?? "https://sz.djkz.top";
        var apiKey = Environment.GetEnvironmentVariable("EQUI_LICENSE_API_KEY");
        var key = Environment.GetEnvironmentVariable("EQUI_LICENSE_KEY");
        var domain = Environment.GetEnvironmentVariable("EQUI_LICENSE_DOMAIN") ?? Environment.MachineName;
        var grace = int.TryParse(Environment.GetEnvironmentVariable("EQUI_LICENSE_GRACE_DAYS"), out var g) ? g : 7;
        var cachePath = Path.Combine(Environment.GetEnvironmentVariable("EQUI_LICENSE_CACHE_DIR") ?? "/data/license", "cache.json");
        var client = new LicenseClient();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                LicenseGate.Enforce = Environment.GetEnvironmentVariable("EQUI_LICENSE_ENFORCE") == "1";
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(apiKey))
                {
                    LicenseGate.Current = new LicenseState("unconfigured", null, null, "未配置授权校验（EQUI_LICENSE_KEY 为空），社区模式运行");
                }
                else
                {
                    var cachedLastOk = ReadCache(cachePath);
                    string? st = null; DateTimeOffset? exp = null;
                    try { (st, exp) = await client.CheckAsync(server, apiKey, key, domain, ct); }
                    catch (Exception ex) { Console.WriteLine($"授权验权网络异常：{ex.Message}"); }
                    var state = LicensePolicy.Evaluate(st, exp, cachedLastOk, DateTimeOffset.UtcNow, grace);
                    LicenseGate.Current = state;
                    if (state.Status == "valid") WriteCache(cachePath, DateTimeOffset.UtcNow);
                    Console.WriteLine($"授权状态：{state.Status} · {state.Message}");
                }
            }
            catch (Exception ex) { Console.WriteLine($"授权服务异常：{ex.Message}"); }
            await Task.Delay(TimeSpan.FromHours(24), ct);
        }
    }

    private static DateTimeOffset? ReadCache(string p)
    {
        try { var n = JsonNode.Parse(File.ReadAllText(p)); return DateTimeOffset.TryParse(n?["lastOk"]?.GetValue<string>(), out var t) ? t : null; }
        catch { return null; }
    }
    private static void WriteCache(string p, DateTimeOffset t)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, new JsonObject { ["lastOk"] = t.ToString("o") }.ToJsonString()); }
        catch (Exception ex) { Console.WriteLine($"授权缓存写入失败：{ex.Message}"); }
    }
}

/// <summary>月底自动算薪：指定日（默认每月最后一天）09:00 起若当期未算薪，以操作员名义自动提交 PayrollRun（依据=锁定工资规则快照哈希，auto=true 留痕）。计算可复算、全员推送；打款仍须人工回单确认（PayrollPaid）。</summary>
public class PayrollAutoService : BackgroundService
{
    private readonly Db _db;
    private readonly EventStore _store;
    public PayrollAutoService(Db db, EventStore store) { _db = db; _store = store; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("EQUI_PAYROLL_AUTO") != "0")
                {
                    var now = DateTimeOffset.Now;
                    var day = int.TryParse(Environment.GetEnvironmentVariable("EQUI_PAYROLL_DAY"), out var d) ? d : DateTime.DaysInMonth(now.Year, now.Month);
                    if (now.Day == day && now.Hour >= 9)
                    {
                        var period = now.ToString("yyyy-MM");
                        await using var conn = _db.App();
                        var exists = Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.payroll_runs WHERE period=@p", ("p", period)));
                        if (exists == 0)
                        {
                            var op = await conn.Scalar("SELECT username FROM proj.accounts WHERE role='Operator' AND enabled=TRUE AND frozen=FALSE ORDER BY username LIMIT 1") as string;
                            if (op != null)
                            {
                                var rules = await conn.Rows("SELECT rule_id,version,params FROM proj.rule_versions WHERE kind='Payroll.Standard' AND status='Active' ORDER BY rule_id,version");
                                var rulesJson = new JsonArray(rules.Select(r => (JsonNode)new JsonObject { ["ruleId"] = Pg.S(r["rule_id"]), ["version"] = Convert.ToInt32(r["version"]), ["params"] = Pg.J(r["params"]) }).ToArray());
                                var hash = EquiLedger.Core.HashChain.Sha256(rulesJson.ToJsonString());
                                await _store.Append(new EventStore.Draft("PayrollRun", op, null,
                                    new JsonArray(new JsonObject { ["name"] = "系统月底自动算薪依据（锁定工资规则快照）", ["hash"] = hash }),
                                    new JsonObject { ["period"] = period, ["auto"] = true }));
                                Console.WriteLine($"月底自动算薪已提交 {period}（操作员 {op} 留痕）");
                            }
                        }
                    }
                    if (Environment.GetEnvironmentVariable("EQUI_DEPR_AUTO") != "0" && now.Day == day && now.Hour >= 9)
                    {
                        var periodD = now.ToString("yyyy-MM");
                        await using var connD = _db.App();
                        var depDone = Convert.ToInt64(await connD.Scalar("SELECT COUNT(*) FROM proj.depreciation_runs WHERE period=@p", ("p", periodD)));
                        var depN = Convert.ToInt64(await connD.Scalar("SELECT COUNT(*) FROM proj.fixed_assets WHERE status='Active'"));
                        if (depDone == 0 && depN > 0)
                        {
                            var dop = await connD.Scalar("SELECT username FROM proj.accounts WHERE role='Operator' AND enabled=TRUE AND frozen=FALSE ORDER BY username LIMIT 1") as string;
                            if (dop != null)
                            {
                                await _store.Append(new EventStore.Draft("FixedAssetDepreciationRun", dop, null,
                                    new JsonArray(new JsonObject { ["name"] = "系统月度自动折旧（直线法·资产卡片快照）", ["hash"] = EquiLedger.Core.HashChain.Sha256(periodD + dop) }),
                                    new JsonObject { ["period"] = periodD, ["auto"] = true }));
                                Console.WriteLine($"月度自动折旧已提交 {periodD}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"自动算薪服务异常：{ex.Message}"); }
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }
}

/// <summary>工作群推送：每次提交的结构化卡片自动推送 webhook（企业微信/钉钉/飞书群机器人）。</summary>
public class PushService : BackgroundService
{
    private readonly Db _db;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    public PushService(Db db) => _db = db;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var url = Environment.GetEnvironmentVariable("EQUI_WEBHOOK_URL");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = _db.App();
                var pending = await conn.Rows("SELECT id,channel,summary FROM proj.notifications WHERE push_status='Pending' ORDER BY id LIMIT 50");
                foreach (var n in pending)
                {
                    var status = "Logged";
                    if (!string.IsNullOrEmpty(url) && Pg.S(n["channel"]) == "WorkGroup")
                    {
                        try
                        {
                            var summary = Pg.J(n["summary"]);
                            var text = $"【EquiLedger】{summary["eventType"]} | 操作员 {summary["actor"]} | 决议 {summary["resolutionNo"] ?? "例行/执行"}\n影响清单：\n" +
                                       string.Join("\n", (summary["impacts"] as JsonArray ?? new JsonArray()).Select(x => "· " + x!.GetValue<string>())) +
                                       "\n—— " + EquiLedger.Core.HashChain.Attribution;
                            var card = new JsonObject { ["msgtype"] = "text", ["text"] = new JsonObject { ["content"] = text } };
                            var resp = await _http.PostAsync(url, new StringContent(card.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);
                            status = resp.IsSuccessStatusCode ? "Sent" : "Failed";
                        }
                        catch { status = "Failed"; }
                    }
                    await conn.Exec("UPDATE proj.notifications SET push_status=@s WHERE id=@i", ("s", status), ("i", n["id"]));
                }
            }
            catch (Exception ex) { Console.WriteLine($"推送服务异常：{ex.Message}"); }
            await Task.Delay(3000, ct);
        }
    }
}
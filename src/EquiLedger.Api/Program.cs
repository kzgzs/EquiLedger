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
        // 与 LicenseOS 一键注入互通：环境变量缺省时读注入包内 license-os/config.json（api_url 带 /api/v2 后缀需剥掉）
        if (string.IsNullOrEmpty(apiKey))
        {
            foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                var cfgPath = Path.Combine(dir, "license-os", "config.json");
                if (!File.Exists(cfgPath)) continue;
                try
                {
                    var cfg = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfgPath));
                    apiKey = cfg?["api_key"]?.GetValue<string>();
                    var au = cfg?["api_url"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EQUI_LICENSE_SERVER")) && !string.IsNullOrEmpty(au))
                        server = au.EndsWith("/api/v2") ? au[..^7] : au;
                }
                catch (Exception ex) { Console.WriteLine($"license-os/config.json 读取失败：{ex.Message}"); }
                break;
            }
        }
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
                    if (Environment.GetEnvironmentVariable("EQUI_IP_AMORT_AUTO") != "0" && now.Day == day && now.Hour >= 9)
                    {
                        var periodI = now.ToString("yyyy-MM");
                        await using var connI = _db.App();
                        var amDone = Convert.ToInt64(await connI.Scalar("SELECT COUNT(*) FROM proj.amort_runs WHERE period=@p", ("p", periodI)));
                        var amN = Convert.ToInt64(await connI.Scalar("SELECT COUNT(*) FROM proj.intangible_assets WHERE status='Active' AND life_months>0"));
                        var iop = await connI.Scalar("SELECT username FROM proj.accounts WHERE role='Operator' AND enabled=TRUE AND frozen=FALSE ORDER BY username LIMIT 1") as string;
                        if (iop != null)
                        {
                            if (amDone == 0 && amN > 0)
                            {
                                await _store.Append(new EventStore.Draft("IpAmortizationRun", iop, null,
                                    new JsonArray(new JsonObject { ["name"] = "系统月度自动摊销（直线法·账面价值封顶）", ["hash"] = EquiLedger.Core.HashChain.Sha256(periodI + iop) }),
                                    new JsonObject { ["period"] = periodI, ["auto"] = true }));
                                Console.WriteLine($"月度自动摊销已提交 {periodI}");
                            }
                            var scanDone = Convert.ToInt64(await connI.Scalar("SELECT COUNT(*) FROM events.event_log WHERE event_type='IpRenewalScan' AND payload->>'period'=@p", ("p", periodI)));
                            if (scanDone == 0)
                            {
                                await _store.Append(new EventStore.Draft("IpRenewalScan", iop, null,
                                    new JsonArray(new JsonObject { ["name"] = "系统月度产权到期/续费扫描", ["hash"] = EquiLedger.Core.HashChain.Sha256(periodI + iop + "scan") }),
                                    new JsonObject { ["period"] = periodI, ["auto"] = true }));
                                Console.WriteLine($"产权到期扫描已提交 {periodI}");
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
        var legacyUrl = Environment.GetEnvironmentVariable("EQUI_WEBHOOK_URL");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = _db.App();
                var groups = (await conn.Rows("SELECT group_id,kind,department,webhook FROM proj.notify_groups WHERE enabled=TRUE"))
                    .Select(g => (groupId: Pg.S(g["group_id"]), kind: Pg.S(g["kind"]), department: Pg.S(g["department"]), webhook: Pg.S(g["webhook"]))).ToList();
                var bindings = (await conn.Rows("SELECT group_id,event_type FROM proj.notify_bindings"))
                    .Select(b => (groupId: Pg.S(b["group_id"]), eventType: Pg.S(b["event_type"]))).ToList();
                var pending = await conn.Rows("SELECT id,channel,summary,source_event_id FROM proj.notifications WHERE push_status='Pending' ORDER BY id LIMIT 50");
                foreach (var n in pending)
                {
                    var status = "Logged";
                    if (Pg.S(n["channel"]) == "WorkGroup")
                    {
                        var summary = Pg.J(n["summary"]);
                        var type = summary["eventType"]?.ToString() ?? "";
                        var payloadNode = await conn.Scalar("SELECT payload FROM events.event_log WHERE event_id=@e", ("e", n["source_event_id"]));
                        var payload = payloadNode is null or DBNull ? new JsonObject() : Pg.J(payloadNode);
                        var (lvl, _) = EquiLedger.Engine.NotifyPolicy.Classify(type, payload);
                        var high = lvl == EquiLedger.Engine.NotifyPolicy.Level.High;
                        var filtered = EquiLedger.Engine.NotifyPolicy.Filter(payload as JsonObject ?? new JsonObject());
                        var refNo = summary["resolutionNo"]?.ToString() ?? $"SEQ-{summary["seq"]}";
                        var hashFull = summary["hash"]?.ToString() ?? "";
                        var hash8 = hashFull[..Math.Min(8, hashFull.Length)];
                        if (groups.Count == 0)
                        {
                            // 未配置群组：兼容旧单 webhook（仍走分级与过滤）
                            if (!string.IsNullOrEmpty(legacyUrl))
                                status = await Send(legacyUrl, EquiLedger.Engine.NotifyPolicy.Render("company", type, refNo, filtered, high, hash8), ct) ? "Sent" : "Failed";
                        }
                        else
                        {
                            var targets = EquiLedger.Engine.NotifyPolicy.Route(groups.Select(g => (g.groupId, g.kind, g.department)), bindings, type, payload);
                            if (targets.Count == 0) status = "NoRoute"; // 默认拒绝：未绑定/超范围不推送
                            else
                            {
                                var okAll = true;
                                foreach (var gid in targets)
                                {
                                    var g = groups.First(x => x.groupId == gid);
                                    var text = EquiLedger.Engine.NotifyPolicy.Render(g.kind, type, refNo, filtered, high, hash8);
                                    var ok = await Send(g.webhook, text, ct);
                                    okAll &= ok;
                                    await conn.Exec("INSERT INTO proj.notify_logs(log_id,notification_id,group_id,event_type,level,digest,content_hash,status,sent_at) VALUES(@l,@n,@g,@t,@lv,@d,@h,@s,@at)",
                                        ("l", $"NL-{n["id"]}-{gid}"), ("n", Convert.ToInt64(n["id"])), ("g", gid), ("t", type),
                                        ("lv", lvl.ToString()), ("d", high), ("h", EquiLedger.Core.HashChain.Sha256(text)), ("s", ok ? "Sent" : "Failed"), ("at", DateTime.UtcNow));
                                }
                                status = okAll ? "Sent" : "Failed";
                            }
                        }
                    }
                    await conn.Exec("UPDATE proj.notifications SET push_status=@s WHERE id=@i", ("s", status), ("i", n["id"]));
                }
            }
            catch (Exception ex) { Console.WriteLine($"推送服务异常：{ex.Message}"); }
            await Task.Delay(3000, ct);
        }
    }

    private async Task<bool> Send(string url, string text, CancellationToken ct)
    {
        try
        {
            var content = text + "\n—— " + EquiLedger.Core.HashChain.Attribution;
            var card = url.Contains("open.feishu.cn")
                ? new JsonObject { ["msg_type"] = "text", ["content"] = new JsonObject { ["text"] = content } }
                : new JsonObject { ["msgtype"] = "text", ["text"] = new JsonObject { ["content"] = content } };
            var resp = await _http.PostAsync(url, new StringContent(card.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
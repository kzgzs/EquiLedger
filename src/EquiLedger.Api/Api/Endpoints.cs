// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Engine;
using EquiLedger.Governance;
using EquiLedger.Storage;
using Npgsql;

namespace EquiLedger.Api;

public static class Endpoints
{
    private static async Task<Caller?> Auth(HttpRequest req, Db db)
    {
        var h = req.Headers.Authorization.ToString();
        if (!h.StartsWith("Bearer ")) return null;
        var token = h[7..];
        await using var conn = db.App();
        var rows = await conn.Rows(@"SELECT a.user_id,a.role,a.party_id,a.enabled,a.frozen FROM proj.sessions s
            JOIN proj.accounts a ON a.user_id=s.user_id WHERE s.token=@t", ("t", token));
        if (rows.Count == 0) return null;
        var r = rows[0];
        return new Caller(Pg.S(r["user_id"]), Pg.S(r["role"]), r["party_id"] as string, (bool)r["enabled"]!, (bool)r["frozen"]!);
    }

    private static IResult Deny(string msg) => Results.Json(new { error = msg }, statusCode: 403);

    // jsonb 列以字符串返回，统一解析为结构化 JSON 供前端直接使用
    private static object? JVal(object? v)
    {
        if (v is string s && (s.StartsWith('{') || s.StartsWith('[')))
        {
            try { return JsonNode.Parse(s) ?? v; } catch { return v; }
        }
        return v;
    }
    private static List<Dictionary<string, object?>> JRows(IEnumerable<Dictionary<string, object?>> rows) =>
        rows.Select(r => r.ToDictionary(k => k.Key, k => JVal(k.Value))).ToList();
    private static Dictionary<string, object?>? JRow(Dictionary<string, object?>? r) =>
        r?.ToDictionary(k => k.Key, k => JVal(k.Value));

    public static void Map(WebApplication app, Db db, EventStore store, Verifier verifier)
    {
        // ---------- 设立向导（仅一次） ----------
        app.MapPost("/api/setup", async (JsonObject body) =>
        {
            var draft = new EventStore.Draft("CompanyFounded", "founder", null,
                body["evidence"] ?? new JsonArray(new JsonObject { ["name"] = "公司章程与股东协议", ["hash"] = HashChain.Sha256(body.ToJsonString()) }),
                body);
            try
            {
                await using var conn = db.App();
                await Guard.Validate(conn, draft, new Caller("founder", "Founder", null, true, false));
                var e = await store.Append(draft);
                return Results.Json(new { ok = true, eventId = e.EventId, hash = e.Hash });
            }
            catch (GuardException g) { return Results.Json(new { error = g.Message, kind = g.Kind }, statusCode: 403); }
        });

        app.MapPost("/api/login", async (JsonObject body) =>
        {
            await using var conn = db.App();
            var rows = await conn.Rows("SELECT * FROM proj.accounts WHERE username=@u", ("u", body["username"]?.GetValue<string>() ?? ""));
            if (rows.Count == 0 || !Crypto.VerifyPassword(body["password"]?.GetValue<string>() ?? "", Pg.S(rows[0]["pwd_hash"])))
                return Results.Json(new { error = "用户名或密码错误" }, statusCode: 401);
            if (!(bool)rows[0]["enabled"]!) return Results.Json(new { error = "账号已停用" }, statusCode: 403);
            var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            await conn.Exec("INSERT INTO proj.sessions(token,user_id,created_at) VALUES(@t,@u,@now)", ("t", token), ("u", rows[0]["user_id"]), ("now", DateTime.UtcNow));
            return Results.Json(new { token, userId = rows[0]["user_id"], role = rows[0]["role"], displayName = rows[0]["display_name"], frozen = rows[0]["frozen"], partyId = rows[0]["party_id"] is DBNull ? null : rows[0]["party_id"] });
        });

        app.MapGet("/api/me", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            return c is null ? Deny("未登录") : Results.Json(new { c.UserId, c.Role, c.PartyId, c.Enabled, c.Frozen });
        });

        // ---------- 通用事件提交（治理守卫 → 事件入链 → 自动联动） ----------
        app.MapPost("/api/events", async (HttpRequest req, JsonObject body) =>
        {
            var caller = await Auth(req, db);
            if (caller is null) return Deny("未登录");
            var type = body["type"]?.GetValue<string>() ?? "";
            var payload = (JsonObject?)body["payload"] ?? new JsonObject();
            var evidence = body["evidence"] ?? new JsonArray();
            var draft = new EventStore.Draft(type, caller.UserId, body["resolutionNo"]?.GetValue<string>(), evidence, payload);
            try
            {
                await using (var conn = db.App())
                    await Guard.Validate(conn, draft, caller);
                // 规则版本快照：事件永远引用其发生时有效的规则版本（P6 可复算）
                await using (var conn = db.App())
                {
                    var rules = await conn.Rows("SELECT rule_id,version FROM proj.rule_versions WHERE status='Active' ORDER BY rule_id");
                    draft = draft with { RuleRefs = new JsonArray(rules.Select(r => (JsonNode)new JsonObject { ["ruleId"] = Pg.S(r["rule_id"]), ["version"] = Convert.ToInt32(r["version"]) }).ToArray()) };
                }
                var e = await store.Append(draft);
                await using var conn2 = db.App();
                var impacts = await conn2.Rows("SELECT impacts FROM proj.impact_lists WHERE source_event_id=@e", ("e", e.EventId));
                return Results.Json(new { ok = true, eventId = e.EventId, seq = e.Seq, hash = e.Hash, impacts = impacts.Count > 0 ? Pg.J(impacts[0]["impacts"]) : new JsonArray() });
            }
            catch (GuardException g)
            {
                await store.Append(new EventStore.Draft("SecurityAlert", caller.UserId, null, new JsonArray(),
                    new JsonObject { ["kind"] = g.Kind, ["detail"] = new JsonObject { ["attemptedType"] = type, ["message"] = g.Message, ["payload"] = payload.DeepClone() } }));
                return Results.Json(new { error = g.Message, kind = g.Kind }, statusCode: 403);
            }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });

        // ---------- 只读视图（股东全量知情权；管理员无业务读取权） ----------
        string[] ReadRoles = { "Operator", "Shareholder", "Supervisor", "Employee", "BackupOperator", "FormerOperator" };

        app.MapGet("/api/dashboard", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问（管理员无业务数据读取权）");
            await using var conn = db.App();
            var company = (await conn.Rows("SELECT * FROM proj.company WHERE id=1")).FirstOrDefault();
            var capVer = await conn.Scalar("SELECT COALESCE(MAX(version),0) FROM proj.cap_versions");
            var eventCount = await conn.Scalar("SELECT COUNT(*) FROM events.event_log");
            var openTodos = await conn.Scalar("SELECT COUNT(*) FROM proj.todos WHERE status='Open'");
            var bank = await conn.Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='Bank'");
            return Results.Json(new { company = JRow(company), capVersion = capVer, eventCount, openTodos, bankBalance = bank });
        });

        app.MapGet("/api/cap", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var versions = await conn.Rows("SELECT * FROM proj.cap_versions ORDER BY version DESC");
            var positions = await conn.Rows(@"SELECT cp.*, p.name FROM proj.cap_positions cp LEFT JOIN proj.parties p ON p.party_id=cp.party_id
                WHERE cp.cap_version=(SELECT COALESCE(MAX(version),0) FROM proj.cap_versions) ORDER BY cp.party_id");
            return Results.Json(new { versions = JRows(versions), positions = JRows(positions) });
        });

        app.MapGet("/api/events", async (HttpRequest req, int limit = 100) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = await conn.Rows("SELECT seq,event_id,event_type,actor_id,occurred_at,resolution_no,hash,payload FROM events.event_log ORDER BY seq DESC LIMIT @l", ("l", limit));
            return Results.Json(JRows(rows));
        });

        app.MapGet("/api/calcs", async (HttpRequest req, string? kind) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = kind is null
                ? await conn.Rows("SELECT * FROM proj.calc_results ORDER BY created_at DESC LIMIT 100")
                : await conn.Rows("SELECT * FROM proj.calc_results WHERE kind=@k ORDER BY created_at DESC LIMIT 100", ("k", kind));
            return Results.Json(JRows(rows));
        });

        app.MapGet("/api/dividends", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.dividend_plans ORDER BY fiscal_year DESC, plan_id DESC LIMIT 50")));
        });

        app.MapGet("/api/resolutions", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.resolutions ORDER BY created_at DESC LIMIT 100")));
        });

        app.MapGet("/api/rules", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.rule_versions ORDER BY rule_id, version")));
        });

        app.MapGet("/api/todos", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.todos ORDER BY created_at DESC LIMIT 200")));
        });

        app.MapGet("/api/notifications", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.notifications ORDER BY id DESC LIMIT 100")));
        });

        app.MapGet("/api/modules", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.module_states ORDER BY module_id")));
        });

        app.MapGet("/api/ledger", async (HttpRequest req, string? period) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = period is null
                ? await conn.Rows("SELECT * FROM proj.ledger ORDER BY id DESC LIMIT 200")
                : await conn.Rows("SELECT * FROM proj.ledger WHERE period=@p ORDER BY id", ("p", period));
            var locks = await conn.Rows("SELECT period FROM proj.period_locks");
            return Results.Json(new { entries = JRows(rows), lockedPeriods = locks.Select(l => Pg.S(l["period"])) });
        });

        // ---------- 模块三：财务会计（科目/凭证/三表/余额表/往来台账；全部为账本聚合视图，不重复建账） ----------
        app.MapGet("/api/chart", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT * FROM proj.chart_of_accounts ORDER BY code")));
        });

        app.MapGet("/api/vouchers", async (HttpRequest req, string period) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = await conn.Rows("SELECT * FROM proj.ledger WHERE period=@p ORDER BY id", ("p", period));
            var evs = await conn.Rows("SELECT seq,event_id,event_type AS type,resolution_no,evidence FROM events.event_log");
            var byId = evs.ToDictionary(e => (Guid)e["event_id"]!, e => e);
            var chart = await conn.Rows("SELECT * FROM proj.chart_of_accounts ORDER BY code");
            return Results.Json(Finance.Vouchers(period, rows, byId, chart));
        });

        app.MapGet("/api/reports", async (HttpRequest req, string period) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var all = await conn.Rows("SELECT * FROM proj.ledger ORDER BY id");
            var chart = await conn.Rows("SELECT * FROM proj.chart_of_accounts ORDER BY code");
            var evs = await conn.Rows("SELECT event_id,event_type AS type FROM events.event_log");
            var year = period[..4];
            var cum = Finance.Sums(all, r => Pg.S(r["period"]).CompareTo(period) <= 0);
            var per = Finance.Sums(all, r => Pg.S(r["period"]) == period);
            var ytd = Finance.Sums(all, r => Pg.S(r["period"]).StartsWith(year) && Pg.S(r["period"]).CompareTo(period) <= 0);
            var perRows = all.Where(r => Pg.S(r["period"]) == period).ToList();
            return Results.Json(new
            {
                balanceSheet = Finance.BalanceSheet(cum, chart),
                income = Finance.Income(per, ytd, chart),
                cashFlow = Finance.CashFlow(perRows, evs.ToDictionary(e => (Guid)e["event_id"]!, e => Pg.S(e["type"])))
            });
        });

        app.MapGet("/api/trial-balance", async (HttpRequest req, string period) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var all = await conn.Rows("SELECT * FROM proj.ledger ORDER BY id");
            var chart = await conn.Rows("SELECT * FROM proj.chart_of_accounts ORDER BY code");
            var cum = Finance.Sums(all, r => Pg.S(r["period"]).CompareTo(period) <= 0);
            var per = Finance.Sums(all, r => Pg.S(r["period"]) == period);
            return Results.Json(Finance.Trial(per, cum, chart));
        });

        app.MapGet("/api/arap", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var orders = await conn.Rows("SELECT order_id,customer,amount,collected,confirmed_at FROM proj.sales_orders WHERE amount>collected ORDER BY confirmed_at");
            var now = DateTimeOffset.UtcNow;
            var recv = orders.Select(o =>
            {
                var ca = DateTime.SpecifyKind((DateTime)o["confirmed_at"]!, DateTimeKind.Utc);
                var days = (now - new DateTimeOffset(ca, TimeSpan.Zero)).TotalDays;
                return new JsonObject
                {
                    ["orderId"] = Pg.S(o["order_id"]), ["customer"] = Pg.S(o["customer"]),
                    ["balance"] = Pg.D(o["amount"]) - Pg.D(o["collected"]),
                    ["aging"] = days <= 30 ? "≤30天" : days <= 90 ? "31-90天" : days <= 180 ? "91-180天" : ">180天"
                };
            }).ToArray();
            var runs = await conn.Rows("SELECT period,totals FROM proj.payroll_runs WHERE status='Calculated' ORDER BY period");
            var plans = await conn.Rows("SELECT plan_id,fiscal_year,allocations FROM proj.dividend_plans WHERE status='Approved' ORDER BY fiscal_year");
            var coms = await conn.Rows("SELECT COALESCE(SUM(amount),0) AS s FROM proj.commissions WHERE status='Pending'");
            var pay = new JsonArray();
            foreach (var r in runs) pay.Add(new JsonObject { ["kind"] = "应付工资", ["ref"] = Pg.S(r["period"]), ["amount"] = decimal.TryParse(Pg.J(r["totals"])["net"]?.ToString(), out var pn) ? pn : 0m });
            foreach (var p in plans)
            {
                decimal sum = 0;
                foreach (var n in Pg.J(p["allocations"]) as JsonArray ?? new JsonArray())
                    sum += decimal.TryParse(n?["net"]?.ToString(), out var v) ? v : (decimal.TryParse(n?["amount"]?.ToString(), out var v2) ? v2 : 0);
                pay.Add(new JsonObject { ["kind"] = "应付股利", ["ref"] = Pg.S(p["plan_id"]), ["amount"] = sum });
            }
            if (Pg.D(coms[0]["s"]) > 0) pay.Add(new JsonObject { ["kind"] = "应付提成", ["ref"] = "Pending", ["amount"] = Pg.D(coms[0]["s"]) });
            return Results.Json(new { receivables = recv, payables = pay });
        });

        // ---------- 第四体系：资源管理（固资/库存/设备/生产/资源计划） ----------
        app.MapGet("/api/assets", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(new JsonArray((await conn.Rows("SELECT * FROM proj.fixed_assets ORDER BY asset_id")).Select(r => (JsonNode)new JsonObject
            {
                ["assetId"] = Pg.S(r["asset_id"]), ["name"] = Pg.S(r["name"]), ["category"] = Pg.S(r["category"]),
                ["cost"] = Pg.D(r["cost"]), ["accumulated"] = Pg.D(r["accumulated_dep"]), ["net"] = Pg.D(r["cost"]) - Pg.D(r["accumulated_dep"]),
                ["lifeMonths"] = Convert.ToInt32(r["life_months"]), ["residualRate"] = Pg.D(r["residual_rate"]),
                ["department"] = Pg.S(r["department"]), ["location"] = Pg.S(r["location"]), ["status"] = Pg.S(r["status"])
            }).ToArray()));
        });
        app.MapGet("/api/stock", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var bal = await conn.Rows("SELECT b.*,m.name,m.unit,m.category,m.safety_stock FROM proj.stock_balances b JOIN proj.materials m ON m.material_id=b.material_id ORDER BY b.material_id,b.warehouse");
            var moves = await conn.Rows("SELECT * FROM proj.stock_moves ORDER BY move_id DESC LIMIT 50");
            return Results.Json(new
            {
                balances = new JsonArray(bal.Select(r => (JsonNode)new JsonObject { ["materialId"] = Pg.S(r["material_id"]), ["name"] = Pg.S(r["name"]), ["warehouse"] = Pg.S(r["warehouse"]), ["qty"] = Pg.D(r["qty"]), ["avgCost"] = Pg.D(r["avg_cost"]), ["value"] = Math.Round(Pg.D(r["qty"]) * Pg.D(r["avg_cost"]), 2), ["unit"] = Pg.S(r["unit"]), ["safetyStock"] = Pg.D(r["safety_stock"]), ["belowSafety"] = Pg.D(r["qty"]) < Pg.D(r["safety_stock"]) }).ToArray()),
                moves = new JsonArray(moves.Select(r => (JsonNode)new JsonObject { ["moveId"] = Pg.S(r["move_id"]), ["materialId"] = Pg.S(r["material_id"]), ["warehouse"] = Pg.S(r["warehouse"]), ["direction"] = Pg.S(r["direction"]), ["qty"] = Pg.D(r["qty"]), ["amount"] = Pg.D(r["amount"]), ["memo"] = Pg.S(r["memo"]) }).ToArray())
            });
        });
        app.MapGet("/api/equipment", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(new JsonArray((await conn.Rows("SELECT * FROM proj.equipment ORDER BY equip_id")).Select(r => (JsonNode)new JsonObject { ["equipId"] = Pg.S(r["equip_id"]), ["name"] = Pg.S(r["name"]), ["type"] = Pg.S(r["type"]), ["location"] = Pg.S(r["location"]), ["status"] = Pg.S(r["status"]), ["capacityPerDay"] = Pg.D(r["capacity_per_day"]), ["linkedAssetId"] = Pg.S(r["linked_asset_id"]) }).ToArray()));
        });
        app.MapGet("/api/production", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(new JsonArray((await conn.Rows("SELECT * FROM proj.production_orders ORDER BY order_no")).Select(r => (JsonNode)new JsonObject { ["orderNo"] = Pg.S(r["order_no"]), ["product"] = Pg.S(r["product_material_id"]), ["qty"] = Pg.D(r["qty"]), ["completed"] = Pg.D(r["completed_qty"]), ["bom"] = Pg.J(r["bom"]), ["equipmentId"] = Pg.S(r["equipment_id"]), ["status"] = Pg.S(r["status"]) }).ToArray()));
        });
        app.MapGet("/api/resource-plan", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var orders = await conn.Rows("SELECT * FROM proj.production_orders WHERE status='Open' ORDER BY order_no");
            var mats = await conn.Rows("SELECT m.*,COALESCE((SELECT SUM(qty) FROM proj.stock_balances b WHERE b.material_id=m.material_id),0) AS onhand FROM proj.materials m ORDER BY m.material_id");
            var gross = new Dictionary<string, decimal>();
            foreach (var o in orders)
                foreach (var bl in Pg.J(o["bom"]) as JsonArray ?? new JsonArray())
                {
                    var mid = bl!["materialId"]!.GetValue<string>();
                    var need = Convert.ToDecimal(bl["qtyPer"]!.ToString()) * (Pg.D(o["qty"]) - Pg.D(o["completed_qty"]));
                    gross[mid] = (gross.TryGetValue(mid, out var g) ? g : 0) + need;
                }
            var reqs = mats.Where(m => gross.ContainsKey(Pg.S(m["material_id"]))).Select(m =>
            {
                var mid = Pg.S(m["material_id"]); var g = gross[mid]; var onhand = Convert.ToDecimal(m["onhand"]);
                var net = Math.Max(0, g - onhand);
                return new JsonObject { ["materialId"] = mid, ["name"] = Pg.S(m["name"]), ["gross"] = g, ["onHand"] = onhand, ["net"] = net, ["safetyStock"] = Pg.D(m["safety_stock"]), ["suggestPurchase"] = net };
            }).ToArray();
            var eqLoad = await conn.Rows("SELECT e.equip_id,e.name,e.capacity_per_day,COALESCE(SUM(o.qty-o.completed_qty),0) AS load FROM proj.equipment e LEFT JOIN proj.production_orders o ON o.equipment_id=e.equip_id AND o.status='Open' GROUP BY e.equip_id,e.name,e.capacity_per_day ORDER BY e.equip_id");
            var load = eqLoad.Select(r => new JsonObject { ["equipId"] = Pg.S(r["equip_id"]), ["name"] = Pg.S(r["name"]), ["openQty"] = Convert.ToDecimal(r["load"]), ["capacityPerDay"] = Pg.D(r["capacity_per_day"]), ["estimatedDays"] = Pg.D(r["capacity_per_day"]) > 0 ? Math.Round(Convert.ToDecimal(r["load"]) / Pg.D(r["capacity_per_day"]), 1) : 0m }).ToArray();
            return Results.Json(new { materialRequirements = reqs, equipmentLoad = load });
        });

        // ---------- 第二体系贯通：采购链 / 借款 / 纳税申报 ----------
        app.MapGet("/api/purchase", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(new
            {
                requests = JRows(await conn.Rows("SELECT req_no,department,items,reason,status FROM proj.purchase_requests ORDER BY req_no")),
                orders = JRows(await conn.Rows("SELECT po_no,req_no,supplier,items,status,paid_amount FROM proj.purchase_orders ORDER BY po_no")),
                deliveries = JRows(await conn.Rows("SELECT delivery_no,po_no,items,delivered_date FROM proj.purchase_deliveries ORDER BY delivery_no")),
                inspections = JRows(await conn.Rows("SELECT inspection_no,po_no,accepted,rejected,inspector FROM proj.purchase_inspections ORDER BY inspection_no")),
                payments = JRows(await conn.Rows("SELECT pay_id,po_no,amount,receipt_hash FROM proj.purchase_payments ORDER BY pay_id"))
            });
        });
        app.MapGet("/api/loans", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(new
            {
                loans = JRows(await conn.Rows("SELECT loan_id,lender,principal,annual_rate,term_months,outstanding_principal,accrued_interest,paid_interest,status FROM proj.loans ORDER BY loan_id")),
                interest = JRows(await conn.Rows("SELECT loan_id,period,amount FROM proj.loan_interest ORDER BY loan_id,period"))
            });
        });
        app.MapGet("/api/tax-returns", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT return_id,period,kind,amount,status FROM proj.tax_returns ORDER BY period,kind")));
        });
        app.MapGet("/api/tax-return", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var period = req.Query["period"].ToString();
            if (string.IsNullOrEmpty(period)) period = DateTimeOffset.UtcNow.ToString("yyyy-MM");
            var output = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(SUM(l.credit),0) FROM proj.ledger l JOIN events.event_log e ON e.event_id=l.source_event_id WHERE l.account='TaxPayable' AND l.period=@p AND e.event_type IN ('RevenueRecorded','SalesOrderConfirmed')", ("p", period)));
            var input = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(SUM(l.debit),0) FROM proj.ledger l JOIN events.event_log e ON e.event_id=l.source_event_id WHERE l.account='TaxPayable' AND l.period=@p AND e.event_type='PurchaseInspected'", ("p", period)));
            var vat = Math.Max(0, output - input);
            var profit = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(SUM(CASE WHEN ch.category='pl_revenue' THEN l.credit-l.debit WHEN ch.category='pl_expense' AND l.account<>'IncomeTaxExpense' THEN l.debit-l.credit ELSE 0 END),0) FROM proj.ledger l JOIN proj.chart_of_accounts ch ON ch.account_key=l.account WHERE l.period=@p", ("p", period)));
            var iit = 0m;
            foreach (var r in await conn.Rows("SELECT totals FROM proj.payroll_runs WHERE period=@p", ("p", period)))
                iit += decimal.TryParse(Pg.J(r["totals"])["iit"]?.ToString(), out var v) ? v : 0;
            return Results.Json(new
            {
                period,
                vat = new { output, input, payable = vat },
                surcharge = new { @base = vat, rate = 0.12m, payable = Math.Round(vat * 0.12m, 2) },
                cit = new { profit, rate = 0.25m, payable = Math.Round(Math.Max(0, profit) * 0.25m, 2), note = "小微企业优惠税率按现行政策另行适用，本表为标准税率草稿" },
                iit = new { payable = iit }
            });
        });

        // ---------- 群通知治理 ----------
        app.MapGet("/api/notify-groups", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var gs = await conn.Rows("SELECT group_id,name,kind,department,webhook,enabled FROM proj.notify_groups ORDER BY group_id");
            var bs = await conn.Rows("SELECT group_id,event_type FROM proj.notify_bindings ORDER BY group_id,event_type");
            var arr = gs.Select(g => new JsonObject
            {
                ["groupId"] = Pg.S(g["group_id"]), ["name"] = Pg.S(g["name"]), ["kind"] = Pg.S(g["kind"]),
                ["department"] = Pg.S(g["department"]), ["enabled"] = (bool)g["enabled"]!,
                ["webhookMasked"] = Pg.S(g["webhook"]).Length > 24 ? Pg.S(g["webhook"])[..24] + "…" : Pg.S(g["webhook"]),
                ["bindings"] = new JsonArray(bs.Where(b => Pg.S(b["group_id"]) == Pg.S(g["group_id"])).Select(b => (JsonNode)Pg.S(b["event_type"])).ToArray())
            }).ToArray();
            return Results.Json(new { groups = arr, kinds = EquiLedger.Engine.NotifyPolicy.GroupKinds, warning = "群消息撤回不等于数据收回；高敏感事件仅推送摘要，明细授权登录系统查看；任何群不接收完整工资账本。" });
        });
        app.MapGet("/api/notify-logs", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT log_id,group_id,event_type,level,digest,status,sent_at FROM proj.notify_logs ORDER BY sent_at DESC LIMIT 100")));
        });

        // ---------- 举报通道 ----------
        app.MapGet("/api/complaints", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT report_id,category,target,description,anonymous,reporter,findings,outcome,action,status FROM proj.reports ORDER BY report_id")));
        });
        app.MapGet("/api/my-complaints", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows("SELECT report_id,category,target,status,outcome FROM proj.reports WHERE reporter_actor=@u ORDER BY report_id", ("u", c.UserId))));
        });

        app.MapGet("/api/employees", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            return Results.Json(JRows(await conn.Rows(@"SELECT e.*, (SELECT MAX(period) FROM proj.payroll_lines pl WHERE pl.employee_id=e.employee_id) AS last_paid_period
                FROM proj.employees e ORDER BY e.employee_id")));
        });

        // ---------- 法务库（关键词检索；条目=系统预置+事件新增，均可验真） ----------
        app.MapGet("/api/legal", async (HttpRequest req, string? q) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = string.IsNullOrWhiteSpace(q)
                ? await conn.Rows("SELECT * FROM proj.legal_library ORDER BY entry_id")
                : await conn.Rows(@"SELECT * FROM proj.legal_library
                    WHERE title ILIKE @q OR keywords ILIKE @q OR scenario ILIKE @q OR options::text ILIKE @q OR clause_template ILIKE @q
                    ORDER BY entry_id", ("q", $"%{q.Trim()}%"));
            return Results.Json(JRows(rows));
        });

        // 薪酬密级=受限：明细仅本人与授权角色（操作员/监事），股东只见汇总
        app.MapGet("/api/payroll", async (HttpRequest req, string? period) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            if (c.Role is "Operator" or "Supervisor")
            {
                var lines = period is null
                    ? await conn.Rows("SELECT * FROM proj.payroll_lines ORDER BY id DESC LIMIT 200")
                    : await conn.Rows("SELECT * FROM proj.payroll_lines WHERE period=@p ORDER BY employee_id", ("p", period));
                var runsOp = await conn.Rows("SELECT run_id,period,totals,status,auto,receipt_hash,paid_event_id,source_event_id FROM proj.payroll_runs ORDER BY period DESC LIMIT 24");
                return Results.Json(new { detail = true, lines = JRows(lines), runs = JRows(runsOp) });
            }
            if (c.Role == "Employee")
            {
                var lines = await conn.Rows(@"SELECT pl.* FROM proj.payroll_lines pl JOIN proj.employees e ON e.employee_id=pl.employee_id
                    WHERE e.party_id=@p ORDER BY pl.period", ("p", (object?)c.PartyId ?? DBNull.Value));
                return Results.Json(new { detail = true, lines = JRows(lines) });
            }
            var runs = await conn.Rows("SELECT run_id,period,totals,status,auto,receipt_hash,source_event_id FROM proj.payroll_runs ORDER BY period DESC LIMIT 24");
            return Results.Json(new { detail = false, runs = JRows(runs) });
        });

        // ---------- 商业授权状态（只读展示，不含密钥） ----------
        app.MapGet("/api/license-status", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            var s = LicenseGate.Current;
            return Results.Json(new { status = s.Status, enforce = LicenseGate.Enforce, expiresAt = s.ExpiresAt, lastOkAt = s.LastOkAt, message = s.Message });
        });

        // ---------- 银行代发批量文件（CSV，UTF-8 BOM，主流企业网银代发格式） ----------
        app.MapGet("/api/payroll-batch", async (HttpRequest req, string period) =>
        {
            var c = await Auth(req, db);
            if (c is null || c.Role != "Operator") return Deny("仅操作员可下载代发文件");
            await using var conn = db.App();
            var rows = await conn.Rows(@"SELECT pl.employee_id, pl.net, e.name, e.profile FROM proj.payroll_lines pl
                JOIN proj.employees e ON e.employee_id=pl.employee_id WHERE pl.period=@p ORDER BY pl.employee_id", ("p", period));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("序号,收款账号,户名,金额(元),用途");
            var i = 0;
            foreach (var r in rows)
            {
                var prof = r["profile"] is null ? new JsonObject() : Pg.J(r["profile"]);
                var acc = prof["bankAccount"]?.GetValue<string>() ?? "";
                sb.AppendLine($"{++i},{acc},{Pg.S(r["name"])},{Pg.D(r["net"]):0.00},工资");
            }
            var bytes = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return Results.File(bytes, "text/csv", $"payroll-batch-{period}.csv");
        });

        app.MapGet("/api/contracts", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            await using var conn = db.App();
            var rows = await conn.Rows("SELECT contract_id,category,contract_type,secrecy,status,signed_at,expires_on,parties FROM proj.contracts ORDER BY signed_at DESC");
            // 私密合同对非当事人隐藏条目内容
            var filtered = rows.Where(r =>
            {
                if (Pg.S(r["secrecy"]) != "Private") return true;
                return c.PartyId != null && Pg.S(r["parties"]).Contains(c.PartyId);
            });
            var employees = await conn.Rows("SELECT e.employee_id,e.name FROM proj.employees e WHERE e.leave_date IS NULL AND NOT EXISTS (SELECT 1 FROM proj.contracts ct WHERE ct.contract_type='劳动合同' AND ct.status='Signed' AND ct.parties::text LIKE '%'||e.party_id||'%')");
            return Results.Json(new { contracts = JRows(filtered), missingLaborContract = JRows(employees) });
        });

        app.MapPost("/api/contracts/{id}/access", async (HttpRequest req, string id, JsonObject body) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            var draft = new EventStore.Draft("ContractAccessed", c.UserId, null, new JsonArray(),
                new JsonObject { ["contractId"] = id, ["purpose"] = body["purpose"]?.GetValue<string>() ?? "", ["dualAuthorized"] = body["dualAuthorized"]?.GetValue<bool>() ?? false });
            try
            {
                await using var conn = db.App();
                await Guard.Validate(conn, draft, c);
                await store.Append(draft);
                await using var conn2 = db.App();
                var rows = await conn2.Rows("SELECT * FROM proj.contracts WHERE contract_id=@c", ("c", id));
                return Results.Json(JRow(rows.FirstOrDefault()));
            }
            catch (GuardException g)
            {
                await store.Append(new EventStore.Draft("SecurityAlert", c.UserId, null, new JsonArray(),
                    new JsonObject { ["kind"] = g.Kind, ["detail"] = new JsonObject { ["contractId"] = id, ["message"] = g.Message } }));
                return Results.Json(new { error = g.Message, kind = g.Kind }, statusCode: 403);
            }
        });

        // ---------- 依据附件上传 ----------
        app.MapPost("/api/evidence", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            if (!req.HasFormContentType || req.Form.Files.Count == 0) return Results.BadRequest(new { error = "缺少文件" });
            var file = req.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var hash = HashChain.Sha256(Convert.ToBase64String(bytes));
            var dir = Environment.GetEnvironmentVariable("EQUI_EVIDENCE_DIR") ?? "/data/evidence";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, hash);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            var e = await store.Append(new EventStore.Draft("EvidenceUploaded", c.UserId, null, new JsonArray(),
                new JsonObject { ["fileHash"] = hash, ["fileName"] = file.FileName, ["storedPath"] = path }));
            return Results.Json(new { hash, fileName = file.FileName, eventId = e.EventId });
        }).DisableAntiforgery();

        // ---------- 验真 / 证据包 / 锚定 ----------
        app.MapGet("/api/verify", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || !ReadRoles.Contains(c.Role)) return Deny("无权访问");
            var report = await verifier.Verify();
            if (!report.Ok)
                await store.Append(new EventStore.Draft("SecurityAlert", c.UserId, null, new JsonArray(),
                    new JsonObject { ["kind"] = "VerifyFailed", ["detail"] = new JsonObject { ["chainErrors"] = new JsonArray(report.ChainErrors.Select(x => (JsonNode)x).ToArray()), ["diffs"] = new JsonArray(report.Diffs.Select(d => (JsonNode)new JsonObject { ["table"] = d.Table, ["missing"] = d.Missing, ["extra"] = d.Extra }).ToArray()) } }));
            return Results.Json(report);
        });

        app.MapGet("/api/evidence-package", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || c.Role is not ("Shareholder" or "Supervisor" or "Operator")) return Deny("无权导出");
            var pkg = await verifier.EvidencePackage();
            return Results.Json(pkg);
        });

        app.MapPost("/api/anchor", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null || c.Role is not ("Operator" or "Supervisor")) return Deny("无权锚定");
            await store.Anchor($"{HashChain.Attribution} | local-anchor:{DateTime.UtcNow:O}");
            return Results.Json(new { ok = true, note = "MVP 本地锚定记录；生产环境替换为可信时间戳/司法存证 API" });
        });

        app.MapPost("/api/accounts/{id}/status", async (HttpRequest req, string id, JsonObject body) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            var draft = new EventStore.Draft("AccountStatusChanged", c.UserId, null,
                new JsonArray(new JsonObject { ["name"] = "账号管理操作记录", ["hash"] = HashChain.Sha256(id + body.ToJsonString()) }),
                new JsonObject { ["userId"] = id, ["enabled"] = body["enabled"]?.GetValue<bool>() ?? true });
            try
            {
                await using var conn = db.App();
                await Guard.Validate(conn, draft, c);
                var e = await store.Append(draft);
                return Results.Json(new { ok = true, eventId = e.EventId });
            }
            catch (GuardException g) { return Results.Json(new { error = g.Message, kind = g.Kind }, statusCode: 403); }
        });

        // ---------- 开发者赞助墙（独立于公司业务：不入事件链、不参与验真、公司清空不波及；append-only+哈希链防篡改） ----------
        app.MapGet("/api/sponsors", async () =>
        {
            await using var conn = db.App();
            var rows = await conn.Rows("SELECT name,amount,message,proof_hash,created_at,row_hash FROM dev.sponsorships ORDER BY id DESC LIMIT 200");
            var total = await conn.Scalar("SELECT COALESCE(SUM(amount),0) FROM dev.sponsorships");
            return Results.Json(new
            {
                total = Pg.D(total),
                items = rows.Select(r => new { name = Pg.S(r["name"]), amount = Pg.D(r["amount"]), message = Pg.S(r["message"]), proofHash = r["proof_hash"] as string, at = r["created_at"], hash = Pg.S(r["row_hash"]) })
            });
        });
        app.MapPost("/api/sponsors", async (JsonObject body) =>
        {
            var name = (body["name"]?.GetValue<string>() ?? "").Trim();
            var message = (body["message"]?.GetValue<string>() ?? "").Trim();
            var proof = body["proofHash"]?.GetValue<string>();
            if (name.Length == 0 || name.Length > 40) return Results.BadRequest(new { error = "请填写姓名/习惯名称（40字内）" });
            if (message.Length > 100) return Results.BadRequest(new { error = "留言100字内" });
            var amount = body["amount"] is JsonNode an ? Convert.ToDecimal(an.GetValue<double>()) : 0m;
            if (amount <= 0 || amount > 100000) return Results.BadRequest(new { error = "金额须为赞赏码固定档位正数" });
            await using var conn = db.App();
            var prev = (await conn.Scalar("SELECT row_hash FROM dev.sponsorships ORDER BY id DESC LIMIT 1")) as string ?? HashChain.Genesis;
            var at = DateTimeOffset.UtcNow;
            var rowHash = HashChain.Sha256($"{prev}|{name}|{amount}|{message}|{proof}|{at:O}");
            await conn.Exec("INSERT INTO dev.sponsorships(name,amount,message,proof_hash,prev_hash,row_hash,created_at) VALUES(@n,@a,@m,@p,@ph,@rh,@at)",
                ("n", name), ("a", amount), ("m", message), ("p", (object?)proof ?? DBNull.Value), ("ph", prev), ("rh", rowHash), ("at", at.UtcDateTime));
            return Results.Json(new { ok = true, hash = rowHash });
        });
        app.MapPost("/api/sponsor-proof", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType || req.Form.Files.Count == 0) return Results.BadRequest(new { error = "缺少文件" });
            var file = req.Form.Files[0];
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            if (bytes.Length > 5 * 1024 * 1024) return Results.BadRequest(new { error = "截图不超过5MB" });
            var hash = HashChain.Sha256(Convert.ToBase64String(bytes));
            var dir = Path.Combine(Environment.GetEnvironmentVariable("EQUI_EVIDENCE_DIR") ?? "/data/evidence", "dev-sponsor");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, hash);
            if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
            return Results.Json(new { hash });
        }).DisableAntiforgery();

        // ---------- 外部电商集成：WooCommerce 订单完成 webhook（HMAC 签名+幂等+归属操作员） ----------
        app.MapPost("/api/integrations/woocommerce", async (HttpRequest req) =>
        {
            using var sr = new StreamReader(req.Body);
            var body = await sr.ReadToEndAsync();
            var sig = req.Headers["X-WC-Webhook-Signature"].FirstOrDefault();
            var secret = Environment.GetEnvironmentVariable("EQUI_WOO_SECRET");
            var (ok, msg) = await EquiLedger.Engine.WooCommerce.Ingest(db, store, body, sig, secret);
            return Results.Json(new { ok, msg }, statusCode: ok ? 200 : 400);
        });

        app.MapGet("/api/health", () => Results.Json(new { ok = true, service = "EquiLedger", time = DateTime.UtcNow }));

        // ---------- 设立状态（公开）：已设立则前端不再显示设立向导 ----------
        app.MapGet("/api/setup-status", async () =>
        {
            var foundedAt = await Maintenance.FoundedAt(db);
            return Results.Json(new { founded = foundedAt != null, foundedAt, graceWindow = Maintenance.WithinGraceWindow(foundedAt) });
        });

        // ---------- 一个月内清空重来（仅操作员；窗口外永久关闭） ----------
        app.MapPost("/api/reset", async (HttpRequest req) =>
        {
            var c = await Auth(req, db);
            if (c is null) return Deny("未登录");
            if (c.Role != "Operator") return Deny("仅操作员可执行清空");
            var foundedAt = await Maintenance.FoundedAt(db);
            if (foundedAt is null) return Results.Json(new { error = "尚未设立，无需清空" }, statusCode: 400);
            if (!Maintenance.WithinGraceWindow(foundedAt))
                return Results.Json(new { error = "已超过设立后一个月的清空窗口，此后一切变更只能走决议事件", kind = "GraceWindowClosed" }, statusCode: 403);
            var dir = Environment.GetEnvironmentVariable("EQUI_EVIDENCE_DIR") ?? "/data/evidence";
            await store.WithLock(async () => { await Maintenance.ResetAll(db, dir); return 0; });
            return Results.Json(new { ok = true, note = "已清空并重建地基，请重新走设立向导" });
        });
    }
}
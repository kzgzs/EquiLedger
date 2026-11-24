// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Core;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

public static class ModuleCatalog
{
    public record Def(string Id, string Name, bool Mandatory, bool DefaultOn, (string kind, string value)[] Prereqs);
    public static readonly Def[] All =
    {
        new("M1", "股权与治理", true, true, Array.Empty<(string, string)>()),
        new("M2", "财务核算", true, true, Array.Empty<(string, string)>()),
        new("M3", "薪酬与人事", true, true, Array.Empty<(string, string)>()),
        new("M4", "税务", true, true, Array.Empty<(string, string)>()),
        new("M7", "合同与印章证照", true, true, Array.Empty<(string, string)>()),
        new("M11", "决议与投票", true, true, Array.Empty<(string, string)>()),
        new("M13", "社保与公积金", true, true, Array.Empty<(string, string)>()),
        new("M5", "采购与物料", false, true, Array.Empty<(string, string)>()),
        new("M6", "销售与提成", false, true, Array.Empty<(string, string)>()),
        new("M8", "资金与借贷", false, true, Array.Empty<(string, string)>()),
        new("M9", "关联交易", false, true, Array.Empty<(string, string)>()),
        new("M10", "资产", false, true, Array.Empty<(string, string)>()),
        new("M12", "自定义部门（食堂等）", false, false, new[] { ("ResolutionKind", "DeptSetup") }),
        new("M14", "股票与上市筹备", false, false, new[] { ("CompanyType", "股份有限公司"), ("ResolutionKind", "Listing") }),
        new("M15", "在线投资（私募融资）", false, false, new[] { ("ResolutionKind", "Financing"), ("ResolutionKind", "ComplianceCheck") }),
        new("M16", "知识产权", false, false, new[] { ("ResolutionKind", "ModuleAuth") }),
        new("M17", "行政办公", false, false, new[] { ("ResolutionKind", "ModuleAuth") }),
        new("M18", "法务与纠纷", false, false, new[] { ("ResolutionKind", "ModuleAuth") }),
        new("M19", "保险与风险", false, false, new[] { ("ResolutionKind", "ModuleAuth") }),
        new("M20", "对外投资与子公司", false, false, new[] { ("ResolutionKind", "ExternalInvest") }),
        new("M21", "安全生产与质量", false, false, new[] { ("IndustryTag", "制造"), ("ResolutionKind", "ModuleAuth") }),
    };
}

public static class SocialInsurance
{
    // 默认参数包（分城市规则可覆盖）：养老16/8 医疗8/2 失业0.5/0.5 工伤0.4/0 公积金7/7
    public static (decimal company, decimal personal) Rates(JsonObject? cityRule)
    {
        decimal g(string k, decimal def) => cityRule?[k] is JsonValue v && v.TryGetValue(out decimal d) ? d : def;
        var company = g("pensionC", 0.16m) + g("medicalC", 0.08m) + g("unemployC", 0.005m) + g("injuryC", 0.004m) + g("fundC", 0.07m);
        var personal = g("pensionP", 0.08m) + g("medicalP", 0.02m) + g("unemployP", 0.005m) + g("fundP", 0.07m);
        return (company, personal);
    }
    public static decimal Base(decimal monthly, JsonObject? cityRule)
    {
        var min = cityRule?["baseMin"] is JsonValue a && a.TryGetValue(out decimal x) ? x : 4000m;
        var max = cityRule?["baseMax"] is JsonValue b && b.TryGetValue(out decimal y) ? y : 30000m;
        return Math.Min(Math.Max(monthly, min), max);
    }
}

public static partial class Projection
{
    private static decimal R2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    private static async Task CompanyFounded(ApplyCtx c)
    {
        var co = (JsonObject)c.P["company"]!;
        await c.ExecJ("INSERT INTO proj.company(id,name,company_type,registered_capital,industry_tags,source_event_id) VALUES(1,@n,@t,@rc,@it,@e) ON CONFLICT DO NOTHING",
            new[] { ("it", co["industryTags"] ?? new JsonArray()) },
            ("n", co["name"]!.GetValue<string>()), ("t", co["type"]!.GetValue<string>()),
            ("rc", co["registeredCapital"]!.GetValue<decimal>()), ("e", c.Evt.EventId));

        foreach (JsonObject a in (JsonArray)c.P["accounts"]!)
        {
            var userId = a["userId"]?.GetValue<string>() ?? a["username"]!.GetValue<string>();
            var partyId = a["partyId"]?.GetValue<string>();
            if (partyId != null) await c.EnsureParty(partyId, a["partyName"]?.GetValue<string>() ?? a["displayName"]!.GetValue<string>());
            var (pub, priv) = Crypto.GenerateKeyPair();
            await c.Exec(@"INSERT INTO proj.accounts(user_id,username,display_name,role,party_id,pwd_hash,pub_key,priv_key,real_name,id_number,source_event_id)
                VALUES(@u,@un,@dn,@r,@p,@pw,@pub,@priv,@rn,@id,@e) ON CONFLICT (user_id) DO NOTHING",
                ("u", userId), ("un", a["username"]!.GetValue<string>()), ("dn", a["displayName"]!.GetValue<string>()),
                ("r", a["role"]!.GetValue<string>()), ("p", (object?)partyId ?? DBNull.Value),
                ("pw", Crypto.HashPassword(a["password"]!.GetValue<string>())), ("pub", pub), ("priv", priv),
                ("rn", (object?)a["realName"]?.GetValue<string>() ?? DBNull.Value), ("id", (object?)a["idNumber"]?.GetValue<string>() ?? DBNull.Value),
                ("e", c.Evt.EventId));
        }
        if (c.P["relations"] is JsonArray rels)
            foreach (JsonObject r in rels)
                await c.Exec("INSERT INTO proj.relations(from_party,to_party,relation,source_event_id) VALUES(@f,@t,@r,@e)",
                    ("f", r["from"]!.GetValue<string>()), ("t", r["to"]!.GetValue<string>()), ("r", r["relation"]!.GetValue<string>()), ("e", c.Evt.EventId));

        var positions = new List<ApplyCtx.Pos>();
        foreach (JsonObject a in (JsonArray)c.P["accounts"]!)
        {
            if (a["subscribed"] is JsonValue sv && sv.TryGetValue(out decimal sub) && sub > 0)
                positions.Add(new ApplyCtx.Pos { PartyId = a["partyId"]!.GetValue<string>(), Subscribed = sub, PaidIn = 0 });
        }
        if (c.P["charterRules"] is JsonArray rules)
            foreach (JsonObject r in rules)
                await InsertRule(c, r["ruleId"]?.GetValue<string>() ?? r["kind"]!.GetValue<string>(), r["kind"]!.GetValue<string>(),
                    r["subject"]?.GetValue<string>(), (JsonObject)r["params"]!, c.Evt.OccurredAt, "FOUNDING");
        if (positions.Count > 0) await c.SaveCap(positions);
        if (positions.Count == 1 && co["type"]!.GetValue<string>() == "有限责任公司")
        {
            await c.Todo("AnnualAudit", "一人有限责任公司法定年审：每年度终了编制财务会计报告并经会计师事务所审计；股东须能证明公司财产独立于个人财产（否则对公司债务承担连带责任）——工资/分红分线与全账入链即为独立性证据",
                new JsonObject { ["partyId"] = positions[0].PartyId });
            c.Impact("识别为一人有限责任公司：已生成年度审计待办；股东决定须采用书面形式并签名后置备于公司（本系统 ResolutionRecorded 即书面决定入链）");
        }
        if (co["type"]!.GetValue<string>() is "个体工商户" or "个人独资企业")
        {
            await c.Todo("OwnerSocialInsurance", "业主社保口径确认：业主/投资者本人可按灵活就业人员参加养老与医疗（非强制职工社保）；雇工必须依法参加职工社保。业主工资不得税前扣除（计税时自动纳税调增），对债务承担无限责任",
                new JsonObject());
            c.Impact($"识别为{co["type"]!.GetValue<string>()}：经营所得5%-35%（个体户≤200万部分减半至2027年底），不缴企业所得税；业主工资自动纳税调增");
        }
        if (co["type"]!.GetValue<string>() == "分支机构")
        {
            await c.Todo("BranchAccounting", "分支机构口径确认：不具有法人资格，民事责任由总公司承担；请书面明确独立核算/非独立核算口径与企业所得税汇总纳税分摊方式",
                new JsonObject());
            c.Impact("识别为分支机构：已生成核算口径待办；签约与责任归属见法务库 SYS-15");
        }

        foreach (var m in ModuleCatalog.All)
        {
            var prereqs = new JsonArray();
            foreach (var (k, v) in m.Prereqs) prereqs.Add(new JsonObject { ["kind"] = k, ["value"] = v });
            await c.ExecJ("INSERT INTO proj.module_states(module_id,name,state,mandatory,prereqs,source_event_id) VALUES(@m,@n,@s,@mand,@p,@e) ON CONFLICT DO NOTHING",
                new[] { ("p", (JsonNode?)prereqs) },
                ("m", m.Id), ("n", m.Name), ("s", m.DefaultOn ? "Enabled" : "Disabled"), ("mand", m.Mandatory), ("e", c.Evt.EventId));
        }
        if (c.P["whitelist"] is JsonArray wl)
            foreach (JsonObject w in wl)
                await c.Exec("INSERT INTO proj.whitelist_accounts(account_no,holder,source_event_id) VALUES(@a,@h,@e) ON CONFLICT DO NOTHING",
                    ("a", w["accountNo"]!.GetValue<string>()), ("h", w["holder"]!.GetValue<string>()), ("e", c.Evt.EventId));
        c.Impact("公司设立完成：治理地基启用，章程条款已固化，模块开关初始化");
    }

    private static async Task InsertRule(ApplyCtx c, string ruleId, string kind, string? subject, JsonObject parameters, DateTimeOffset effectiveFrom, string resolutionNo)
    {
        var ver = Convert.ToInt32(await c.Scalar("SELECT COALESCE(MAX(version),0)+1 FROM proj.rule_versions WHERE rule_id=@r", ("r", ruleId)));
        await c.Exec("UPDATE proj.rule_versions SET status='Superseded' WHERE rule_id=@r AND status='Active'", ("r", ruleId));
        await c.ExecJ(@"INSERT INTO proj.rule_versions(rule_id,version,kind,subject,params,effective_from,resolution_no,source_event_id)
            VALUES(@r,@v,@k,@s,@p,@f,@rn,@e)",
            new[] { ("p", (JsonNode?)parameters) },
            ("r", ruleId), ("v", ver), ("k", kind), ("s", (object?)subject ?? DBNull.Value),
            ("f", effectiveFrom.UtcDateTime), ("rn", resolutionNo), ("e", c.Evt.EventId));
        c.Impact($"规则 {ruleId} v{ver} 生效（{kind}，生效时间 {effectiveFrom:yyyy-MM-dd}，历史业务仍按旧版本计算）");
    }

    private static async Task AccountStatusChanged(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.accounts SET enabled=@en WHERE user_id=@u", ("en", c.Pb("enabled")), ("u", c.Ps("userId")));
        c.Impact($"账号 {c.Ps("userId")} 可用性变更：{(c.Pb("enabled") ? "启用" : "停用")}（管理员仅管账号，不触碰业务数据）");
    }

    private static async Task OperatorHandover(ApplyCtx c)
    {
        var nu = c.Ps("newOperatorUserId");
        await c.Exec("UPDATE proj.accounts SET role='FormerOperator' WHERE role='Operator'");
        await c.Exec("UPDATE proj.accounts SET role='Operator' WHERE user_id=@u", ("u", nu));
        c.Impact($"操作员移交：{nu} 就任唯一授权操作员（紧急决议双签依据入链）");
    }

    private static async Task RelationDeclared(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.relations(from_party,to_party,relation,source_event_id) VALUES(@f,@t,@r,@e)",
            ("f", c.Ps("fromParty")), ("t", c.Ps("toParty")), ("r", c.Ps("relation")), ("e", c.Evt.EventId));
        c.Impact($"关联关系申报：{c.Ps("fromParty")} —{c.Ps("relation")}→ {c.Ps("toParty")}（回避机制数据基础）");
    }

    private static async Task ResolutionProposed(ApplyCtx c)
    {
        await c.ExecJ(@"INSERT INTO proj.resolutions(resolution_no,title,kind,params,votes,passed,evidence,created_at,source_event_id)
            VALUES(@no,@t,@k,@p,'[]',FALSE,@ev,@at,@e)",
            new[] { ("p", c.P["params"] ?? new JsonObject()), ("ev", c.Evt.Evidence) },
            ("no", c.Ps("resolutionNo")), ("t", c.Ps("title")), ("k", c.Ps("kind")), ("at", c.Evt.OccurredAt.UtcDateTime), ("e", c.Evt.EventId));
        await c.Todo("Resolution", $"议案公示与表决：{c.Ps("title")}", new JsonObject { ["resolutionNo"] = c.Ps("resolutionNo") });
        c.Impact($"议案 {c.Ps("resolutionNo")} 已发起，进入公示与表决");
    }

    private static async Task ResolutionVoted(ApplyCtx c)
    {
        var no = c.Ps("resolutionNo");
        var rows = await c.Rows("SELECT * FROM proj.resolutions WHERE resolution_no=@n", ("n", no));
        if (rows.Count == 0) throw new InvalidOperationException("议案不存在");
        var res = rows[0];
        var votes = (JsonArray)Pg.J(res["votes"]);
        var partyId = c.Ps("partyId");
        var cap = await c.CurrentCap();
        var pos = cap.FirstOrDefault(p => p.PartyId == partyId);
        var weight = pos?.VotingRatio ?? 0m;
        var avoided = false;
        var subjectParty = Pg.J(res["params"])["subjectParty"]?.GetValue<string>();
        if (subjectParty != null)
        {
            if (subjectParty == partyId) avoided = true;
            else
            {
                var rel = await c.Scalar("SELECT COUNT(*) FROM proj.relations WHERE (from_party=@a AND to_party=@b) OR (from_party=@b AND to_party=@a)", ("a", partyId), ("b", subjectParty));
                if (Convert.ToInt64(rel) > 0) avoided = true;
            }
        }
        votes.Add(new JsonObject { ["partyId"] = partyId, ["approve"] = c.Pb("approve"), ["weight"] = avoided ? 0m : weight, ["avoided"] = avoided, ["at"] = c.Evt.OccurredAt.ToString("O") });
        var threshold = 0.5m;
        var vr = await c.ActiveRule("Charter.Voting");
        if (vr?["threshold"] is JsonValue tv && tv.TryGetValue(out decimal t)) threshold = t;
        var kind = Pg.S(res["kind"]);
        if (kind is "CapitalIncrease" or "CharterAmend" or "Guarantee" or "Merger" or "Dissolution") threshold = Math.Max(threshold, 2m / 3m);
        var approveSum = votes.Where(v => v!["approve"]!.GetValue<bool>()).Sum(v => v!["weight"]!.GetValue<decimal>());
        var passed = approveSum > threshold;
        await c.ExecJ("UPDATE proj.resolutions SET votes=@v, passed=@p WHERE resolution_no=@n",
            new[] { ("v", (JsonNode?)votes) }, ("p", passed), ("n", no));
        c.Impact(avoided
            ? $"表决回避：{partyId} 与议案利益相关，自动剔除计票并留痕（议案 {no}）"
            : $"表决记录：{partyId} {(c.Pb("approve") ? "赞成" : "反对")} 权重 {weight:P2}（议案 {no}，累计赞成 {approveSum:P2}，门槛 {threshold:P2}，{(passed ? "已通过" : "未通过")}）");
    }

    private static async Task ResolutionRecorded(ApplyCtx c)
    {
        await c.ExecJ(@"INSERT INTO proj.resolutions(resolution_no,title,kind,params,votes,passed,evidence,created_at,source_event_id)
            VALUES(@no,@t,@k,@p,@v,@pa,@ev,@at,@e) ON CONFLICT DO NOTHING",
            new[] { ("p", c.P["params"] ?? new JsonObject()), ("v", c.P["votes"] ?? new JsonArray()), ("ev", c.Evt.Evidence) },
            ("no", c.Ps("resolutionNo")), ("t", c.Ps("title")), ("k", c.Ps("kind")), ("pa", c.Pb("passed")),
            ("at", c.Evt.OccurredAt.UtcDateTime), ("e", c.Evt.EventId));
        c.Impact($"会议决议 {c.Ps("resolutionNo")} 入链存证：{c.Ps("title")}（{(c.Pb("passed") ? "通过" : "未通过")}）");
    }

    private static async Task RuleActivated(ApplyCtx c)
    {
        var eff = c.P["effectiveFrom"] is JsonValue v && DateTimeOffset.TryParse(v.GetValue<string>(), out var dt) ? dt : c.Evt.OccurredAt;
        await InsertRule(c, c.Ps("ruleId"), c.Ps("kind"), c.P["subject"]?.GetValue<string>(), (JsonObject)c.P["params"]!, eff, c.Evt.ResolutionNo ?? "");
    }

    private static async Task ContractSigned(ApplyCtx c)
    {
        DateOnly? expires = c.P["expiresOn"] is JsonValue ev && DateOnly.TryParse(ev.GetValue<string>(), out var d) ? d : null;
        await c.ExecJ(@"INSERT INTO proj.contracts(contract_id,category,contract_type,parties,secrecy,doc_hash,clause_rule_ids,status,signed_at,expires_on,supplement_of,source_event_id)
            VALUES(@id,@cat,@ct,@pa,@se,@dh,'[]','Signed',@sa,@ex,@so,@e)",
            new[] { ("pa", c.P["parties"] ?? new JsonArray()) },
            ("id", c.Ps("contractId")), ("cat", c.Ps("category")), ("ct", c.Ps("contractType")),
            ("se", c.Ps("secrecy")), ("dh", c.Ps("docHash")), ("sa", c.Evt.OccurredAt.UtcDateTime),
            ("ex", (object?)expires ?? DBNull.Value),
            ("so", (object?)c.P["supplementOf"]?.GetValue<string>() ?? DBNull.Value), ("e", c.Evt.EventId));
        if (c.P["supplementOf"] is JsonValue so)
            await c.Exec("UPDATE proj.contracts SET status='Superseded' WHERE contract_id=@c", ("c", so.GetValue<string>()));
        if (expires != null)
            await c.Todo("ContractExpiry", $"合同到期预警：{c.Ps("contractId")}（{expires}）", new JsonObject { ["contractId"] = c.Ps("contractId"), ["expiresOn"] = expires.ToString() }, idSuffix: c.Ps("contractId"));
        c.Impact($"合同签署入链：{c.Ps("contractId")}（{c.Ps("contractType")}，密级 {c.Ps("secrecy")}，文本哈希 {c.Ps("docHash")[..Math.Min(12, c.Ps("docHash").Length)]}…）");
    }

    private static async Task ContractTerminated(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.contracts SET status='Terminated' WHERE contract_id=@c", ("c", c.Ps("contractId")));
        c.Impact($"合同解除归档：{c.Ps("contractId")}（原文本永久保留）");
    }

    private static async Task ContractAccessed(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.access_logs(user_id,target,action,at) VALUES(@u,@t,'ReadPrivate',@at)",
            ("u", c.Evt.ActorId), ("t", c.Ps("contractId")), ("at", c.Evt.OccurredAt.UtcDateTime));
        c.Impact($"私密合同访问留痕：{c.Evt.ActorId} 查阅 {c.Ps("contractId")}（目的：{c.P["purpose"]?.GetValue<string>() ?? "未注明"}）");
    }

    private static async Task EvidenceUploaded(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.evidence_files(file_hash,file_name,stored_path,uploaded_by,uploaded_at) VALUES(@h,@n,@p,@u,@t) ON CONFLICT DO NOTHING",
            ("h", c.Ps("fileHash")), ("n", c.Ps("fileName")), ("p", c.Ps("storedPath")), ("u", c.Evt.ActorId), ("t", c.Evt.OccurredAt.UtcDateTime));
    }

    private static async Task ShareSubscription(ApplyCtx c)
    {
        var partyId = c.Ps("partyId");
        await c.EnsureParty(partyId, c.P["partyName"]?.GetValue<string>() ?? partyId);
        var cap = await c.CurrentCap();
        // 两种入股口径：① 直接认缴额 subscribed；② 目标持股比例 targetRatio（自动换算新增注册资本，溢价另走实缴 premium）
        var sub = c.Pd("subscribed");
        var mode = "认缴额";
        if (sub <= 0 && c.P["targetRatio"] != null)
        {
            var r = c.Pd("targetRatio");
            if (r <= 0 || r >= 1) throw new InvalidOperationException("targetRatio 须在 0–1 之间");
            var total = cap.Where(p => p.Status == "Normal").Sum(p => p.Subscribed);
            sub = R2(total * r / (1 - r));
            mode = $"目标比例 {r:P2} 换算";
        }
        if (sub <= 0) throw new InvalidOperationException("须填写 subscribed 或 targetRatio");
        var exist = cap.FirstOrDefault(p => p.PartyId == partyId);
        if (exist != null) exist.Subscribed += sub;
        else cap.Add(new ApplyCtx.Pos { PartyId = partyId, Subscribed = sub });
        await c.SaveCap(cap);
        await c.Todo("BusinessRegistry", "工商变更待办：认缴出资登记", new JsonObject { ["partyId"] = partyId, ["subscribed"] = sub });
        c.Impact($"增资认缴 {sub}（{partyId}，{mode}）；如为溢价增资，实缴时溢价部分自动计入资本公积（归全体股东共享，不缴企业所得税）");
    }

    private static async Task CapitalPaidIn(ApplyCtx c)
    {
        var partyId = c.Ps("partyId");
        var amount = c.Pd("amount");
        var premium = c.Pd("premium");
        var cap = await c.CurrentCap();
        var pos = cap.First(p => p.PartyId == partyId);
        pos.PaidIn += amount;
        await c.SaveCap(cap);
        await c.Ledger(c.Period, "Bank", amount + premium, 0, $"实缴到账 {partyId}" + (premium > 0 ? $"（含溢价 {premium}）" : ""));
        await c.Ledger(c.Period, "PaidInCapital", 0, amount, $"实缴到账 {partyId}");
        if (premium > 0)
        {
            await c.Ledger(c.Period, "CapitalReserve", 0, premium, $"资本溢价 {partyId}（增资扩股）");
            c.Impact($"溢价 {premium} 计入资本公积——资本溢价：归全体股东共享，属资本性投入不确认收入、不缴企业所得税；营业账簿印花税按实收资本+资本公积增加额计税");
        }
        c.Impact($"实缴到账 {amount}（{partyId}），银行回单入链，实缴比例与分红基数已重算");
    }

    private static async Task InvestmentMade(ApplyCtx c)
    {
        var target = c.Ps("target");
        var amount = c.Pd("amount");
        var method = c.P["method"]?.GetValue<string>() ?? "cost";
        var ratio = c.Pd("shareRatio");
        await c.Exec(@"INSERT INTO proj.external_investments(invest_id,target,method,share_ratio,cost,carrying,source_event_id)
            VALUES(@i,@t,@m,@r,@a,@a,@e) ON CONFLICT (invest_id) DO NOTHING",
            ("i", "INV-" + c.Eid), ("t", target), ("m", method), ("r", ratio), ("a", amount), ("e", Guid.Parse(c.Eid)));
        await c.Ledger(c.Period, "LongTermEquityInvestment", amount, 0, $"对外投资 {target}");
        await c.Ledger(c.Period, "Bank", 0, amount, $"对外投资 {target}");
        c.Impact(method == "equity"
            ? $"权益法核算：按持股比例 {ratio:P2} 确认应享有被投资单位净损益份额（借长投-损益调整 贷投资收益），宣告分红减少账面价值；税会差异须纳税调整"
            : "成本法核算：按初始投资成本计价，被投资单位宣告分派的现金股利确认当期投资收益");
        c.Impact("税务：居民企业直接投资于其他居民企业取得的股息红利免征企业所得税（企业所得税法第26条）；上市股票持有不足12个月的除外");
    }

    private static async Task InvestmentReturnReceived(ApplyCtx c)
    {
        var target = c.Ps("target");
        var amount = c.Pd("amount");
        var rows = await c.Rows("SELECT invest_id,method,carrying FROM proj.external_investments WHERE target=@t ORDER BY invest_id LIMIT 1", ("t", target));
        await c.Ledger(c.Period, "Bank", amount, 0, $"投资回报 {target}");
        if (rows.Count > 0 && Pg.S(rows[0]["method"]) == "equity")
        {
            var carrying = Pg.D(rows[0]["carrying"]) - amount;
            await c.Exec("UPDATE proj.external_investments SET carrying=@c WHERE invest_id=@i", ("c", carrying), ("i", Pg.S(rows[0]["invest_id"])));
            await c.Ledger(c.Period, "LongTermEquityInvestment", 0, amount, $"权益法分红冲减账面 {target}");
            c.Impact($"权益法：收到分红 {amount} 冲减长期股权投资账面价值（不确认投资收益），余额 {carrying}");
        }
        else
        {
            await c.Ledger(c.Period, "InvestmentIncome", 0, amount, $"投资收益 {target}");
            c.Impact($"成本法：确认投资收益 {amount}；居民企业直接投资股息红利免征企业所得税，申报时做免税纳税调减");
        }
    }

    private static async Task ShareTransfer(ApplyCtx c)
    {
        var from = c.Ps("fromParty"); var to = c.Ps("toParty");
        var capital = c.Pd("capitalAmount"); var price = c.Pd("price");
        await c.EnsureParty(to, c.P["toName"]?.GetValue<string>() ?? to);
        var cap = await c.CurrentCap();
        var fp = cap.First(p => p.PartyId == from);
        var movePaid = fp.Subscribed > 0 ? R2(fp.PaidIn * capital / fp.Subscribed) : 0m;
        fp.Subscribed -= capital; fp.PaidIn -= movePaid;
        var tp = cap.FirstOrDefault(p => p.PartyId == to);
        if (tp != null) { tp.Subscribed += capital; tp.PaidIn += movePaid; }
        else cap.Add(new ApplyCtx.Pos { PartyId = to, Subscribed = capital, PaidIn = movePaid });
        await c.SaveCap(cap);

        var iit = TaxCalc.TransferIit(price, capital, 0m);
        await c.Calc("TransferIIT", from, iit);
        var stamp = TaxCalc.StampDutyTransfer(price, true, DateOnly.FromDateTime(c.Evt.OccurredAt.UtcDateTime));
        await c.Calc("StampDuty", $"{from}->{to}", stamp);
        await c.Todo("TaxFiling", $"股权转让个税代扣申报（{from}，{iit.Amount} 元）", new JsonObject { ["amount"] = iit.Amount });
        await c.Todo("TaxFiling", $"印花税申报（产权转移书据 {stamp.Amount} 元）", new JsonObject { ["amount"] = stamp.Amount }, idSuffix: c.Eid + "-s");
        await c.Todo("BusinessRegistry", "工商变更待办：股权转让登记", new JsonObject { ["from"] = from, ["to"] = to, ["capital"] = capital });
    }

    private static async Task ShareExit(ApplyCtx c)
    {
        var partyId = c.Ps("partyId");
        var capital = c.Pd("capitalAmount");
        var cap = await c.CurrentCap();
        var pos = cap.First(p => p.PartyId == partyId);
        var totalSub = cap.Where(p => p.Status is "Normal" or "Deceased").Sum(p => p.Subscribed);
        var ratio = totalSub > 0 ? capital / totalSub : 0m;
        var paidPortion = pos.Subscribed > 0 ? R2(pos.PaidIn * capital / pos.Subscribed) : 0m;

        var rule = await c.ActiveRule("Charter.ExitValuation");
        var method = rule?["method"]?.GetValue<string>() ?? "NetAsset";
        var rows = new List<DerivationRow>();
        decimal value;
        if (method == "PaidInMultiple")
        {
            var mult = rule?["multiplier"] is JsonValue mv && mv.TryGetValue(out decimal m) ? m : 1m;
            value = R2(paidPortion * mult);
            rows.Add(new("约定倍数法", $"实缴 {paidPortion} × 约定倍数 {mult} = {value}", value, ApplyCtx.RuleRef(rule), "股东协议退股估值条款（入股时固化）"));
        }
        else
        {
            var na = await c.NetAssets();
            value = R2(na * ratio);
            rows.Add(new("取数", $"最新账面净资产 = {na}（资产−负债，投影自复式分录）", na, ApplyCtx.RuleRef(rule), "财务模块事件流"));
            rows.Add(new("净资产法", $"{na} × 退出持股比例 {ratio:P4} = {value}", value, ApplyCtx.RuleRef(rule), "股东协议退股估值条款（入股时固化）"));
        }
        var outcome = new CalcOutcome(value, rows);
        await c.Calc("ExitValuation", partyId, outcome);

        pos.Subscribed -= capital; pos.PaidIn -= paidPortion;
        if (pos.Subscribed <= 0) { pos.Status = "Exited"; pos.Subscribed = 0; pos.PaidIn = 0; }
        await c.SaveCap(cap);

        await c.Ledger(c.Period, "PaidInCapital", capital, 0, $"退股减资 {partyId}");
        if (value != capital) await c.Ledger(c.Period, "ExitGainLoss", value > capital ? value - capital : 0, value < capital ? capital - value : 0, $"退股估值差额 {partyId}");
        await c.Ledger(c.Period, "ExitPayable", 0, value, $"应付退股款 {partyId}");

        var installments = rule?["installments"] is JsonValue iv && iv.TryGetValue(out int n) && n > 0 ? n : 3;
        var plan = new JsonArray();
        for (var i = 1; i <= installments; i++)
            plan.Add(new JsonObject { ["期次"] = i, ["金额"] = R2(value / installments), ["计划月"] = c.Evt.OccurredAt.AddMonths(i).ToString("yyyy-MM") });
        await c.Todo("ExitPayment", $"退股款分期支付计划（{partyId}，共 {value} 元 {installments} 期）", new JsonObject { ["plan"] = plan });

        var iit = TaxCalc.TransferIit(value, paidPortion, 0m);
        await c.Calc("ExitIIT", partyId, iit);
        var stamp = TaxCalc.StampDutyTransfer(value, true, DateOnly.FromDateTime(c.Evt.OccurredAt.UtcDateTime));
        await c.Calc("ExitStamp", partyId, stamp);
        await c.Todo("TaxFiling", $"退股个税代扣申报（{partyId}，{iit.Amount} 元）", new JsonObject { ["amount"] = iit.Amount });
        await c.Todo("BusinessRegistry", "工商变更待办：退股减资 + 章程修正案", new JsonObject { ["partyId"] = partyId });
        c.Impact("资金流出计划更新：应付退股款进入现金预警监测");
    }

    private static async Task ShareholderDeceased(ApplyCtx c)
    {
        var partyId = c.Ps("partyId");
        await c.Exec("UPDATE proj.accounts SET frozen=TRUE WHERE party_id=@p", ("p", partyId));
        var cap = await c.CurrentCap();
        var pos = cap.First(p => p.PartyId == partyId);
        pos.Status = "Deceased";
        await c.SaveCap(cap);
        var rule = await c.ActiveRule("Charter.Inheritance");
        var mode = rule?["mode"]?.GetValue<string>() ?? "InheritQualification";
        await c.Todo("Inheritance", $"股东离世继承处理（{partyId}，章程模式：{mode}）",
            new JsonObject { ["partyId"] = partyId, ["mode"] = mode, ["说明"] = "账号已即时冻结；继承人确定前表决权不计入基数，分红计提挂账专户" });
        c.Impact($"股东 {partyId} 账号冻结，继承人确定前分红挂账专户，绝不悬空");
    }

    private static async Task DividendApproved(ApplyCtx c)
    {
        var planId = c.Ps("planId");
        var rows = await c.Rows("SELECT * FROM proj.dividend_plans WHERE plan_id=@p", ("p", planId));
        if (rows.Count == 0) throw new InvalidOperationException("分红方案不存在");
        var plan = rows[0];
        var allocs = (JsonArray)Pg.J(plan["allocations"]);
        decimal totalTax = 0, totalNet = 0, suspense = 0;
        var listed = await c.Scalar("SELECT state FROM proj.module_states WHERE module_id='M14'");
        foreach (JsonObject a in allocs)
        {
            var gross = a["gross"]!.GetValue<decimal>();
            if (a["suspended"]?.GetValue<bool>() == true) { suspense += gross; continue; }
            int? holdMonths = null;
            if (Pg.S(listed) == "Enabled" && a["holdingMonths"] is JsonValue hv && hv.TryGetValue(out int hm)) holdMonths = hm;
            var iit = TaxCalc.DividendIit(gross, holdMonths);
            await c.Calc("DividendIIT", a["partyId"]!.GetValue<string>(), iit);
            totalTax += iit.Amount; totalNet += gross - iit.Amount;
            a["iit"] = iit.Amount; a["net"] = gross - iit.Amount;
        }
        var total = allocs.Sum(a => a!["gross"]!.GetValue<decimal>());
        await c.Ledger(c.Period, "ProfitDistribution", total, 0, $"利润分配 {planId}");
        if (totalNet > 0) await c.Ledger(c.Period, "DividendPayable", 0, totalNet, $"应付股利 {planId}");
        if (totalTax > 0) await c.Ledger(c.Period, "TaxPayable", 0, totalTax, $"股息红利个税代扣 {planId}");
        if (suspense > 0) await c.Ledger(c.Period, "DividendSuspense", 0, suspense, $"继承人未定分红挂账专户 {planId}");
        await c.ExecJ("UPDATE proj.dividend_plans SET status='Approved', allocations=@a, resolution_no=@r WHERE plan_id=@p",
            new[] { ("a", (JsonNode?)allocs) }, ("r", c.Evt.ResolutionNo ?? ""), ("p", planId));
        await c.Todo("TaxFiling", $"分红个税代扣申报（合计 {totalTax} 元，未申报不得标记分红完成）", new JsonObject { ["planId"] = planId, ["amount"] = totalTax }, idSuffix: planId);
        await c.Todo("DividendPayment", $"分红支付计划（实付合计 {totalNet} 元）", new JsonObject { ["planId"] = planId, ["allocations"] = allocs.DeepClone() }, idSuffix: planId);
    }

    private static async Task DividendPaid(ApplyCtx c)
    {
        var planId = c.Ps("planId");
        var rows = await c.Rows("SELECT allocations FROM proj.dividend_plans WHERE plan_id=@p AND status='Approved'", ("p", planId));
        if (rows.Count == 0) throw new InvalidOperationException("方案未经决议批准");
        var allocs = (JsonArray)Pg.J(rows[0]["allocations"]);
        var net = allocs.Where(a => a!["suspended"]?.GetValue<bool>() != true).Sum(a => a!["net"]?.GetValue<decimal>() ?? 0m);
        await c.Ledger(c.Period, "DividendPayable", net, 0, $"分红支付 {planId}");
        await c.Ledger(c.Period, "Bank", 0, net, $"分红支付 {planId}");
        await c.Exec("UPDATE proj.dividend_plans SET status='Paid' WHERE plan_id=@p", ("p", planId));
        c.Impact($"分红已支付 {net} 元（{planId}），代扣个税已申报");
    }

    private static async Task RevenueRecorded(ApplyCtx c)
    {
        var amount = c.Pd("amount");
        var period = c.P["period"]?.GetValue<string>() ?? c.Period;
        var before = Pg.D(await c.Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='Revenue' AND period=@p", ("p", period)));
        var at = DateOnly.FromDateTime(c.Evt.OccurredAt.UtcDateTime);
        var vatBefore = TaxCalc.VatSmallScale(before, at).Amount;
        var vatAfter = TaxCalc.VatSmallScale(before + amount, at);
        var vatDelta = vatAfter.Amount - vatBefore;
        var net = amount - vatDelta;
        await c.Ledger(period, "Bank", amount, 0, $"销售收入回款 {c.Ps("customer")}");
        await c.Ledger(period, "Revenue", 0, net, $"收入确认 {c.Ps("customer")}（价税分离）");
        if (vatDelta != 0) await c.Ledger(period, "TaxPayable", 0, vatDelta, $"增值税计提 {period}");
        await c.Calc("VAT", period, vatAfter);
        await c.Todo("TaxFiling", $"增值税申报（{period}，本月累计应纳 {vatAfter.Amount} 元）", new JsonObject { ["period"] = period, ["amount"] = vatAfter.Amount }, idSuffix: period);
    }

    private static async Task ExpenseRecorded(ApplyCtx c)
    {
        var amount = c.Pd("amount");
        var period = c.P["period"]?.GetValue<string>() ?? c.Period;
        await c.Ledger(period, "Expense", amount, 0, $"{c.Ps("category")}：{c.P["memo"]?.GetValue<string>() ?? ""}（收款方 {c.Ps("payee")}）");
        await c.Ledger(period, "Bank", 0, amount, $"付款 {c.Ps("payee")}");
        var related = await c.Scalar(@"SELECT COUNT(*) FROM proj.relations r JOIN proj.parties p ON p.name=@payee WHERE r.relation='Control'", ("payee", c.Ps("payee")));
        if (Convert.ToInt64(related) > 0)
            await c.Todo("RelatedParty", $"关联交易审查：付款对象 {c.Ps("payee")} 疑似关联方", new JsonObject { ["amount"] = amount, ["payee"] = c.Ps("payee") });
        if (c.P["routineCategory"] is JsonValue rc)
        {
            var month = c.Period; var cat = rc.GetValue<string>();
            await c.Exec(@"INSERT INTO proj.routine_usage(month,category,used) VALUES(@m,@c,@u)
                ON CONFLICT (month,category) DO UPDATE SET used=proj.routine_usage.used+@u", ("m", month), ("c", cat), ("u", amount));
        }
        c.Impact($"费用入账 {amount}（{c.Ps("category")}），依据与凭证入链");
    }

    private static async Task SalesOrderConfirmed(ApplyCtx c)
    {
        var emp = c.Ps("employeeId");
        var rule = await c.ActiveRule("Commission.Rate", emp);
        await c.Exec(@"INSERT INTO proj.sales_orders(order_id,customer,amount,employee_id,commission_rule_id,commission_rule_version,confirmed_at,source_event_id)
            VALUES(@o,@cu,@a,@emp,@rid,@rv,@t,@e)",
            ("o", c.Ps("orderId")), ("cu", c.Ps("customer")), ("a", c.Pd("amount")), ("emp", emp),
            ("rid", (object?)ApplyCtx.RuleRef(rule) ?? DBNull.Value),
            ("rv", rule?["__version"] is JsonValue vv ? vv.GetValue<int>() : (object)DBNull.Value),
            ("t", c.Evt.OccurredAt.UtcDateTime), ("e", c.Evt.EventId));
        await c.Ledger(c.Period, "AR", c.Pd("amount"), 0, $"应收确认 {c.Ps("orderId")}");
        await c.Ledger(c.Period, "Revenue", 0, c.Pd("amount"), $"收入确认 {c.Ps("orderId")}（提成规则版本已锁定）");
        c.Impact($"订单 {c.Ps("orderId")} 确认，提成按签单时规则版本 {ApplyCtx.RuleRef(rule)} 锁定，任何人无法追溯改比例");
    }

    private static async Task CollectionReceived(ApplyCtx c)
    {
        var orderId = c.Ps("orderId"); var amount = c.Pd("amount");
        var rows = await c.Rows("SELECT * FROM proj.sales_orders WHERE order_id=@o", ("o", orderId));
        if (rows.Count == 0) throw new InvalidOperationException("订单不存在");
        var order = rows[0];
        await c.Exec("UPDATE proj.sales_orders SET collected=collected+@a WHERE order_id=@o", ("a", amount), ("o", orderId));
        await c.Ledger(c.Period, "Bank", amount, 0, $"回款 {orderId}");
        await c.Ledger(c.Period, "AR", 0, amount, $"应收核销 {orderId}");
        var ruleRef = Pg.S(order["commission_rule_id"]);
        decimal rate = 0m;
        if (ruleRef.Contains('v'))
        {
            var parts = ruleRef.Split('v');
            var rv = await c.Rows("SELECT params FROM proj.rule_versions WHERE rule_id=@r AND version=@v", ("r", parts[0]), ("v", int.Parse(parts[1])));
            if (rv.Count > 0 && Pg.J(rv[0]["params"])["rate"] is JsonValue jv && jv.TryGetValue(out decimal rr)) rate = rr;
        }
        var commission = R2(amount * rate);
        if (commission > 0)
        {
            await c.Exec("INSERT INTO proj.commissions(id,employee_id,order_id,amount,period,rule_ref,source_event_id) VALUES(@id,@emp,@o,@a,@p,@r,@e)",
                ("id", $"COM-{c.Eid}"), ("emp", Pg.S(order["employee_id"])), ("o", orderId), ("a", commission), ("p", c.Period), ("r", ruleRef), ("e", c.Evt.EventId));
            await c.Todo("Commission", $"提成计提 {commission} 元（{Pg.S(order["employee_id"])}，按锁定期规则 {ruleRef}），并入当期工资代扣个税", new JsonObject { ["amount"] = commission });
        }
        c.Impact($"回款 {amount}（{orderId}）→ 应收核销 → 提成 {commission}（规则 {ruleRef}）");
    }

    private static async Task BankFlowImported(ApplyCtx c)
    {
        foreach (JsonObject f in (JsonArray)c.P["flows"]!)
        {
            var refId = f["refEventId"]?.GetValue<string>();
            var matched = false;
            if (refId != null && Guid.TryParse(refId, out var g))
                matched = Convert.ToInt64(await c.Scalar("SELECT COUNT(*) FROM events.event_log WHERE event_id=@e", ("e", g))) > 0;
            var status = matched ? "Matched" : "UnmatchedException";
            await c.Exec("INSERT INTO proj.bank_flows(flow_id,direction,counterparty,amount,flow_date,matched_event,status,source_event_id) VALUES(@f,@d,@cp,@a,@dt,@m,@s,@e) ON CONFLICT DO NOTHING",
                ("f", f["flowId"]!.GetValue<string>()), ("d", f["direction"]!.GetValue<string>()), ("cp", f["counterparty"]!.GetValue<string>()),
                ("a", f["amount"]!.GetValue<decimal>()), ("dt", DateOnly.Parse(f["flowDate"]!.GetValue<string>())),
                ("m", (object?)(matched ? Guid.Parse(refId!) : DBNull.Value)), ("s", status), ("e", c.Evt.EventId));
            if (!matched)
                await c.Todo("BankException", $"流水异常待查：{f["counterparty"]!.GetValue<string>()} {f["amount"]!.GetValue<decimal>()} 元无业务事件匹配（防体外循环）",
                    new JsonObject { ["flow"] = f.DeepClone() }, idSuffix: f["flowId"]!.GetValue<string>());
        }
        c.Impact("银行流水导入完成，未匹配流水已挂异常待查");
    }

    private static async Task PeriodClosed(ApplyCtx c)
    {
        var period = c.Ps("period");
        await c.Exec("INSERT INTO proj.period_locks(period,source_event_id) VALUES(@p,@e) ON CONFLICT DO NOTHING", ("p", period), ("e", c.Evt.EventId));
        c.Impact($"期间 {period} 结账锁定：此后任何调整只能做当期更正事件（红字冲销，原值永久可见）");
    }

    private static async Task CorrectionRecorded(ApplyCtx c)
    {
        var target = Guid.Parse(c.Ps("targetEventId"));
        var entries = await c.Rows("SELECT * FROM proj.ledger WHERE source_event_id=@t AND reversed_by IS NULL", ("t", target));
        foreach (var e in entries)
        {
            await c.Ledger(c.Period, Pg.S(e["account"]), Pg.D(e["credit"]), Pg.D(e["debit"]), $"红字冲销：{Pg.S(e["memo"])}（原因：{c.Ps("reason")}）");
            await c.Exec("UPDATE proj.ledger SET reversed_by=(SELECT MAX(id) FROM proj.ledger) WHERE id=@i", ("i", e["id"]));
        }
        c.Impact($"更正事件：原记录永久保留，红字冲销 {entries.Count} 条分录（依据+决议入链）");
    }

    private static async Task EmployeeHired(ApplyCtx c)
    {
        await c.EnsureParty(c.Ps("partyId"), c.Ps("name"));
        var isSh = Convert.ToInt64(await c.Scalar("SELECT COUNT(*) FROM proj.cap_positions cp JOIN proj.cap_versions cv ON cp.cap_version=cv.version WHERE cp.party_id=@p AND cp.status='Normal' AND cv.version=(SELECT MAX(version) FROM proj.cap_versions)", ("p", c.Ps("partyId")))) > 0;
        // 员工档案：除结构化字段外的资料（性别/年龄/部门/技能/薪酬结构/社保状态等）整体存入 profile
        var known = new HashSet<string> { "employeeId", "partyId", "name", "position", "hireDate", "contractId", "city" };
        var profile = new JsonObject();
        foreach (var kv in c.P.AsObject()) if (!known.Contains(kv.Key) && kv.Value != null) profile[kv.Key] = kv.Value.DeepClone();
        await c.Exec(@"INSERT INTO proj.employees(employee_id,party_id,name,position,hire_date,is_shareholder,social_insurance_city,profile,source_event_id)
            VALUES(@id,@p,@n,@pos,@h,@sh,@city,@prof::jsonb,@e)",
            ("id", c.Ps("employeeId")), ("p", c.Ps("partyId")), ("n", c.Ps("name")),
            ("pos", (object?)c.P["position"]?.GetValue<string>() ?? DBNull.Value),
            ("h", DateOnly.Parse(c.Ps("hireDate"))), ("sh", isSh),
            ("city", c.P["city"]?.GetValue<string>() ?? "default"),
            ("prof", profile.Count > 0 ? (object)profile.ToJsonString() : DBNull.Value), ("e", c.Evt.EventId));
        await c.Todo("SocialInsurance", $"参保登记待办：{c.Ps("name")}（入职当月核定基数）", new JsonObject { ["employeeId"] = c.Ps("employeeId") });
        if (isSh) c.Impact($"股东兼任员工：{c.Ps("name")} 双身份分账（工资走薪酬线，分红走股权线，绝不混同）");
        c.Impact($"员工入职：{c.Ps("name")}，劳动合同 {c.Ps("contractId")} 已绑定");
    }

    private static async Task BonusGranted(ApplyCtx c)
    {
        await c.Exec("INSERT INTO proj.bonuses(id,employee_id,amount,reason,period,resolution_no,source_event_id) VALUES(@id,@emp,@a,@r,@p,@rn,@e)",
            ("id", $"BON-{c.Eid}"), ("emp", c.Ps("employeeId")), ("a", c.Pd("amount")), ("r", c.Ps("reason")),
            ("p", c.P["period"]?.GetValue<string>() ?? c.Period), ("rn", c.Evt.ResolutionNo ?? ""), ("e", c.Evt.EventId));
        c.Impact($"一次性奖金事件：{c.Ps("employeeId")} {c.Pd("amount")} 元（决议 {c.Evt.ResolutionNo}）——奖金是事件，不改工资规则版本");
    }

    private static async Task PayrollRun(ApplyCtx c)
    {
        var period = c.Ps("period");
        var year = period[..4];
        var employees = await c.Rows("SELECT * FROM proj.employees WHERE leave_date IS NULL OR leave_date >= @pe ORDER BY employee_id", ("pe", DateOnly.Parse(period + "-01").AddMonths(1).AddDays(-1)));
        var lines = new JsonArray();
        decimal totalGross = 0, totalNet = 0, totalIit = 0, totalSiP = 0, totalSiC = 0;
        foreach (var emp in employees)
        {
            var empId = Pg.S(emp["employee_id"]);
            var wageRule = await c.ActiveRule("Payroll.Standard", empId);
            var monthly = wageRule?["monthly"] is JsonValue wv && wv.TryGetValue(out decimal w) ? w : 0m;
            var bonuses = Pg.D(await c.Scalar("SELECT COALESCE(SUM(amount),0) FROM proj.bonuses WHERE employee_id=@emp AND period=@p AND status='Pending'", ("emp", empId), ("p", period)));
            var commissions = Pg.D(await c.Scalar("SELECT COALESCE(SUM(amount),0) FROM proj.commissions WHERE employee_id=@emp AND status='Pending'", ("emp", empId)));
            var gross = monthly + bonuses + commissions;
            if (gross <= 0) continue;

            var cityRule = await c.ActiveRule("SocialInsurance.City", Pg.S(emp["social_insurance_city"]));
            var siBase = SocialInsurance.Base(monthly, cityRule);
            var (rc, rp) = SocialInsurance.Rates(cityRule);
            var siPersonal = R2(siBase * rp); var siCompany = R2(siBase * rc);

            var past = await c.Rows("SELECT gross,si_personal,iit FROM proj.payroll_lines WHERE employee_id=@emp AND period LIKE @y AND period < @p", ("emp", empId), ("y", $"{year}-%"), ("p", period));
            var cumIncome = past.Sum(x => Pg.D(x["gross"])) + gross;
            var cumSi = past.Sum(x => Pg.D(x["si_personal"])) + siPersonal;
            var withheld = past.Sum(x => Pg.D(x["iit"]));
            var iit = TaxCalc.WageIitCumulative(cumIncome, past.Count + 1, cumSi, 0m, withheld);
            var net = gross - siPersonal - iit.Amount;

            var derivation = new JsonObject
            {
                ["工资标准"] = monthly, ["规则版本"] = ApplyCtx.RuleRef(wageRule),
                ["一次性奖金"] = bonuses, ["提成"] = commissions,
                ["社保基数"] = siBase, ["个人社保公积金"] = siPersonal, ["单位社保公积金"] = siCompany,
                ["个税推导单"] = iit.RowsJson(), ["实发"] = net
            };
            await c.ExecJ(@"INSERT INTO proj.payroll_lines(run_id,employee_id,period,gross,si_personal,iit,net,derivation,source_event_id)
                VALUES(@r,@emp,@p,@g,@si,@iit,@n,@d,@e)",
                new[] { ("d", (JsonNode?)derivation) },
                ("r", $"PR-{c.Eid}"), ("emp", empId), ("p", period), ("g", gross), ("si", siPersonal), ("iit", iit.Amount), ("n", net), ("e", c.Evt.EventId));
            lines.Add(new JsonObject { ["employeeId"] = empId, ["gross"] = gross, ["siPersonal"] = siPersonal, ["siCompany"] = siCompany, ["iit"] = iit.Amount, ["net"] = net });
            // 工资条通知：员工本人登录可见自己的明细（薪酬密级分级），同时推送留痕
            await c.Notify("Employee", new JsonObject
            {
                ["kind"] = "Payslip", ["employeeId"] = empId, ["partyId"] = Pg.S(emp["party_id"]),
                ["period"] = period, ["gross"] = gross, ["siPersonal"] = siPersonal, ["iit"] = iit.Amount, ["net"] = net,
                ["note"] = "工资条已生成：本人登录可在薪酬页查看逐行推导单"
            });
            totalGross += gross; totalNet += net; totalIit += iit.Amount; totalSiP += siPersonal; totalSiC += siCompany;
        }
        await c.Exec("UPDATE proj.bonuses SET status='PaidInPayroll' WHERE period=@p AND status='Pending'", ("p", period));
        await c.Exec("UPDATE proj.commissions SET status='PaidInPayroll' WHERE status='Pending' AND period<=@p", ("p", period));
        await c.ExecJ("INSERT INTO proj.payroll_runs(run_id,period,lines,totals,source_event_id,auto) VALUES(@r,@p,@l,@t,@e,@a)",
            new[] { ("l", (JsonNode?)lines), ("t", (JsonNode?)new JsonObject { ["gross"] = totalGross, ["net"] = totalNet, ["iit"] = totalIit, ["siPersonal"] = totalSiP, ["siCompany"] = totalSiC }) },
            ("r", $"PR-{c.Eid}"), ("p", period), ("e", c.Evt.EventId), ("a", c.Pb("auto")));
        await c.Ledger(period, "PayrollExpense", totalGross, 0, $"工资薪金 {period}");
        await c.Ledger(period, "SICompanyExpense", totalSiC, 0, $"单位社保公积金 {period}");
        await c.Ledger(period, "Bank", 0, totalNet, $"工资发放 {period}");
        if (totalIit > 0) await c.Ledger(period, "TaxPayable", 0, totalIit, $"工资个税代扣 {period}");
        if (totalSiP + totalSiC > 0) await c.Ledger(period, "SIPayable", 0, totalSiP + totalSiC, $"社保公积金应缴 {period}");
        await c.Todo("TaxFiling", $"工资个税代扣申报（{period}，{totalIit} 元）", new JsonObject { ["period"] = period, ["amount"] = totalIit }, idSuffix: period + "-w");
        c.Impact($"工资计算完成 {period}：应发 {totalGross}，个税 {totalIit}，实发 {totalNet}（工资标准按锁定规则版本，逐人推导单可查）");
        c.Impact($"银行代发文件与打款指令单已可生成（薪酬页下载）；操作员银行打款后上传回单确认，完成发放闭环");
    }

    private static async Task PayrollPaid(ApplyCtx c)
    {
        var period = c.Ps("period");
        var receipt = c.Evt.Evidence is JsonArray ea && ea.Count > 0 ? ea[0]!["hash"]?.GetValue<string>() ?? "" : "";
        await c.Exec("UPDATE proj.payroll_runs SET status='Paid', paid_event_id=@e, receipt_hash=@h WHERE period=@p",
            ("e", c.Evt.EventId), ("h", receipt), ("p", period));
        var lines = await c.Rows("SELECT employee_id,net FROM proj.payroll_lines WHERE period=@p", ("p", period));
        foreach (var l in lines)
            await c.Notify("Employee", new JsonObject
            {
                ["kind"] = "PayslipPaid", ["employeeId"] = Pg.S(l["employee_id"]), ["period"] = period, ["net"] = Pg.D(l["net"]),
                ["note"] = "工资已发放：银行回单已入链，可在薪酬页核对"
            });
        c.Impact($"工资发放确认 {period}：银行回单入链，计提与实付勾稽闭环，全员收到发放通知");
    }

    private static async Task EmployeeLeft(ApplyCtx c)
    {
        var empId = c.Ps("employeeId");
        var rows = await c.Rows("SELECT * FROM proj.employees WHERE employee_id=@e", ("e", empId));
        if (rows.Count == 0) throw new InvalidOperationException("员工不存在");
        var emp = rows[0];
        var leaveDate = DateOnly.Parse(c.Ps("leaveDate"));
        var hireDate = emp["hire_date"] is DateOnly dOnly ? dOnly : DateOnly.FromDateTime((DateTime)emp["hire_date"]!);
        var wageRule = await c.ActiveRule("Payroll.Standard", empId);
        var monthly = wageRule?["monthly"] is JsonValue wv && wv.TryGetValue(out decimal w) ? w : 0m;

        var workedDays = leaveDate.Day;
        var daysInMonth = DateTime.DaysInMonth(leaveDate.Year, leaveDate.Month);
        var prorated = R2(monthly / daysInMonth * workedDays);
        var leaveDays = c.Pd("remainingLeaveDays");
        var leavePay = R2(monthly / 21.75m * leaveDays * 2m);
        var pendingCom = Pg.D(await c.Scalar("SELECT COALESCE(SUM(amount),0) FROM proj.commissions WHERE employee_id=@emp AND status='Pending'", ("emp", empId)));

        var years = (leaveDate.ToDateTime(TimeOnly.MinValue) - hireDate.ToDateTime(TimeOnly.MinValue)).TotalDays / 365.25;
        var n = Math.Floor(years) + (years - Math.Floor(years) >= 0.5 ? 1 : (years - Math.Floor(years) > 0 ? 0.5 : 0));
        var reason = c.Ps("reason");
        decimal severanceMonths = reason switch
        {
            "TerminatedNoNotice" => (decimal)n + 1,
            "MutualTermination" or "Layoff" or "SiWaiverResign" => (decimal)n,
            _ => 0m
        };
        var severance = R2(monthly * severanceMonths);
        var total = prorated + leavePay + pendingCom + severance;

        var rowsD = new List<DerivationRow>
        {
            new("截至离职日工资", $"{monthly} ÷ {daysInMonth} × {workedDays} 天 = {prorated}", prorated, ApplyCtx.RuleRef(wageRule), "劳动合同+锁定薪酬规则"),
            new("未休年假折算", $"{monthly} ÷ 21.75 × {leaveDays} 天 × 200% = {leavePay}", leavePay, "annual-leave-v1", "职工带薪年休假条例第五条"),
            new("提成尾款", $"未结提成挂账合计 = {pendingCom}（按签单时锁定规则）", pendingCom, "commission-locked", "提成方案（签单时版本）"),
            new("经济补偿金", $"事由 {reason} → {severanceMonths} 个月 × {monthly} = {severance}", severance, "severance-v1", "劳动合同法第四十六/四十七条"),
            new("离职结算合计", $"{prorated} + {leavePay} + {pendingCom} + {severance} = {total}", total, "settlement-v1", "离职当日自动结算")
        };
        await c.Calc("LeaveSettlement", empId, new CalcOutcome(total, rowsD));
        await c.Exec("UPDATE proj.employees SET leave_date=@d WHERE employee_id=@e", ("d", leaveDate), ("e", empId));
        await c.Todo("SettlementReview", $"离职结算单复核付款：{Pg.S(emp["name"])} 合计 {total} 元", new JsonObject { ["employeeId"] = empId, ["total"] = total });
        await c.Todo("SocialInsurance", $"社保公积金减员停缴：{Pg.S(emp["name"])}（按参保地当月/次月口径）", new JsonObject { ["employeeId"] = empId, ["leaveDate"] = leaveDate.ToString() });

        var nc = await c.Rows("SELECT contract_id,doc_hash FROM proj.contracts WHERE contract_type='竞业限制协议' AND status='Signed' AND parties::text LIKE @p", ("p", $"%{Pg.S(emp["party_id"])}%"));
        if (nc.Count > 0)
        {
            var comp = R2(monthly * 0.3m);
            await c.Todo("NonCompete", $"竞业限制经济补偿按月支付计划：{comp} 元/月（离职后 ≤24 个月，逾期未付协议失效风险）",
                new JsonObject { ["employeeId"] = empId, ["monthly"] = comp, ["contractId"] = Pg.S(nc[0]["contract_id"]) });
        }
        c.Impact($"离职事件生效：{Pg.S(emp["name"])} 当日自动生成结算单 {total} 元，不存在人走账不清");
    }

    private static async Task ModuleSwitchChanged(ApplyCtx c)
    {
        var mid = c.Ps("moduleId"); var target = c.Ps("targetState");
        await c.Exec("UPDATE proj.module_states SET state=@s, resolution_no=@r, source_event_id=@e WHERE module_id=@m",
            ("s", target), ("r", c.Evt.ResolutionNo ?? ""), ("e", c.Evt.EventId), ("m", mid));
        c.Impact($"模块开关迁移：{mid} → {target}（决议 {c.Evt.ResolutionNo} 入链推送；历史数据只读保留，不删除）");
        if (target is "Suspended" or "Archived" or "Disabled")
            await c.Notify("WorkGroup", new JsonObject { ["type"] = "UpstreamDisabled", ["moduleId"] = mid, ["说明"] = "上游模块已停用，下游按预设降级规则处理，禁止静默丢数" });
    }

    private static async Task InvestorQualified(ApplyCtx c)
    {
        await c.EnsureParty(c.Ps("investorId"), c.Ps("name"));
        await c.Exec("INSERT INTO proj.investors(investor_id,name,qualified,assets_proof_hash,source_event_id) VALUES(@i,@n,TRUE,@h,@e) ON CONFLICT DO NOTHING",
            ("i", c.Ps("investorId")), ("n", c.Ps("name")), ("h", c.Ps("proofHash")), ("e", c.Evt.EventId));
        c.Impact($"合格投资者认定：{c.Ps("name")}（实名+风险测评+资产证明入链+反洗钱筛查）");
    }

    private static async Task InvestmentReceived(ApplyCtx c)
    {
        var investor = c.Ps("investorId"); var amount = c.Pd("amount"); var subscribed = c.Pd("subscribed");
        var cap = await c.CurrentCap();
        var exist = cap.FirstOrDefault(p => p.PartyId == investor);
        if (exist != null) { exist.Subscribed += subscribed; exist.PaidIn += amount; }
        else cap.Add(new ApplyCtx.Pos { PartyId = investor, Subscribed = subscribed, PaidIn = amount });
        await c.SaveCap(cap);
        await c.Ledger(c.Period, "Bank", amount, 0, $"私募注资到账 {investor}（白名单监管账户，与合同匹配成功）");
        await c.Ledger(c.Period, "PaidInCapital", 0, subscribed, $"新增注册资本 {investor}");
        var premium = amount - subscribed;
        if (premium > 0) await c.Ledger(c.Period, "CapitalReserve", 0, premium, $"资本溢价 {investor}");
        var stamp = R2((subscribed + Math.Max(premium, 0)) * 0.00025m * 0.5m);
        await c.Calc("BooksStamp", investor, new CalcOutcome(stamp, new List<DerivationRow>
        {
            new("营业账簿印花税", $"(实收资本 {subscribed} + 资本公积 {Math.Max(premium, 0)}) × 0.025% × 50%(六税两费减半) = {stamp}", stamp, TaxRules.V + "/STAMP-books", "印花税法+财政部 税务总局公告2023年第12号")
        }));
        await c.Todo("BusinessRegistry", "工商变更待办：增资登记", new JsonObject { ["investorId"] = investor, ["subscribed"] = subscribed });
        await c.Todo("DilutionConfirm", "稀释影响测算表推送每位老股东逐一确认", new JsonObject { ["capVersion"] = "latest" });
        c.Impact($"注资闭环：到账 {amount} 与入股合同匹配 → Cap Table 更新 → 印花税 {stamp} → 工商待办，全链路自动联动");
    }

    private static async Task AccountAdded(ApplyCtx c)
    {
        // 设立后新增成员（股东/监事/管理员等）：决议驱动+实名制；股权另行走 ShareSubscription
        var userId = c.P["userId"]?.GetValue<string>() ?? c.Ps("username");
        var partyId = c.P["partyId"]?.GetValue<string>();
        if (partyId != null) await c.EnsureParty(partyId, c.Ps("realName"));
        var (pub, priv) = Crypto.GenerateKeyPair();
        await c.Exec(@"INSERT INTO proj.accounts(user_id,username,display_name,role,party_id,pwd_hash,pub_key,priv_key,real_name,id_number,source_event_id)
            VALUES(@u,@un,@dn,@r,@p,@pw,@pub,@priv,@rn,@id,@e)",
            ("u", userId), ("un", c.Ps("username")), ("dn", c.P["displayName"]?.GetValue<string>() ?? c.Ps("realName")),
            ("r", c.Ps("role")), ("p", (object?)partyId ?? DBNull.Value),
            ("pw", Crypto.HashPassword(c.Ps("password"))), ("pub", pub), ("priv", priv),
            ("rn", c.Ps("realName")), ("id", c.Ps("idNumber")), ("e", c.Evt.EventId));
        c.Impact($"新增成员：{c.Ps("realName")}（{c.Ps("role")}，决议 {c.Evt.ResolutionNo}）；如涉股权请另行走增资认缴事件");
    }

    private static async Task LegalEntryAdded(ApplyCtx c)
    {
        var opts = c.P["options"] as JsonArray ?? new JsonArray();
        await c.ExecJ(@"INSERT INTO proj.legal_library(entry_id,title,keywords,scenario,options,clause_template,origin,source_event_id)
            VALUES(@id,@t,@k,@s,@o,@c,'Event',@e)",
            new[] { ("o", (JsonNode?)opts) },
            ("id", $"LEG-{c.Eid}"), ("t", c.Ps("title")), ("k", c.Ps("keywords")), ("s", c.Ps("scenario")),
            ("c", c.P["clauseTemplate"]?.GetValue<string>() ?? ""), ("e", c.Evt.EventId));
        c.Impact($"法务库新增条目：{c.Ps("title")}（凭依据入链，全员可检索，条款可直接写入合同）");
    }

    private static async Task CompanyInfoChanged(ApplyCtx c)
    {
        // 公司名称/行业标签变更：决议驱动，原设立事件永久保留（P2 只增不改）
        if (c.P["name"] is JsonValue nv)
            await c.Exec("UPDATE proj.company SET name=@n, source_event_id=@e WHERE id=1", ("n", nv.GetValue<string>()), ("e", c.Evt.EventId));
        if (c.P["industryTags"] is JsonArray tags)
            await c.ExecJ("UPDATE proj.company SET industry_tags=@t WHERE id=1", new[] { ("t", (JsonNode?)tags) });
        c.Impact($"公司信息变更（决议 {c.Evt.ResolutionNo}）：名称/行业标签更新，设立原文永久入链可查；企业类型变更须凭工商变更登记另行处理");
    }

    private static async Task TodoCompleted(ApplyCtx c)
    {
        await c.Exec("UPDATE proj.todos SET status='Done' WHERE todo_id=@t", ("t", c.Ps("todoId")));
        c.Impact($"待办完成：{c.Ps("todoId")}（完成依据入链）");
    }

    private static async Task SecurityAlert(ApplyCtx c)
    {
        await c.Todo("SecurityAlert", $"安全告警：{c.Ps("kind")}", new JsonObject { ["detail"] = c.P["detail"]?.DeepClone(), ["actor"] = c.Evt.ActorId });
        await c.Notify("Shareholder", new JsonObject { ["type"] = "SecurityAlert", ["kind"] = c.Ps("kind"), ["actor"] = c.Evt.ActorId });
        c.Impact($"安全告警入链：{c.Ps("kind")}（直达监事与全体股东）");
    }
}
// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Storage;

namespace EquiLedger.Engine;

/// <summary>依赖图联动引擎：事件落库后自动重算全部受影响节点，生成影响清单与通知（P8）。</summary>
public static class Propagation
{
    private static readonly HashSet<string> ProfitAffecting = new()
    {
        "CompanyFounded", "RevenueRecorded", "ExpenseRecorded", "PayrollRun", "PeriodClosed",
        "CapitalPaidIn", "ShareSubscription", "ShareTransfer", "ShareExit", "ShareholderDeceased",
        "InvestmentReceived", "CorrectionRecorded", "DividendApproved", "EmployeeLeft", "SalesOrderConfirmed"
    };

    private static decimal R2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    public static async Task Run(ApplyCtx c)
    {
        if (ProfitAffecting.Contains(c.Evt.Type))
            await RecomputeProfitAndDividend(c);

        // 每次提交自动推送工作群（结构化摘要），推送记录本身入投影、可入链核验
        if (c.Evt.Type != "EvidenceUploaded")
        {
            var summary = new JsonObject
            {
                ["eventType"] = c.Evt.Type,
                ["actor"] = c.Evt.ActorId,
                ["resolutionNo"] = c.Evt.ResolutionNo,
                ["eventId"] = c.Eid,
                ["seq"] = c.Evt.Seq,
                ["hash"] = c.Evt.Hash,
                ["impacts"] = new JsonArray(c.Impacts.Select(i => (JsonNode)i).ToArray())
            };
            await c.Notify("WorkGroup", summary);
        }

        if (c.Impacts.Count > 0)
            await c.ExecJ("INSERT INTO proj.impact_lists(source_event_id,impacts,created_at) VALUES(@e,@i,@t)",
                new[] { ("i", (JsonNode?)new JsonArray(c.Impacts.Select(x => (JsonNode)x).ToArray())) },
                ("e", c.Evt.EventId), ("t", c.Evt.OccurredAt.UtcDateTime));
    }

    private static async Task RecomputeProfitAndDividend(ApplyCtx c)
    {
        var year = c.Evt.OccurredAt.Year;
        var at = DateOnly.FromDateTime(c.Evt.OccurredAt.UtcDateTime);
        var co = (await c.Rows("SELECT * FROM proj.company WHERE id=1")).FirstOrDefault();
        if (co is null) return;
        var companyType = Pg.S(co["company_type"]);
        var regCap = Pg.D(co["registered_capital"]);
        var profit = await c.ProfitBeforeTax(year);
        var empCount = Convert.ToInt32(await c.Scalar("SELECT COUNT(*) FROM proj.employees WHERE leave_date IS NULL"));
        var assets = Pg.D(await c.Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account IN ('Bank','AR','Inventory','Materials','FixedAssets','ExternalInvestment')"));

        var rows = new List<DerivationRow>
        {
            new("期间损益", $"收入 − 费用 − 工资 − 单位社保（{year} 年，投影自复式分录）= {profit}", profit, "ledger-projection", "财务模块事件流")
        };

        if (companyType is "个人独资企业" or "个体工商户")
        {
            // 业主/投资者工资不得税前扣除：按唯一出资人识别业主，其工资全额纳税调增（财税〔2000〕91号）
            var owner = (await c.Rows("SELECT party_id FROM proj.cap_positions WHERE cap_version=(SELECT COALESCE(MAX(version),0) FROM proj.cap_versions) AND status='Normal' ORDER BY subscribed DESC")).FirstOrDefault();
            decimal ownerWage = 0;
            if (owner != null)
                ownerWage = Pg.D(await c.Scalar(@"SELECT COALESCE(SUM(pl.gross),0) FROM proj.payroll_lines pl JOIN proj.employees e ON e.employee_id=pl.employee_id
                    WHERE e.party_id=@o AND pl.period LIKE @y", ("o", Pg.S(owner["party_id"])), ("y", $"{year}-%")));
            var taxable = profit + ownerWage;
            if (ownerWage > 0)
                rows.Add(new("业主工资纳税调增", $"业主/投资者工资 {ownerWage} 不得税前扣除，应纳税所得额 = {profit} + {ownerWage} = {taxable}", ownerWage, TaxRules.V + "/BIZ-owner", "财税〔2000〕91号附件1第六条"));
            var biz = TaxCalc.BusinessIncomeIit(taxable, companyType == "个体工商户", at);
            rows.AddRange(biz.Rows);
            rows.Add(new("口径说明", "不缴企业所得税，经营所得归投资者/业主（业主工资不得税前扣除）；业主社保可按灵活就业参加，雇工必须依法参保", biz.Amount, TaxRules.V + "/BIZ", "财税〔2000〕91号；社会保险法第58条"));
            await c.Calc("IncomeTax", companyType, new CalcOutcome(biz.Amount, rows));
            return;
        }
        if (companyType == "合伙企业")
        {
            var allocRule = await c.ActiveRule("Partnership.Allocation");
            var partners = new List<(string, decimal, bool)>();
            if (allocRule?["partners"] is JsonArray pa)
                foreach (JsonObject p in pa)
                    partners.Add((p["partyId"]!.GetValue<string>(), p["ratio"]!.GetValue<decimal>(), p["isNatural"]?.GetValue<bool>() ?? true));
            if (partners.Count == 0)
            {
                var cap = await c.CurrentCap();
                var active = cap.Where(p => p.Status == "Normal" && p.Subscribed > 0).ToList();
                var total = active.Sum(p => p.Subscribed);
                partners = active.Select(p => (p.PartyId, Math.Round(p.Subscribed / total, 6), true)).ToList();
            }
            decimal totalTax = 0;
            foreach (var (partner, share, tax) in TaxCalc.PartnershipAllocate(Math.Max(profit, 0), partners, at))
            {
                await c.Calc("PartnerIIT", partner, tax);
                totalTax += tax.Amount;
                rows.Add(new($"先分后税：{partner}", $"所得 {profit} × 协议比例 → 份额 {share}，个税 {tax.Amount}（留存未分配同样计税）", tax.Amount, ApplyCtx.RuleRef(allocRule), "财税〔2008〕159号"));
            }
            await c.Calc("IncomeTax", "合伙企业（先分后税）", new CalcOutcome(totalTax, rows));
            var distributed = Pg.D(await c.Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='ProfitDistribution' AND period LIKE @y", ("y", $"{year}-%")));
            if (profit > distributed)
                await c.Todo("PartnerCashAlert", $"合伙人纳税现金流预警：留存所得 {profit - distributed} 元未分配也需缴税", new JsonObject { ["retained"] = profit - distributed, ["tax"] = totalTax }, idSuffix: $"{year}-{c.Eid}");
            return;
        }

        // 公司制：企业所得税 → 法定公积金 → 可分配利润 → 分红候选稿
        var cit = TaxCalc.CorporateIncomeTax(profit, empCount, assets, at);
        rows.AddRange(cit.Rows);
        var afterTax = profit - cit.Amount;
        var reserveBalance = Pg.D(await c.Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='ReserveFund'"));
        var reserve = TaxCalc.StatutoryReserve(afterTax, reserveBalance, regCap);
        rows.AddRange(reserve.Rows);
        var priorDist = Pg.D(await c.Scalar("SELECT COALESCE(SUM(debit-credit),0) FROM proj.ledger WHERE account='ProfitDistribution' AND period LIKE @y", ("y", $"{year}-%")));
        var distributable = afterTax - reserve.Amount - priorDist;
        rows.Add(new("可分配利润", $"{afterTax}(税后) − {reserve.Amount}(公积金) − {priorDist}(已分配) = {distributable}", distributable, "profit-derivation-v1", "公司法第二百一十条"));
        await c.Calc("ProfitDerivation", year.ToString(), new CalcOutcome(distributable, rows));

        // 小微临界预警
        if (profit is > 2_700_000m and <= 3_000_000m)
            await c.Todo("TaxThreshold", "小型微利临界预警：应纳税所得额接近 300 万上限，越线税负 5%→25%", new JsonObject { ["profit"] = profit }, idSuffix: $"{year}-{c.Eid}");

        if (distributable <= 0) return;

        var capNow = await c.CurrentCap();
        var payers = capNow.Where(p => (p.Status is "Normal" or "Deceased") && p.PaidIn > 0).ToList();
        var totalPaid = payers.Sum(p => p.PaidIn);
        if (totalPaid <= 0) return;

        var divRule = await c.ActiveRule("Charter.Dividend");
        var payoutRatio = divRule?["payoutRatio"] is JsonValue pv && pv.TryGetValue(out decimal pr) ? pr : 1m;
        var pool = R2(distributable * payoutRatio);
        var allocs = new JsonArray();
        foreach (var p in payers)
        {
            var gross = R2(pool * p.PaidIn / totalPaid);
            allocs.Add(new JsonObject
            {
                ["partyId"] = p.PartyId, ["paidIn"] = p.PaidIn,
                ["ratio"] = Math.Round(p.PaidIn / totalPaid, 6), ["gross"] = gross,
                ["suspended"] = p.Status == "Deceased"
            });
        }
        // 旧候选稿标注失效，新候选稿自动生成（人工只能发起修改规则的决议，不能改结果数字）
        await c.Exec("UPDATE proj.dividend_plans SET status='Superseded' WHERE status='Candidate' AND fiscal_year=@y", ("y", year));
        await c.ExecJ(@"INSERT INTO proj.dividend_plans(plan_id,fiscal_year,distributable,reserve_extracted,allocations,status,source_event_id)
            VALUES(@p,@y,@d,@r,@a,'Candidate',@e)",
            new[] { ("a", (JsonNode?)allocs) },
            ("p", $"DP-{c.Eid}"), ("y", year), ("d", distributable), ("r", reserve.Amount), ("e", c.Evt.EventId));
        c.Impact($"分红候选稿自动重算（{year}）：可分配 {distributable}，按实缴比例生成，待股东会决议");

        var forceMin = divRule?["forceMinRatio"] is JsonValue fv && fv.TryGetValue(out decimal fm) ? fm : 0m;
        if (forceMin > 0)
        {
            var approved = Pg.D(await c.Scalar("SELECT COALESCE(SUM(distributable),0) FROM proj.dividend_plans WHERE fiscal_year=@y AND status IN ('Approved','Paid')", ("y", year)));
            if (approved < distributable * forceMin)
                await c.Todo("ForcedDividend", $"强制分红条款达标：可分配利润的 {forceMin:P0} 必须分配，已自动生成议案候选稿", new JsonObject { ["distributable"] = distributable, ["forceMinRatio"] = forceMin }, idSuffix: $"{year}-{c.Eid}");
        }
    }
}
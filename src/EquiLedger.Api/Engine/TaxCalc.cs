// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;

namespace EquiLedger.Engine;

/// <summary>推导单一行：每一步公式、金额、规则版本与政策文号。</summary>
public record DerivationRow(string Step, string Formula, decimal Value, string RuleRef, string Policy);

public record CalcOutcome(decimal Amount, List<DerivationRow> Rows)
{
    public JsonArray RowsJson()
    {
        var arr = new JsonArray();
        foreach (var r in Rows)
            arr.Add(new JsonObject
            {
                ["step"] = r.Step, ["formula"] = r.Formula,
                ["value"] = r.Value, ["ruleRef"] = r.RuleRef, ["policy"] = r.Policy
            });
        return arr;
    }
    public JsonArray RuleRefsJson()
    {
        var arr = new JsonArray();
        foreach (var g in Rows.Select(r => r.RuleRef).Distinct()) arr.Add(g);
        return arr;
    }
}

/// <summary>税费规则库 v1（策划书 11.3 / 11.5 快照，全部版本化、附政策文号）。</summary>
public static class TaxRules
{
    public const string V = "tax-pack-v1";
    public static readonly DateOnly Cliff2027 = new(2027, 12, 31);

    // 工资薪金累计预扣（年度表）
    public static readonly (decimal upper, decimal rate, decimal quick)[] WageBrackets =
    {
        (36000m, 0.03m, 0m), (144000m, 0.10m, 2520m), (300000m, 0.20m, 16920m),
        (420000m, 0.25m, 31920m), (660000m, 0.30m, 52920m), (960000m, 0.35m, 85920m),
        (decimal.MaxValue, 0.45m, 181920m)
    };

    // 经营所得 5%–35% 五级超额累进（年度表）
    public static readonly (decimal upper, decimal rate, decimal quick)[] BizBrackets =
    {
        (30000m, 0.05m, 0m), (90000m, 0.10m, 1500m), (300000m, 0.20m, 10500m),
        (500000m, 0.30m, 40500m), (decimal.MaxValue, 0.35m, 65500m)
    };

    public static bool InEffect(DateOnly at, DateOnly expiry) => at <= expiry;
}

/// <summary>确定性计算引擎：decimal 定点、同输入同版本逐分不差。</summary>
public static class TaxCalc
{
    private static decimal R2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    private static (decimal rate, decimal quick) Bracket(decimal taxable, (decimal upper, decimal rate, decimal quick)[] table)
    {
        foreach (var b in table) if (taxable <= b.upper) return (b.rate, b.quick);
        var last = table[^1];
        return (last.rate, last.quick);
    }

    /// <summary>企业所得税（有限公司/股份公司）。小型微利：所得≤300万 且 从业≤300 且 资产≤5000万。</summary>
    public static CalcOutcome CorporateIncomeTax(decimal taxableIncome, int employees, decimal totalAssets, DateOnly at)
    {
        var rows = new List<DerivationRow>();
        if (taxableIncome <= 0)
            return new CalcOutcome(0m, new List<DerivationRow> { new("应税所得", "应纳税所得额 ≤ 0，不缴纳", 0m, TaxRules.V + "/CIT", "企业所得税法") });

        bool small = taxableIncome <= 3_000_000m && employees <= 300 && totalAssets <= 50_000_000m
                     && TaxRules.InEffect(at, TaxRules.Cliff2027);
        if (small)
        {
            var reduced = R2(taxableIncome * 0.25m);
            rows.Add(new("小微判定", $"所得额{taxableIncome}≤300万 且 从业{employees}≤300 且 资产{totalAssets}≤5000万 → 适用小型微利优惠", 1m, TaxRules.V + "/CIT-small", "财政部 税务总局公告2023年第12号"));
            rows.Add(new("减计所得", $"{taxableIncome} × 25% = {reduced}", reduced, TaxRules.V + "/CIT-small", "财政部 税务总局公告2023年第12号"));
            var tax = R2(reduced * 0.20m);
            rows.Add(new("应纳税额", $"{reduced} × 20% = {tax}（实际税负5%）", tax, TaxRules.V + "/CIT-small", "财政部 税务总局公告2023年第12号"));
            return new CalcOutcome(tax, rows);
        }
        var std = R2(taxableIncome * 0.25m);
        rows.Add(new("应纳税额", $"{taxableIncome} × 25% = {std}", std, TaxRules.V + "/CIT-std", "企业所得税法第四条"));
        return new CalcOutcome(std, rows);
    }

    /// <summary>增值税（小规模纳税人，按月申报口径）。月销售额≤10万免征；3%减按1%（至2027底）。</summary>
    public static CalcOutcome VatSmallScale(decimal monthlySales, DateOnly at)
    {
        var rows = new List<DerivationRow>();
        bool inEffect = TaxRules.InEffect(at, TaxRules.Cliff2027);
        if (inEffect && monthlySales <= 100_000m)
        {
            rows.Add(new("免征判定", $"月销售额 {monthlySales} ≤ 100,000 → 免征增值税", 0m, TaxRules.V + "/VAT-small", "财政部 税务总局公告2023年第19号"));
            return new CalcOutcome(0m, rows);
        }
        var rate = inEffect ? 0.01m : 0.03m;
        var net = R2(monthlySales / (1 + rate));
        rows.Add(new("价税分离", $"{monthlySales} ÷ (1+{rate:P0}) = {net}", net, TaxRules.V + "/VAT-small", "财政部 税务总局公告2023年第19号"));
        var tax = R2(net * rate);
        rows.Add(new("应纳税额", $"{net} × {rate:P0} = {tax}" + (inEffect ? "（3%减按1%）" : ""), tax, TaxRules.V + "/VAT-small", "财政部 税务总局公告2023年第19号"));
        return new CalcOutcome(tax, rows);
    }

    /// <summary>股息红利个税 20% 代扣。listedHoldingMonths 非 null 时按上市公司差别化（财税〔2015〕101号）。</summary>
    public static CalcOutcome DividendIit(decimal gross, int? listedHoldingMonths = null)
    {
        var rows = new List<DerivationRow>();
        decimal include = 1m;
        string policy = "个人所得税法第三条";
        if (listedHoldingMonths is int m)
        {
            policy = "财税〔2015〕101号";
            include = m > 12 ? 0m : m >= 1 ? 0.5m : 1m;
            rows.Add(new("差别化判定", $"持股 {m} 个月 → 计入比例 {include:P0}", include, TaxRules.V + "/DIV-listed", policy));
        }
        var taxable = R2(gross * include);
        rows.Add(new("计税所得", $"{gross} × {include:P0} = {taxable}", taxable, TaxRules.V + "/DIV", policy));
        var tax = R2(taxable * 0.20m);
        rows.Add(new("代扣税额", $"{taxable} × 20% = {tax}", tax, TaxRules.V + "/DIV", policy));
        return new CalcOutcome(tax, rows);
    }

    /// <summary>工资薪金累计预扣。cumTaxable = 累计收入 − 累计5000/月 − 累计专项扣除 − 累计专项附加。</summary>
    public static CalcOutcome WageIitCumulative(decimal cumIncome, int months, decimal cumSocialPersonal, decimal cumSpecialExtra, decimal alreadyWithheld)
    {
        var rows = new List<DerivationRow>();
        var deduction = 5000m * months;
        var cumTaxable = cumIncome - deduction - cumSocialPersonal - cumSpecialExtra;
        rows.Add(new("累计应纳税所得额", $"{cumIncome} − {deduction}(基本减除) − {cumSocialPersonal}(专项) − {cumSpecialExtra}(专项附加) = {cumTaxable}", cumTaxable, TaxRules.V + "/WAGE", "个人所得税法及实施条例"));
        if (cumTaxable <= 0)
        {
            rows.Add(new("本期应预扣", "累计应纳税所得额 ≤ 0 → 0", 0m, TaxRules.V + "/WAGE", "国家税务总局公告2018年第61号"));
            return new CalcOutcome(0m, rows);
        }
        var (rate, quick) = Bracket(cumTaxable, TaxRules.WageBrackets);
        var cumTax = R2(cumTaxable * rate - quick);
        rows.Add(new("累计应纳税额", $"{cumTaxable} × {rate:P0} − {quick}(速算扣除) = {cumTax}", cumTax, TaxRules.V + "/WAGE", "个人所得税法税率表"));
        var due = cumTax - alreadyWithheld;
        if (due < 0) due = 0; // 累计预扣不退负，年度汇算处理
        rows.Add(new("本期应预扣", $"{cumTax} − {alreadyWithheld}(已预扣) = {due}", due, TaxRules.V + "/WAGE", "国家税务总局公告2018年第61号"));
        return new CalcOutcome(due, rows);
    }

    /// <summary>股权/份额转让个税：财产转让所得 20%。</summary>
    public static CalcOutcome TransferIit(decimal income, decimal originalCost, decimal reasonableFees)
    {
        var taxable = income - originalCost - reasonableFees;
        var rows = new List<DerivationRow>
        {
            new("应纳税所得额", $"{income}(转让收入) − {originalCost}(原值) − {reasonableFees}(合理费用) = {taxable}", taxable, TaxRules.V + "/TRANSFER", "国家税务总局公告2014年第67号")
        };
        if (taxable <= 0)
        {
            rows.Add(new("应纳税额", "所得 ≤ 0 → 0", 0m, TaxRules.V + "/TRANSFER", "个人所得税法"));
            return new CalcOutcome(0m, rows);
        }
        var tax = R2(taxable * 0.20m);
        rows.Add(new("应纳税额", $"{taxable} × 20% = {tax}", tax, TaxRules.V + "/TRANSFER", "个人所得税法第三条"));
        return new CalcOutcome(tax, rows);
    }

    /// <summary>印花税·产权转移书据：价款万分之五；小微/小规模减半（六税两费）。</summary>
    public static CalcOutcome StampDutyTransfer(decimal amount, bool halfReduction, DateOnly at)
    {
        var rate = 0.0005m;
        var rows = new List<DerivationRow>();
        var tax = R2(amount * rate);
        rows.Add(new("产权转移书据", $"{amount} × 0.05% = {tax}", tax, TaxRules.V + "/STAMP", "印花税法税目税率表"));
        if (halfReduction && TaxRules.InEffect(at, TaxRules.Cliff2027))
        {
            var half = R2(tax * 0.5m);
            rows.Add(new("六税两费减半", $"{tax} × 50% = {half}", half, TaxRules.V + "/STAMP-half", "财政部 税务总局公告2023年第12号"));
            return new CalcOutcome(half, rows);
        }
        return new CalcOutcome(tax, rows);
    }

    /// <summary>法定公积金：税后利润 10%，累计达注册资本 50% 可不再提取。</summary>
    public static CalcOutcome StatutoryReserve(decimal afterTaxProfit, decimal reserveBalance, decimal registeredCapital)
    {
        var rows = new List<DerivationRow>();
        var cap = registeredCapital * 0.5m;
        if (afterTaxProfit <= 0 || reserveBalance >= cap)
        {
            rows.Add(new("提取判定", afterTaxProfit <= 0 ? "无税后利润，不提取" : $"累计公积金 {reserveBalance} ≥ 注册资本50%({cap})，可不再提取", 0m, TaxRules.V + "/RESERVE", "公司法第二百一十条"));
            return new CalcOutcome(0m, rows);
        }
        var want = R2(afterTaxProfit * 0.10m);
        var extract = Math.Min(want, cap - reserveBalance);
        rows.Add(new("按比例提取", $"{afterTaxProfit} × 10% = {want}", want, TaxRules.V + "/RESERVE", "公司法第二百一十条"));
        if (extract < want)
            rows.Add(new("封顶调整", $"补足至注册资本50%上限 {cap} → 实提 {extract}", extract, TaxRules.V + "/RESERVE", "公司法第二百一十条"));
        return new CalcOutcome(extract, rows);
    }

    /// <summary>经营所得个税（个独/合伙自然人/个体户）。individualHalf：个体户 ≤200万部分减半（至2027底）。</summary>
    public static CalcOutcome BusinessIncomeIit(decimal taxableIncome, bool individualHalf, DateOnly at)
    {
        var rows = new List<DerivationRow>();
        if (taxableIncome <= 0)
            return new CalcOutcome(0m, new List<DerivationRow> { new("应税所得", "所得 ≤ 0 → 0", 0m, TaxRules.V + "/BIZ", "财税〔2000〕91号") });
        var (rate, quick) = Bracket(taxableIncome, TaxRules.BizBrackets);
        var tax = R2(taxableIncome * rate - quick);
        rows.Add(new("五级超额累进", $"{taxableIncome} × {rate:P0} − {quick}(速算扣除) = {tax}", tax, TaxRules.V + "/BIZ", "个人所得税法经营所得税率表"));
        if (individualHalf && TaxRules.InEffect(at, TaxRules.Cliff2027))
        {
            var halfBase = Math.Min(taxableIncome, 2_000_000m);
            var (r2, q2) = Bracket(halfBase, TaxRules.BizBrackets);
            var halfTaxPortion = R2(halfBase * r2 - q2);
            // 减半口径：应纳税额中不超过200万所得对应部分减半
            var relief = R2(tax * (halfBase / taxableIncome) * 0.5m);
            var final = tax - relief;
            rows.Add(new("个体户减半", $"≤200万部分({halfBase})对应税额减半，减免 {relief} → {final}", final, TaxRules.V + "/BIZ-half", "财政部 税务总局公告2023年第12号"));
            return new CalcOutcome(final, rows);
        }
        return new CalcOutcome(tax, rows);
    }

    /// <summary>合伙企业先分后税（财税〔2008〕159号）：按协议比例划分所得，留存也计税。</summary>
    public static List<(string partner, decimal share, CalcOutcome tax)> PartnershipAllocate(
        decimal totalIncome, List<(string partner, decimal ratio, bool isNatural)> partners, DateOnly at)
    {
        var sum = partners.Sum(p => p.ratio);
        if (Math.Abs(sum - 1m) > 0.000001m) throw new InvalidOperationException("合伙分配比例之和必须为1（159号文）");
        var result = new List<(string, decimal, CalcOutcome)>();
        foreach (var p in partners)
        {
            var share = R2(totalIncome * p.ratio);
            CalcOutcome tax = p.isNatural
                ? BusinessIncomeIit(share, false, at)
                : new CalcOutcome(R2(share * 0.25m), new List<DerivationRow>
                  {
                      new("法人合伙人", $"分得所得 {share} 并入自身企业所得税（25%示意）", R2(share * 0.25m), TaxRules.V + "/PARTNER-corp", "财税〔2008〕159号")
                  });
            result.Add((p.partner, share, tax));
        }
        return result;
    }
}
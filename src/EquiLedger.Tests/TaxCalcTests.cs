// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using EquiLedger.Engine;
using Xunit;

namespace EquiLedger.Tests;

/// <summary>A7/A11：税额确定性用例（策划书 11.4 / 11.5.4 + 边界）。</summary>
public class TaxCalcTests
{
    private static readonly DateOnly Now = new(2026, 11, 23);
    private static readonly DateOnly AfterCliff = new(2028, 1, 1);

    [Fact] // 11.4：小微 240 万 → 12 万（实际税负 5%）
    public void SmallProfit_240w()
    {
        var r = TaxCalc.CorporateIncomeTax(2_400_000m, 10, 10_000_000m, Now);
        Assert.Equal(120_000m, r.Amount);
        Assert.All(r.Rows, row => Assert.False(string.IsNullOrEmpty(row.Policy)));
    }

    [Fact] // 11.4：分红 10 万 → 代扣 2 万，实付 8 万
    public void Dividend_10w()
    {
        var r = TaxCalc.DividendIit(100_000m);
        Assert.Equal(20_000m, r.Amount);
    }

    [Fact] // 11.5.4：合伙企业先分后税 6:4，A=144500 B=79500
    public void Partnership_60_40()
    {
        var r = TaxCalc.PartnershipAllocate(1_000_000m,
            new List<(string, decimal, bool)> { ("A", 0.6m, true), ("B", 0.4m, true) }, Now);
        Assert.Equal(144_500m, r.First(x => x.partner == "A").tax.Amount);
        Assert.Equal(79_500m, r.First(x => x.partner == "B").tax.Amount);
    }

    [Fact] // 小微临界：300 万整仍适用，300 万零 1 元跳 25%
    public void SmallProfit_Boundary()
    {
        Assert.Equal(150_000m, TaxCalc.CorporateIncomeTax(3_000_000m, 300, 50_000_000m, Now).Amount);
        Assert.Equal(750_000.25m, TaxCalc.CorporateIncomeTax(3_000_001m, 300, 50_000_000m, Now).Amount);
        Assert.Equal(600_000m, TaxCalc.CorporateIncomeTax(2_400_000m, 301, 10_000_000m, Now).Amount); // 从业超限
        Assert.Equal(600_000m, TaxCalc.CorporateIncomeTax(2_400_000m, 10, 50_000_001m, Now).Amount); // 资产超限
    }

    [Fact] // 政策悬崖：2028-01-01 优惠到期自动切换
    public void PolicyCliff_2027()
    {
        Assert.Equal(120_000m, TaxCalc.CorporateIncomeTax(2_400_000m, 10, 10_000_000m, new DateOnly(2027, 12, 31)).Amount);
        Assert.Equal(600_000m, TaxCalc.CorporateIncomeTax(2_400_000m, 10, 10_000_000m, AfterCliff).Amount);
        Assert.Equal(0m, TaxCalc.VatSmallScale(100_000m, Now).Amount);
        Assert.Equal(3000m, TaxCalc.VatSmallScale(103_000m, AfterCliff).Amount); // 恢复3%：103000/1.03*0.03
    }

    [Fact] // 增值税：月 10 万免征边界；10.3 万含税 → 1%
    public void Vat_SmallScale()
    {
        Assert.Equal(0m, TaxCalc.VatSmallScale(100_000m, Now).Amount);
        Assert.Equal(1019.80m, TaxCalc.VatSmallScale(103_000m, Now).Amount); // 103000/1.01*0.01
    }

    [Fact] // 工资累计预扣：月薪8000 社保1400，首月 (8000-5000-1400)*3%=48
    public void Wage_FirstMonth()
    {
        var r = TaxCalc.WageIitCumulative(8000m, 1, 1400m, 0m, 0m);
        Assert.Equal(48m, r.Amount);
    }

    [Fact] // 股权转让：溢价 50 万 → 10 万；平价不缴
    public void TransferIit()
    {
        Assert.Equal(20_000m, TaxCalc.TransferIit(600_000m, 500_000m, 0m).Amount);
        Assert.Equal(0m, TaxCalc.TransferIit(500_000m, 500_000m, 0m).Amount);
    }

    [Fact] // 印花税万分之五 + 六税两费减半
    public void Stamp()
    {
        Assert.Equal(250m, TaxCalc.StampDutyTransfer(1_000_000m, true, Now).Amount);
        Assert.Equal(500m, TaxCalc.StampDutyTransfer(1_000_000m, false, Now).Amount);
        Assert.Equal(500m, TaxCalc.StampDutyTransfer(1_000_000m, true, AfterCliff).Amount);
    }

    [Fact] // 法定公积金 10% 封顶注册资本 50%
    public void Reserve()
    {
        Assert.Equal(100_000m, TaxCalc.StatutoryReserve(1_000_000m, 0m, 10_000_000m).Amount);
        Assert.Equal(50_000m, TaxCalc.StatutoryReserve(1_000_000m, 450_000m, 1_000_000m).Amount); // 封顶补足
        Assert.Equal(0m, TaxCalc.StatutoryReserve(1_000_000m, 500_000m, 1_000_000m).Amount);
        Assert.Equal(0m, TaxCalc.StatutoryReserve(-100m, 0m, 1_000_000m).Amount);
    }

    [Fact] // 个体户 ≤200 万减半；上市公司股息差别化
    public void BizHalve_And_ListedDividend()
    {
        var full = TaxCalc.BusinessIncomeIit(1_000_000m, false, Now).Amount;   // 1000000*35%-65500=284500
        Assert.Equal(284_500m, full);
        Assert.Equal(142_250m, TaxCalc.BusinessIncomeIit(1_000_000m, true, Now).Amount);
        Assert.Equal(0m, TaxCalc.DividendIit(100_000m, 13).Amount);   // 持股超1年暂免
        Assert.Equal(10_000m, TaxCalc.DividendIit(100_000m, 6).Amount); // 1月-1年减半
        Assert.Equal(20_000m, TaxCalc.DividendIit(100_000m, 0).Amount); // 1月内全额
    }

    [Fact] // 确定性：同输入同版本逐分不差
    public void Deterministic()
    {
        for (var i = 0; i < 5; i++)
            Assert.Equal(120_000m, TaxCalc.CorporateIncomeTax(2_400_000m, 10, 10_000_000m, Now).Amount);
    }
}
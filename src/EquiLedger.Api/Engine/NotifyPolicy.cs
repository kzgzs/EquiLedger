// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text;
using System.Text.Json.Nodes;

namespace EquiLedger.Engine;

/// <summary>群通知治理策略（代码强制，不依赖人工配置）：
/// 1) 群组与事件多对多绑定，未绑定不推送（默认拒绝）；
/// 2) 同一事件按群类型生成不同模板内容；
/// 3) 发送前数据分级+字段过滤+接收范围检查；高敏感一律摘要+系统内授权查看；
/// 4) 任何群组不得自动接收完整工资账本（工资类事件强制 High 摘要）。
/// 群消息撤回不等于数据收回，故原始敏感数据不出系统。</summary>
public static class NotifyPolicy
{
    public enum Level { Public, Internal, High }
    public static readonly string[] GroupKinds = { "company", "board", "shareholder", "supervisor", "department", "staff" };
    public static readonly string[] KindCn = { "公司群", "董事会群", "股东群", "监事群", "部门群", "员工群" };

    private static readonly Dictionary<string, (Level lvl, string[] kinds)> Policy = new()
    {
        ["CompanyFounded"] = (Level.Public, new[] { "company", "board", "shareholder", "staff" }),
        ["CompanyInfoChanged"] = (Level.Internal, new[] { "company", "board" }),
        ["RuleActivated"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["ResolutionRecorded"] = (Level.Internal, new[] { "board", "shareholder", "supervisor" }),
        ["ShareSubscription"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["CapitalPaidIn"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["ShareTransfer"] = (Level.High, new[] { "board", "shareholder" }),
        ["ShareExit"] = (Level.High, new[] { "board", "shareholder" }),
        ["ShareholderDeceased"] = (Level.High, new[] { "board", "shareholder" }),
        ["DividendApproved"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["DividendPaid"] = (Level.High, new[] { "board", "shareholder" }),
        ["PayrollRun"] = (Level.High, new[] { "company", "board" }),
        ["PayrollPaid"] = (Level.High, new[] { "company", "board", "staff" }),
        ["EmployeeHired"] = (Level.High, new[] { "board", "department" }),
        ["EmployeeLeft"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["BonusGranted"] = (Level.High, new[] { "board", "shareholder" }),
        ["RevenueRecorded"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["CollectionReceived"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["SalesOrderConfirmed"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["ExpenseRecorded"] = (Level.Internal, new[] { "company", "board", "department", "supervisor" }),
        ["ContractSigned"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["InvestmentMade"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["InvestmentReceived"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["InvestmentReturnReceived"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["PeriodClosed"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["CorrectionRecorded"] = (Level.Internal, new[] { "board", "supervisor" }),
        ["SecurityAlert"] = (Level.Internal, new[] { "supervisor", "board" }),
        ["ModuleSwitchChanged"] = (Level.Internal, new[] { "company", "board" }),
        ["OperatorHandover"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["AccountAdded"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["FixedAssetAcquired"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["FixedAssetDepreciationRun"] = (Level.Internal, new[] { "company", "board" }),
        ["FixedAssetDisposed"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["StockReceived"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["StockIssued"] = (Level.Internal, new[] { "department", "board" }),
        ["StockCounted"] = (Level.Internal, new[] { "department", "supervisor", "board" }),
        ["ProductionOrderCreated"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["ProductionCompleted"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["PurchaseRequested"] = (Level.Internal, new[] { "department", "board" }),
        ["PurchaseApproved"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["PurchaseOrderCreated"] = (Level.Internal, new[] { "department", "board", "supervisor" }),
        ["PurchaseDelivered"] = (Level.Internal, new[] { "department", "board" }),
        ["PurchaseInspected"] = (Level.Internal, new[] { "department", "board", "supervisor" }),
        ["PurchasePaymentApproved"] = (Level.Internal, new[] { "board", "supervisor" }),
        ["PurchasePaid"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["LoanReceived"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["LoanInterestAccrued"] = (Level.Internal, new[] { "board", "supervisor" }),
        ["LoanRepaid"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["TaxReturnFiled"] = (Level.Internal, new[] { "board", "supervisor" }),
        ["TaxPaid"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["NotifyGroupConfigured"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["ReportFiled"] = (Level.Internal, new[] { "supervisor", "board" }),
        ["ReportInvestigated"] = (Level.Internal, new[] { "supervisor", "board" }),
        ["ReportClosed"] = (Level.Internal, new[] { "supervisor", "board", "company" }),
        ["SupervisionEmailSet"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["IpRegistered"] = (Level.Internal, new[] { "company", "board", "department" }),
        ["IpRecordAdded"] = (Level.Internal, new[] { "company", "board" }),
        ["IpValued"] = (Level.Internal, new[] { "company", "board" }),
        ["IpCapitalized"] = (Level.Internal, new[] { "company", "board" }),
        ["IpAmortizationRun"] = (Level.Internal, new[] { "company", "board" }),
        ["IpImpaired"] = (Level.Internal, new[] { "board", "supervisor" }),
        ["IpDisposed"] = (Level.Internal, new[] { "company", "board", "supervisor" }),
        ["IpRenewalScan"] = (Level.Internal, new[] { "company", "department" }),
        ["EsopApplied"] = (Level.Internal, new[] { "board" }),
        ["EsopReviewed"] = (Level.Internal, new[] { "board" }),
        ["EsopGranted"] = (Level.High, new[] { "board", "shareholder" }),
        ["EsopVested"] = (Level.Internal, new[] { "board" }),
        ["EsopExercised"] = (Level.High, new[] { "board", "shareholder" }),
        ["EsopExited"] = (Level.High, new[] { "board", "shareholder" }),
        ["VirtualDividendDeclared"] = (Level.High, new[] { "board" }),
        ["VirtualDividendPaid"] = (Level.High, new[] { "company", "board" }),
        ["EmployeeShareSubscription"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["EmployeeShareGranted"] = (Level.Internal, new[] { "company", "board", "shareholder" }),
        ["TodoCompleted"] = (Level.Public, new[] { "company", "department" }),
        ["LegalEntryAdded"] = (Level.Internal, new[] { "company", "board" })
    };

    /// <summary>分级+接收范围：工资/薪酬规则/涉密合同/个人薪酬强制 High；未登记事件默认 Internal 且仅公司/董事会。</summary>
    public static (Level lvl, string[] kinds) Classify(string type, JsonNode? payload)
    {
        if (type is "PayrollRun" or "PayrollPaid" or "EmployeeHired" or "BonusGranted") return (Level.High, Policy[type].kinds);
        if (type == "RuleActivated" && payload?["kind"]?.ToString() is "PayrollChange" or "CommissionChange") return (Level.High, new[] { "board" });
        if (type == "ContractSigned" && payload?["secrecy"]?.ToString() == "Secret") return (Level.High, new[] { "board" });
        return Policy.TryGetValue(type, out var p) ? p : (Level.Internal, new[] { "company", "board" });
    }

    // 字段黑名单：任何群消息不得携带（个人银行账号/身份证/密码/姓名/工资明细/表决明细等）
    private static readonly HashSet<string> NeverSend = new()
    {
        "bankAccount", "idNumber", "password", "realName", "allocations", "lines", "payslips", "totals", "votes", "params", "items", "bom"
    };

    /// <summary>发送前字段过滤：去黑名单、去数组明细（明细一律系统内授权查看）。</summary>
    public static JsonObject Filter(JsonObject payload)
    {
        var o = new JsonObject();
        foreach (var kv in payload)
            if (!NeverSend.Contains(kv.Key) && kv.Value is not JsonArray && kv.Value is not JsonObject)
                o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    /// <summary>路由：绑定 ∩ 策略允许范围 ∩ 部门匹配；未绑定=不推送（默认拒绝）。</summary>
    public static List<string> Route(IEnumerable<(string groupId, string kind, string department)> groups,
        IEnumerable<(string groupId, string eventType)> bindings, string type, JsonNode? payload)
    {
        var (_, kinds) = Classify(type, payload);
        var bound = bindings.Where(b => b.eventType == type).Select(b => b.groupId).ToHashSet();
        var dept = payload?["department"]?.ToString();
        return groups.Where(g => bound.Contains(g.groupId) && kinds.Contains(g.kind)
                && (g.kind != "department" || g.department == "" || dept == null || dept == g.department))
            .Select(g => g.groupId).ToList();
    }

    /// <summary>按群类型渲染不同模板；High 一律摘要+授权查看提示，不含任何金额明细。</summary>
    public static string Render(string kind, string typeCn, string refNo, JsonObject filtered, bool high, string hash8)
    {
        var head = kind switch
        {
            "company" => "【公司群·公司事项】",
            "board" => "【董事会群·决策留痕】",
            "shareholder" => "【股东群·权益影响】",
            "supervisor" => "【监事群·监督提示】",
            "department" => "【部门群·业务执行】",
            "staff" => "【员工群·行政通知】",
            _ => "【通知】"
        };
        if (high)
            return $"{head}{typeCn}（{refNo}）已入链。\n敏感明细不推送群聊：请授权登录 EquiLedger 查看（事件哈希 {hash8}）。\n提示：群消息撤回不等于数据收回，原始数据仅存系统内。";
        var sb = new StringBuilder($"{head}{typeCn}（{refNo}）\n");
        foreach (var kv in filtered) sb.Append($"· {kv.Key}：{kv.Value}\n");
        sb.Append("明细与凭证请授权登录 EquiLedger 查看。");
        return sb.ToString();
    }
}
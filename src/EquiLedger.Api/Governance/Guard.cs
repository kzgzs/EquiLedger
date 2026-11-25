// 玄铁栈道 EquiLedger · 企业智能计算治理系统
// 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
// 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
using System.Text.Json.Nodes;
using EquiLedger.Engine;
using EquiLedger.Storage;
using Npgsql;

namespace EquiLedger.Governance;

public class GuardException : Exception
{
    public string Kind;
    public GuardException(string kind, string message) : base(message) { Kind = kind; }
}

public record Caller(string UserId, string Role, string? PartyId, bool Enabled, bool Frozen);

/// <summary>治理守卫：任何写请求先过 身份→权限→回避→决议/授权→规则守卫（治理层在业务层之上游）。</summary>
public static class Guard
{
    private static readonly HashSet<string> StrictResolution = new()
    {
        "RuleActivated", "ShareSubscription", "ShareTransfer", "ShareExit", "DividendApproved",
        "BonusGranted", "ModuleSwitchChanged", "InvestmentReceived", "OperatorHandover",
        "CorrectionRecorded", "PeriodClosed", "EmployeeLeft", "CompanyInfoChanged", "AccountAdded", "InvestmentMade",
        "FixedAssetDisposed", "ProductionOrderCreated",
        "PurchaseApproved", "PurchasePaymentApproved", "LoanReceived", "LoanRepaid", "TaxReturnFiled",
        "EmployeeShareSubscription", "EmployeeShareGranted", "ReportClosed",
        "EsopGranted", "EsopExercised", "EsopExited", "VirtualDividendDeclared"
    };
    private static readonly HashSet<string> RoutineOrResolution = new()
    {
        "ExpenseRecorded", "ContractSigned", "EmployeeHired", "SalesOrderConfirmed"
    };
    // 执行/采集类：执行已生效决议或锁定规则、只读采集，凭依据即可（全量留痕推送）
    private static readonly HashSet<string> ExecutionOrCollection = new()
    {
        "CapitalPaidIn", "DividendPaid", "CollectionReceived", "RevenueRecorded",
        "PayrollRun", "BankFlowImported", "EvidenceUploaded", "RelationDeclared", "InvestorQualified", "ShareholderDeceased", "TodoCompleted", "LegalEntryAdded", "InvestmentReturnReceived", "PayrollPaid",
        "FixedAssetAcquired", "FixedAssetDepreciationRun", "StockReceived", "StockIssued", "StockCounted", "MaterialDefined", "WarehouseDefined", "EquipmentRegistered", "ProductionCompleted",
        "PurchaseRequested", "PurchaseOrderCreated", "PurchaseDelivered", "PurchaseInspected", "PurchasePaid", "LoanInterestAccrued", "TaxPaid",
        "NotifyGroupConfigured", "SupervisionEmailSet", "ReportFiled",
        "IpRegistered", "IpRecordAdded", "IpValued", "IpCapitalized", "IpAmortizationRun", "IpImpaired", "IpDisposed", "IpRenewalScan",
        "EsopApplied", "EsopReviewed", "EsopVested", "VirtualDividendPaid",
        "DeviceRegistered", "ScanPacketCreated", "ScanAccepted", "ScanQuarantined", "ScanReviewResolved",
        "QualityInspected", "LogisticsScanned", "SalesScanned"
    };
    private static readonly Dictionary<string, string> RoutineCategory = new()
    {
        ["ExpenseRecorded"] = "Expense", ["ContractSigned"] = "Contract", ["EmployeeHired"] = "Hire", ["SalesOrderConfirmed"] = "Sales"
    };
    private static readonly Dictionary<string, string[]> AllowedResolutionKinds = new()
    {
        ["RuleActivated"] = new[] { "RuleChange", "PayrollChange", "CommissionChange", "CharterAmend" },
        ["ShareSubscription"] = new[] { "CapitalIncrease", "ShareSubscription", "Founding" },
        ["ShareTransfer"] = new[] { "ShareTransfer" },
        ["ShareExit"] = new[] { "ShareExit" },
        ["DividendApproved"] = new[] { "Dividend" },
        ["BonusGranted"] = new[] { "Bonus" },
        ["ModuleSwitchChanged"] = new[] { "ModuleSwitch" },
        ["InvestmentReceived"] = new[] { "Financing", "CapitalIncrease" },
        ["InvestmentMade"] = new[] { "ExternalInvestment" },
        ["FixedAssetDisposed"] = new[] { "AssetDisposal" },
        ["ProductionOrderCreated"] = new[] { "ProductionPlan" },
        ["PurchaseApproved"] = new[] { "PurchasePlan" },
        ["PurchasePaymentApproved"] = new[] { "PurchasePayment" },
        ["LoanReceived"] = new[] { "Loan" },
        ["LoanRepaid"] = new[] { "LoanRepayment" },
        ["TaxReturnFiled"] = new[] { "TaxFiling" },
        ["EmployeeShareSubscription"] = new[] { "EmployeeShare" },
        ["EmployeeShareGranted"] = new[] { "EmployeeShareGrant" },
        ["ReportClosed"] = new[] { "ReportHandling" },
        ["EsopGranted"] = new[] { "EsopPlan" },
        ["EsopExercised"] = new[] { "EsopExercise" },
        ["EsopExited"] = new[] { "EsopExit" },
        ["VirtualDividendDeclared"] = new[] { "VirtualDividend" },
        ["OperatorHandover"] = new[] { "OperatorHandover" },
        ["CorrectionRecorded"] = new[] { "Correction" },
        ["PeriodClosed"] = new[] { "PeriodClose" },
        ["EmployeeLeft"] = new[] { "EmployeeLeave" },
        ["CompanyInfoChanged"] = new[] { "CompanyChange" },
        ["AccountAdded"] = new[] { "AccountManage", "General" },
        ["ShareholderDeceased"] = new[] { "ShareholderDeath", "General" },
    };

    public static async Task Validate(NpgsqlConnection conn, EventStore.Draft d, Caller caller)
    {
        if (LicenseGate.BlocksWrites)
            throw new GuardException("LicenseInvalid", "商业授权无效或离线超宽限：新增写入已冻结（读取/验真/证据包不受影响），请续订或恢复授权服务器连接");
        var t = d.Type;
        var p = d.Payload;
        string Ps(string k) => p[k]?.GetValue<string>() ?? "";
        decimal Pd(string k) => ApplyCtx.JsonNum(p[k]);
        static void ValidateIdentity(JsonObject a)
        {
            var rn = a["realName"]?.GetValue<string>();
            var id = a["idNumber"]?.GetValue<string>();
            var un = a["username"]?.GetValue<string>() ?? "?";
            if (string.IsNullOrWhiteSpace(rn) || string.IsNullOrWhiteSpace(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, @"^\d{17}[\dXx]$"))
                throw new GuardException("InvalidIdentity", $"账号 {un} 缺真实姓名或有效18位身份证号（全员实名制）");
        }
        if (t == "AccountAdded")
        {
            ValidateIdentity(p);
            if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.accounts WHERE username=@u", ("u", Ps("username")))) > 0)
                throw new GuardException("DuplicateAccount", "登录名已存在");
        }

        if (t == "CompanyFounded")
        {
            if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.company")) > 0)
                throw new GuardException("DuplicateSetup", "公司已完成设立，设立向导只能执行一次");
            if (d.Payload["accounts"] is JsonArray accs)
                foreach (JsonObject a in accs) ValidateIdentity(a);
            return;
        }
        if (t == "SecurityAlert") return; // 系统告警通道
        if (!caller.Enabled || caller.Frozen)
            throw new GuardException("AccountDisabled", "账号已停用/冻结，一切操作被拒绝");

        // ---- 角色通道 ----
        if (t == "AccountStatusChanged")
        {
            if (caller.Role != "Admin") throw new GuardException("RoleDenied", "仅系统管理员可管理账号可用性（且无业务数据权限）");
            return;
        }
        if (t is "ResolutionProposed" or "ResolutionRecorded")
        {
            if (caller.Role is not ("Operator" or "Shareholder")) throw new GuardException("RoleDenied", "议案通道仅限股东/操作员");
            if (t == "ResolutionRecorded" && caller.Role != "Operator") throw new GuardException("RoleDenied", "线下决议纪要由操作员凭签名纪要入链");
            if ((d.Evidence as JsonArray)?.Count is null or 0) throw new GuardException("NoEvidence", "议案/决议必须附材料或签名纪要（P4 凡变更必有依据）");
            return;
        }
        if (t == "ResolutionVoted")
        {
            // 股东表决；一人公司老板可兼操作员与唯一股东（双身份），仅能为本人持股表决
            var soleOwner = caller.Role == "Operator" && caller.PartyId != null && Ps("partyId") == caller.PartyId;
            if (caller.Role != "Shareholder" && !soleOwner) throw new GuardException("RoleDenied", "表决权限属股东（治理通道，非业务写入）");
            return;
        }
        if (t == "ContractAccessed")
        {
            var cid = Ps("contractId");
            var rows = await conn.Rows("SELECT secrecy,parties FROM proj.contracts WHERE contract_id=@c", ("c", cid));
            if (rows.Count == 0) throw new GuardException("NotFound", "合同不存在");
            var secrecy = Pg.S(rows[0]["secrecy"]);
            if (secrecy == "Private")
            {
                var parties = Pg.J(rows[0]["parties"]).ToJsonString();
                var isParty = caller.PartyId != null && parties.Contains(caller.PartyId);
                var dualAuth = p["dualAuthorized"]?.GetValue<bool>() == true; // 监事调查/司法导出双授权
                if (!isParty && !(caller.Role == "Supervisor" && dualAuth))
                    throw new GuardException("PrivateContract", "私密协议仅签署当事人可查；监事调查/司法导出需双授权且访问留痕");
            }
            return;
        }
        if (t == "ReportInvestigated")
        {
            // 调查取证：监事或操作员（高层共同监督）；须附调查依据
            if (caller.Role != "Supervisor" && caller.Role != "Operator") throw new GuardException("RoleDenied", "举报调查权归监事/操作员（高层共同监督）");
            if (string.IsNullOrWhiteSpace(Ps("findings"))) throw new GuardException("BadParams", "调查结论必填");
            if ((d.Evidence as JsonArray)?.Count is null or 0) throw new GuardException("NoEvidence", "调查取证须附依据");
            return;
        }
        if (caller.Role != "Operator")
            throw new GuardException("NotOperator", "单一授权操作员模式：非操作员的一切写请求拒绝（决策在会议、计算在引擎、执行在一人）");

        // ---- 依据（P4）----
        var evCount = (d.Evidence as JsonArray)?.Count ?? 0;
        if (t != "EvidenceUploaded" && evCount == 0)
            throw new GuardException("NoEvidence", "缺少依据附件，事件不生效（P4：凡变更必有依据）");

        // ---- 期间锁定 ----
        var period = p["period"]?.GetValue<string>();
        if (period != null && Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.period_locks WHERE period=@p", ("p", period))) > 0)
            throw new GuardException("PeriodLocked", $"期间 {period} 已结账锁定，只能做当期更正事件（红字冲销）");

        // ---- 决议 / 例行授权 ----
        if (StrictResolution.Contains(t) || RoutineOrResolution.Contains(t))
        {
            var usedRoutine = false;
            if (d.ResolutionNo is null && RoutineOrResolution.Contains(t))
            {
                usedRoutine = await CheckRoutineAuth(conn, t, Pd("amount"), caller);
                if (usedRoutine) d.Payload["routineCategory"] = RoutineCategory[t];
            }
            if (!usedRoutine)
            {
                if (string.IsNullOrEmpty(d.ResolutionNo))
                    throw new GuardException("NoResolution", "无决议，不提交：写操作必须绑定有效决议编号（或例行授权额度内）");
                var rr = await conn.Rows("SELECT * FROM proj.resolutions WHERE resolution_no=@r", ("r", d.ResolutionNo));
                if (rr.Count == 0) throw new GuardException("NoResolution", "决议编号不存在");
                var res = rr[0];
                if (!(bool)res["passed"]!) throw new GuardException("ResolutionNotPassed", "决议未通过，不得执行");
                if ((bool)res["consumed"]!) throw new GuardException("ResolutionConsumed", "决议已被消费（一次决议一次执行），不得重复使用");
                var kind = Pg.S(res["kind"]);
                if (AllowedResolutionKinds.TryGetValue(t, out var allowed) && !allowed.Contains(kind) && kind != "General")
                    throw new GuardException("ResolutionKind", $"决议类型 {kind} 不适用于 {t}（需 {string.Join("/", allowed)}）");
                if (AllowedResolutionKinds.TryGetValue(t, out var a2) && kind == "General" && StrictResolution.Contains(t) && t != "ShareholderDeceased")
                    throw new GuardException("ResolutionKind", "关键操作不接受通用决议，必须专项决议");
                // 决议一致性比对：决议说涨薪到8000，录成9000 → 直接拒绝
                var resParams = (JsonObject)Pg.J(res["params"]);
                CompareConsistency(resParams, p, "");
            }
        }
        else if (!ExecutionOrCollection.Contains(t) && t != "CompanyFounded")
        {
            throw new GuardException("UnknownType", $"事件类型 {t} 未在治理协议注册");
        }

        // ---- 事件专项守卫 ----
        switch (t)
        {
            case "CapitalPaidIn":
                {
                    var cap = await CapRow(conn, Ps("partyId"));
                    if (cap is null) throw new GuardException("NoPosition", "股东不存在");
                    if (Pd("amount") <= 0 || Pd("amount") > Pg.D(cap["subscribed"]) - Pg.D(cap["paid_in"]))
                        throw new GuardException("AmountMismatch", "实缴金额超过认缴余额");
                    break;
                }
            case "PayrollPaid":
                {
                    var payPeriod = Ps("period");
                    var payRuns = await conn.Rows("SELECT status,totals FROM proj.payroll_runs WHERE period=@p", ("p", payPeriod));
                    if (payRuns.Count == 0) throw new GuardException("NoRun", "该期间尚未算薪，先提交 PayrollRun");
                    if (Pg.S(payRuns[0]["status"]) != "Calculated") throw new GuardException("AlreadyPaid", "该期间已确认发放，不可重复确认");
                    var payNet = decimal.TryParse(Pg.J(payRuns[0]["totals"])["net"]?.ToString(), out var pn) ? pn : 0m;
                    if (Pd("totalAmount") != payNet) throw new GuardException("AmountMismatch", $"确认金额须等于实发合计 {payNet}（与银行回单一致才入链）");
                    break;
                }
            case "FixedAssetAcquired":
                if (Pd("cost") <= 0 || Pd("lifeMonths") <= 0 || Pd("residualRate") < 0 || Pd("residualRate") >= 1) throw new GuardException("BadParams", "成本/折旧月数须为正，残值率∈[0,1)");
                if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.fixed_assets WHERE asset_id=@i", ("i", Ps("assetId")))) > 0) throw new GuardException("Duplicate", "资产卡片已存在");
                break;
            case "FixedAssetDepreciationRun":
                if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.depreciation_runs WHERE period=@p", ("p", Ps("period")))) > 0) throw new GuardException("AlreadyRun", "该期间折旧已计提");
                break;
            case "FixedAssetDisposed":
                {
                    var fa = await conn.Rows("SELECT status FROM proj.fixed_assets WHERE asset_id=@i", ("i", Ps("assetId")));
                    if (fa.Count == 0) throw new GuardException("NoAsset", "资产卡片不存在");
                    if (Pg.S(fa[0]["status"]) == "Disposed") throw new GuardException("AlreadyDisposed", "已处置");
                    if (Pd("proceeds") < 0) throw new GuardException("BadParams", "处置收款不能为负");
                    break;
                }
            case "StockReceived":
                if (Pd("qty") <= 0 || Pd("unitCost") < 0) throw new GuardException("BadParams", "数量须为正、单位成本非负");
                if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.materials WHERE material_id=@i", ("i", Ps("materialId")))) == 0) throw new GuardException("NoMaterial", "物料主数据不存在");
                break;
            case "StockIssued":
                {
                    if (Pd("qty") <= 0) throw new GuardException("BadParams", "出库数量须为正");
                    var sb = await conn.Rows("SELECT qty FROM proj.stock_balances WHERE material_id=@m AND warehouse=@w", ("m", Ps("materialId")), ("w", Ps("warehouse")));
                    if (sb.Count == 0 || Pg.D(sb[0]["qty"]) < Pd("qty")) throw new GuardException("InsufficientStock", "库存不足");
                    break;
                }
            case "StockCounted":
                if (Pd("countedQty") < 0) throw new GuardException("BadParams", "盘点数量非负");
                break;
            case "ProductionCompleted":
                {
                    var po = await conn.Rows("SELECT status,qty,completed_qty,bom FROM proj.production_orders WHERE order_no=@o", ("o", Ps("orderNo")));
                    if (po.Count == 0) throw new GuardException("NoOrder", "生产订单不存在");
                    if (Pg.S(po[0]["status"]) != "Open") throw new GuardException("OrderClosed", "订单已关闭");
                    var cq = Pd("completedQty");
                    if (cq <= 0 || cq > Pg.D(po[0]["qty"]) - Pg.D(po[0]["completed_qty"])) throw new GuardException("BadParams", "完工数量超过订单剩余");
                    foreach (var bl in Pg.J(po[0]["bom"]) as JsonArray ?? new JsonArray())
                    {
                        var need = Convert.ToDecimal(bl!["qtyPer"]!.ToString()) * cq;
                        var have = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(SUM(qty),0) FROM proj.stock_balances WHERE material_id=@m", ("m", bl["materialId"]!.GetValue<string>())));
                        if (have < need) throw new GuardException("InsufficientStock", $"物料 {bl["materialId"]} 不足以满足 BOM 领料");
                    }
                    break;
                }
            case "PurchaseRequested":
                {
                    var items = p["items"] as JsonArray;
                    if (items is null || items.Count == 0) throw new GuardException("BadParams", "采购明细不能为空");
                    foreach (var it in items) if (Convert.ToDecimal(it!["qty"]!.ToString()) <= 0) throw new GuardException("BadParams", "采购数量须为正");
                    break;
                }
            case "PurchaseApproved":
                {
                    var rq = await conn.Rows("SELECT status FROM proj.purchase_requests WHERE req_no=@r", ("r", Ps("reqNo")));
                    if (rq.Count == 0) throw new GuardException("NoRequest", "采购申请不存在");
                    if (Pg.S(rq[0]["status"]) != "Requested") throw new GuardException("AlreadyApproved", "申请已审批");
                    if (Pd("approvedAmount") <= 0) throw new GuardException("BadParams", "授权额度须为正");
                    break;
                }
            case "PurchaseOrderCreated":
                {
                    var rq = await conn.Rows("SELECT status FROM proj.purchase_requests WHERE req_no=@r", ("r", Ps("reqNo")));
                    if (rq.Count == 0 || Pg.S(rq[0]["status"]) != "Approved") throw new GuardException("NotApproved", "采购申请未经决议批准，不得下单");
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.purchase_orders WHERE po_no=@p", ("p", Ps("poNo")))) > 0) throw new GuardException("Duplicate", "订单号已存在");
                    var items = p["items"] as JsonArray;
                    if (items is null || items.Count == 0) throw new GuardException("BadParams", "订单明细不能为空");
                    break;
                }
            case "PurchaseDelivered":
                {
                    var po = await conn.Rows("SELECT status FROM proj.purchase_orders WHERE po_no=@p", ("p", Ps("poNo")));
                    if (po.Count == 0) throw new GuardException("NoOrder", "采购订单不存在");
                    if (Pg.S(po[0]["status"]) != "Open") throw new GuardException("OrderState", "订单状态不允许交货");
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.purchase_deliveries WHERE delivery_no=@d", ("d", Ps("deliveryNo")))) > 0) throw new GuardException("Duplicate", "交货单号已存在");
                    break;
                }
            case "PurchaseInspected":
                {
                    var dl = await conn.Rows("SELECT items FROM proj.purchase_deliveries WHERE delivery_no=@d", ("d", Ps("deliveryNo")));
                    if (dl.Count == 0) throw new GuardException("NoDelivery", "交货单不存在");
                    if (string.IsNullOrEmpty(Ps("inspector"))) throw new GuardException("BadParams", "验收人必填");
                    var delivered = new Dictionary<string, decimal>();
                    foreach (var it in Pg.J(dl[0]["items"]) as JsonArray ?? new JsonArray())
                        delivered[it!["materialId"]!.GetValue<string>()] = Convert.ToDecimal(it["qty"]!.ToString());
                    foreach (var key in new[] { "accepted", "rejected" })
                        foreach (var it in p[key] as JsonArray ?? new JsonArray())
                        {
                            var mid = it!["materialId"]!.GetValue<string>();
                            var q = Convert.ToDecimal(it["qty"]!.ToString());
                            if (q < 0) throw new GuardException("BadParams", "验收数量非负");
                            delivered[mid] = (delivered.TryGetValue(mid, out var left) ? left : 0) - q;
                            if (delivered[mid] < 0) throw new GuardException("OverAccepted", $"验收+拒收超过交货数量：{mid}");
                        }
                    break;
                }
            case "PurchasePaymentApproved":
            case "PurchasePaid":
                {
                    var po = await conn.Rows("SELECT status,paid_amount FROM proj.purchase_orders WHERE po_no=@p", ("p", Ps("poNo")));
                    if (po.Count == 0) throw new GuardException("NoOrder", "采购订单不存在");
                    if (t == "PurchasePaid")
                    {
                        if (string.IsNullOrEmpty(d.ResolutionNo)) throw new GuardException("NeedResolution", "采购支付必须挂付款审批决议");
                        if (Pg.S(po[0]["status"]) == "Closed") throw new GuardException("OrderClosed", "订单已结清");
                    }
                    else if (Pg.S(po[0]["status"]) != "Inspected") throw new GuardException("OrderState", "未验收不得审批付款");
                    decimal acceptedValue = 0;
                    var ins = await conn.Rows("SELECT accepted FROM proj.purchase_inspections WHERE po_no=@p", ("p", Ps("poNo")));
                    foreach (var i in ins)
                        foreach (var it in Pg.J(i["accepted"]) as JsonArray ?? new JsonArray())
                        {
                            var q = Convert.ToDecimal(it!["qty"]!.ToString()); var u = Convert.ToDecimal(it["unitPrice"]!.ToString());
                            var r = it["taxRate"] is null ? 0m : Convert.ToDecimal(it["taxRate"]!.ToString());
                            acceptedValue += Math.Round(q * u, 2) + Math.Round(Math.Round(q * u, 2) * r, 2);
                        }
                    var outstanding = acceptedValue - Pg.D(po[0]["paid_amount"]);
                    if (Pd("amount") <= 0 || Pd("amount") > outstanding) throw new GuardException("AmountExceed", $"支付金额须∈(0, 未付应付 {outstanding}]");
                    break;
                }
            case "LoanReceived":
                if (Pd("principal") <= 0 || Pd("annualRate") < 0 || Pd("termMonths") <= 0) throw new GuardException("BadParams", "本金/期数须为正、利率非负");
                if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.loans WHERE loan_id=@i", ("i", Ps("loanId")))) > 0) throw new GuardException("Duplicate", "借款编号已存在");
                break;
            case "LoanInterestAccrued":
                {
                    var ln = await conn.Rows("SELECT status,outstanding_principal,annual_rate FROM proj.loans WHERE loan_id=@i", ("i", Ps("loanId")));
                    if (ln.Count == 0) throw new GuardException("NoLoan", "借款不存在");
                    if (Pg.S(ln[0]["status"]) != "Active") throw new GuardException("LoanClosed", "借款已结清");
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.loan_interest WHERE loan_id=@i AND period=@p", ("i", Ps("loanId")), ("p", Ps("period")))) > 0) throw new GuardException("AlreadyAccrued", "该期利息已计提");
                    var expect = Math.Round(Pg.D(ln[0]["outstanding_principal"]) * Pg.D(ln[0]["annual_rate"]) / 12, 2);
                    if (Pd("amount") != expect) throw new GuardException("AmountMismatch", $"利息须等于 剩余本金×年利率÷12 = {expect}");
                    break;
                }
            case "LoanRepaid":
                {
                    var ln = await conn.Rows("SELECT outstanding_principal,accrued_interest FROM proj.loans WHERE loan_id=@i", ("i", Ps("loanId")));
                    if (ln.Count == 0) throw new GuardException("NoLoan", "借款不存在");
                    var pp = Pd("principalPart"); var ip = Pd("interestPart");
                    if (pp < 0 || ip < 0 || pp + ip <= 0) throw new GuardException("BadParams", "还本付息金额非负且合计为正");
                    if (pp > Pg.D(ln[0]["outstanding_principal"])) throw new GuardException("AmountExceed", "还本超过剩余本金");
                    if (ip > Pg.D(ln[0]["accrued_interest"])) throw new GuardException("AmountExceed", "付息超过已计提未付利息");
                    break;
                }
            case "TaxReturnFiled":
                {
                    if (Ps("kind") is not ("VAT" or "Surcharge" or "CIT" or "IIT")) throw new GuardException("BadParams", "税种须为 VAT/Surcharge/CIT/IIT");
                    if (Pd("amount") < 0) throw new GuardException("BadParams", "税额非负");
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.tax_returns WHERE period=@p AND kind=@k", ("p", Ps("period")), ("k", Ps("kind")))) > 0) throw new GuardException("AlreadyFiled", "该期该税种已申报");
                    break;
                }
            case "TaxPaid":
                {
                    var tr = await conn.Rows("SELECT status,amount FROM proj.tax_returns WHERE period=@p AND kind=@k", ("p", Ps("period")), ("k", Ps("kind")));
                    if (tr.Count == 0) throw new GuardException("NoReturn", "先申报后缴纳");
                    if (Pg.S(tr[0]["status"]) != "Filed") throw new GuardException("AlreadyPaid", "已缴纳");
                    if (Pd("amount") != Pg.D(tr[0]["amount"])) throw new GuardException("AmountMismatch", "缴纳金额须等于申报额");
                    break;
                }
            case "NotifyGroupConfigured":
                {
                    if (!EquiLedger.Engine.NotifyPolicy.GroupKinds.Contains(Ps("kind"))) throw new GuardException("BadParams", "群类型须为 company/board/shareholder/supervisor/department/staff");
                    if (!Ps("webhook").StartsWith("https://")) throw new GuardException("BadParams", "webhook 须为 https 地址（企业微信/钉钉/飞书群机器人）");
                    if (p["bindings"] is not JsonArray) throw new GuardException("BadParams", "bindings 须为事件类型数组");
                    break;
                }
            case "EmployeeShareSubscription":
                {
                    var em = await conn.Rows("SELECT party_id,leave_date FROM proj.employees WHERE employee_id=@i", ("i", Ps("employeeId")));
                    if (em.Count == 0) throw new GuardException("NoEmployee", "员工不存在");
                    if (em[0]["leave_date"] is not null and not DBNull) throw new GuardException("EmployeeLeft", "离职员工不得入股");
                    if (Pd("subscribed") <= 0 && (p["targetRatio"] is null || Pd("targetRatio") <= 0 || Pd("targetRatio") >= 1)) throw new GuardException("BadParams", "须填认缴额 subscribed 或目标比例 targetRatio(0,1)");
                    break;
                }
            case "EmployeeShareGranted":
                {
                    var em = await conn.Rows("SELECT party_id,leave_date FROM proj.employees WHERE employee_id=@i", ("i", Ps("employeeId")));
                    if (em.Count == 0) throw new GuardException("NoEmployee", "员工不存在");
                    if (em[0]["leave_date"] is not null and not DBNull) throw new GuardException("EmployeeLeft", "离职员工不得分股");
                    var cap = Pd("capitalAmount");
                    if (cap <= 0) throw new GuardException("BadParams", "分股额度须为正");
                    var src = Ps("source");
                    if (src == "ShareholderGift")
                    {
                        var have = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(subscribed,0) FROM proj.cap_positions WHERE party_id=@f AND cap_version=(SELECT MAX(version) FROM proj.cap_versions)", ("f", Ps("fromParty"))));
                        if (have < cap) throw new GuardException("InsufficientShares", $"赠与股东持股不足（{have} < {cap}）");
                    }
                    else if (src == "CapitalReserve")
                    {
                        var reserve = Convert.ToDecimal(await conn.Scalar("SELECT COALESCE(SUM(credit-debit),0) FROM proj.ledger WHERE account='CapitalReserve'"));
                        if (reserve < cap) throw new GuardException("InsufficientReserve", $"资本公积余额不足（{reserve} < {cap}）");
                    }
                    else throw new GuardException("BadParams", "source 须为 CapitalReserve 或 ShareholderGift");
                    break;
                }
            case "ReportClosed":
                {
                    var rp = await conn.Rows("SELECT status FROM proj.reports WHERE report_id=@r", ("r", Ps("reportId")));
                    if (rp.Count == 0) throw new GuardException("NoReport", "举报不存在");
                    if (Pg.S(rp[0]["status"]) == "Closed") throw new GuardException("AlreadyClosed", "举报已办结");
                    if (Ps("outcome") is not ("Substantiated" or "PartiallySubstantiated" or "Unsubstantiated")) throw new GuardException("BadParams", "结论须为 Substantiated/PartiallySubstantiated/Unsubstantiated");
                    if (Ps("severity") is not ("General" or "Serious")) throw new GuardException("BadParams", "情节须为 General/Serious（严重者可群通告）");
                    break;
                }
            case "ReportFiled":
                {
                    // 举报处理：操作员（铁面判官）登记监管邮箱转来的材料；被举报≠违规，须调查取证
                    if (Ps("category") is not ("Embezzlement" or "Fraud" or "RelatedPartyTransfer" or "ConflictOfInterest" or "AbuseOfPower" or "Other"))
                        throw new GuardException("BadParams", "举报类别须为 Embezzlement/Fraud/RelatedPartyTransfer/ConflictOfInterest/AbuseOfPower/Other");
                    if (string.IsNullOrWhiteSpace(Ps("description"))) throw new GuardException("BadParams", "举报描述必填");
                    if (string.IsNullOrWhiteSpace(Ps("target"))) throw new GuardException("BadParams", "被举报对象必填");
                    if (Ps("source") is not ("Mailbox" or "Other")) throw new GuardException("BadParams", "来源须为 Mailbox（监管邮箱）或 Other");
                    break;
                }
            case "SupervisionEmailSet":
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(Ps("email"), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                        throw new GuardException("BadParams", "监管邮箱格式无效");
                    break;
                }
            case "IpRegistered":
                {
                    if (Ps("category") is not ("Patent" or "Trademark" or "Copyright" or "TechSecret" or "DataRight" or "Franchise" or "Other"))
                        throw new GuardException("BadParams", "产权类别须为 Patent/Trademark/Copyright/TechSecret/DataRight/Franchise/Other");
                    if (string.IsNullOrWhiteSpace(Ps("name")) || string.IsNullOrWhiteSpace(Ps("ownerParty"))) throw new GuardException("BadParams", "产权名称与权利人必填");
                    break;
                }
            case "IpRecordAdded":
                {
                    if (await IpRow(conn, Ps("ipId")) is null) throw new GuardException("NoIp", "产权档案不存在");
                    if (Ps("kind") is not ("Transfer" or "License" or "Pledge" or "Dispute" or "Litigation" or "Renewal" or "StatusChange" or "Other"))
                        throw new GuardException("BadParams", "记录类型须为 Transfer/License/Pledge/Dispute/Litigation/Renewal/StatusChange/Other");
                    break;
                }
            case "IpValued":
                {
                    if (await IpRow(conn, Ps("ipId")) is null) throw new GuardException("NoIp", "产权档案不存在");
                    if (Pd("appraisedValue") < 0 || string.IsNullOrWhiteSpace(Ps("appraisalReport"))) throw new GuardException("BadParams", "评估价值须非负且附评估报告引用");
                    break;
                }
            case "IpCapitalized":
                {
                    // CAS6 确认闸门：可辨认性+经济利益流入+成本可靠计量，三者缺一不得入账
                    var ip = await IpRow(conn, Ps("ipId"));
                    if (ip is null) throw new GuardException("NoIp", "产权档案不存在");
                    if (ip["linked_asset_id"] is not null and not DBNull) throw new GuardException("AlreadyLinked", "该产权已关联无形资产账簿");
                    var cr = p["criteria"] as JsonObject;
                    if (cr?["identifiable"]?.GetValue<bool>() != true || cr?["economicBenefits"]?.GetValue<bool>() != true || cr?["costReliable"]?.GetValue<bool>() != true)
                        throw new GuardException("NotRecognizable", "不满足无形资产确认条件（可辨认性/经济利益流入/成本可靠计量），产权档案保留但不得计入财务资产");
                    if (Pd("cost") <= 0 || (int)Pd("lifeMonths") <= 0) throw new GuardException("BadParams", "成本须为正且使用寿命月数须为正");
                    if (Ps("payMode") is not ("Bank" or "AP")) throw new GuardException("BadParams", "payMode 须为 Bank/AP");
                    break;
                }
            case "IpImpaired":
                {
                    var ia = await IntangibleRow(conn, Ps("assetId"));
                    if (ia is null || Pg.S(ia["status"]) != "Active") throw new GuardException("NoAsset", "无形资产不存在或已处置");
                    var book = Pg.D(ia["cost"]) - Pg.D(ia["accumulated_amort"]) - Pg.D(ia["impairment"]);
                    if (Pd("amount") <= 0 || Pd("amount") > book) throw new GuardException("AmountExceed", $"减值额须为正且不超过账面价值 {book}");
                    break;
                }
            case "IpDisposed":
                {
                    var ia = await IntangibleRow(conn, Ps("assetId"));
                    if (ia is null || Pg.S(ia["status"]) != "Active") throw new GuardException("NoAsset", "无形资产不存在或已处置");
                    if (Pd("proceeds") < 0) throw new GuardException("BadParams", "处置收入须非负");
                    break;
                }
            case "IpAmortizationRun":
                {
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.amort_runs WHERE period=@p", ("p", Ps("period")))) > 0)
                        throw new GuardException("AlreadyRun", $"期间 {Ps("period")} 已摊销");
                    break;
                }
            case "EsopApplied":
                {
                    var em = await conn.Rows("SELECT leave_date FROM proj.employees WHERE employee_id=@i", ("i", Ps("employeeId")));
                    if (em.Count == 0) throw new GuardException("NoEmployee", "员工不存在");
                    if (em[0]["leave_date"] is not null and not DBNull) throw new GuardException("EmployeeLeft", "离职员工不得参与持股计划");
                    if (Ps("form") is not ("DirectTransfer" or "CapitalIncrease" or "Platform" or "Option" or "Virtual"))
                        throw new GuardException("BadParams", "形式须为 DirectTransfer/CapitalIncrease/Platform/Option/Virtual（五形式分离管理）");
                    if (Pd("quantity") <= 0 && (p["ratio"] is null || Pd("ratio") <= 0 || Pd("ratio") >= 1)) throw new GuardException("BadParams", "须填数量 quantity 或比例 ratio(0,1)");
                    break;
                }
            case "EsopReviewed":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null) throw new GuardException("NoPlan", "持股计划不存在");
                    if (Pg.S(pl["status"]) != "Applied") throw new GuardException("BadStatus", "仅已申请计划可审核");
                    if (Ps("result") is not ("Approved" or "Rejected")) throw new GuardException("BadParams", "审核结果须为 Approved/Rejected");
                    if (string.IsNullOrWhiteSpace(Ps("review"))) throw new GuardException("BadParams", "审核意见必填");
                    break;
                }
            case "EsopGranted":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null) throw new GuardException("NoPlan", "持股计划不存在");
                    if (Pg.S(pl["status"]) != "Reviewed") throw new GuardException("BadStatus", "计划须先通过资格审核");
                    if (string.IsNullOrWhiteSpace(Ps("confirmRef"))) throw new GuardException("NoConfirm", "须附员工确认凭据（授予不等于自动完成法律变更）");
                    var form = Pg.S(pl["form"]);
                    if (form == "DirectTransfer")
                    {
                        await RequireSignedContract(conn, Ps("contractId"), new[] { "股权类" });
                        var res = await ResolutionParams(conn, d.ResolutionNo!);
                        if (res?["preemptionWaived"]?.GetValue<bool>() != true)
                            throw new GuardException("Preemption", "受让老股须其他股东放弃优先购买权（决议载明）");
                        if (string.IsNullOrWhiteSpace(Ps("fromParty"))) throw new GuardException("BadParams", "须填出让股东 fromParty");
                    }
                    else if (form == "Platform" && string.IsNullOrWhiteSpace(Ps("platformParty") == "" ? Pg.S(pl["platform_party"]) : Ps("platformParty")))
                        throw new GuardException("BadParams", "持股平台形式须填平台股东 platformParty");
                    else if (form == "Virtual" && (Pg.D(pl["ratio"]) <= 0 || Pg.D(pl["ratio"]) >= 1))
                        throw new GuardException("BadParams", "虚拟分红权比例须在 (0,1)");
                    break;
                }
            case "EsopVested":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null || Pg.S(pl["form"]) != "Option") throw new GuardException("BadForm", "仅期权计划可归属");
                    if (Pg.S(pl["status"]) != "Granted") throw new GuardException("BadStatus", "计划须已授予");
                    if (p["conditionsMet"]?.GetValue<bool>() != true) throw new GuardException("ConditionsNotMet", "归属条件未满足（服务年限/业绩等）");
                    if (Pd("quantity") <= 0 || Pd("quantity") > Pg.D(pl["granted_qty"]) - Pg.D(pl["vested_qty"])) throw new GuardException("AmountExceed", "归属数量超过未归属余额");
                    if (Pd("fairValue") < 0) throw new GuardException("BadParams", "授予日公允价值须非负");
                    break;
                }
            case "EsopExercised":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null || Pg.S(pl["form"]) != "Option") throw new GuardException("BadForm", "仅期权计划可行权");
                    if (Pd("quantity") <= 0 || Pd("quantity") > Pg.D(pl["vested_qty"]) - Pg.D(pl["exercised_qty"])) throw new GuardException("AmountExceed", "行权数量超过已归属未行权余额");
                    break;
                }
            case "EsopExited":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null) throw new GuardException("NoPlan", "持股计划不存在");
                    if (Pg.S(pl["status"]) == "Exited") throw new GuardException("AlreadyExited", "计划已退出");
                    if (Pg.S(pl["form"]) is "DirectTransfer" or "CapitalIncrease" && Pd("capitalAmount") <= 0) throw new GuardException("BadParams", "实股退出须填退出注册资本额 capitalAmount");
                    break;
                }
            case "VirtualDividendDeclared":
                {
                    var pl = await EsopRow(conn, Ps("planId"));
                    if (pl is null || Pg.S(pl["form"]) != "Virtual") throw new GuardException("BadForm", "仅虚拟分红权计划可宣告分红");
                    if (Pg.S(pl["status"]) != "Granted") throw new GuardException("BadStatus", "计划须已授予");
                    if (Pd("amount") <= 0) throw new GuardException("BadParams", "分红额须为正");
                    break;
                }
            case "VirtualDividendPaid":
                {
                    var vd = await conn.Rows("SELECT status,amount FROM proj.esop_virtual_dividends WHERE id=@i", ("i", Ps("dividendId")));
                    if (vd.Count == 0) throw new GuardException("NoDividend", "先宣告后支付");
                    if (Pg.S(vd[0]["status"]) != "Declared") throw new GuardException("AlreadyPaid", "已支付");
                    if (Pd("amount") != Pg.D(vd[0]["amount"])) throw new GuardException("AmountMismatch", "支付额须等于宣告额");
                    break;
                }
            case "DeviceRegistered":
                {
                    if (Ps("adapter") is not ("usb" or "serial" or "network" or "sdk" or "camera")) throw new GuardException("BadParams", "适配器须为 usb/serial/network/sdk/camera（统一接入协议，不绑定品牌）");
                    if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.devices WHERE device_id=@d", ("d", Ps("deviceId")))) > 0) throw new GuardException("DuplicateDevice", "设备已注册");
                    break;
                }
            case "ScanPacketCreated":
                {
                    if (Ps("kind") is not ("ProductionPack" or "WarehouseIn" or "WarehouseOut" or "WarehouseCount" or "QualityCheck" or "Logistics" or "Sales"))
                        throw new GuardException("BadParams", "业务包类型须为 ProductionPack/WarehouseIn/WarehouseOut/WarehouseCount/QualityCheck/Logistics/Sales");
                    if (string.IsNullOrWhiteSpace(Ps("refId")) || p["payload"] is not JsonObject) throw new GuardException("BadParams", "refId 与 payload 必填");
                    break;
                }
            case "ScanAccepted":
                {
                    var pk = await conn.Rows("SELECT status FROM proj.scan_packets WHERE packet_id=@p", ("p", Ps("packetId")));
                    if (pk.Count == 0) throw new GuardException("NoPacket", "业务包不存在");
                    if (Pg.S(pk[0]["status"]) != "Ready") throw new GuardException("DuplicateScan", "业务包已消费/隔离，重复扫描拒绝");
                    var dv = await conn.Rows("SELECT enabled FROM proj.devices WHERE device_id=@d", ("d", Ps("deviceId")));
                    if (dv.Count == 0 || !(bool)dv[0]["enabled"]!) throw new GuardException("DeviceDenied", "设备未注册或已停用");
                    break;
                }
            case "ScanQuarantined":
                {
                    var pk = await conn.Rows("SELECT status FROM proj.scan_packets WHERE packet_id=@p", ("p", Ps("packetId")));
                    if (pk.Count == 0 || Pg.S(pk[0]["status"]) != "Ready") throw new GuardException("BadStatus", "仅待消费业务包可隔离");
                    if (string.IsNullOrWhiteSpace(Ps("reason"))) throw new GuardException("BadParams", "隔离原因必填");
                    break;
                }
            case "ScanReviewResolved":
                {
                    var rv = await conn.Rows("SELECT status FROM proj.scan_reviews WHERE review_id=@r", ("r", Ps("reviewId")));
                    if (rv.Count == 0) throw new GuardException("NoReview", "复核单不存在");
                    if (Pg.S(rv[0]["status"]) != "Open") throw new GuardException("AlreadyResolved", "复核已处理");
                    if (Ps("result") is not ("Approved" or "Rejected")) throw new GuardException("BadParams", "复核结果须为 Approved/Rejected");
                    break;
                }
            case "QualityInspected":
                {
                    if (Ps("result") is not ("Pass" or "Fail")) throw new GuardException("BadParams", "质检结果须为 Pass/Fail");
                    if (Pd("qty") <= 0 || string.IsNullOrWhiteSpace(Ps("batch")) || string.IsNullOrWhiteSpace(Ps("product")) || string.IsNullOrWhiteSpace(Ps("inspector")) || string.IsNullOrWhiteSpace(Ps("standard")))
                        throw new GuardException("BadParams", "批次/产品/检验人/检验标准/数量必填");
                    break;
                }
            case "LogisticsScanned":
                {
                    if (Ps("stage") is not ("Handover" or "Ship" or "Receive")) throw new GuardException("BadParams", "物流阶段须为 Handover/Ship/Receive");
                    if (string.IsNullOrWhiteSpace(Ps("tracking"))) throw new GuardException("BadParams", "运单/包裹号必填");
                    break;
                }
            case "SalesScanned":
                {
                    if (Pd("amount") <= 0 || Pd("qty") <= 0 || string.IsNullOrWhiteSpace(Ps("orderId")) || string.IsNullOrWhiteSpace(Ps("customer")) || string.IsNullOrWhiteSpace(Ps("materialId")) || string.IsNullOrWhiteSpace(Ps("warehouse")))
                        throw new GuardException("BadParams", "销售扫码须含订单/客户/金额/物料/仓库/数量");
                    break;
                }
            case "ShareSubscription":
            case "ShareTransfer":
                {
                    await RequireSignedContract(conn, Ps("contractId"), new[] { "股权类", "融资类" });
                    if (t == "ShareTransfer")
                    {
                        var res = await ResolutionParams(conn, d.ResolutionNo!);
                        if (res?["preemptionWaived"]?.GetValue<bool>() != true)
                            throw new GuardException("Preemption", "对外转让必须走完通知→优先购买权行使期流程（决议须载明其他股东放弃优先购买权）");
                    }
                    break;
                }
            case "ShareExit":
                {
                    var cap = await CapRow(conn, Ps("partyId"));
                    if (cap is null || Pd("capitalAmount") > Pg.D(cap["subscribed"]))
                        throw new GuardException("AmountMismatch", "退股额度超过持股");
                    break;
                }
            case "DividendApproved":
                {
                    var plan = await conn.Rows("SELECT status,distributable FROM proj.dividend_plans WHERE plan_id=@p", ("p", Ps("planId")));
                    if (plan.Count == 0 || Pg.S(plan[0]["status"]) != "Candidate")
                        throw new GuardException("PlanState", "分红方案不存在或非候选稿（结果数字由引擎生成，人工只能决议）");
                    break;
                }
            case "DividendPaid":
                {
                    var done = await conn.Scalar("SELECT status FROM proj.todos WHERE todo_id=@t", ("t", $"TD-TaxFiling-{Ps("planId")}"));
                    if (Pg.S(done) != "Done")
                        throw new GuardException("TaxNotFiled", "代扣个税未申报，不允许标记分红完成");
                    break;
                }
            case "RuleActivated":
                {
                    if (p["effectiveFrom"] is JsonValue ef && DateTimeOffset.TryParse(ef.GetValue<string>(), out var eff) && eff.Date < DateTime.UtcNow.Date)
                        throw new GuardException("Retroactive", "规则只对未来生效，禁止追溯修改（P1 规则前置）");
                    var kind = Ps("kind");
                    if (kind == "Charter.ExemptLoss")
                        throw new GuardException("InvalidClause", "“某股东永远不承担亏损”类条款与公司法强制性规定冲突，无效条款拦截");
                    if (kind == "Partnership.Allocation" && p["params"]?["partners"] is JsonArray pa)
                    {
                        var sum = 0m; var anyZero = false;
                        foreach (JsonObject x in pa) { var r = x["ratio"]!.GetValue<decimal>(); sum += r; if (r <= 0) anyZero = true; }
                        if (Math.Abs(sum - 1m) > 0.000001m || anyZero)
                            throw new GuardException("InvalidClause", "合伙协议不得约定将全部利润分配给部分合伙人（159号文），比例之和须为1且人人有份");
                    }
                    break;
                }
            case "ContractSigned": await ContractGuard(conn, d); break;
            case "EmployeeLeft":
                if (p["selfConfirmed"]?.GetValue<bool>() != true)
                    throw new GuardException("SelfConfirm", "离职事件需本人实名确认，代签不可能（P5）");
                break;
            case "ModuleSwitchChanged": await ModuleGuard(conn, d); break;
            case "InvestmentReceived": await InvestmentGuard(conn, d); break;
            case "ExpenseRecorded":
                {
                    var acct = p["payeeAccount"]?.GetValue<string>();
                    if (acct != null && Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.whitelist_accounts WHERE account_no=@a", ("a", acct))) == 0)
                        throw new GuardException("Whitelist", "收款账户不在白名单（新增账户需双人复核决议），付款拦截");
                    break;
                }
            case "OperatorHandover":
                {
                    var res = await ResolutionParams(conn, d.ResolutionNo!);
                    if (res?["supervisorSign"]?.GetValue<bool>() != true || res?["shareholderSign"]?.GetValue<bool>() != true)
                        throw new GuardException("DualSign", "后备操作员紧急启用需监事+股东代表双签决议");
                    break;
                }
        }
    }

    private static async Task<bool> CheckRoutineAuth(NpgsqlConnection conn, string type, decimal amount, Caller caller)
    {
        var rows = await conn.Rows("SELECT params FROM proj.rule_versions WHERE kind='RoutineAuth' AND status='Active' ORDER BY version DESC LIMIT 1");
        if (rows.Count == 0) return false;
        var rule = (JsonObject)Pg.J(rows[0]["params"]);
        var cat = RoutineCategory[type];
        var cats = rule["categories"] as JsonArray;
        if (cats is null || !cats.Any(x => x!.GetValue<string>() == cat)) return false;
        var single = ApplyCtx.JsonNum(rule["singleLimit"]);
        var monthly = ApplyCtx.JsonNum(rule["monthlyCap"]);
        if (amount > single) return false;
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        var used = Pg.D(await conn.Scalar("SELECT used FROM proj.routine_usage WHERE month=@m AND category=@c", ("m", month), ("c", cat)));
        if (used + amount > monthly) return false;
        return true;
    }

    private static void CompareConsistency(JsonObject resParams, JsonObject payload, string path)
    {
        foreach (var kv in resParams)
        {
            if (kv.Key is "subjectParty" or "preemptionWaived" or "supervisorSign" or "shareholderSign" or "note") continue;
            var pv = payload[kv.Key];
            if (pv is null) throw new GuardException("ResolutionMismatch", $"决议参数 {path}{kv.Key} 在录入内容中缺失");
            CompareNode(kv.Value, pv, path + kv.Key + ".");
        }
    }

    private static void CompareNode(JsonNode? a, JsonNode? b, string path)
    {
        if (a is JsonObject ao && b is JsonObject bo) { CompareConsistency(ao, bo, path); return; }
        if (a is JsonArray aa && b is JsonArray ba)
        {
            if (aa.Count != ba.Count) throw new GuardException("ResolutionMismatch", $"决议与录入不一致：{path} 数组长度");
            for (var i = 0; i < aa.Count; i++) CompareNode(aa[i], ba[i], path + $"[{i}].");
            return;
        }
        decimal? Na(JsonNode? n) => n is JsonValue v && v.TryGetValue(out decimal d) ? d : null;
        var na = Na(a); var nb = Na(b);
        if (na != null && nb != null) { if (na != nb) throw new GuardException("ResolutionMismatch", $"决议与录入不一致：{path} 决议={na} 录入={nb}"); return; }
        var sa = a?.ToJsonString(); var sb = b?.ToJsonString();
        if (sa != sb) throw new GuardException("ResolutionMismatch", $"决议与录入不一致：{path} 决议={sa} 录入={sb}");
    }

    private static async Task<Dictionary<string, object?>?> CapRow(NpgsqlConnection conn, string partyId)
    {
        var rows = await conn.Rows(@"SELECT cp.* FROM proj.cap_positions cp WHERE cp.cap_version=(SELECT MAX(version) FROM proj.cap_versions) AND cp.party_id=@p", ("p", partyId));
        return rows.FirstOrDefault();
    }

    private static async Task<Dictionary<string, object?>?> IpRow(NpgsqlConnection conn, string ipId)
        => (await conn.Rows("SELECT * FROM proj.ip_rights WHERE ip_id=@i", ("i", ipId))).FirstOrDefault();

    private static async Task<Dictionary<string, object?>?> IntangibleRow(NpgsqlConnection conn, string assetId)
        => (await conn.Rows("SELECT * FROM proj.intangible_assets WHERE asset_id=@i", ("i", assetId))).FirstOrDefault();

    private static async Task<Dictionary<string, object?>?> EsopRow(NpgsqlConnection conn, string planId)
        => (await conn.Rows("SELECT * FROM proj.esop_plans WHERE plan_id=@i", ("i", planId))).FirstOrDefault();

    private static async Task<JsonObject?> ResolutionParams(NpgsqlConnection conn, string no)
    {
        var rows = await conn.Rows("SELECT params FROM proj.resolutions WHERE resolution_no=@r", ("r", no));
        return rows.Count == 0 ? null : (JsonObject)Pg.J(rows[0]["params"]);
    }

    private static async Task RequireSignedContract(NpgsqlConnection conn, string contractId, string[] categories)
    {
        if (string.IsNullOrEmpty(contractId)) throw new GuardException("NoContract", "合同未签署入链，股权登记不生效（M7：合同是规则来源）");
        var rows = await conn.Rows("SELECT category,status FROM proj.contracts WHERE contract_id=@c", ("c", contractId));
        if (rows.Count == 0 || Pg.S(rows[0]["status"]) != "Signed" || !categories.Contains(Pg.S(rows[0]["category"])))
            throw new GuardException("NoContract", "必须绑定已签署的股权类/融资类合同");
    }

    private static async Task ContractGuard(NpgsqlConnection conn, EventStore.Draft d)
    {
        var p = d.Payload;
        var type = p["contractType"]?.GetValue<string>() ?? "";
        var clauses = p["clauses"] as JsonArray ?? new JsonArray();
        decimal Num(JsonNode? n) => ApplyCtx.JsonNum(n);

        if (type.Contains("放弃社保") || clauses.Any(x => x?["kind"]?.GetValue<string>() == "SiWaiver"))
            throw new GuardException("SiWaiverInvalid", "“自愿放弃社保”约定依法无效（法释〔2025〕12号第十九条），拒绝录入为免缴依据；员工据此解约可主张经济补偿N");
        if (type == "竞业限制协议")
        {
            var months = Num(clauses.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == "NonCompete")?["months"]);
            var comp = Num(clauses.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == "NonCompete")?["monthlyCompensation"]);
            if (months > 24) throw new GuardException("InvalidClause", "竞业限制期限不得超过2年（劳动合同法第二十四条）");
            if (comp <= 0) throw new GuardException("InvalidClause", "竞业限制必须约定按月经济补偿，否则协议对员工无约束力");
        }
        if (type == "培训服务期协议")
        {
            var fee = Num(clauses.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == "Training")?["trainingFee"]);
            var penalty = Num(clauses.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == "Training")?["penalty"]);
            if (penalty > fee) throw new GuardException("InvalidClause", "服务期违约金不得超过培训费用（劳动合同法第二十二条）");
        }
        if (type == "担保合同")
        {
            var rn = d.ResolutionNo;
            var ok = rn != null && Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.resolutions WHERE resolution_no=@r AND kind='Guarantee' AND passed=TRUE", ("r", rn))) > 0;
            if (!ok) throw new GuardException("GuaranteeBlocked", "对外担保未经股东会决议，系统硬拦截并向全体股东告警");
        }
        var text = System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if ((type.Contains("投资") || type.Contains("入股") || p["category"]?.GetValue<string>() == "融资类") &&
            (text.Contains("保本") || text.Contains("保收益") || text.Contains("固定回报") || text.Contains("承诺回购保")))
            throw new GuardException("IllegalFundraising", "保本保收益/固定回报条款涉嫌非法集资，守卫直接拦截");
    }

    private static async Task ModuleGuard(NpgsqlConnection conn, EventStore.Draft d)
    {
        var mid = d.Payload["moduleId"]?.GetValue<string>() ?? "";
        var target = d.Payload["targetState"]?.GetValue<string>() ?? "";
        var rows = await conn.Rows("SELECT * FROM proj.module_states WHERE module_id=@m", ("m", mid));
        if (rows.Count == 0) throw new GuardException("NoModule", "模块不存在");
        var m = rows[0];
        if (target is "Disabled" or "Suspended" or "Archived")
        {
            if ((bool)m["mandatory"]!)
            {
                if (mid == "M13" && Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.employees WHERE leave_date IS NULL")) > 0)
                    throw new GuardException("MandatoryModule", "存在在册员工，社保模块不可停用（法定义务）");
                if (mid != "M13")
                    throw new GuardException("MandatoryModule", "基础治理模块不可关闭（治理地基）");
            }
            return;
        }
        if (target == "Enabled")
        {
            var prereqs = (JsonArray)Pg.J(m["prereqs"]);
            foreach (JsonObject q in prereqs)
            {
                var kind = q["kind"]!.GetValue<string>(); var value = q["value"]!.GetValue<string>();
                var ok = kind switch
                {
                    "CompanyType" => Pg.S(await conn.Scalar("SELECT company_type FROM proj.company WHERE id=1")) == value,
                    "IndustryTag" => Pg.S(await conn.Scalar("SELECT industry_tags FROM proj.company WHERE id=1")).Contains(value),
                    "ResolutionKind" => Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.resolutions WHERE kind=@k AND passed=TRUE", ("k", value))) > 0,
                    _ => false
                };
                if (!ok) throw new GuardException("PrereqUnmet", $"模块 {mid} 先决条件未满足：{kind}={value}（开关置灰，强行开启被拒绝并留痕）");
            }
        }
    }

    private static async Task InvestmentGuard(NpgsqlConnection conn, EventStore.Draft d)
    {
        var p = d.Payload;
        if (Pg.S(await conn.Scalar("SELECT state FROM proj.module_states WHERE module_id='M15'")) != "Enabled")
            throw new GuardException("ModuleDisabled", "在线投资模块未启用（需融资决议+合规检查）");
        var investor = p["investorId"]?.GetValue<string>() ?? "";
        if (Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.investors WHERE investor_id=@i AND qualified=TRUE", ("i", investor))) == 0)
            throw new GuardException("NotQualified", "投资人未完成合格投资者认定，禁止注资");
        var cid = p["contractId"]?.GetValue<string>() ?? "";
        var contracts = await conn.Rows("SELECT status,category,doc_hash FROM proj.contracts WHERE contract_id=@c", ("c", cid));
        if (contracts.Count == 0 || Pg.S(contracts[0]["status"]) != "Signed")
            throw new GuardException("NoContract", "入股合同未电子签署入链，到账不确认为注资（款项挂异常原路处理）");
        var amount = ApplyCtx.JsonNum(p["amount"]);
        var clauseAmount = p["contractAmount"] is null ? -1m : ApplyCtx.JsonNum(p["contractAmount"]);
        if (clauseAmount != amount)
            throw new GuardException("AmountMismatch", "到账与合同金额不符（多付/少付/第三方代付），挂异常不确认入股");
        var companyType = Pg.S(await conn.Scalar("SELECT company_type FROM proj.company WHERE id=1"));
        var limit = companyType == "股份有限公司" ? 200 : 50;
        var count = Convert.ToInt64(await conn.Scalar("SELECT COUNT(*) FROM proj.cap_positions WHERE cap_version=(SELECT MAX(version) FROM proj.cap_versions) AND status='Normal'"));
        var isNew = await CapRow(conn, investor) is null;
        if (count + (isNew ? 1 : 0) > limit)
            throw new GuardException("HeadcountRedline", $"股东人数触及红线（{limit}人），融资流程冻结——越线涉嫌擅自公开发行/非法集资");
    }
}
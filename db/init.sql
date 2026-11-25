-- 玄铁栈道 EquiLedger · 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
-- 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。
-- EquiLedger 数据库初始化（由迁移账号执行，运行账号无 DDL 权限）
-- L2 数据层防线：事件表仅授 INSERT/SELECT，物理上无 UPDATE/DELETE 通道

CREATE SCHEMA IF NOT EXISTS events;
CREATE SCHEMA IF NOT EXISTS proj;

-- ============ 事件层（Append-Only） ============
CREATE TABLE IF NOT EXISTS events.event_log (
    seq           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    event_id      UUID NOT NULL UNIQUE,
    event_type    TEXT NOT NULL,
    actor_id      TEXT NOT NULL,
    occurred_at   TIMESTAMPTZ NOT NULL,
    resolution_no TEXT,
    evidence      JSONB NOT NULL DEFAULT '[]',
    payload       JSONB NOT NULL,
    rule_refs     JSONB NOT NULL DEFAULT '[]',
    prev_hash     CHAR(64) NOT NULL,
    hash          CHAR(64) NOT NULL,
    signature     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_event_type ON events.event_log(event_type);

CREATE TABLE IF NOT EXISTS events.anchors (
    id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    upto_seq    BIGINT NOT NULL,
    head_hash   CHAR(64) NOT NULL,
    anchored_at TIMESTAMPTZ NOT NULL,
    anchor_ref  TEXT NOT NULL
);

-- 任何 UPDATE/DELETE 一律拒绝（即使误授权也拦截）
CREATE OR REPLACE FUNCTION events.deny_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'APPEND-ONLY: event_log 禁止 UPDATE/DELETE（EquiLedger P2/P7）';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_event_no_update ON events.event_log;
CREATE TRIGGER trg_event_no_update BEFORE UPDATE OR DELETE ON events.event_log
    FOR EACH ROW EXECUTE FUNCTION events.deny_mutation();
DROP TRIGGER IF EXISTS trg_event_no_truncate ON events.event_log;
CREATE TRIGGER trg_event_no_truncate BEFORE TRUNCATE ON events.event_log
    FOR EACH STATEMENT EXECUTE FUNCTION events.deny_mutation();
DROP TRIGGER IF EXISTS trg_anchor_no_mutation ON events.anchors;
CREATE TRIGGER trg_anchor_no_mutation BEFORE UPDATE OR DELETE OR TRUNCATE ON events.anchors
    FOR EACH STATEMENT EXECUTE FUNCTION events.deny_mutation();

-- ============ 投影层（可销毁重建） ============
CREATE TABLE IF NOT EXISTS proj.accounts (
    user_id     TEXT PRIMARY KEY,
    username    TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    role        TEXT NOT NULL,           -- Operator/BackupOperator/Shareholder/Supervisor/Admin/Employee
    party_id    TEXT,
    pwd_hash    TEXT NOT NULL,
    pub_key     TEXT NOT NULL,
    priv_key    TEXT NOT NULL,           -- MVP：加密存储于库，生产应接硬件密钥
    enabled     BOOLEAN NOT NULL DEFAULT TRUE,
    frozen      BOOLEAN NOT NULL DEFAULT FALSE,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.sessions (
    token TEXT PRIMARY KEY, user_id TEXT NOT NULL, created_at TIMESTAMPTZ NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.company (
    id INT PRIMARY KEY DEFAULT 1,
    name TEXT NOT NULL, company_type TEXT NOT NULL,
    registered_capital NUMERIC(19,4) NOT NULL,
    industry_tags JSONB NOT NULL DEFAULT '[]',
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.parties (
    party_id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL,
    id_number_enc TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.relations (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    from_party TEXT NOT NULL, to_party TEXT NOT NULL,
    relation TEXT NOT NULL,             -- Spouse/Parent/Child/Sibling/Control/Invest
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.resolutions (
    resolution_no TEXT PRIMARY KEY,
    title TEXT NOT NULL, kind TEXT NOT NULL,
    params JSONB NOT NULL,              -- 决议固化参数（一致性比对依据）
    votes JSONB NOT NULL,               -- 表决记录（含回避剔除）
    passed BOOLEAN NOT NULL,
    evidence JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    consumed BOOLEAN NOT NULL DEFAULT FALSE,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.rule_versions (
    rule_id TEXT NOT NULL, version INT NOT NULL,
    kind TEXT NOT NULL, subject TEXT,   -- 适用对象（员工/股东/全局）
    params JSONB NOT NULL,
    effective_from TIMESTAMPTZ NOT NULL,
    resolution_no TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active', -- Active/Superseded
    source_event_id UUID NOT NULL,
    PRIMARY KEY (rule_id, version)
);
CREATE TABLE IF NOT EXISTS proj.cap_versions (
    version INT PRIMARY KEY,
    effective_at TIMESTAMPTZ NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.cap_positions (
    cap_version INT NOT NULL,
    party_id TEXT NOT NULL,
    subscribed NUMERIC(19,4) NOT NULL,
    paid_in NUMERIC(19,4) NOT NULL,
    ratio NUMERIC(9,6) NOT NULL,
    voting_ratio NUMERIC(9,6) NOT NULL,
    status TEXT NOT NULL DEFAULT 'Normal', -- Normal/Exited/Deceased/Frozen
    PRIMARY KEY (cap_version, party_id)
);
CREATE TABLE IF NOT EXISTS proj.ledger (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    period TEXT NOT NULL,               -- YYYY-MM
    account TEXT NOT NULL,
    debit NUMERIC(19,4) NOT NULL DEFAULT 0,
    credit NUMERIC(19,4) NOT NULL DEFAULT 0,
    memo TEXT NOT NULL,
    reversed_by BIGINT,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.period_locks (
    period TEXT PRIMARY KEY, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.calc_results (
    calc_id TEXT PRIMARY KEY,
    kind TEXT NOT NULL, subject TEXT,
    amount NUMERIC(19,4),
    derivation JSONB NOT NULL,          -- 推导单：逐行公式+规则版本+政策文号
    rule_refs JSONB NOT NULL,
    input_events JSONB NOT NULL,
    status TEXT NOT NULL DEFAULT 'Current', -- Current/PendingUpdate
    created_at TIMESTAMPTZ NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.dividend_plans (
    plan_id TEXT PRIMARY KEY,
    fiscal_year INT NOT NULL,
    distributable NUMERIC(19,4) NOT NULL,
    reserve_extracted NUMERIC(19,4) NOT NULL,
    allocations JSONB NOT NULL,
    status TEXT NOT NULL,               -- Candidate/Approved/Paid
    resolution_no TEXT,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.employees (
    employee_id TEXT PRIMARY KEY, party_id TEXT NOT NULL, name TEXT NOT NULL,
    position TEXT, hire_date DATE NOT NULL, leave_date DATE,
    is_shareholder BOOLEAN NOT NULL DEFAULT FALSE,
    social_insurance_city TEXT NOT NULL DEFAULT 'default',
    si_waiver_rejected BOOLEAN NOT NULL DEFAULT FALSE,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.payroll_runs (
    run_id TEXT PRIMARY KEY, period TEXT NOT NULL,
    lines JSONB NOT NULL, totals JSONB NOT NULL,
    source_event_id UUID NOT NULL
);
-- 发放闭环（幂等升级）：算薪=Calculated，回单确认=Paid
ALTER TABLE proj.payroll_runs ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'Calculated';
ALTER TABLE proj.payroll_runs ADD COLUMN IF NOT EXISTS paid_event_id UUID;
ALTER TABLE proj.payroll_runs ADD COLUMN IF NOT EXISTS receipt_hash TEXT;
ALTER TABLE proj.payroll_runs ADD COLUMN IF NOT EXISTS auto BOOLEAN NOT NULL DEFAULT FALSE;
CREATE TABLE IF NOT EXISTS proj.contracts (
    contract_id TEXT PRIMARY KEY,
    category TEXT NOT NULL, contract_type TEXT NOT NULL,
    parties JSONB NOT NULL, secrecy TEXT NOT NULL, -- Public/Internal/Restricted/Private
    doc_hash TEXT NOT NULL, clause_rule_ids JSONB NOT NULL DEFAULT '[]',
    status TEXT NOT NULL,               -- Signed/Superseded/Terminated
    signed_at TIMESTAMPTZ, expires_on DATE,
    supplement_of TEXT,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.module_states (
    module_id TEXT PRIMARY KEY, name TEXT NOT NULL,
    state TEXT NOT NULL,                -- Disabled/PendingPrereq/Enabled/Suspended/Archived
    mandatory BOOLEAN NOT NULL DEFAULT FALSE,
    prereqs JSONB NOT NULL DEFAULT '[]',
    resolution_no TEXT,
    source_event_id UUID
);
CREATE TABLE IF NOT EXISTS proj.todos (
    todo_id TEXT PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL,
    detail JSONB NOT NULL, assignee TEXT,
    status TEXT NOT NULL DEFAULT 'Open', -- Open/Done
    created_at TIMESTAMPTZ NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.notifications (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    channel TEXT NOT NULL,              -- WorkGroup/Shareholder
    summary JSONB NOT NULL,
    pushed_at TIMESTAMPTZ NOT NULL,
    push_status TEXT NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.bank_flows (
    flow_id TEXT PRIMARY KEY, direction TEXT NOT NULL,
    counterparty TEXT NOT NULL, amount NUMERIC(19,4) NOT NULL,
    flow_date DATE NOT NULL,
    matched_event UUID, status TEXT NOT NULL, -- Matched/UnmatchedException
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.whitelist_accounts (
    account_no TEXT PRIMARY KEY, holder TEXT NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.investors (
    investor_id TEXT PRIMARY KEY, name TEXT NOT NULL,
    qualified BOOLEAN NOT NULL, assets_proof_hash TEXT,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.evidence_files (
    file_hash TEXT PRIMARY KEY, file_name TEXT NOT NULL,
    stored_path TEXT NOT NULL, uploaded_by TEXT NOT NULL,
    uploaded_at TIMESTAMPTZ NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.routine_usage (
    month TEXT NOT NULL, category TEXT NOT NULL,
    used NUMERIC(19,4) NOT NULL DEFAULT 0,
    PRIMARY KEY (month, category)
);
CREATE TABLE IF NOT EXISTS proj.impact_lists (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    source_event_id UUID NOT NULL,
    impacts JSONB NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.sales_orders (
    order_id TEXT PRIMARY KEY, customer TEXT NOT NULL,
    amount NUMERIC(19,4) NOT NULL, collected NUMERIC(19,4) NOT NULL DEFAULT 0,
    employee_id TEXT NOT NULL, commission_rule_id TEXT, commission_rule_version INT,
    confirmed_at TIMESTAMPTZ NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.commissions (
    id TEXT PRIMARY KEY, employee_id TEXT NOT NULL, order_id TEXT NOT NULL,
    amount NUMERIC(19,4) NOT NULL, period TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    rule_ref TEXT NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.bonuses (
    id TEXT PRIMARY KEY, employee_id TEXT NOT NULL,
    amount NUMERIC(19,4) NOT NULL, reason TEXT NOT NULL, period TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    resolution_no TEXT NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.payroll_lines (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id TEXT NOT NULL, employee_id TEXT NOT NULL, period TEXT NOT NULL,
    gross NUMERIC(19,4) NOT NULL, si_personal NUMERIC(19,4) NOT NULL,
    iit NUMERIC(19,4) NOT NULL, net NUMERIC(19,4) NOT NULL,
    derivation JSONB NOT NULL,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.access_logs (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    user_id TEXT NOT NULL, target TEXT NOT NULL, action TEXT NOT NULL,
    at TIMESTAMPTZ NOT NULL
);

-- 员工档案扩展（幂等升级）
ALTER TABLE proj.employees ADD COLUMN IF NOT EXISTS profile JSONB;
-- 账号实名（幂等升级）：真实姓名与身份证号（读取接口一律脱敏）
ALTER TABLE proj.accounts ADD COLUMN IF NOT EXISTS real_name TEXT;
ALTER TABLE proj.accounts ADD COLUMN IF NOT EXISTS id_number TEXT;

-- 法务库（系统预置知识 + 事件新增条目，均可验真复算）
CREATE TABLE IF NOT EXISTS proj.legal_library (
    entry_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    keywords TEXT NOT NULL,
    scenario TEXT NOT NULL,
    options JSONB NOT NULL,
    clause_template TEXT NOT NULL DEFAULT '',
    origin TEXT NOT NULL,               -- System/Event
    source_event_id UUID
);

-- ============ 运行账号最小权限 ============
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'equi_app') THEN
        CREATE ROLE equi_app LOGIN PASSWORD 'equi_app_pwd';
    END IF;
END $$;
GRANT USAGE ON SCHEMA events, proj TO equi_app;
GRANT SELECT, INSERT ON events.event_log, events.anchors TO equi_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA events TO equi_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA proj TO equi_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA proj TO equi_app;
-- 事件表显式收回修改权
REVOKE UPDATE, DELETE, TRUNCATE ON events.event_log, events.anchors FROM equi_app;

-- ============ 对外投资台账（投影表，参与验真复算） ============
CREATE TABLE IF NOT EXISTS proj.external_investments (
    invest_id TEXT PRIMARY KEY,
    target TEXT NOT NULL,
    method TEXT NOT NULL DEFAULT 'cost',   -- cost 成本法 / equity 权益法
    share_ratio NUMERIC(9,6) NOT NULL DEFAULT 0,
    cost NUMERIC(19,4) NOT NULL,
    carrying NUMERIC(19,4) NOT NULL,
    source_event_id UUID NOT NULL
);
GRANT SELECT, INSERT, UPDATE, DELETE ON proj.external_investments TO equi_app;

-- ============ 开发者赞助墙（独立 dev schema：不入公司事件链、不参与验真、公司清空不波及） ============
CREATE SCHEMA IF NOT EXISTS dev;
CREATE TABLE IF NOT EXISTS dev.sponsorships (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name TEXT NOT NULL,
    amount NUMERIC(12,2) NOT NULL,
    message TEXT NOT NULL DEFAULT '',
    proof_hash TEXT,
    prev_hash TEXT NOT NULL,
    row_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);
GRANT USAGE ON SCHEMA dev TO equi_app;
GRANT SELECT, INSERT ON dev.sponsorships TO equi_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA dev TO equi_app;
REVOKE UPDATE, DELETE, TRUNCATE ON dev.sponsorships FROM equi_app;
CREATE OR REPLACE FUNCTION dev.no_mutate() RETURNS trigger AS $$
BEGIN RAISE EXCEPTION 'dev.sponsorships append-only：赞助记录不可修改删除'; END;
$$ LANGUAGE plpgsql;
DROP TRIGGER IF EXISTS sponsorships_no_mutate ON dev.sponsorships;
CREATE TRIGGER sponsorships_no_mutate BEFORE UPDATE OR DELETE OR TRUNCATE ON dev.sponsorships
    FOR EACH STATEMENT EXECUTE FUNCTION dev.no_mutate();

-- ========== 模块三：会计科目表（参考数据·静态种子·不参与事件重放比对） ==========
-- account_key 与 proj.ledger.account 对应；code 为小企业会计准则科目码（辅助核算码带后缀）
CREATE TABLE IF NOT EXISTS proj.chart_of_accounts (
    account_key TEXT PRIMARY KEY,
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    category TEXT NOT NULL,          -- asset/liability/equity/pl_revenue/pl_expense
    normal_balance TEXT NOT NULL,    -- debit/credit
    auxiliary TEXT NOT NULL DEFAULT '',
    system TEXT NOT NULL DEFAULT 'SmallEnterprise'
);
INSERT INTO proj.chart_of_accounts(account_key,code,name,category,normal_balance,auxiliary) VALUES
 ('Cash','1001','库存现金','asset','debit',''),
 ('Bank','1002','银行存款','asset','debit',''),
 ('AR','1122','应收账款','asset','debit','客户'),
 ('Prepayments','1123','预付账款','asset','debit','供应商'),
 ('Materials','1403','原材料','asset','debit','物料'),
 ('Inventory','1405','库存商品','asset','debit','物料'),
 ('FixedAssets','1501','固定资产','asset','debit','资产卡片'),
 ('AccumDepreciation','1502','累计折旧','asset','credit','资产卡片'),
 ('LongTermEquityInvestment','1511','长期股权投资','asset','debit','被投资单位'),
 ('IntangibleAssets','1601','无形资产','asset','debit','资产卡片'),
 ('AP','2202','应付账款','liability','credit','供应商'),
 ('PayrollPayable','2211','应付职工薪酬','liability','credit','员工'),
 ('SIPayable','2211-SI','应付社保公积金','liability','credit','员工'),
 ('TaxPayable','2221','应交税费','liability','credit','税种'),
 ('DividendPayable','2231','应付股利','liability','credit','股东'),
 ('ExitPayable','2241','其他应付款-退股款','liability','credit','股东'),
 ('DividendSuspense','2241-SUS','其他应付款-分红挂账','liability','credit','股东'),
 ('PaidInCapital','4001','实收资本','equity','credit','股东'),
 ('CapitalReserve','4002','资本公积','equity','credit','股东'),
 ('SurplusReserve','4101','盈余公积','equity','credit',''),
 ('CurrentProfit','4103','本年利润','equity','credit',''),
 ('ProfitDistribution','4104','利润分配','equity','debit','股东'),
 ('Revenue','5001','主营业务收入','pl_revenue','credit','客户/产品'),
 ('InvestmentIncome','5051','投资收益','pl_revenue','credit','被投资单位'),
 ('CostOfSales','5401','主营业务成本','pl_expense','debit','产品'),
 ('TaxSurcharge','5403','税金及附加','pl_expense','debit','税种'),
 ('SellingExpense','5601','销售费用','pl_expense','debit','部门'),
 ('Expense','5602','管理费用','pl_expense','debit','部门/费用类别'),
 ('PayrollExpense','5602-WAGE','管理费用-工资','pl_expense','debit','员工'),
 ('SICompanyExpense','5602-SI','管理费用-社保','pl_expense','debit','员工'),
 ('FinanceExpense','5603','财务费用','pl_expense','debit',''),
 ('ExitGainLoss','5711','营业外支出-退股差额','pl_expense','debit','股东')
ON CONFLICT (account_key) DO NOTHING;
GRANT SELECT ON proj.chart_of_accounts TO equi_app;

-- ============ 第四体系：企业资源管理（固资/库存/设备/生产） ============
CREATE TABLE IF NOT EXISTS proj.fixed_assets (
    asset_id TEXT PRIMARY KEY, name TEXT NOT NULL, category TEXT, cost NUMERIC(19,4) NOT NULL,
    residual_rate NUMERIC(9,4) NOT NULL DEFAULT 0, life_months INT NOT NULL,
    accumulated_dep NUMERIC(19,4) NOT NULL DEFAULT 0, acquired_date DATE NOT NULL,
    department TEXT, location TEXT, status TEXT NOT NULL DEFAULT 'Active', disposed_date DATE,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.depreciation_runs (
    period CHAR(7) PRIMARY KEY, total NUMERIC(19,4) NOT NULL, lines JSONB NOT NULL, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.materials (
    material_id TEXT PRIMARY KEY, name TEXT NOT NULL, unit TEXT NOT NULL, spec TEXT,
    category TEXT NOT NULL DEFAULT 'raw', safety_stock NUMERIC(19,4) NOT NULL DEFAULT 0, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.warehouses (
    warehouse_id TEXT PRIMARY KEY, name TEXT NOT NULL, location TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.stock_balances (
    material_id TEXT NOT NULL, warehouse TEXT NOT NULL, qty NUMERIC(19,4) NOT NULL DEFAULT 0,
    avg_cost NUMERIC(19,4) NOT NULL DEFAULT 0, PRIMARY KEY (material_id, warehouse)
);
CREATE TABLE IF NOT EXISTS proj.stock_moves (
    move_id TEXT PRIMARY KEY, material_id TEXT NOT NULL, warehouse TEXT NOT NULL, direction TEXT NOT NULL,
    qty NUMERIC(19,4) NOT NULL, unit_cost NUMERIC(19,4) NOT NULL, amount NUMERIC(19,4) NOT NULL,
    memo TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.equipment (
    equip_id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT, location TEXT, status TEXT NOT NULL DEFAULT 'Idle',
    capacity_per_day NUMERIC(19,4) NOT NULL DEFAULT 0, linked_asset_id TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.production_orders (
    order_no TEXT PRIMARY KEY, product_material_id TEXT NOT NULL, qty NUMERIC(19,4) NOT NULL,
    completed_qty NUMERIC(19,4) NOT NULL DEFAULT 0, bom JSONB NOT NULL, equipment_id TEXT,
    planned_start DATE, planned_end DATE, status TEXT NOT NULL DEFAULT 'Open', source_event_id UUID NOT NULL
);
GRANT SELECT, INSERT, UPDATE, DELETE ON proj.fixed_assets, proj.depreciation_runs, proj.materials, proj.warehouses, proj.stock_balances, proj.stock_moves, proj.equipment, proj.production_orders TO equi_app;

INSERT INTO proj.chart_of_accounts(account_key, code, name, category, normal_balance, auxiliary) VALUES
 ('DepreciationExpense','5602-DEP','管理费用-折旧费','pl_expense','debit','department'),
 ('ProductionCost','5001','生产成本','cost','debit','production_order'),
 ('StockAdjust','5602-STK','管理费用-存货盘盈亏','pl_expense','debit','material'),
 ('AssetDisposalGainLoss','5711-ASSET','资产处置损益','pl_expense','debit','asset')
ON CONFLICT (account_key) DO NOTHING;

-- ============ 第二体系贯通：采购八步链 / 借款 / 纳税申报 ============
CREATE TABLE IF NOT EXISTS proj.purchase_requests (
    req_no TEXT PRIMARY KEY, department TEXT NOT NULL, items JSONB NOT NULL, reason TEXT,
    status TEXT NOT NULL DEFAULT 'Requested', source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.purchase_orders (
    po_no TEXT PRIMARY KEY, req_no TEXT NOT NULL, supplier TEXT NOT NULL, items JSONB NOT NULL,
    expected_date DATE, status TEXT NOT NULL DEFAULT 'Open', paid_amount NUMERIC(19,4) NOT NULL DEFAULT 0,
    source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.purchase_deliveries (
    delivery_no TEXT PRIMARY KEY, po_no TEXT NOT NULL, items JSONB NOT NULL, delivered_date DATE, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.purchase_inspections (
    inspection_no TEXT PRIMARY KEY, delivery_no TEXT NOT NULL, po_no TEXT NOT NULL, accepted JSONB NOT NULL,
    rejected JSONB NOT NULL, inspector TEXT NOT NULL, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.purchase_payments (
    pay_id TEXT PRIMARY KEY, po_no TEXT NOT NULL, amount NUMERIC(19,4) NOT NULL, receipt_hash TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.loans (
    loan_id TEXT PRIMARY KEY, lender TEXT NOT NULL, principal NUMERIC(19,4) NOT NULL, annual_rate NUMERIC(9,6) NOT NULL,
    term_months INT NOT NULL, start_date DATE NOT NULL, outstanding_principal NUMERIC(19,4) NOT NULL,
    accrued_interest NUMERIC(19,4) NOT NULL DEFAULT 0, paid_interest NUMERIC(19,4) NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'Active', source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.loan_interest (
    loan_id TEXT NOT NULL, period CHAR(7) NOT NULL, amount NUMERIC(19,4) NOT NULL, source_event_id UUID NOT NULL,
    PRIMARY KEY (loan_id, period)
);
CREATE TABLE IF NOT EXISTS proj.tax_returns (
    return_id TEXT PRIMARY KEY, period CHAR(7) NOT NULL, kind TEXT NOT NULL, amount NUMERIC(19,4) NOT NULL,
    detail JSONB NOT NULL, status TEXT NOT NULL DEFAULT 'Filed', source_event_id UUID NOT NULL,
    UNIQUE (period, kind)
);
GRANT SELECT, INSERT, UPDATE, DELETE ON proj.purchase_requests, proj.purchase_orders, proj.purchase_deliveries, proj.purchase_inspections, proj.purchase_payments, proj.loans, proj.loan_interest, proj.tax_returns TO equi_app;

-- ============ 群通知治理：群组/绑定/推送留痕（敏感分级在代码强制） ============
CREATE TABLE IF NOT EXISTS proj.notify_groups (
    group_id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, department TEXT NOT NULL DEFAULT '',
    webhook TEXT NOT NULL, enabled BOOLEAN NOT NULL DEFAULT TRUE, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.notify_bindings (
    group_id TEXT NOT NULL, event_type TEXT NOT NULL, PRIMARY KEY (group_id, event_type)
);
CREATE TABLE IF NOT EXISTS proj.notify_logs (
    log_id TEXT PRIMARY KEY, notification_id BIGINT NOT NULL, group_id TEXT NOT NULL, event_type TEXT NOT NULL,
    level TEXT NOT NULL, digest BOOLEAN NOT NULL, content_hash TEXT NOT NULL, status TEXT NOT NULL, sent_at TIMESTAMPTZ NOT NULL
);
GRANT SELECT, INSERT, UPDATE, DELETE ON proj.notify_groups, proj.notify_bindings TO equi_app;
GRANT SELECT, INSERT ON proj.notify_logs TO equi_app;

-- ============ 举报通道（任何人可举报·匿名保护·监事调查·决议办结） ============
CREATE TABLE IF NOT EXISTS proj.reports (
    report_id TEXT PRIMARY KEY, category TEXT NOT NULL, target TEXT NOT NULL, description TEXT NOT NULL,
    anonymous BOOLEAN NOT NULL DEFAULT FALSE, reporter TEXT NOT NULL, reporter_actor TEXT NOT NULL,
    findings TEXT, outcome TEXT, action TEXT, status TEXT NOT NULL DEFAULT 'Open', source_event_id UUID NOT NULL
);
GRANT SELECT, INSERT, UPDATE ON proj.reports TO equi_app;

INSERT INTO proj.chart_of_accounts(account_key, code, name, category, normal_balance, auxiliary) VALUES
 ('LoanPayable','2501','长期借款','liability','credit','债权人'),
 ('InterestPayable','2232','应付利息','liability','credit','债权人'),
 ('InterestExpense','5603-INT','财务费用-利息支出','pl_expense','debit','债权人'),
 ('IncomeTaxExpense','5801','所得税费用','pl_expense','debit','')
ON CONFLICT (account_key) DO NOTHING;

-- ============ 群设置操作员直设 / 监管邮箱 / 举报处理 ============
ALTER TABLE proj.company ADD COLUMN IF NOT EXISTS supervision_email TEXT NOT NULL DEFAULT '';
ALTER TABLE proj.reports ADD COLUMN IF NOT EXISTS source TEXT NOT NULL DEFAULT 'Mailbox';
ALTER TABLE proj.reports ADD COLUMN IF NOT EXISTS severity TEXT NOT NULL DEFAULT 'General';
ALTER TABLE proj.reports ADD COLUMN IF NOT EXISTS evidence_refs JSONB NOT NULL DEFAULT '[]';
ALTER TABLE proj.reports ADD COLUMN IF NOT EXISTS investigated_by TEXT;

-- ============ 知识产权档案 + 无形资产会计双账簿 ============
CREATE TABLE IF NOT EXISTS proj.ip_rights (
    ip_id TEXT PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL, reg_no TEXT,
    owner_party TEXT NOT NULL, controller TEXT, acquire_mode TEXT, acquire_date DATE,
    cert_refs JSONB NOT NULL DEFAULT '[]', cost NUMERIC(19,4) NOT NULL DEFAULT 0, fees NUMERIC(19,4) NOT NULL DEFAULT 0,
    appraised_value NUMERIC(19,4), appraisal_report TEXT,
    valid_until DATE, renew_date DATE, status TEXT NOT NULL DEFAULT 'Active',
    linked_asset_id TEXT, history JSONB NOT NULL DEFAULT '[]', source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.ip_events_log (
    log_id TEXT PRIMARY KEY, ip_id TEXT NOT NULL, kind TEXT NOT NULL, detail JSONB NOT NULL, memo TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.intangible_assets (
    asset_id TEXT PRIMARY KEY, ip_id TEXT, name TEXT NOT NULL, cost NUMERIC(19,4) NOT NULL,
    life_months INT NOT NULL, accumulated_amort NUMERIC(19,4) NOT NULL DEFAULT 0, impairment NUMERIC(19,4) NOT NULL DEFAULT 0,
    recognized_date DATE NOT NULL, criteria JSONB NOT NULL, status TEXT NOT NULL DEFAULT 'Active', source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.amort_runs (
    period CHAR(7) PRIMARY KEY, total NUMERIC(19,4) NOT NULL, lines JSONB NOT NULL, source_event_id UUID NOT NULL
);
GRANT SELECT, INSERT, UPDATE ON proj.ip_rights, proj.ip_events_log, proj.intangible_assets, proj.amort_runs TO equi_app;

-- ============ 员工持股与股权激励中心（五形式分离） ============
CREATE TABLE IF NOT EXISTS proj.esop_plans (
    plan_id TEXT PRIMARY KEY, employee_id TEXT NOT NULL, form TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Applied',
    quantity NUMERIC(19,4) NOT NULL DEFAULT 0, ratio NUMERIC(9,6) NOT NULL DEFAULT 0, price NUMERIC(19,4) NOT NULL DEFAULT 0,
    platform_party TEXT, vesting JSONB NOT NULL DEFAULT '{}', review TEXT, confirm_ref TEXT,
    granted_qty NUMERIC(19,4) NOT NULL DEFAULT 0, vested_qty NUMERIC(19,4) NOT NULL DEFAULT 0, exercised_qty NUMERIC(19,4) NOT NULL DEFAULT 0,
    sbc_expense NUMERIC(19,4) NOT NULL DEFAULT 0, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.esop_virtual_dividends (
    id TEXT PRIMARY KEY, plan_id TEXT NOT NULL, period CHAR(7) NOT NULL, amount NUMERIC(19,4) NOT NULL,
    status TEXT NOT NULL DEFAULT 'Declared', source_event_id UUID NOT NULL
);
GRANT SELECT, INSERT, UPDATE ON proj.esop_plans, proj.esop_virtual_dividends TO equi_app;

INSERT INTO proj.chart_of_accounts(account_key, code, name, category, normal_balance, auxiliary) VALUES
 ('AccumAmortization','1602','累计摊销','asset','credit','资产卡片'),
 ('IntangibleImpairment','1603','无形资产减值准备','asset','credit','资产卡片'),
 ('AmortExpense','5602-AMORT','管理费用-无形资产摊销','pl_expense','debit','资产卡片'),
 ('ImpairmentLoss','5711-IMP','资产减值损失','pl_expense','debit','资产卡片'),
 ('ShareBasedExpense','5602-SBC','管理费用-股份支付','pl_expense','debit','员工'),
 ('OtherCapitalReserve','4002-OTHER','资本公积-其他资本公积','equity','credit','员工')
ON CONFLICT (account_key) DO NOTHING;

-- ============ 扫码自动采集网关：设备/业务包/复核/计件/质检/物流 ============
CREATE TABLE IF NOT EXISTS proj.devices (
    device_id TEXT PRIMARY KEY, name TEXT NOT NULL, adapter TEXT NOT NULL, department TEXT NOT NULL DEFAULT '',
    secret TEXT NOT NULL, enabled BOOLEAN NOT NULL DEFAULT TRUE, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.scan_packets (
    packet_id TEXT PRIMARY KEY, kind TEXT NOT NULL, ref_id TEXT NOT NULL, payload JSONB NOT NULL,
    salt TEXT NOT NULL, check_code TEXT NOT NULL, barcode TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Ready', source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.scan_reviews (
    review_id TEXT PRIMARY KEY, packet_id TEXT NOT NULL, kind TEXT NOT NULL, payload JSONB NOT NULL,
    reason TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'Open', result TEXT, reviewer TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.piece_records (
    id TEXT PRIMARY KEY, employee_id TEXT NOT NULL, process TEXT NOT NULL, qty NUMERIC(19,4) NOT NULL,
    period CHAR(7) NOT NULL, order_no TEXT, packet_id TEXT NOT NULL, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.quality_records (
    qc_id TEXT PRIMARY KEY, batch TEXT NOT NULL, product TEXT NOT NULL, inspector TEXT NOT NULL, standard TEXT NOT NULL,
    result TEXT NOT NULL, qty NUMERIC(19,4) NOT NULL, order_no TEXT, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.logistics_scans (
    id TEXT PRIMARY KEY, tracking TEXT NOT NULL, stage TEXT NOT NULL, batch TEXT, status TEXT NOT NULL, source_event_id UUID NOT NULL
);
CREATE TABLE IF NOT EXISTS proj.scan_logs (
    log_id TEXT PRIMARY KEY, device_id TEXT, barcode TEXT NOT NULL, result TEXT NOT NULL, detail TEXT,
    event_ids JSONB NOT NULL DEFAULT '[]', at TIMESTAMPTZ NOT NULL
);
GRANT SELECT, INSERT, UPDATE ON proj.devices, proj.scan_packets, proj.scan_reviews, proj.piece_records, proj.quality_records, proj.logistics_scans TO equi_app;
GRANT SELECT, INSERT ON proj.scan_logs TO equi_app;
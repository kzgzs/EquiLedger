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
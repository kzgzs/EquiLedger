/* 玄铁栈道 EquiLedger · 企业智能计算治理系统
 * 版权所有 (c) 2026 XTZDDJ · 微信 XTZDDJ · QQ 248197309/68822685 · admin@djkz.top
 * 商业授权条件：保留本署名。移除/篡改署名即授权终止并构成侵权。 */
let TOKEN = localStorage.getItem('equi_token') || '';
let ME = null;
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

// ---------- 明暗主题 ----------
function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  localStorage.setItem('equi_theme', t);
  const b = $('themeBtn');
  if (b) { b.textContent = t === 'light' ? '🌙' : '☀️'; b.title = t === 'light' ? '切换到暗黑模式' : '切换到明亮模式'; }
}
function toggleTheme() { applyTheme(document.documentElement.dataset.theme === 'light' ? 'dark' : 'light'); }
applyTheme(localStorage.getItem('equi_theme') || 'dark');

async function api(path, method = 'GET', body) {
  const opt = { method, headers: { 'Authorization': 'Bearer ' + TOKEN } };
  if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
  const r = await fetch(path, opt);
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status));
  return j;
}

function table(cols, rows, fmt = {}) {
  if (!rows || !rows.length) return '<p class="hint">暂无数据</p>';
  let h = '<table><tr>' + cols.map(c => `<th>${esc(c[1])}</th>`).join('') + '</tr>';
  for (const r of rows) {
    h += '<tr>' + cols.map(c => {
      let v = r[c[0]];
      if (fmt[c[0]]) v = fmt[c[0]](v, r);
      else if (v !== null && typeof v === 'object') v = '<pre>' + esc(JSON.stringify(v, null, 1)) + '</pre>';
      const num = typeof r[c[0]] === 'number' ? ' class="num"' : '';
      return `<td${num}>${v ?? ''}</td>`;
    }).join('') + '</tr>';
  }
  return h + '</table>';
}
const money = v => v == null ? '' : Number(v).toLocaleString('zh-CN', { minimumFractionDigits: 2 });
const tag = (v, map) => `<span class="tag ${map[v] || 'dim'}">${esc(v)}</span>`;

// ---------- 中文对照（不影响任何功能，仅显示层） ----------
const EVENT_CN = {
  CompanyFounded: '公司设立', ResolutionRecorded: '决议入链（线下纪要）', ResolutionProposed: '发起议案', ResolutionVoted: '股东表决',
  CapitalPaidIn: '实缴到账', RevenueRecorded: '收入确认', ExpenseRecorded: '费用报销', RuleActivated: '规则生效',
  ContractSigned: '合同签署', ContractAccessed: '私密合同查阅', EmployeeHired: '员工入职', PayrollRun: '工资计算发放',
  BonusGranted: '一次性奖金', EmployeeLeft: '员工离职结算', ShareSubscription: '增资认缴', ShareTransfer: '股权转让',
  ShareExit: '退股', ShareholderDeceased: '股东离世登记', DividendApproved: '分红方案批准', DividendPaid: '分红支付',
  SalesOrderConfirmed: '销售订单确认', CollectionReceived: '回款登记', BankFlowImported: '银行流水导入',
  PeriodClosed: '期间结账', CorrectionRecorded: '红字更正', ModuleSwitchChanged: '模块开关变更',
  InvestorQualified: '合格投资者认定', InvestmentReceived: '投资款到账', RelationDeclared: '关联关系申报',
  InvestmentMade: '对外投资', InvestmentReturnReceived: '投资回报到账', PayrollPaid: '工资发放确认',
  FixedAssetAcquired: '固定资产购置', FixedAssetDepreciationRun: '月度折旧计提', FixedAssetDisposed: '固定资产处置',
  MaterialDefined: '物料建档', WarehouseDefined: '仓库建档', StockReceived: '入库', StockIssued: '出库', StockCounted: '盘点',
  EquipmentRegistered: '设备登记', ProductionOrderCreated: '生产订单下达', ProductionCompleted: '完工入库',
  CompanyInfoChanged: '公司信息变更', OperatorHandover: '操作员移交', TodoCompleted: '待办办结', AccountAdded: '新增成员账号',
  EvidenceUploaded: '依据上传', SecurityAlert: '安全告警', AccountStatusChanged: '账号启停', LegalEntryAdded: '法务库新增条目'
};
const RULE_CN = {
  'Charter.Voting': '章程·表决规则', 'Charter.ExitValuation': '章程·退股估值', 'Charter.Dividend': '章程·分红政策',
  'Charter.Inheritance': '章程·股权继承', RoutineAuth: '例行授权额度', 'Payroll.Standard': '工资标准（锁定）',
  'SocialInsurance.City': '城市社保参数', 'Commission.Rate': '提成比例（签单锁定）', 'Partnership.Allocation': '合伙分配比例'
};
const RESKIND_CN = {
  Founding: '设立', CapitalIncrease: '增资', ShareSubscription: '认缴', ShareTransfer: '股权转让', ShareExit: '退股', ExternalInvestment: '对外投资',
  Dividend: '分红', Bonus: '奖金', PayrollChange: '薪酬变更', CommissionChange: '提成变更', RuleChange: '规则变更',
  CharterAmend: '章程修正', ContractSign: '合同签署', Hire: '聘用', Expense: '费用', SalesOrder: '销售订单',
  ModuleSwitch: '模块开关', ModuleAuth: '模块授权', Financing: '融资', ComplianceCheck: '合规检查', Listing: '上市筹备',
  ExternalInvest: '对外投资', DeptSetup: '部门设立', OperatorHandover: '操作员移交', Correction: '更正',
  PeriodClose: '期间结账', EmployeeLeave: '员工离职', CompanyChange: '公司信息变更', ShareholderDeath: '股东离世',
  Guarantee: '担保', General: '一般议案', AccountManage: '账号管理'
};
const TODO_CN = {
  TaxFiling: '税务申报', BusinessRegistry: '工商变更', SocialInsurance: '参保登记', AnnualAudit: '年度审计（一人公司）',
  OwnerSocialInsurance: '业主社保口径', BranchAccounting: '分支核算口径', Inheritance: '继承处理', ExitPayment: '退股款支付',
  ForcedDividend: '强制分红提示', TaxThreshold: '小微临界预警', PartnerCashAlert: '合伙纳税现金流', SecurityAlert: '安全告警',
  BankException: '流水异常', LaborContract: '补签劳动合同', RelatedReview: '关联交易审查', NonCompetePay: '竞业补偿支付',
  DilutionConfirm: '稀释确认', SeveranceReview: '离职结算复核'
};
const cn = (map, v) => map[v] ? `${map[v]}` : esc(v);
const cnSub = (map, v) => map[v] ? `${map[v]} <code class="hint">${esc(v)}</code>` : esc(v);

// ---------- 登录 / 设立 ----------
async function login() {
  try {
    const j = await api('/api/login', 'POST', { username: $('loginUser').value, password: $('loginPass').value });
    TOKEN = j.token; localStorage.setItem('equi_token', TOKEN);
    await boot();
  } catch (e) { $('authMsg').className = 'msg err'; $('authMsg').textContent = e.message; }
}
// ---------- 设立向导（可视化表格） ----------
const ROLES = [['Operator', '授权操作员'], ['BackupOperator', '后备操作员'], ['Shareholder', '股东'], ['Supervisor', '监事'], ['Admin', 'IT管理员'], ['Employee', '员工']];
let MEMBERS = [];
let WLS = [];
function showSetup() { $('authView').style.display = 'none'; $('setupView').style.display = 'block'; if (!MEMBERS.length) loadPreset(true); }
function hideSetup() { $('setupView').style.display = 'none'; $('authView').style.display = 'block'; }
function addMember(m) {
  MEMBERS.push(m || { role: 'Shareholder', username: '', password: '', realName: '', idNumber: '', subscribed: '' });
  renderMembers();
}
function delMember(i) { MEMBERS.splice(i, 1); renderMembers(); }
function renderMembers() {
  $('memberTable').querySelector('tbody').innerHTML = MEMBERS.map((m, i) => `<tr>
    <td><select onchange="MEMBERS[${i}].role=this.value">${ROLES.map(r => `<option value="${r[0]}" ${m.role === r[0] ? 'selected' : ''}>${r[1]}</option>`).join('')}</select></td>
    <td><input value="${esc(m.username)}" oninput="MEMBERS[${i}].username=this.value" placeholder="登录名"></td>
    <td><input value="${esc(m.password)}" oninput="MEMBERS[${i}].password=this.value" placeholder="密码"></td>
    <td><input value="${esc(m.realName)}" oninput="MEMBERS[${i}].realName=this.value" placeholder="身份证上的姓名"></td>
    <td><input value="${esc(m.idNumber)}" oninput="MEMBERS[${i}].idNumber=this.value" placeholder="18位身份证号" maxlength="18"></td>
    <td><input type="number" value="${esc(m.subscribed)}" oninput="MEMBERS[${i}].subscribed=this.value" placeholder="0"></td>
    <td><button class="ghost" onclick="delMember(${i})">移除</button></td></tr>`).join('');
}
function addWl(w) { WLS.push(w || { accountNo: '', holder: '' }); renderWl(); }
function delWl(i) { WLS.splice(i, 1); renderWl(); }
function renderWl() {
  $('wlTable').querySelector('tbody').innerHTML = WLS.map((w, i) => `<tr>
    <td><input value="${esc(w.accountNo)}" oninput="WLS[${i}].accountNo=this.value" placeholder="银行账号"></td>
    <td><input value="${esc(w.holder)}" oninput="WLS[${i}].holder=this.value" placeholder="户名"></td>
    <td><button class="ghost" onclick="delWl(${i})">移除</button></td></tr>`).join('');
}
const PRESETS = {
  'llc-multi': { name: '玄铁栈道科技有限责任公司', type: '有限责任公司', cap: 1000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345'], ['Shareholder', 'alice', 'A#12345', 600000], ['Shareholder', 'bob', 'B#12345', 400000], ['Supervisor', 'sup', 'S#12345'], ['Admin', 'adm', 'Ad#12345']], wl: [['6222000011112222', '供应商A']] },
  'llc-solo': { name: '玄铁栈道科技有限责任公司', type: '有限责任公司', cap: 1000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345', 1000000], ['Supervisor', 'sup', 'S#12345']], wl: [] },
  'joint': { name: '玄铁栈道科技股份有限公司', type: '股份有限公司', cap: 5000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345'], ['Shareholder', 'alice', 'A#12345', 3000000], ['Shareholder', 'bob', 'B#12345', 2000000], ['Supervisor', 'sup', 'S#12345'], ['Admin', 'adm', 'Ad#12345']], wl: [['6222000011112222', '供应商A']] },
  'sole': { name: '玄铁栈道科技中心', type: '个人独资企业', cap: 1000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345', 1000000], ['Supervisor', 'sup', 'S#12345']], wl: [] },
  'partner': { name: '玄铁栈道科技合伙企业（普通合伙）', type: '合伙企业', cap: 1000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345'], ['Shareholder', 'alice', 'A#12345', 600000], ['Shareholder', 'bob', 'B#12345', 400000], ['Supervisor', 'sup', 'S#12345']], wl: [['6222000011112222', '供应商A']] },
  'individual': { name: '玄铁栈道经营部', type: '个体工商户', cap: 100000, tags: '零售',
    members: [['Operator', 'op', 'Op#12345', 100000], ['Supervisor', 'sup', 'S#12345']], wl: [] },
  'foreign': { name: '玄铁栈道科技（外商投资）有限责任公司', type: '外商投资企业', cap: 1000000, tags: '软件',
    members: [['Operator', 'op', 'Op#12345'], ['Shareholder', 'alice', 'A#12345', 600000], ['Shareholder', 'bob', 'B#12345', 400000], ['Supervisor', 'sup', 'S#12345'], ['Admin', 'adm', 'Ad#12345']], wl: [['6222000011112222', '供应商A']] },
  'branch': { name: '玄铁栈道科技有限责任公司南阳分公司', type: '分支机构', cap: 0, tags: '软件',
    members: [['Operator', 'op', 'Op#12345'], ['Supervisor', 'sup', 'S#12345']], wl: [] }
};
function loadPreset(skipConfirm) {
  if (!skipConfirm && !confirm('建议检查您公司营业执照上的全名，和您所勾选的企业类型。\n\n取消 = 不载入模板\n确定 = 载入模板（载入后请逐项修改为真实信息）')) return;
  const p = PRESETS[$('setupPreset').value] || PRESETS['llc-multi'];
  $('coName').value = p.name; $('coType').value = p.type; $('coCap').value = p.cap; $('coTags').value = p.tags;
  MEMBERS = p.members.map(m => ({ role: m[0], username: m[1], password: m[2], realName: '', idNumber: '', subscribed: m[3] ?? '' }));
  WLS = p.wl.map(w => ({ accountNo: w[0], holder: w[1] }));
  renderMembers(); renderWl();
}
async function doSetup() {
  // 表单校验
  const name = $('coName').value.trim();
  if (!name) return setupErr('请填写企业全名（对照营业执照）');
  if (!MEMBERS.length) return setupErr('请至少添加一名成员');
  if (!MEMBERS.some(m => m.role === 'Operator')) return setupErr('必须有一名授权操作员（Operator）');
  for (const m of MEMBERS) {
    if (!m.username || !m.password) return setupErr(`成员 ${m.realName || m.username || '(未命名)'} 缺登录名或密码`);
    if (!m.realName.trim()) return setupErr(`成员 ${m.username} 缺真实姓名（实名制）`);
    if (!/^\d{17}[\dXx]$/.test(m.idNumber.trim())) return setupErr(`成员 ${m.username} 身份证号须为18位`);
  }
  if (!confirm('请确认你填写的各项是否正确（企业全名/类型/成员实名/认缴额/治理参数）。\n\n取消 = 返回重新修改\n确认 = 正式载入公司到系统（设立仅一次，入链后不可撤销）')) return;
  const sub = m => Number(m.subscribed) || 0;
  const accounts = MEMBERS.map(m => {
    const a = { username: m.username.trim(), displayName: m.realName.trim(), role: m.role, password: m.password, realName: m.realName.trim(), idNumber: m.idNumber.trim() };
    if (sub(m) > 0) { a.partyId = m.username.trim(); a.subscribed = sub(m); }
    return a;
  });
  const rules = [
    { kind: 'Charter.Voting', params: { basis: 'Subscribed', threshold: Number($('voteThreshold').value) } },
    { kind: 'Charter.ExitValuation', params: { method: 'NetAsset', installments: 3 } },
    { kind: 'Charter.Dividend', params: { payoutRatio: Number($('payoutRatio').value) || 1, forceMinRatio: Number($('forceMinRatio').value) || 0 } },
    { kind: 'RoutineAuth', params: { singleLimit: Number($('singleLimit').value) || 0, monthlyCap: Number($('monthlyCap').value) || 0, categories: ['Expense'] } }
  ];
  if ($('coType').value === '合伙企业') {
    const holders = accounts.filter(a => a.subscribed > 0);
    const total = holders.reduce((s, a) => s + a.subscribed, 0);
    if (total > 0) rules.push({ kind: 'Partnership.Allocation', params: { partners: holders.map(a => ({ partyId: a.partyId, ratio: Math.round(a.subscribed / total * 1000000) / 1000000, isNatural: true })) } });
  }
  if ($('extraRules').value.trim()) {
    try { const extra = JSON.parse($('extraRules').value); if (Array.isArray(extra)) rules.push(...extra); else return setupErr('高级条款须为 JSON 数组'); }
    catch { return setupErr('高级条款 JSON 解析失败'); }
  }
  const payload = {
    company: { name, type: $('coType').value, registeredCapital: Number($('coCap').value) || 0, industryTags: $('coTags').value.split(/[,，]/).map(s => s.trim()).filter(Boolean) },
    accounts, charterRules: rules,
    whitelist: WLS.filter(w => w.accountNo && w.holder)
  };
  try {
    const j = await api('/api/setup', 'POST', payload);
    $('setupMsg').className = 'msg ok'; $('setupMsg').textContent = '设立完成，事件哈希 ' + j.hash + '。请返回登录。';
    $('setupEntry').style.display = 'none';
    setTimeout(hideSetup, 1500);
  } catch (e) { setupErr(e.message); }
}
function setupErr(msg) { $('setupMsg').className = 'msg err'; $('setupMsg').textContent = msg; }
// 已设立则不再显示设立向导
fetch('/api/setup-status').then(r => r.json()).then(s => { if (s.founded) $('setupEntry').style.display = 'none'; }).catch(() => { });

// ---------- 主框架 ----------
const TABS = [
  ['quick', '快捷'], ['dash', '总览'], ['cap', '股权结构'], ['res', '决议'], ['rules', '规则库'], ['div', '分红'],
  ['pay', '薪酬'], ['fin', '财务'], ['erp', '资源'], ['contract', '合同'], ['legal', '法务库'], ['module', '模块开关'], ['todo', '待办'], ['events', '事件流'],
  ['submit', '操作员录入'], ['audit', '审计验真']
];
let curTab = 'dash';
async function boot() {
  try { ME = await api('/api/me'); } catch { TOKEN = ''; localStorage.removeItem('equi_token'); return; }
  $('authView').style.display = 'none'; $('setupView').style.display = 'none'; $('mainView').style.display = 'block';
  $('userBox').innerHTML = `<b>${esc(ME.userId)}</b> · ${esc(ME.role)} ${ME.frozen ? '<span class="tag err">已冻结</span>' : ''} <button class="ghost" onclick="logout()">退出</button>`;
  $('tabs').innerHTML = TABS.map(t => `<button id="tab-${t[0]}" onclick="openTab('${t[0]}')">${t[1]}</button>`).join('');
  openTab(matchMedia('(max-width:768px)').matches ? 'quick' : 'dash');
}
function logout() { TOKEN = ''; localStorage.removeItem('equi_token'); location.reload(); }
async function openTab(t) {
  curTab = t;
  document.querySelectorAll('nav button').forEach(b => b.classList.remove('on'));
  $('tab-' + t)?.classList.add('on');
  $('tabBody').innerHTML = '<p class="hint">加载中…</p>';
  try { await RENDER[t](); } catch (e) { $('tabBody').innerHTML = `<div class="msg err">${esc(e.message)}</div>`; }
}

const RENDER = {
  async quick() {
    // 移动快捷页：紧急场景"看+表决+办结+工资条"，复杂录入回电脑端
    const d = await api('/api/dashboard');
    const rs = await api('/api/resolutions');
    const ts = await api('/api/todos');
    let h = `<h2>移动快捷（紧急查看与操作）</h2>
    <div class="cards">
      <div class="card"><div class="v">${money(d.bankBalance)}</div><div class="k">银行账面</div></div>
      <div class="card"><div class="v">${d.openTodos}</div><div class="k">未结待办</div></div>
      <div class="card"><div class="v">${d.eventCount}</div><div class="k">链上事件</div></div>
    </div><h3>决议表决</h3>`;
    const canVote = ME.role === 'Shareholder' || (ME.role === 'Operator' && ME.partyId);
    const pending = rs.filter(r => !r.passed);
    h += canVote
      ? (pending.length ? pending.map(r => `<div class="panel m-card"><span><b>${esc(r.resolution_no)}</b> ${esc(r.title)} <span class="tag dim">${cn(RESKIND_CN, r.kind)}</span></span><span class="row"><button onclick="quickVote('${esc(r.resolution_no)}',true)">赞成</button><button class="warn" onclick="quickVote('${esc(r.resolution_no)}',false)">反对</button></span></div>`).join('') : '<p class="hint">暂无待表决议案。</p>')
      : '<p class="hint">当前身份无表决权（股东或兼任股东才可表决）。</p>';
    h += '<h3>待办办结</h3>' + (ts.filter(t => t.status === 'Open').map(t => `<div class="panel m-card"><span><b>${cn(TODO_CN, t.kind)}</b> ${esc(t.title)}</span>${ME.role === 'Operator' ? `<button class="ghost" onclick="showDoneTodo('${esc(t.todo_id)}')">上传依据并办结</button>` : ''}</div>`).join('') || '<p class="hint">无未结待办。</p>');
    h += '<h3>工资条（最近）</h3><div id="qPayslip"></div><p class="hint">完整账表/录入请在电脑端打开；本页为手机平板紧急通道。</p>';
    $('tabBody').innerHTML = h;
    const ns = await api('/api/notifications');
    const slips = ns.filter(n => n.summary?.kind === 'Payslip');
    $('qPayslip').innerHTML = slips.length ? slips.slice(0, 6).map(n => `<div class="panel m-card"><span><b>${esc(n.summary.period || '')}</b> · ${esc(n.summary.employeeId || n.summary.name || '')}</span><button class="ghost" onclick='showJson(${esc(JSON.stringify(n.summary))})'>查看工资条</button></div>`).join('') : '<p class="hint">暂无工资条通知（发薪后自动生成）。</p>';
  },
  async dash() {
    const d = await api('/api/dashboard');
    const c = d.company || {};
    if (c.name) $('brandName').textContent = c.name;
    const st = await fetch('/api/setup-status').then(r => r.json()).catch(() => ({}));
    const resetBtn = (ME.role === 'Operator' && st.graceWindow)
      ? `<div class="panel" style="border-color:var(--err)"><b style="color:var(--err)">设立冷静期（一个月内）</b><p class="hint">设立后 30 天内可清空重来；窗口关闭后一切变更只能走决议事件。</p><button class="warn" onclick="doReset()">清空数据库并重新设立（不可恢复）</button></div>` : '';
    $('tabBody').innerHTML = `<h2>${esc(c.name || '')} <span class="tag ok">${esc(c.company_type || '')}</span></h2>
    <div class="cards">
      <div class="card"><div class="v">${money(c.registered_capital)}</div><div class="k">注册资本</div></div>
      <div class="card"><div class="v">${money(d.bankBalance)}</div><div class="k">银行账面余额</div></div>
      <div class="card"><div class="v">${d.eventCount}</div><div class="k">链上事件数</div></div>
      <div class="card"><div class="v">${d.capVersion}</div><div class="k">股权结构版本</div></div>
      <div class="card"><div class="v">${d.openTodos}</div><div class="k">未结待办</div></div>
    </div>
    <h3>利润推导单（最新）</h3><div id="profitBox"></div>
    <h3>工作群推送（最近）</h3><div id="notifBox"></div>` + resetBtn;
    const calcs = await api('/api/calcs?kind=ProfitDerivation');
    $('profitBox').innerHTML = calcs.length ? derivHtml(calcs[0]) : '<p class="hint">暂无经营数据</p>';
    const ns = await api('/api/notifications');
    $('notifBox').innerHTML = table([['id', '#'], ['channel', '通道'], ['push_status', '推送'], ['summary', '摘要']], ns.slice(0, 8),
      { push_status: v => tag(v, { Sent: 'ok', Logged: 'dim', Failed: 'err', Pending: 'warn' }), summary: v => `<span class="hint">${esc(EVENT_CN[v.eventType] || v.eventType || v.kind || '')} · ${esc(v.actor ?? v.employeeId ?? '')} · ${v.resolutionNo ? '决议 ' + esc(v.resolutionNo) : (v.kind === 'Payslip' ? '工资条 ' + esc(v.period) : '例行/执行')}</span>` });
  },
  async cap() {
    const d = await api('/api/cap');
    $('tabBody').innerHTML = '<h2>股权结构表（Cap Table · 最新版本）</h2>' +
      table([['party_id', '股东'], ['name', '姓名'], ['subscribed', '认缴'], ['paid_in', '实缴'], ['ratio', '持股比例'], ['voting_ratio', '表决权'], ['status', '状态']],
        d.positions, { subscribed: money, paid_in: money, ratio: v => (v * 100).toFixed(4) + '%', voting_ratio: v => (v * 100).toFixed(4) + '%', status: v => tag(v, { Normal: 'ok', Deceased: 'warn', Exited: 'dim', Frozen: 'err' }) }) +
      '<h3>历史版本（永远按当时版本可复算）</h3>' + table([['version', '版本'], ['effective_at', '生效时间'], ['source_event_id', '源事件']], d.versions);
  },
  async res() {
    const rs = await api('/api/resolutions');
    $('tabBody').innerHTML = '<h2>决议与表决（决策权在会议）</h2>' +
      ((ME.role === 'Shareholder' || (ME.role === 'Operator' && ME.partyId)) ? `<div class="panel"><h3>股东表决</h3><div class="row"><input id="voteNo" placeholder="议案编号"><select id="voteApprove"><option value="true">赞成</option><option value="false">反对</option></select><button onclick="vote()">实名表决（利益相关自动回避）</button></div><div id="voteMsg" class="msg"></div></div>` : '') +
      table([['resolution_no', '编号'], ['title', '事项'], ['kind', '类型'], ['passed', '通过'], ['consumed', '已执行'], ['params', '固化参数'], ['votes', '表决记录']], rs,
        { kind: v => cnSub(RESKIND_CN, v), passed: v => tag(v ? '通过' : '未通过', { 通过: 'ok', 未通过: 'warn' }), consumed: v => v ? '是' : '否' });
  },
  async rules() {
    const rs = await api('/api/rules');
    $('tabBody').innerHTML = '<h2>规则库（版本化 · 前置固化 · 旧版本永久封存）</h2>' +
      table([['rule_id', '规则'], ['version', '版本'], ['kind', '类型'], ['subject', '对象'], ['params', '参数'], ['effective_from', '生效'], ['status', '状态'], ['resolution_no', '决议依据']], rs,
        { kind: v => cnSub(RULE_CN, v), status: v => tag(v, { Active: 'ok', Superseded: 'dim' }) });
  },
  async div() {
    const ds = await api('/api/dividends');
    $('tabBody').innerHTML = '<h2>分红方案（引擎自动生成候选稿，人工只能决议）</h2>' +
      table([['plan_id', '方案'], ['fiscal_year', '年度'], ['distributable', '可分配利润'], ['reserve_extracted', '提取公积金'], ['status', '状态'], ['allocations', '分配明细']], ds,
        { distributable: money, reserve_extracted: money, status: v => tag(v, { Candidate: 'warn', Approved: 'ok', Paid: 'ok', Superseded: 'dim' }), allocations: v => table([['partyId', '股东'], ['ratio', '比例'], ['gross', '税前'], ['iit', '代扣个税'], ['net', '实付'], ['suspended', '挂账']], v, { ratio: x => (x * 100).toFixed(2) + '%', gross: money, iit: money, net: money, suspended: x => x ? '专户挂账' : '' }) });
    const cs = await api('/api/calcs?kind=DividendIIT');
    $('tabBody').innerHTML += '<h3>股息红利个税推导单</h3>' + cs.map(derivHtml).join('');
  },
  async pay() {
    const d = await api('/api/payroll');
    const emps = await api('/api/employees');
    const BANK = {}; emps.forEach(e => { BANK[e.employee_id] = (e.profile || {}).bankAccount || ''; });
    let h = '<h2>薪酬（工资标准锁定 · 奖金是事件 · 股东与员工身份分账）</h2>';
    if (d.detail) h += table([['employee_id', '员工'], ['period', '期间'], ['gross', '应发'], ['si_personal', '个人社保'], ['iit', '个税'], ['net', '实发'], ['bankAccount', '收款账号'], ['derivation', '推导单']],
      d.lines.map(l => Object.assign({}, l, { bankAccount: BANK[l.employee_id] || '待补' })),
      { gross: money, si_personal: money, iit: money, net: money, derivation: v => `<button class="ghost" onclick='showJson(${esc(JSON.stringify(v))})'>展开</button>` });
    else h += '<p class="hint">薪酬明细密级=受限：股东视图仅展示汇总。</p>' + table([['period', '期间'], ['totals', '汇总']], d.runs);
    if (d.runs?.length) {
      h += '<h3>发放闭环（银行代发：自动算薪→代发文件→回单确认入链）</h3>' + d.runs.map(r => `<div class="panel m-card">
        <span><b>${esc(r.period)}</b> ${tag(r.status === 'Paid' ? '已发放' : '待发放', { 已发放: 'ok', 待发放: 'warn' })} ${r.auto ? '<span class="tag dim">月底自动算薪</span>' : ''} 实发合计 ${money(r.totals?.net)}</span>
        <span class="row">${ME.role === 'Operator' ? `<button class="ghost" onclick="downloadBatch('${esc(r.period)}')">下载银行代发文件</button>` : ''}${ME.role === 'Operator' && r.status === 'Calculated' ? `<button onclick="showPayPaid('${esc(r.period)}',${r.totals?.net ?? 0})">上传回单确认发放</button>` : ''}${r.status === 'Paid' ? `<span class="hint">回单 ${esc((r.receipt_hash || '').slice(0, 16))}…</span>` : ''}</span>
      </div>`).join('');
    }
    const SAL = { Fixed: '固定工资', BasePlusCommission: '底薪+提成' };
    h += '<h3>员工档案（入职资料 · 薪酬结构 · 社保与发放状态）</h3>' + table([['employee_id', '编号'], ['name', '姓名'], ['gender', '性别'], ['age', '年龄'], ['department', '部门'], ['position', '岗位'], ['skills', '技能'], ['salaryType', '薪酬结构'], ['baseSalary', '底薪'], ['siStatus', '社保状态'], ['last_paid_period', '最近发薪期间'], ['hire_date', '入职'], ['leave_date', '离职'], ['is_shareholder', '股东兼任']],
      emps.map(e => Object.assign({}, e, e.profile || {})),
      { salaryType: v => SAL[v] || esc(v ?? ''), baseSalary: money, siStatus: v => v ? tag(v, { 已参保: 'ok', 办理中: 'warn', 未参保: 'err' }) : '<span class="tag warn">待登记</span>', last_paid_period: v => v || '未发放', is_shareholder: v => v ? '是（双身份分账）' : '' });
    $('tabBody').innerHTML = h;
  },
  async fin() {
    const p = ($('finPeriod') && $('finPeriod').value) || new Date().toISOString().slice(0, 7);
    const [vo, rp, tb, ap] = await Promise.all([
      api('/api/vouchers?period=' + encodeURIComponent(p)), api('/api/reports?period=' + encodeURIComponent(p)),
      api('/api/trial-balance?period=' + encodeURIComponent(p)), api('/api/arap')]);
    const bs = rp.balanceSheet, inc = rp.income, cf = rp.cashFlow;
    const balTable = (arr) => table([['code', '科目码'], ['name', '科目'], ['balance', '余额']], arr, { balance: money });
    let h = `<h2>财务会计（科目·凭证·报表·往来）</h2>
    <div class="row"><input id="finPeriod" value="${esc(p)}" placeholder="YYYY-MM" style="width:110px"><button onclick="openTab('fin')">查询</button>
    <span class="hint">凭证=账本按源事件归集（不重复建账）；三表=科目聚合；与治理事件链同源、可逐笔追溯</span></div>
    <h3>资产负债表（截至 ${esc(p)}） ${tag(bs.balanced ? '资产=负债+权益' : '不平衡！', { '资产=负债+权益': 'ok', '不平衡！': 'err' })}</h3>
    <div class="grid2"><div><h4>资产 ${money(bs.totalAssets)}</h4>${balTable(bs.assets)}</div><div><h4>负债 ${money(bs.totalLiabilities)}</h4>${balTable(bs.liabilities)}</div></div>
    <h4>权益 ${money(bs.totalEquity)}</h4>${balTable(bs.equity)}
    <h3>利润表（本期 / 本年累计）</h3>
    <div class="grid2"><div><h4>本期净利 ${money(inc.period.netProfit)}</h4>${balTable(inc.period.revenue)}${balTable(inc.period.expenses)}</div>
    <div><h4>本年累计净利 ${money(inc.ytd.netProfit)}</h4><p class="hint">收入 ${money(inc.ytd.totalRevenue)} · 费用 ${money(inc.ytd.totalExpense)}</p></div></div>
    <h3>现金流量表（本期）</h3>
    ${table([['k', '活动'], ['inflow', '流入'], ['outflow', '流出'], ['net', '净额']],
      [['operating', '经营'], ['investing', '投资'], ['financing', '筹资']].map(x => Object.assign({ k: x[1] }, cf[x[0]])),
      { inflow: money, outflow: money, net: money })}
    <p class="hint">现金净增加 ${money(cf.netIncrease)}（与银行/现金科目本期发生勾稽）</p>
    <h3>科目余额表</h3>
    ${table([['code', '科目码'], ['name', '科目'], ['periodDebit', '本期借方'], ['periodCredit', '本期贷方'], ['balance', '累计余额']], tb,
      { periodDebit: money, periodCredit: money, balance: money })}
    <h3>记账凭证（${vo.length} 张）</h3>
    ${vo.map(v => `<div class="panel m-card"><b>${esc(v.voucherNo)}</b> ${tag(EVENT_CN[v.type] || v.type, {})} ${v.resolutionNo ? '<span class="tag dim">决议 ' + esc(v.resolutionNo) + '</span>' : ''} ${tag(v.balanced ? '借贷平衡' : '不平衡！', { 借贷平衡: 'ok', '不平衡！': 'err' })}
      ${table([['code', '科目码'], ['name', '科目'], ['debit', '借方'], ['credit', '贷方'], ['memo', '摘要']], v.lines, { debit: money, credit: money })}
      <span class="hint">合计 借 ${money(v.totalDebit)} / 贷 ${money(v.totalCredit)} · 依据：${(v.evidence || []).map(esc).join('、') || '无附件'}</span></div>`).join('')}
    <h3>应收应付台账</h3>
    <h4>应收（账龄）</h4>${table([['orderId', '订单'], ['customer', '客户'], ['balance', '未收'], ['aging', '账龄']], ap.receivables, { balance: money })}
    <h4>应付</h4>${table([['kind', '类别'], ['ref', '引用'], ['amount', '金额']], ap.payables, { amount: money })}`;
    $('tabBody').innerHTML = h;
  },
  async erp() {
    const [assets, stock, eq, prod, plan] = await Promise.all([api('/api/assets'), api('/api/stock'), api('/api/equipment'), api('/api/production'), api('/api/resource-plan')]);
    let h = `<h2>企业资源（第四体系）</h2><p class="muted">固资卡片+按月自动折旧 · 物料/库存/仓储（移动加权）· 设备/生产订单 · MRP 净需求与设备负荷。入库/出库/领料/完工一次提交同时落资源账+会计分录+治理留痕；折旧由系统月底自动计提（EQUI_DEPR_AUTO=0 可关）。</p>
    <h3>固定资产卡片</h3>` + table(['资产', '名称', '原值', '累计折旧', '净值', '折旧月数', '部门', '状态'], assets, a => [a.assetId, a.name, money(a.cost), money(a.accumulated), money(a.net), a.lifeMonths, a.department, a.status]);
    h += `<h3>库存余额（低于安全库存标⚠）</h3>` + table(['物料', '名称', '仓库', '数量', '均价', '金额', '安全库存'], stock.balances, b => [b.materialId, b.name, b.warehouse, b.qty, b.avgCost, money(b.value), b.belowSafety ? '⚠ ' + b.safetyStock : b.safetyStock]);
    h += `<h3>最近出入库/盘点流水</h3>` + table(['流水', '物料', '仓库', '方向', '数量', '金额', '摘要'], stock.moves, m => [m.moveId, m.materialId, m.warehouse, m.direction, m.qty, money(m.amount), m.memo]);
    h += `<h3>设备</h3>` + table(['设备', '名称', '类型', '位置', '状态', '日产能', '关联资产'], eq, e => [e.equipId, e.name, e.type, e.location, e.status, e.capacityPerDay, e.linkedAssetId]);
    h += `<h3>生产订单</h3>` + table(['订单', '产品', '计划', '完工', '设备', '状态'], prod, p => [p.orderNo, p.product, p.qty, p.completed, p.equipmentId, p.status]);
    h += `<h3>资源计划（MRP 净需求）</h3>` + table(['物料', '名称', '毛需求', '在库', '净需求', '建议采购'], plan.materialRequirements, r => [r.materialId, r.name, r.gross, r.onHand, r.net, r.suggestPurchase]);
    h += `<h3>设备负荷</h3>` + table(['设备', '名称', '待产数量', '日产能', '预计天数'], plan.equipmentLoad, l => [l.equipId, l.name, l.openQty, l.capacityPerDay, l.estimatedDays]);
    $('tabBody').innerHTML = h;
  },
  async contract() {
    const d = await api('/api/contracts');
    let h = '<h2>合同中心（凡关系必有合同 · 合同版本=规则版本）</h2>';
    if (d.missingLaborContract?.length) h += `<div class="msg err">红牌：${d.missingLaborContract.map(e => esc(e.name)).join('、')} 缺失有效劳动合同，已生成补签待办</div>`;
    h += table([['contract_id', '编号'], ['contract_type', '类型'], ['category', '类别'], ['secrecy', '密级'], ['status', '状态'], ['expires_on', '到期'], ['parties', '当事人']], d.contracts,
      { secrecy: v => tag(v, { Public: 'dim', Internal: 'dim', Restricted: 'warn', Private: 'err' }), status: v => tag(v, { Signed: 'ok', Superseded: 'dim', Terminated: 'warn' }), contract_id: (v, r) => r.secrecy === 'Private' ? `${esc(v)} <button class="ghost" onclick="accessPrivate('${esc(v)}')">授权查阅(留痕)</button>` : esc(v) });
    $('tabBody').innerHTML = h;
  },
  async module() {
    const ms = await api('/api/modules');
    const PRQ = { ResolutionKind: '需决议', CompanyType: '需企业类型', IndustryTag: '需行业标签' };
    $('tabBody').innerHTML = '<h2>模块开关与先决条件（迁移需决议入链 · 关闭不删数据）</h2>' +
      `<div class="panel hint">开关规则：① 治理地基模块（财务/人事/税务/合同/社保等）<b>不可关闭</b>，这是制度不是缺陷；② 扩展模块默认关闭，开启三步：先录「模块授权 ModuleAuth」决议（M14 需 Listing、M15 需 Financing+ComplianceCheck、M20 需 ExternalInvest、M12 需 DeptSetup）→ 再录「模块开关 ModuleSwitch」决议（params 写明 moduleId 与 targetState）→ 操作员提交 ModuleSwitchChanged 事件绑定决议；③ 停用可停模块后历史数据只读保留，下游自动收到停用通知。</div>` +
      table([['module_id', '模块'], ['name', '名称'], ['state', '状态'], ['mandatory', '治理地基'], ['prereqs', '先决条件'], ['resolution_no', '决议依据']], ms,
        { state: v => tag(v, { Enabled: 'ok', Disabled: 'dim', Suspended: 'warn', Archived: 'dim' }), mandatory: v => v ? '不可关闭' : '可开关', prereqs: v => v.length ? v.map(p => esc((PRQ[p.kind] || p.kind) + '：' + (RESKIND_CN[p.value] || p.value))).join('<br>') : '—' });
  },
  async todo() {
    const ts = await api('/api/todos');
    $('tabBody').innerHTML = '<h2>待办与告警</h2><p class="hint">办结=这件事在现实中办完了（如报税完成、参保完成）。操作员点「上传依据并办结」，选择办结凭证（回执/截图/文件）上传后自动办结入链，无需手填哈希。</p>' + table([['todo_id', '编号'], ['kind', '类型'], ['title', '事项'], ['status', '状态'], ['detail', '明细']], ts,
      { kind: v => tag(cn(TODO_CN, v), { 安全告警: 'err', 流水异常: 'err', 税务申报: 'warn', 强制分红提示: 'warn' }), status: v => tag(v, { Open: 'warn', Done: 'ok' }), todo_id: (v, r) => esc(v) + (r.status === 'Open' && ME.role === 'Operator' ? ` <button class="ghost" onclick="showDoneTodo('${esc(v)}')">上传依据并办结</button>` : '') });
  },
  async events() {
    const es = await api('/api/events?limit=100');
    $('tabBody').innerHTML = '<h2>不可篡改事件流（Append-Only · 哈希链）</h2>' +
      table([['seq', '#'], ['event_type', '类型'], ['actor_id', '发起人'], ['occurred_at', '时间'], ['resolution_no', '决议'], ['hash', '哈希'], ['payload', '内容']], es,
        { event_type: v => cnSub(EVENT_CN, v), hash: v => `<code class="hint">${esc(v?.slice(0, 16))}…</code>`, payload: v => `<button class="ghost" onclick='showJson(${esc(JSON.stringify(v))})'>查看</button>` });
  },
  async submit() {
    if (ME.role !== 'Operator') { $('tabBody').innerHTML = '<div class="msg err">单一授权操作员模式：仅操作员可录入。决策在会议、计算在引擎、执行在一人、监督在全员。</div>'; return; }
    $('tabBody').innerHTML = `<h2>操作员录入（无决议不提交 · 无依据不生效 · 提交即推送工作群）</h2>
    <p class="hint">标准流程：① 上传依据附件 → ② 录入会议决议 ResolutionRecorded（params 写清批准的事项与数字）→ ③ 提交业务事件并绑定决议编号（录入内容与决议自动一致性比对）。执行/采集类（实缴、收入、工资计算、流水导入等）凭依据直录。详见项目目录《唯一全面操作教程.md》。</p>
    <div class="grid2"><div>
      <h3>1. 事件类型</h3><select id="evType" onchange="evTemplate()">${Object.keys(TEMPLATES).map(t => `<option value="${t}">${EVENT_CN[t] || t}（${t}）</option>`).join('')}</select>
      <h3>2. 决议编号（执行/采集类可留空）</h3><input id="evRes" placeholder="如 RES-2026-001">
      <h3>3. 依据附件（上传自动入链存证）</h3><input type="file" id="evFile"><button class="blue" onclick="uploadEvidence()">上传依据</button><div id="evList" class="hint"></div>
      <h3>4. 业务参数（数字由引擎计算，录入内容与决议一致性自动比对）</h3><textarea id="evPayload" rows="12"></textarea>
      <button onclick="submitEvent()">提交生效（入链 + 自动联动 + 推送）</button>
      <button class="warn" onclick="refuse()">一键拒绝违规指令并留痕（直达监事/全体股东）</button>
    </div><div><h3>提交结果 / 影响清单</h3><div id="evResult" class="msg"></div><div id="evImpacts"></div></div></div>`;
    evTemplate();
  },
  async legal() {
    $('tabBody').innerHTML = `<h2>法务库（常见事项应对方案 · 关键词搜索 · 条款可直接写入合同）</h2>
    <div class="row"><input id="legalQ" placeholder="关键词：社保 / 竞业 / 担保 / 分红 / 个体户 / 辞退…" style="max-width:420px"><button onclick="searchLegal()">搜索</button><button class="ghost" onclick="searchLegal(true)">全部</button></div>
    ${ME.role === 'Operator' ? '<p class="hint">操作员可凭依据新增条目：在「操作员录入」选「法务库新增条目（LegalEntryAdded）」提交，入链后全员可检索。</p>' : ''}
    <div id="legalList"></div>`;
    $('legalQ').addEventListener('keydown', e => { if (e.key === 'Enter') searchLegal(); });
    await searchLegal(true);
  },
  async audit() {
    const lic = await api('/api/license-status');
    const licCn = { valid: '授权有效', grace: '离线宽限', unconfigured: '未配置授权', expired: '已过期', invalid: '无效', tampered: '篡改暂停', suspended: '平台暂停', offline_expired: '离线超宽限' }[lic.status] || lic.status;
    $('tabBody').innerHTML = `<h2>审计与验真</h2>
    <div class="panel m-card"><span>商业授权 ${tag(licCn, { 授权有效: 'ok', 离线宽限: 'warn', 未配置授权: 'dim' })} ${lic.enforce ? '<span class="tag warn">强制开</span>' : '<span class="tag dim">强制关</span>'}</span><span class="hint">${esc(lic.message)}${lic.expiresAt ? ' · 到期 ' + esc(String(lic.expiresAt).slice(0, 10)) : ''}</span></div>
    <button onclick="doVerify()">一键验真（哈希链 + 签名 + 全量复算比对）</button>
    <button class="blue" onclick="exportPkg()">导出司法证据包</button>
    ${ME.role === 'Operator' || ME.role === 'Supervisor' ? '<button class="ghost" onclick="doAnchor()">链头锚定</button>' : ''}
    <div id="verifyResult"></div>`;
  }
};

async function searchLegal(all) {
  const q = all ? '' : $('legalQ').value.trim();
  const rows = await api('/api/legal' + (q ? '?q=' + encodeURIComponent(q) : ''));
  $('legalList').innerHTML = rows.length ? rows.map(r => `<div class="panel">
    <b>${esc(r.title)}</b> <span class="tag ${r.origin === 'System' ? 'dim' : 'ok'}">${r.origin === 'System' ? '系统预置' : '事件新增'}</span>
    <div class="hint">关键词：${esc(r.keywords)}</div>
    <p>${esc(r.scenario)}</p>
    ${(r.options || []).map(o => `<div class="deriv"><b>${esc(o.label)}</b><div class="f">${esc(o.action)}</div><div class="p">依据：${esc(o.basis)}</div></div>`).join('')}
    ${r.clause_template ? `<div class="row"><pre style="flex:1">${esc(r.clause_template)}</pre><button class="ghost" onclick='copyClause(${esc(JSON.stringify(r.clause_template))})'>复制条款（粘贴进合同）</button></div>` : ''}
  </div>`).join('') : '<p class="hint">无匹配条目，换个关键词试试。</p>';
}
function copyClause(text) {
  navigator.clipboard?.writeText(text).then(() => alert('条款已复制。在「操作员录入 → 合同签署」的 clauses 数组或合同文档中粘贴使用。'))
    .catch(() => showJson(text));
}
function derivHtml(c) {
  let h = `<div class="panel"><b>${esc(c.kind)}</b> ${esc(c.subject ?? '')} = <b>${money(c.amount)}</b> <span class="tag dim">${esc(c.status)}</span>`;
  for (const r of c.derivation || []) h += `<div class="deriv"><b>${esc(r.step)}</b><div class="f">${esc(r.formula)}</div><div class="p">规则 ${esc(r.ruleRef)} · 依据 ${esc(r.policy)}</div></div>`;
  return h + '</div>';
}
function showJson(v) { $('modalTitle').textContent = '明细'; $('modalBody').innerHTML = '<pre>' + esc(JSON.stringify(v, null, 2)) + '</pre>'; $('modal').style.display = 'flex'; }
function closeModal() { $('modal').style.display = 'none'; }

async function vote() {
  try {
    const j = await api('/api/events', 'POST', { type: 'ResolutionVoted', payload: { resolutionNo: $('voteNo').value, partyId: ME.partyId, approve: $('voteApprove').value === 'true' }, evidence: [] });
    $('voteMsg').className = 'msg ok'; $('voteMsg').textContent = '表决已入链：' + j.hash;
    openTab('res');
  } catch (e) { $('voteMsg').className = 'msg err'; $('voteMsg').textContent = e.message; }
}
async function quickVote(no, approve) {
  try {
    const j = await api('/api/events', 'POST', { type: 'ResolutionVoted', payload: { resolutionNo: no, partyId: ME.partyId, approve }, evidence: [] });
    alert('表决已入链：' + j.hash.slice(0, 16) + '…');
    openTab(curTab);
  } catch (e) { alert(e.message); }
}
async function downloadBatch(period) {
  const r = await fetch('/api/payroll-batch?period=' + encodeURIComponent(period), { headers: { 'Authorization': 'Bearer ' + TOKEN } });
  if (!r.ok) return alert((await r.json()).error || '下载失败');
  const blob = await r.blob();
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `payroll-batch-${period}.csv`; a.click();
}
function showPayPaid(period, net) {
  $('modalTitle').textContent = '确认发放 ' + period;
  $('modalBody').innerHTML = `<p class="hint">银行代发完成后，上传银行回单（批量代发回执）。系统核对确认金额=实发合计一致后自动入链闭环，全员收到发放通知：</p>
    <p>实发合计：<b>${money(net)}</b> 元</p>
    <input type="file" id="payPaidFile"><br><br>
    <button onclick="submitPayPaid('${esc(period)}',${net})">上传回单并确认发放</button><div id="payPaidMsg" class="msg"></div>`;
  $('modal').style.display = 'flex';
}
async function submitPayPaid(period, net) {
  const f = $('payPaidFile').files[0];
  if (!f) { $('payPaidMsg').className = 'msg err'; $('payPaidMsg').textContent = '请先选择银行回单文件'; return; }
  try {
    const fd = new FormData(); fd.append('file', f);
    const r = await fetch('/api/evidence', { method: 'POST', headers: { 'Authorization': 'Bearer ' + TOKEN }, body: fd });
    const j = await r.json(); if (!r.ok) throw new Error(j.error);
    await api('/api/events', 'POST', { type: 'PayrollPaid', payload: { period, totalAmount: net }, evidence: [{ name: j.fileName, hash: j.hash }] });
    closeModal(); openTab('pay');
  } catch (e) { $('payPaidMsg').className = 'msg err'; $('payPaidMsg').textContent = e.message; }
}
async function accessPrivate(id) {
  const purpose = prompt('查阅目的（访问行为全程入链留痕）：'); if (purpose === null) return;
  try { const j = await api(`/api/contracts/${id}/access`, 'POST', { purpose, dualAuthorized: ME.role === 'Supervisor' }); showJson(j); }
  catch (e) { alert(e.message); }
}
function showDoneTodo(id) {
  $('modalTitle').textContent = '办结待办 ' + id;
  $('modalBody').innerHTML = `<p class="hint">选择办结凭证（申报回执/参保凭证/付款回单等），上传后自动办结入链：</p>
    <input type="file" id="todoFile"><br><br>
    <button onclick="submitDoneTodo('${esc(id)}')">上传依据并办结</button><div id="todoDoneMsg" class="msg"></div>`;
  $('modal').style.display = 'flex';
}
async function submitDoneTodo(id) {
  const f = $('todoFile').files[0];
  if (!f) { $('todoDoneMsg').className = 'msg err'; $('todoDoneMsg').textContent = '请先选择文件'; return; }
  try {
    const fd = new FormData(); fd.append('file', f);
    const r = await fetch('/api/evidence', { method: 'POST', headers: { 'Authorization': 'Bearer ' + TOKEN }, body: fd });
    const j = await r.json(); if (!r.ok) throw new Error(j.error);
    await api('/api/events', 'POST', { type: 'TodoCompleted', payload: { todoId: id }, evidence: [{ name: j.fileName, hash: j.hash }] });
    closeModal(); openTab('todo');
  } catch (e) { $('todoDoneMsg').className = 'msg err'; $('todoDoneMsg').textContent = e.message; }
}
let evEvidence = [];
async function uploadEvidence() {
  const f = $('evFile').files[0]; if (!f) return alert('选择文件');
  const fd = new FormData(); fd.append('file', f);
  const r = await fetch('/api/evidence', { method: 'POST', headers: { 'Authorization': 'Bearer ' + TOKEN }, body: fd });
  const j = await r.json();
  if (!r.ok) return alert(j.error);
  evEvidence.push({ name: j.fileName, hash: j.hash });
  $('evList').innerHTML = evEvidence.map(e => `✓ ${esc(e.name)} <code>${e.hash.slice(0, 16)}…</code>`).join('<br>');
}
async function submitEvent() {
  try {
    const j = await api('/api/events', 'POST', { type: $('evType').value, resolutionNo: $('evRes').value || null, payload: JSON.parse($('evPayload').value), evidence: evEvidence });
    $('evResult').className = 'msg ok'; $('evResult').textContent = `已入链 seq=${j.seq}\n哈希 ${j.hash}`;
    $('evImpacts').innerHTML = '<h3>影响清单（自动传播，不可挑选）</h3>' + (j.impacts || []).map(i => `<div class="deriv">${esc(i)}</div>`).join('');
    evEvidence = []; $('evList').innerHTML = '';
  } catch (e) { $('evResult').className = 'msg err'; $('evResult').textContent = '拒绝：' + e.message + '\n（越权/违规尝试已告警入链）'; }
}
async function refuse() {
  const detail = prompt('描述违规指令（将作为举报依据入链，直达监事与全体股东）：'); if (!detail) return;
  await api('/api/events', 'POST', { type: 'SecurityAlert', payload: { kind: 'OperatorRefusal', detail: { instruction: detail } }, evidence: evEvidence });
  alert('已拒绝并留痕，直达监事与全体股东。');
}
async function doVerify() {
  $('verifyResult').innerHTML = '<p class="hint">全量重放校验中…</p>';
  const r = await api('/api/verify');
  $('verifyResult').innerHTML = r.ok
    ? `<div class="panel"><h2 style="color:var(--acc)">账本完整 ✅</h2><p>事件 ${r.eventsChecked} 条 · 哈希链连续 · 签名有效 · 投影与事件流重放逐表一致（误差 0.00）</p></div>`
    : `<div class="panel"><h2 style="color:var(--err)">存在异常 ❌</h2><pre>${esc(JSON.stringify(r, null, 2))}</pre></div>`;
}
async function exportPkg() {
  const pkg = await api('/api/evidence-package');
  const blob = new Blob([JSON.stringify(pkg, null, 2)], { type: 'application/json' });
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'equiledger-evidence-package.json'; a.click();
}
async function doAnchor() { const j = await api('/api/anchor', 'POST', {}); alert(j.note); }
async function doReset() {
  if (!confirm('⚠️ 清空数据库：全部事件、账目、依据文件将被删除且不可恢复！\n仅限设立后一个月内。确定继续？')) return;
  if (!confirm('再次确认：清空后需重新走设立向导。确定清空？')) return;
  try { await api('/api/reset', 'POST', {}); alert('已清空。请重新设立公司。'); logout(); }
  catch (e) { alert(e.message); }
}

// ---------- 开发者信息（部署时修改此处；二维码图片放 web/dev-wechat-qr.png） ----------
const DEVELOPER = { name: 'XTZDDJ', wechat: 'XTZDDJ', qq: '248197309 / 68822685', email: 'admin@djkz.top', qr: '/dev-wechat-qr.png' };
function showDev() {
  $('modalTitle').textContent = '关于 / 开发者';
  $('modalBody').innerHTML = `<p>玄铁栈道 EquiLedger · 企业智能计算治理系统</p>
    <p>开发者：<b>${esc(DEVELOPER.name)}</b></p>
    <p>微信：<button class="ghost" onclick="showWechatQR()">${esc(DEVELOPER.wechat)}（点击显示二维码）</button></p>
    <p>QQ：${esc(DEVELOPER.qq)}</p>
    <p>邮箱：${esc(DEVELOPER.email)}</p>
    <p class="hint">版权所有 © 2026 ${esc(DEVELOPER.name)}。保留开发者署名是商业授权条件；移除或篡改署名即授权终止并构成侵权。</p>`;
  $('modal').style.display = 'flex';
}
function showWechatQR() {
  $('modalTitle').textContent = '微信二维码';
  $('modalBody').innerHTML = `<img src="${DEVELOPER.qr}" style="max-width:280px;display:block;margin:0 auto" onerror="this.outerHTML='<p class=hint>二维码图片尚未配置：请将微信二维码图片命名为 dev-wechat-qr.png 放入 web/ 目录后重新构建。</p>'"><p class="hint" style="text-align:center">微信号：${esc(DEVELOPER.wechat)}</p>`;
  $('modal').style.display = 'flex';
}

// 赞助档位：须与开发者微信赞赏码里设置的固定金额一致；改赞赏码时同步修改此处。
const SPONSOR_TIERS = [10, 20, 50, 100, 200];
let SP_TIER = 0;
async function showSponsor() {
  $('modalTitle').textContent = '赞助开发';
  $('modalBody').innerHTML = `
    <p style="text-align:center;font-weight:600">赞助鼓励开发者把本项目更加完善</p>
    <img src="/dev-sponsor-qr.png" style="max-width:240px;display:block;margin:0 auto" onerror="this.outerHTML='<p class=hint>赞赏码尚未配置：请将微信赞赏码图片命名为 dev-sponsor-qr.png 放入 web/ 目录后重新构建。</p>'">
    <p class="hint" style="text-align:center">微信扫码赞助（固定档位）。付款后请填写下表登记上墙。</p>
    <div class="sponsor-tiers">${SPONSOR_TIERS.map(t => `<button class="ghost" onclick="pickTier(this,${t})">¥${t}</button>`).join('')}</div>
    <label>姓名/习惯名称 <input id="spName" maxlength="40" placeholder="将显示在赞助墙"></label>
    <label>留言（可选） <input id="spMsg" maxlength="100" placeholder="给开发者的一句话"></label>
    <label>付款截图（可选，供开发者对账） <input id="spProof" type="file" accept="image/*"></label>
    <button onclick="submitSponsor()">登记上墙</button>
    <p class="hint">金额为赞助人自报（赞赏码固定档位），开发者凭截图定期对账。赞助墙只增不改+哈希链，不可篡改删除，且与贵公司业务账本完全独立、互不影响。</p>
    <div class="sponsor-wall"><div id="spWall" class="sponsor-scroll"></div></div>
    <p class="hint" id="spTotal"></p>`;
  $('modal').style.display = 'flex';
  SP_TIER = 0;
  await loadSponsors();
}
function pickTier(btn, t) {
  document.querySelectorAll('.sponsor-tiers button').forEach(b => b.classList.remove('picked'));
  btn.classList.add('picked'); SP_TIER = t;
}
async function loadSponsors() {
  const d = await (await fetch('/api/sponsors')).json();
  const items = d.items || [];
  const html = items.map(s => `<div class="sponsor-item">${esc(s.name)} 赞助 ¥${s.amount}${s.message ? ' · ' + esc(s.message) : ''}${s.proofHash ? '（附截图）' : ''}</div>`).join('') || '<div class="sponsor-item">还没有赞助记录，来做第一位鼓励者。</div>';
  $('spWall').innerHTML = html + html;
  $('spTotal').textContent = `累计赞助 ${items.length} 笔 · 合计 ¥${d.total}`;
}
async function submitSponsor() {
  const name = $('spName').value.trim();
  if (!name) return alert('请填写姓名/习惯名称');
  if (!SP_TIER) return alert('请先选择赞助档位');
  let proof = null;
  const f = $('spProof').files[0];
  if (f) {
    const fd = new FormData(); fd.append('file', f);
    const up = await fetch('/api/sponsor-proof', { method: 'POST', body: fd });
    if (!up.ok) return alert('截图上传失败');
    proof = (await up.json()).hash;
  }
  const r = await fetch('/api/sponsors', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name, amount: SP_TIER, message: $('spMsg').value.trim(), proofHash: proof }) });
  if (!r.ok) return alert((await r.json()).error || '登记失败');
  alert('感谢赞助！已登记上墙。');
  await loadSponsors();
}

const TEMPLATES = {
  ResolutionRecorded: { resolutionNo: 'RES-2026-001', title: '事项', kind: 'Bonus', params: { employeeId: 'e1', amount: 2000 }, votes: [{ partyId: 'alice', approve: true }], passed: true },
  ResolutionProposed: { resolutionNo: 'RES-2026-002', title: '议案', kind: 'PayrollChange', params: { subjectParty: 'alice' } },
  CapitalPaidIn: { partyId: 'alice', amount: 600000, premium: 0 },
  RevenueRecorded: { amount: 1000000, customer: '客户X' },
  ExpenseRecorded: { amount: 3000, category: '办公', payee: '供应商A', payeeAccount: '6222000011112222' },
  RuleActivated: { ruleId: 'wage-e1', kind: 'Payroll.Standard', subject: 'e1', params: { monthly: 8000 } },
  ContractSigned: { contractId: 'CT-001', category: '人事类', contractType: '劳动合同', parties: ['e1'], secrecy: 'Restricted', docHash: '填写上传依据的哈希', clauses: [] },
  EmployeeHired: { employeeId: 'e1', partyId: 'e1', name: '张三', position: '工程师', hireDate: '2026-11-01', contractId: 'CT-001', gender: '男', age: 28, department: '技术部', skills: 'Java/运维', salaryType: 'Fixed', baseSalary: 8000, siStatus: '办理中', bankAccount: '6222000011112222（银行代发收款账号）', bankName: '开户行（可选）', emergencyContact: '家属电话（可选）' },
  PayrollRun: { period: '2026-11' },
  PayrollPaid: { period: '2026-11', totalAmount: 6552 },
  FixedAssetAcquired: { assetId: 'FA-01', name: '服务器', category: '电子设备', cost: 120000, residualRate: 0, lifeMonths: 120, acquiredDate: '2026-11-01', department: '技术部', location: '机房' },
  FixedAssetDepreciationRun: { period: '2026-11' },
  FixedAssetDisposed: { assetId: 'FA-01', disposalDate: '2026-12-01', proceeds: 50000 },
  MaterialDefined: { materialId: 'M1', name: '钢材', unit: '件', spec: 'Q235', category: 'raw', safetyStock: 10 },
  WarehouseDefined: { warehouseId: 'WH1', name: '主仓', location: '一号库' },
  StockReceived: { materialId: 'M1', warehouse: 'WH1', qty: 100, unitCost: 10, paid: false, supplier: '供应商A', memo: '采购入库' },
  StockIssued: { materialId: 'M1', warehouse: 'WH1', qty: 30, purpose: 'sale' },
  StockCounted: { materialId: 'M1', warehouse: 'WH1', countedQty: 68, reason: '月度盘点' },
  EquipmentRegistered: { equipId: 'EQ1', name: '切割机', type: '加工', location: '车间', status: 'Idle', capacityPerDay: 5, linkedAssetId: '' },
  ProductionOrderCreated: { orderNo: 'PO-1', productMaterialId: 'P1', qty: 10, bom: [{ materialId: 'M1', qtyPer: 2 }], equipmentId: 'EQ1', plannedStart: '2026-11-01', plannedEnd: '2026-11-30' },
  ProductionCompleted: { orderNo: 'PO-1', completedQty: 10, warehouse: 'WH1' },
  BonusGranted: { employeeId: 'e1', amount: 2000, reason: '月度突出贡献', period: '2026-11' },
  EmployeeLeft: { employeeId: 'e1', leaveDate: '2026-11-30', reason: 'MutualTermination', remainingLeaveDays: 5, selfConfirmed: true },
  ShareSubscription: { partyId: 'carol', partyName: '股东丙', subscribed: 100000, targetRatio: 0, contractId: 'CT-002' },
  ShareTransfer: { fromParty: 'bob', toParty: 'carol', toName: '股东丙', capitalAmount: 100000, price: 150000, contractId: 'CT-003' },
  ShareExit: { partyId: 'bob', capitalAmount: 100000 },
  ShareholderDeceased: { partyId: 'bob' },
  DividendApproved: { planId: '在分红页查看候选稿编号' },
  DividendPaid: { planId: '方案编号' },
  SalesOrderConfirmed: { orderId: 'SO-1', customer: '客户Y', amount: 200000, employeeId: 'e1' },
  CollectionReceived: { orderId: 'SO-1', amount: 200000 },
  BankFlowImported: { flows: [{ flowId: 'F1', direction: 'In', counterparty: '客户X', amount: 1000000, flowDate: '2026-11-20', refEventId: null }] },
  PeriodClosed: { period: '2026-11' },
  CorrectionRecorded: { targetEventId: '原事件ID', reason: '录入错误' },
  ModuleSwitchChanged: { moduleId: 'M16', targetState: 'Enabled' },
  InvestorQualified: { investorId: 'inv1', name: '投资人甲', proofHash: '资产证明哈希' },
  InvestmentReceived: { investorId: 'inv1', amount: 500000, subscribed: 100000, contractId: 'CT-INV', contractAmount: 500000 },
  InvestmentMade: { target: '子公司A', amount: 200000, method: 'cost', shareRatio: 0.6 },
  InvestmentReturnReceived: { target: '子公司A', amount: 30000 },
  RelationDeclared: { fromParty: 'alice', toParty: 'bob', relation: 'Spouse' },
  CompanyInfoChanged: { name: '新公司名称', industryTags: ['软件'] },
  OperatorHandover: { newOperatorUserId: 'backup-op' },
  TodoCompleted: { todoId: '待办编号' },
  LegalEntryAdded: { title: '事项名称（如：停车场收费纠纷应对）', keywords: '停车场 收费 纠纷', scenario: '什么情况下会遇到这件事', options: [{ label: '方案一', action: '具体怎么做', basis: '法律/政策依据' }], clauseTemplate: '可直接写入合同的条款文本（可空）' },
  AccountAdded: { username: 'newuser', displayName: '新成员', role: 'Shareholder', password: 'N#12345', realName: '真实姓名', idNumber: '18位身份证号', partyId: 'newuser（股东填，与登录名一致）' }
};
function evTemplate() { $('evPayload').value = JSON.stringify(TEMPLATES[$('evType').value], null, 2); }

if (TOKEN) boot();
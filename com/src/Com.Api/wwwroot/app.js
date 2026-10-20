/* COM 门户 —— 无构建、无框架。一个文件读完一条链路，方便课堂上直接改。 */
const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const money = (n, cur = 'CNY') => (cur === 'CNY' ? '¥' : cur + ' ') + Number(n || 0).toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const dt = (s) => { if (!s) return '—'; const d = new Date(s); return d.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }); };
const dOnly = (s) => { if (!s) return '—'; return new Date(s).toLocaleDateString('zh-CN'); };

const S = { cfg: {}, api: '', token: localStorage.getItem('com.token') || '', user: null, route: '' };

/* ── 网络 ─────────────────────────────────────────────── */
async function api(path, opts = {}) {
  const headers = Object.assign({}, opts.headers || {});
  if (S.token) headers['Authorization'] = 'Bearer ' + S.token;
  if (opts.body && !(opts.body instanceof FormData)) headers['Content-Type'] = 'application/json';
  const res = await fetch(S.api + path, Object.assign({}, opts, { headers }));
  if (res.status === 401 && S.token) { logout(); throw new Error('登录已过期'); }
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = text; }
  if (!res.ok) {
    const msg = (data && (data.message || data.title)) || res.status + ' ' + res.statusText;
    throw new Error(msg);
  }
  return data;
}
const get = (p) => api(p);
const post = (p, body) => api(p, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

let toastTimer;
function toast(msg, isErr) {
  const t = $('#toast');
  t.textContent = msg; t.className = 'toast' + (isErr ? ' err' : '');
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.add('hidden'), 3200);
}

/* ── 启动 ─────────────────────────────────────────────── */
(async function boot() {
  try {
    const cfg = await fetch('/config.json').then(r => r.json()).catch(() => ({}));
    S.cfg = cfg || {}; S.api = (cfg.apiBase || '').replace(/\/$/, '');
  } catch { }

  $('#sf-env').textContent = S.cfg.environment || 'local';
  $('#sf-region').textContent = S.cfg.region || 'local';

  const accounts = await fetch(S.api + '/api/auth/demo-accounts').then(r => r.json()).catch(() => []);
  $('#login-upn').innerHTML = accounts.map(a => `<option value="${esc(a.upn)}">${esc(a.name)}</option>`).join('')
    || '<option value="sales@com.demo">sales@com.demo</option>';

  if (S.token) {
    try { S.user = await get('/api/auth/me'); showApp(); return; } catch { }
  }
  // 演示便捷入口：?as=finance 直达角色（仅演示环境开启）
  const as = new URLSearchParams(location.search).get('as');
  if (as && S.cfg.demoAutoLogin) {
    try {
      const r = await post('/api/auth/login', { upn: as, password: 'Com@2026' });
      S.token = r.token; S.user = r.user; localStorage.setItem('com.token', r.token);
      showApp(); return;
    } catch { }
  }
  $('#login').classList.remove('hidden');
})();

$('#login-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('#login-err').textContent = '';
  try {
    const r = await post('/api/auth/login', { upn: $('#login-upn').value, password: $('#login-pwd').value });
    S.token = r.token; S.user = r.user;
    localStorage.setItem('com.token', r.token);
    showApp();
  } catch (err) { $('#login-err').textContent = err.message; }
});

$('#switch-user').addEventListener('click', logout);

function logout() {
  localStorage.removeItem('com.token'); S.token = ''; S.user = null;
  $('#app').classList.add('hidden'); $('#panel').classList.add('hidden');
  $('#login').classList.remove('hidden');
}

function showApp() {
  $('#login').classList.add('hidden');
  $('#app').classList.remove('hidden');
  $('#user-name').textContent = S.user.name.split('·')[0].trim();
  $('#user-role').textContent = S.user.roleLabel;
  $('#user-av').textContent = S.user.name.slice(0, 1);
  $('#sf-ver').textContent = S.cfg.version || '1.0.0';
  refreshBadge();
  if (!location.hash || location.hash === '#') location.hash = '#/';
  route();
}

window.addEventListener('hashchange', route);

async function refreshBadge() {
  try {
    const s = await get('/api/approvals/summary');
    const b = $('#nav-badge');
    if (s.pending > 0) { b.textContent = s.pending; b.classList.remove('hidden'); }
    else b.classList.add('hidden');
  } catch { }
}

/* ── 路由 ─────────────────────────────────────────────── */
async function route() {
  if (!S.user) return;
  const h = location.hash.replace(/^#/, '') || '/';
  S.route = h;
  $$('nav a').forEach(a => a.classList.toggle('on', a.getAttribute('href') === '#' + h.split('/').slice(0, 2).join('/') || a.getAttribute('href') === '#' + h));
  const parts = h.split('/').filter(Boolean);

  $('#panel').classList.add('hidden');
  try {
    if (parts.length === 0) await viewDash();
    else if (parts[0] === 'orders' && parts[1] === 'new') { $('#crumb').textContent = '新建订单'; await viewNewOrder(); }
    else if (parts[0] === 'orders' && parts[1]) { $('#crumb').textContent = '订单详情'; await viewOrders(); await openOrder(parts[1]); }
    else if (parts[0] === 'orders') { $('#crumb').textContent = '订单与合同'; await viewOrders(); }
    else if (parts[0] === 'approvals') { $('#crumb').textContent = '审批中心'; await viewApprovals(); }
    else if (parts[0] === 'intake') { $('#crumb').textContent = '智能录入'; await viewIntake(); }
    else if (parts[0] === 'ops') { $('#crumb').textContent = '运维看板'; await viewOps(); }
  } catch (e) {
    $('#view').innerHTML = `<div class="card" style="border-color:#4A2323"><b style="color:#FF6B6B">加载失败</b><div class="sub" style="margin:8px 0 0">${esc(e.message)}</div></div>`;
  }
}

/* ── 工作台 ───────────────────────────────────────────── */
async function viewDash() {
  const [orders, deps] = await Promise.all([get('/api/orders'), get('/api/ops/deps')]);
  const pending = orders.filter(o => o.status === 'PendingApproval').length;
  const archived = orders.filter(o => o.status === 'Archived').length;
  const total = orders.reduce((a, o) => a + o.totalAmount, 0);
  const bad = deps.filter(d => !d.ok).length;

  $('#view').innerHTML = `
    <h1>工作台</h1>
    <div class="sub">合同全流程：建单 → 审批 → 合同生成 → 不可变归档</div>
    <div class="grid g4" style="margin-bottom:16px">
      <div class="card stat acc"><div class="k">订单总额</div><div class="v">${(total / 10000).toFixed(1)}<span style="font-size:13px;color:#8D919B"> 万</span></div><div class="d">${orders.length} 张订单</div></div>
      <div class="card stat"><div class="k">审批中</div><div class="v">${pending}</div><div class="d">48 小时 SLA</div></div>
      <div class="card stat"><div class="k">已归档合同</div><div class="v">${archived}</div><div class="d">WORM 保留 30 天</div></div>
      <div class="card stat"><div class="k">依赖探针</div><div class="v" style="color:${bad ? 'var(--red)' : 'var(--green)'}">${deps.length - bad}/${deps.length}</div><div class="d">${bad ? bad + ' 项不可用' : '全部正常'}</div></div>
    </div>

    <div class="row" style="margin-bottom:10px"><b style="font-size:13px">最近订单</b><div class="spacer"></div>
      <a class="btn sm" href="#/orders">查看全部</a>
      <a class="btn primary sm" href="#/orders/new">+ 新建订单</a></div>
    <div class="tbl-wrap">${ordersTable(orders.slice(0, 8))}</div>

    <div class="grid g2" style="margin-top:16px">
      <div class="card"><div class="k" style="font-size:10.5px;color:var(--muted-2);letter-spacing:.08em">运行模式</div>
        <div id="modes" style="margin-top:10px"></div></div>
      <div class="card"><div class="k" style="font-size:10.5px;color:var(--muted-2);letter-spacing:.08em">依赖状态</div>
        <div style="margin-top:6px">${deps.map(d => `<div class="dep"><span class="st ${d.ok ? 'ok' : 'bad'}"></span><span class="nm">${esc(d.name)}</span><span class="ms">${d.latencyMs}ms</span></div>`).join('')}</div></div>
    </div>`;

  try {
    const s = await get('/api/ops/summary');
    $('#modes').innerHTML = Object.entries(s.modes).map(([k, v]) =>
      `<div class="dep"><span class="kd" style="width:74px">${esc(k)}</span><span class="nm" style="font-family:var(--mono);font-size:11.5px">${esc(v)}</span></div>`).join('');
  } catch { }
}

function ordersTable(list) {
  if (!list.length) return '<div class="empty">暂无订单</div>';
  return `<table><thead><tr><th>订单号</th><th>客户</th><th>金额</th><th>状态</th><th>审批进度</th><th>创建</th></tr></thead><tbody>
    ${list.map(o => `<tr onclick="location.hash='#/orders/${o.id}'">
      <td class="mono">${esc(o.orderNo)}</td>
      <td>${esc(o.customerName)}</td>
      <td class="num">${money(o.totalAmount, o.currency)}</td>
      <td><span class="pill ${o.status}">${esc(o.statusLabel)}</span></td>
      <td style="font-size:11.8px;color:var(--muted)">${esc(o.approvalStage)}</td>
      <td style="font-size:11.5px;color:var(--muted-2)">${dt(o.createdAt)}</td></tr>`).join('')}
  </tbody></table>`;
}

/* ── 订单列表 ─────────────────────────────────────────── */
let orderFilter = '';
async function viewOrders() {
  const q = orderFilter ? '?status=' + orderFilter : '';
  const orders = await get('/api/orders' + q);
  const tabs = [['', '全部'], ['Draft', '草稿'], ['PendingApproval', '审批中'], ['Approved', '已通过'], ['Archived', '已归档']];
  $('#view').innerHTML = `
    <h1>订单与合同</h1>
    <div class="sub">${S.user.role === 'sales' ? '销售视角：仅显示本人创建的订单' : '管理视角：显示全部订单'}</div>
    <div class="row" style="margin-bottom:12px">
      ${tabs.map(([v, t]) => `<button class="btn sm" style="${orderFilter === v ? 'border-color:var(--accent);color:#C9D3FF' : ''}" onclick="orderFilter='${v}';route()">${t}</button>`).join('')}
      <div class="spacer"></div>
      <a class="btn primary sm" href="#/orders/new">+ 新建订单</a>
    </div>
    <div class="tbl-wrap">${ordersTable(orders)}</div>`;
}

/* ── 新建订单 ─────────────────────────────────────────── */
let draft = null;
async function viewNewOrder() {
  const [customers, products] = await Promise.all([get('/api/customers'), get('/api/products')]);
  draft = { customerId: customers[0]?.id, terms: '款到发货', remark: '', lines: [] };
  addLine(products);

  $('#view').innerHTML = `
    <h1>新建订单</h1>
    <div class="sub">订单号由服务端按日生成；金额由行明细汇总，前端不参与计算</div>
    <div class="card">
      <div class="form-grid" style="margin-bottom:14px">
        <div class="field"><label>客户</label><select id="f-cust">${customers.map(c => `<option value="${c.id}">${esc(c.name)}（额度 ${(c.creditLimit / 10000).toFixed(0)} 万）</option>`).join('')}</select></div>
        <div class="field"><label>付款条款</label><select id="f-terms"><option>款到发货</option><option>月结 30 天</option><option>预付 30% + 验收后 70%</option></select></div>
      </div>
      <div class="field" style="margin-bottom:14px"><label>备注</label><input id="f-remark" placeholder="选填，例如：客户要求分批发货"></div>

      <div class="sec-t" style="margin-top:4px">订单明细</div>
      <div id="lines"></div>
      <button class="btn sm" id="add-line">+ 添加一行</button>

      <div class="row" style="margin-top:18px;border-top:1px solid var(--line);padding-top:14px">
        <div class="spacer"></div>
        <div style="text-align:right"><div style="font-size:10.5px;color:var(--muted-2);text-transform:uppercase;letter-spacing:.08em">合计</div>
          <div id="total" class="num" style="font-size:18px;font-weight:650">¥0.00</div></div>
        <button class="btn primary" id="save-draft">保存草稿</button>
        <button class="btn" id="save-submit">保存并提交审批</button>
      </div>
    </div>`;

  window.__products = products;
  renderLines();
  $('#add-line').onclick = () => { addLine(products); renderLines(); };
  $('#save-draft').onclick = () => saveOrder(false);
  $('#save-submit').onclick = () => saveOrder(true);
}

function addLine(products) {
  draft.lines.push({ productId: products[0]?.id, qty: 1, unitPrice: products[0]?.unitPrice });
}

function renderLines() {
  const products = window.__products || [];
  $('#lines').innerHTML = draft.lines.map((l, i) => {
    const p = products.find(x => x.id === l.productId) || {};
    const amt = (l.qty || 0) * (l.unitPrice || 0);
    return `<div class="line-row">
      <select onchange="setLine(${i},'productId',this.value)">${products.map(x => `<option value="${x.id}" ${x.id === l.productId ? 'selected' : ''}>${esc(x.name)}</option>`).join('')}</select>
      <input type="number" min="1" value="${l.qty}" onchange="setLine(${i},'qty',+this.value)">
      <input type="number" min="0" step="0.01" value="${l.unitPrice}" onchange="setLine(${i},'unitPrice',+this.value)">
      <div class="amt">${money(amt)}</div>
      <button class="x" onclick="draft.lines.splice(${i},1);renderLines()">×</button>
    </div>`;
  }).join('');
  const total = draft.lines.reduce((a, l) => a + (l.qty || 0) * (l.unitPrice || 0), 0);
  $('#total').textContent = money(total);
}

function setLine(i, k, v) {
  draft.lines[i][k] = v;
  if (k === 'productId') {
    const p = (window.__products || []).find(x => x.id === v);
    if (p) draft.lines[i].unitPrice = p.unitPrice;
  }
  renderLines();
}

async function saveOrder(andSubmit) {
  try {
    const body = {
      customerId: $('#f-cust').value,
      terms: $('#f-terms').value,
      remark: $('#f-remark').value,
      lines: draft.lines.map(l => ({ productId: l.productId, qty: +l.qty, unitPrice: +l.unitPrice })),
      idempotencyKey: crypto.randomUUID()
    };
    const order = await post('/api/orders', body);
    if (andSubmit) { await post(`/api/orders/${order.id}/submit`); toast('已提交审批'); }
    else toast('草稿已保存');
    location.hash = '#/orders/' + order.id;
  } catch (e) { toast(e.message, true); }
}

/* ── 订单详情 ─────────────────────────────────────────── */
async function openOrder(id) {
  const [o, tl] = await Promise.all([get('/api/orders/' + id), get(`/api/orders/${id}/timeline`).catch(() => [])]);
  $('#panel-title').textContent = o.orderNo;
  const steps = o.approvals.map(a => {
    const cls = a.decision === 'Approved' ? 'Approved' : a.decision === 'Rejected' ? 'Rejected' : a.decision === 'Pending' ? 'Pending' : '';
    const label = a.decision === 'Approved' ? '已批准' : a.decision === 'Rejected' ? '已驳回' : a.decision === 'Pending' ? (a.overdue ? '待审批 · 已超时' : '待审批') : '未开始';
    return `<div class="step ${cls}">
      <div class="n">L${a.level}</div>
      <div class="b"><div class="t">${esc(a.roleLabel)} <span style="font-weight:400;color:var(--muted);font-size:11px">${esc(a.approverUpn)}</span></div>
        <div class="s">${label}${a.decidedAt ? ' · ' + dt(a.decidedAt) : ''}${a.overdue ? ' · <span class="overdue">超时</span>' : ''}</div>
        ${a.comment ? `<div class="cm">${esc(a.comment)}</div>` : ''}
      </div></div>`;
  }).join('');

  const c = o.contract;
  const contractHtml = c ? `
    <div class="contract-box">
      <div class="row" style="margin-bottom:8px">
        <span class="pill ${c.status === 'Archived' ? 'Archived' : c.status === 'Failed' ? 'Rejected' : 'PendingApproval'}">${c.status}</span>
        <span style="font-size:11.5px;color:var(--muted)">v${c.version} · ${(c.sizeBytes / 1024).toFixed(0)} KB</span>
        <div class="spacer"></div>
        ${c.status === 'Archived' ? `<button class="btn sm" onclick="downloadContract('${c.id}')">下载 PDF</button>
        <button class="btn sm" onclick="verifyContract('${c.id}')">校验 SHA256</button>` : ''}
      </div>
      ${c.sha256 ? `<div style="font-size:10.5px;color:var(--muted-2);margin-bottom:3px">SHA256</div><div class="hash">${esc(c.sha256)}</div>` : ''}
      ${c.immutableUntil ? `<div style="font-size:11px;color:var(--muted-2);margin-top:8px">WORM 不可变至 ${dOnly(c.immutableUntil)}</div>` : ''}
      ${c.error ? `<div style="color:var(--red);font-size:11.5px;margin-top:6px">${esc(c.error)}</div>` : ''}
      ${c.sasUrl ? `<div style="font-size:10.5px;color:var(--muted-2);margin-top:8px;word-break:break-all">SAS 直连地址已签发（5 分钟有效）</div>` : ''}
    </div>` : `<div class="empty" style="padding:20px 0">尚未生成合同</div>`;

  $('#panel-body').innerHTML = `
    <div class="dh"><div style="flex:1">
      <div class="no">${esc(o.orderNo)}</div>
      <div class="cust">${esc(o.customerName)} · ${esc(o.customerCode)}</div>
    </div><span class="pill ${o.status}">${esc(o.statusLabel)}</span></div>

    <dl class="kv">
      <dt>订单金额</dt><dd class="num" style="text-align:left;font-size:14px;font-weight:600">${money(o.totalAmount, o.currency)}</dd>
      <dt>审批规则</dt><dd>${esc(o.approvalRule)}</dd>
      <dt>付款条款</dt><dd>${esc(o.terms)}</dd>
      <dt>创建</dt><dd>${esc(o.createdBy)} · ${dt(o.createdAt)}</dd>
      ${o.remark ? `<dt>备注</dt><dd>${esc(o.remark)}</dd>` : ''}
    </dl>

    <div class="sec-t">明细</div>
    <table class="line-items"><thead><tr><th style="width:34px">#</th><th>产品</th><th class="num">数量</th><th class="num">单价</th><th class="num">金额</th></tr></thead>
      <tbody>${o.lines.map(l => `<tr><td class="mono">${l.lineNo}</td><td>${esc(l.productName)}</td><td class="num">${l.qty}</td><td class="num">${money(l.unitPrice)}</td><td class="num">${money(l.amount)}</td></tr>`).join('')}
      <tr class="total-row"><td colspan="4" style="text-align:right;color:var(--muted)">合计</td><td class="num">${money(o.totalAmount)}</td></tr></tbody></table>

    <div class="sec-t">审批链</div>
    <div class="steps">${steps}</div>

    <div class="sec-t">合同</div>
    ${contractHtml}

    <div class="sec-t">审计时间线</div>
    <div class="tl">${tl.length ? tl.map(t => `<div class="tl-item"><div class="tm">${dt(t.at)}</div><div><div class="ac"><b>${esc(t.action)}</b>${t.from ? ` <span style="color:var(--muted-2)">${esc(t.from)} → ${esc(t.to)}</span>` : ''}</div><div class="who">${esc(t.actor)}</div></div></div>`).join('') : '<div class="empty" style="padding:12px">无记录</div>'}</div>

    <div class="row" style="margin-top:20px;border-top:1px solid var(--line);padding-top:14px">
      ${o.canSubmit ? `<button class="btn primary" onclick="doSubmit('${o.id}')">提交审批</button>` : ''}
      ${((o.status === 'Draft' || o.status === 'PendingApproval') && (o.createdBy === S.user.upn || S.user.role === 'admin')) ? `<button class="btn danger" onclick="doCancel('${o.id}')">取消订单</button>` : ''}
      ${o.status === 'Approved' ? `<button class="btn primary" onclick="doGenerate('${o.id}')">生成合同 PDF</button>` : ''}
      ${o.status === 'Contracted' ? `<span style="font-size:12px;color:var(--amber)">合同生成中，异步任务处理中…</span>` : ''}
      ${o.canApprove ? `<div class="spacer"></div>
        <input id="cmt" placeholder="审批意见（可选）" style="background:var(--panel-2);border:1px solid var(--line-2);border-radius:7px;padding:7px 10px;color:var(--text);font-size:12.5px;width:180px">
        <button class="btn ok" onclick="doApprove('${o.id}', true)">批准</button>
        <button class="btn danger" onclick="doApprove('${o.id}', false)">驳回</button>` : ''}
    </div>`;
  $('#panel').classList.remove('hidden');
}

$('#panel-close').onclick = () => { $('#panel').classList.add('hidden'); if (location.hash.startsWith('#/orders/')) location.hash = '#/orders'; };
$('#notif-close').onclick = () => $('#notif-panel').classList.add('hidden');

async function doSubmit(id) { try { await post(`/api/orders/${id}/submit`); toast('已提交，审批链已启动'); openOrder(id); refreshBadge(); } catch (e) { toast(e.message, true); } }
async function doCancel(id) { try { await post(`/api/orders/${id}/cancel`); toast('订单已取消'); openOrder(id); } catch (e) { toast(e.message, true); } }
async function doGenerate(id) { try { await post(`/api/contracts/${id}/generate`); toast('已投递渲染任务'); setTimeout(() => openOrder(id), 1200); } catch (e) { toast(e.message, true); } }

async function doApprove(orderId, approve) {
  const cmt = $('#cmt') ? $('#cmt').value : '';
  try {
    const o = await get('/api/orders/' + orderId);
    const task = await get('/api/approvals/pending');
    const t = task.find(x => x.orderId === orderId);
    if (!t) { toast('没有待你处理的审批任务', true); return; }
    const r = await post(`/api/approvals/${t.taskId}/decision`, { decision: approve ? 'approve' : 'reject', comment: cmt });
    toast(approve ? (r.workflow === 'resumed' ? '已批准 · 工作流已恢复' : '已批准') : '已驳回');
    openOrder(orderId); refreshBadge();
  } catch (e) { toast(e.message, true); }
}

async function downloadContract(cid) {
  try {
    const res = await fetch(S.api + `/api/contracts/${cid}/download`, { headers: { Authorization: 'Bearer ' + S.token } });
    if (!res.ok) throw new Error('下载失败');
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob); a.download = 'contract.pdf'; a.click();
    URL.revokeObjectURL(a.href);
  } catch (e) { toast(e.message, true); }
}

async function verifyContract(cid) {
  try {
    const r = await get(`/api/contracts/${cid}/verify`);
    toast(r.match ? '摘要一致：文件未被篡改' : '摘要不一致！文件可能被替换', !r.match);
  } catch (e) { toast(e.message, true); }
}

/* ── 审批中心 ─────────────────────────────────────────── */
let selected = new Set();
async function viewApprovals() {
  const [list, summary] = await Promise.all([get('/api/approvals/pending'), get('/api/approvals/summary')]);
  selected = new Set();
  $('#view').innerHTML = `
    <h1>审批中心</h1>
    <div class="sub">分级待办：金额越大，审批链越长。审批权限由应用角色判定，与 Azure RBAC 无关</div>
    <div class="grid g3" style="margin-bottom:16px">
      <div class="card stat acc"><div class="k">待我审批</div><div class="v">${summary.pending}</div><div class="d">48 小时 SLA</div></div>
      <div class="card stat"><div class="k">已超时</div><div class="v" style="color:${summary.overdue ? 'var(--red)' : 'var(--text)'}">${summary.overdue}</div><div class="d">需要优先处理</div></div>
      <div class="card stat"><div class="k">近 7 天已处理</div><div class="v">${summary.decided7d}</div><div class="d">含批准与驳回</div></div>
    </div>
    <div class="row" style="margin-bottom:10px"><b style="font-size:13px">待办列表</b><div class="spacer"></div>
      <button class="btn sm" onclick="batchApprove()">批量批准所选</button></div>
    <div id="appr-list">${list.length ? list.map(a => `
      <div class="appr">
        <input type="checkbox" data-task="${a.taskId}" onchange="toggleSel(this)" style="margin-right:2px">
        <div class="l">
          <div class="no">${esc(a.orderNo)} <span class="lvl">L${a.level}/${a.maxLevel}</span></div>
          <div class="meta">${esc(a.customerName)} · ${esc(a.rule)} · 提交 ${dt(a.createdAt)}</div>
        </div>
        <div class="amt">${money(a.totalAmount, a.currency)}</div>
        <div style="text-align:right;width:96px">
          <div style="font-size:10.5px;color:var(--muted-2)">截止</div>
          <div class="${a.overdue ? 'overdue' : ''}" style="font-size:11.5px">${dt(a.dueAt)}</div>
        </div>
        <button class="btn sm" onclick="location.hash='#/orders/${a.orderId}'">查看</button>
      </div>`).join('') : '<div class="empty">没有待处理的审批。切换到审批人账号试试。</div>'}</div>`;
}

function toggleSel(el) {
  if (el.checked) selected.add(el.dataset.task); else selected.delete(el.dataset.task);
}

async function batchApprove() {
  if (!selected.size) return toast('请先勾选待办', true);
  try {
    const r = await post('/api/approvals/batch', { taskIds: [...selected], decision: 'approve', comment: '批量批准（演示）' });
    toast(`已批准 ${r.succeeded} 条`);
    refreshBadge(); route();
  } catch (e) { toast(e.message, true); }
}

/* ── 智能录入 ─────────────────────────────────────────── */
async function viewIntake() {
  const docs = await get('/api/intake');
  $('#view').innerHTML = `
    <h1>智能录入</h1>
    <div class="sub">上传扫描件 → Blob 触发异步抽取 → 生成草稿订单。低置信度字段需人工确认</div>
    <div class="grid g2">
      <div class="card">
        <div class="sec-t" style="margin-top:0">上传扫描件</div>
        <div class="drop" id="drop">
          <div style="font-size:22px;color:var(--muted-2);margin-bottom:6px">⌁</div>
          点击选择文件，或把文件拖进来<br><span style="font-size:11px;color:var(--muted-2)">支持 PDF / PNG / JPG，最大 20MB</span>
        </div>
        <input type="file" id="file" class="hidden">
        <div id="intake-result" style="margin-top:14px"></div>
      </div>
      <div class="card">
        <div class="sec-t" style="margin-top:0">最近上传</div>
        ${docs.length ? docs.map(d => `<div class="fld-row" style="cursor:pointer" onclick="showIntake('${d.id}')">
          <span class="lb" style="width:auto;flex:1;color:var(--text-2)">${esc(d.fileName)}</span>
          <span class="pill ${d.status === 'Extracted' ? 'Approved' : d.status === 'Failed' ? 'Rejected' : 'PendingApproval'}">${d.status}</span>
        </div>`).join('') : '<div class="empty" style="padding:20px 0">还没有上传记录</div>'}
      </div>
    </div>`;

  $('#drop').onclick = () => $('#file').click();
  $('#file').onchange = (e) => uploadIntake(e.target.files[0]);
  $('#drop').ondragover = (e) => { e.preventDefault(); $('#drop').style.borderColor = 'var(--accent)'; };
  $('#drop').ondragleave = () => { $('#drop').style.borderColor = ''; };
  $('#drop').ondrop = (e) => { e.preventDefault(); $('#drop').style.borderColor = ''; uploadIntake(e.dataTransfer.files[0]); };
}

async function uploadIntake(file) {
  if (!file) return;
  const fd = new FormData(); fd.append('file', file, file.name);
  try {
    const r = await api('/api/intake/upload', { method: 'POST', body: fd });
    toast('已上传，正在后台抽取…');
    setTimeout(() => showIntake(r.id), 1400);
  } catch (e) { toast(e.message, true); }
}

async function showIntake(id) {
  const d = await get('/api/intake/' + id);
  $('#intake-result').innerHTML = `
    <div style="font-size:12.5px;margin-bottom:8px"><b>${esc(d.fileName)}</b>
      <span class="pill ${d.status === 'Extracted' ? 'Approved' : 'PendingApproval'}" style="margin-left:6px">${d.status}</span></div>
    ${d.fields.map(f => `<div class="fld-row">
      <span class="lb">${esc(f.label)}</span>
      <span class="vl">${esc(f.value)}</span>
      <span class="conf ${f.confidence >= 0.8 ? 'hi' : 'lo'}">${(f.confidence * 100).toFixed(0)}%</span>
    </div>`).join('')}
    ${d.draftOrderId ? `<button class="btn primary sm" style="margin-top:12px" onclick="location.hash='#/orders/${d.draftOrderId}'">打开生成的草稿订单</button>` : '<div style="color:var(--muted);font-size:12px;margin-top:10px">处理中…</div>'}
    ${d.error ? `<div style="color:var(--red);font-size:12px;margin-top:8px">${esc(d.error)}</div>` : ''}`;
}

/* ── 运维看板 ─────────────────────────────────────────── */
async function viewOps() {
  const s = await get('/api/ops/summary');
  const bad = s.dependencies.filter(d => !d.ok).length;
  $('#view').innerHTML = `
    <h1>运维看板</h1>
    <div class="sub">这一页就是"可观测性"课上要展示的内容：依赖、模式、链路</div>
    <div class="grid g4" style="margin-bottom:16px">
      <div class="card stat ${bad ? '' : 'acc'}"><div class="k">环境</div><div class="v" style="font-size:17px">${esc(s.environment)}</div><div class="d">${esc(s.region)}</div></div>
      <div class="card stat"><div class="k">依赖健康</div><div class="v" style="color:${bad ? 'var(--red)' : 'var(--green)'}">${s.dependencies.length - bad}/${s.dependencies.length}</div><div class="d">${bad ? bad + ' 项异常' : '全部正常'}</div></div>
      <div class="card stat"><div class="k">待审批</div><div class="v">${s.pendingApprovals}</div><div class="d">全系统</div></div>
      <div class="card stat"><div class="k">已归档合同</div><div class="v">${s.archivedContracts}</div><div class="d">WORM</div></div>
    </div>
    <div class="grid g2">
      <div class="card"><div class="sec-t" style="margin-top:0">依赖探针</div>
        ${s.dependencies.map(d => `<div class="dep"><span class="st ${d.ok ? 'ok' : 'bad'}"></span>
          <div style="flex:1"><div>${esc(d.name)}</div><div class="kd">${esc(d.kind)} · ${esc(d.detail)}</div></div>
          <span class="ms">${d.latencyMs}ms</span></div>`).join('')}
        <button class="btn sm" style="margin-top:12px" onclick="route()">重新探测</button>
      </div>
      <div class="card"><div class="sec-t" style="margin-top:0">运行模式</div>
        ${Object.entries(s.modes).map(([k, v]) => `<div class="dep"><span class="kd" style="width:80px">${esc(k)}</span>
          <span style="flex:1;font-family:var(--mono);font-size:11.8px;color:var(--text-2)">${esc(v)}</span></div>`).join('')}
        <div style="margin-top:12px;font-size:11.5px;color:var(--muted-2);line-height:1.7">
          服务器时间 ${new Date(s.serverTime).toLocaleString('zh-CN')}<br>
          版本 ${esc(s.version)}
        </div>
      </div>
    </div>`;
}

/* ── 通知 ─────────────────────────────────────────────── */
$('#notif-btn').onclick = async () => {
  const panel = $('#notif-panel');
  if (!panel.classList.contains('hidden')) { panel.classList.add('hidden'); return; }
  try {
    const list = await get('/api/notifications');
    $('#notif-list').innerHTML = list.length ? list.map(n => `
      <div class="tl-item" style="padding:9px 0;border-bottom:1px solid #1A1D22">
        <div style="flex:1"><div style="font-weight:600;font-size:12.5px">${esc(n.title)}</div>
        <div style="color:var(--muted);font-size:11.5px;margin-top:2px">${esc(n.body)}</div>
        <div style="color:var(--muted-2);font-size:10.5px;margin-top:3px">${dt(n.createdAt)}</div></div>
      </div>`).join('') : '<div class="empty">暂无通知</div>';
    panel.classList.remove('hidden');
  } catch (e) { toast(e.message, true); }
};

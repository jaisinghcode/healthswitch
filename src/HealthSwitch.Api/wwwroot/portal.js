// One website per organisation (/palmgrove, /riverside, /northstar, /sapphire, /switch, /nhia).
// Each signs in separately and only sees its own organisation's data; everything that crosses
// organisations goes through the HealthSwitch gateway and queues.
(() => {
'use strict';
const $ = s => document.querySelector(s);
const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const naira = n => '₦' + Math.round(n || 0).toLocaleString('en-NG');
const pad = n => String(n).padStart(2, '0');
const clock = iso => { const d = new Date(iso); return pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()); };
const dateOnly = iso => new Date(iso).toLocaleDateString('en-GB');
const when = iso => { const d = new Date(iso); return (Date.now() - d < 864e5 ? 'Today ' : dateOnly(iso) + ' ') + pad(d.getHours()) + ':' + pad(d.getMinutes()); };
const fsize = n => n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : (n / 1048576).toFixed(1) + ' MB';
const initials = s => s.replace(/^Dr\.?\s+/, '').split(/\s+/).map(w => w[0]).slice(0, 2).join('').toUpperCase();
let toastTimer;
function toast(msg) { const t = $('#toast'); t.textContent = msg; t.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => t.classList.remove('show'), 3500); }

// ---------- which website is this ----------
const SLUG = (location.pathname.split('/').filter(Boolean)[0] || '').toLowerCase();
const B = window.HS_BRANDS[SLUG];
if (!B) { location.href = '/'; return; }
document.documentElement.dataset.portal = SLUG;
document.body.classList.add(B.layout);
document.title = B.name + ' · ' + B.app;
const MARK = window.HS_MARKS[B.mark];
const ROLE = B.role;

// ---------- storage and API ----------
const store = { get: k => { try { return localStorage.getItem(k) || ''; } catch (e) { return ''; } }, set: (k, v) => { try { v ? localStorage.setItem(k, v) : localStorage.removeItem(k); } catch (e) {} } };
let KEY = store.get('hs-key'), TOKEN = store.get('hs-token-' + SLUG), keyRequired = false;
class AuthError extends Error {}
async function http(method, url, body, isForm) {
  const headers = {};
  if (KEY) headers['X-Demo-Key'] = KEY;
  if (TOKEN) headers.Authorization = 'Bearer ' + TOKEN;
  if (body && !isForm) headers['Content-Type'] = 'application/json';
  const res = await fetch(url, { method, headers, body: body ? (isForm ? body : JSON.stringify(body)) : undefined });
  const text = await res.text(); let data = null; try { data = text ? JSON.parse(text) : null; } catch (e) {}
  const err = data && data.error;
  if (res.status === 401 && err && err.code === 'ERR_ACCESS_DENIED') { KEY = ''; store.set('hs-key', ''); stopApp(); showLogin('The demo access key is missing or wrong.'); throw new AuthError('key'); }
  if (res.status === 401 && err && err.code === 'ERR_LOGIN_REQUIRED') { TOKEN = ''; store.set('hs-token-' + SLUG, ''); stopApp(); showLogin('Your session ended. Please sign in again.'); throw new AuthError('login'); }
  if (!res.ok) throw new Error((err && err.message) || ('Request failed (' + res.status + ')'));
  return data;
}
const get = u => http('GET', u), post = (u, b) => http('POST', u, b || {});
const fileUrl = id => '/api/attachments/' + id + '?token=' + encodeURIComponent(TOKEN) + (KEY ? '&key=' + encodeURIComponent(KEY) : '');

// ---------- state ----------
const TABS = {
  hospital: [['submissions', 'Submissions'], ['new', 'New request'], ['recon', 'Payments']],
  payer: [['inbox', 'Review inbox'], ['claims', 'All claims'], ['payments', 'Payments']],
  switch: [['traffic', 'Message flow']],
  regulator: [['overview', 'Overview']]
};
const newDraft = () => ({ lookup: '', member: null, corr: null, elig: null, consent: false, dx: 'K35.8', lines: [{ code: 'CONS-01', amount: 15000 }] });
const S = {
  tab: TABS[ROLE][0][0], selClaim: null, selPayerClaim: null, selHist: null,
  filters: { q: '', payer: 'all', status: 'all' }, hq: '',
  draft: newDraft(), review: {}, upload: {}, batchHospital: null, batchSel: {}, resetArmed: false, transport: ''
};
const D = { ref: null, claims: [], queue: [], hist: [], hLedger: null, pLedger: null, batches: [], sw: null, nhia: null };
const ORG = () => D.ref.org.code;
const payerOf = code => D.ref.payers.find(p => p.code === code) || { name: code, mode: 'portal' };
const isSapphire = SLUG === 'sapphire';

// ---------- loading ----------
async function load() {
  const jobs = [];
  if (ROLE === 'hospital') {
    if (S.tab === 'submissions') jobs.push(get('/api/claims').then(r => { D.claims = r; }));
    if (S.tab === 'recon') jobs.push(get('/api/hospitals/' + ORG() + '/ledger').then(r => { D.hLedger = r; }));
    if (S.tab === 'new' && S.draft.corr && S.draft.elig && S.draft.elig.state === 'waiting')
      jobs.push(get('/api/eligibility/' + S.draft.corr).then(r => { if (r.status !== 'waiting') S.draft.elig = { state: r.status, via: r.via }; }));
  } else if (ROLE === 'payer') {
    jobs.push(get('/api/claims?awaiting=true').then(r => { D.queue = r; }));
    if (S.tab === 'claims') jobs.push(get('/api/claims').then(r => { D.hist = r; }));
    if (S.tab === 'payments') {
      jobs.push(get('/api/payers/' + ORG() + '/ledger' + (S.batchHospital ? '?hospital=' + S.batchHospital : '')).then(r => { D.pLedger = r; }));
      jobs.push(get('/api/payers/' + ORG() + '/batches').then(r => { D.batches = r; }));
    }
    if (isSapphire) jobs.push(get('/api/reference').then(r => { D.ref.org = r.org; }));
  } else if (ROLE === 'switch') jobs.push(get('/api/switch/overview').then(r => { D.sw = r; }));
  else jobs.push(get('/api/nhia/summary').then(r => { D.nhia = r; }));
  await Promise.all(jobs);
}
let loading = false, lastSig = '', timer = null;
async function refresh() {
  if (loading || !D.ref) return; loading = true;
  try {
    await load(); setConn(true);
    const sig = JSON.stringify([S.tab, D.claims, D.queue, D.hist, D.hLedger, D.pLedger, D.batches, D.sw, D.nhia, S.draft.elig, D.ref.org]);
    if (sig !== lastSig) { lastSig = sig; render(); }
  } catch (e) { if (!(e instanceof AuthError)) setConn(false, e.message); } finally { loading = false; }
}
function setConn(ok, msg) {
  const c = $('#conn'); if (!c) return;
  c.className = 'live-dot' + (ok ? '' : ' off');
  c.innerHTML = '<i></i>' + (ok ? 'Connected' : esc(msg || 'Reconnecting…'));
}

// ---------- shared bits ----------
const pill = (t, k) => `<span class="pill p-${k === 'paid' ? 'paid' : k}">${esc(t)}</span>`;
const deliveryPill = d => ({ sending: pill('Sending', 'info'), queued: pill('Queued at switch', 'info'), retrying: pill('Waiting for payer (retrying)', 'warn'), dead: pill('Not delivered', 'bad') }[d] || pill('Delivered', 'neutral'));
const itemPill = i => i.status === 'approved' ? pill(i.approved < i.requested ? 'Approved at ' + naira(i.approved) : 'Approved', 'ok') : i.status === 'declined' ? pill('Declined', 'bad') : i.status === 'query' ? pill('Info requested', 'warn') : pill('Pending', 'neutral');
function filesHtml(files, opts) {
  opts = opts || {};
  return `<div class="files">${files.map((f, k) => {
    const isImg = (f.contentType || f.type || '').startsWith('image/');
    const src = f.local ? f.url : fileUrl(f.id);
    return `<span class="file">${isImg ? `<img src="${src}" alt="">` : `<span class="fi">${esc((f.fileName.split('.').pop() || 'FILE').slice(0, 4).toUpperCase())}</span>`}<span><b>${esc(f.fileName)}</b><br><span class="muted">${fsize(f.size)}</span></span>${f.local ? '' : `<button class="btn quiet sm" type="button" data-preview="${f.id}" data-img="${isImg}" data-name="${esc(f.fileName)}">View</button>`}${opts.removable ? `<button class="btn quiet sm" type="button" data-unfile="${k}" data-c="${opts.cid}" data-l="${opts.lid}">Remove</button>` : ''}</span>`;
  }).join('')}</div>`;
}
function remarksHtml(c) {
  return c.remarks.length ? `<div style="display:grid;gap:6px"><div class="lbl">Reviewer remarks</div>${c.remarks.map(r => `<div class="remark"><span class="muted" style="font-size:11.5px">${esc(r.by)} · ${clock(r.at)}</span><br>${esc(r.text)}</div>`).join('')}</div>` : '';
}
const timelineHtml = c => `<div><div class="lbl" style="margin-bottom:6px">Timeline</div><div class="timeline">${c.events.map(e => `<div class="tl"><time>${clock(e.at)}</time><span>${esc(e.text)}</span></div>`).join('')}</div></div>`;
const decidedBy = c => c.awaitingPayer ? 'In review inbox' : c.remarks.some(r => /automated rules/.test(r.by)) ? 'Rules engine' : c.rollup === 'pending' ? '–' : 'Reviewer';

// ---------- login ----------
async function showLogin(msg) {
  const portalsReq = KEY || !keyRequired ? fetch('/api/portals', { headers: KEY ? { 'X-Demo-Key': KEY } : {} }).then(r => r.ok ? r.json() : null).catch(() => null) : Promise.resolve(null);
  const me = (await portalsReq || []).find(p => p.slug === SLUG);
  const needKey = keyRequired && !KEY;
  $('#app').innerHTML = `<div class="login">
    <section class="login-brand"><div>
      <div class="logo"><span class="logo-mark">${MARK}</span><span><b>${esc(B.name)}</b><small>${esc(B.app)}</small></span></div>
      <h1>${esc(B.app)}</h1><p class="kind">${esc(me ? me.kind : B.name)}</p></div>
      <p class="blurb">${esc(me ? me.blurb : '')}</p>
      <p class="fine">Fictional organisation in the HealthSwitch demo. <a href="/" style="color:inherit">All demo websites</a></p>
    </section>
    <section class="login-side"><div class="login-card">
      <h2>Sign in</h2>
      <form id="login-form" autocomplete="off">
        ${needKey ? `<div class="field"><label for="li-key">Demo access key</label><input id="li-key" type="text" required></div>` : ''}
        <div class="field"><label for="li-user">Work email</label><input id="li-user" type="email" required autocomplete="username"></div>
        <div class="field"><label for="li-pass">Password</label><input id="li-pass" type="password" required autocomplete="current-password"></div>
        <p class="err" id="li-err">${esc(msg || '')}</p>
        <button class="btn" type="submit" id="li-go">Sign in</button>
      </form>
      ${me ? `<div class="demo-cred"><b>Demo login</b><span class="mono">${esc(me.userName)} · ${esc(me.password)}</span><span class="note">${esc(me.userDisplay)}, ${esc(me.userTitle.toLowerCase())}</span><div><button class="btn ghost sm" type="button" id="li-fill" data-u="${esc(me.userName)}" data-p="${esc(me.password)}">Use demo login</button></div></div>`
        : `<div class="demo-cred note">The demo logins are listed on the <a href="/">HealthSwitch demo home page</a>.</div>`}
    </div></section></div>`;
  const first = $(needKey ? '#li-key' : '#li-user'); if (first) first.focus();
}
async function doLogin(e) {
  e.preventDefault();
  const btn = $('#li-go'), err = $('#li-err'); btn.classList.add('busy'); err.textContent = '';
  try {
    const k = $('#li-key'); if (k) { KEY = k.value.trim(); store.set('hs-key', KEY); }
    TOKEN = '';
    const r = await post('/api/login', { portal: SLUG, userName: $('#li-user').value, password: $('#li-pass').value });
    TOKEN = r.token; store.set('hs-token-' + SLUG, TOKEN);
    await startApp();
  } catch (ex) { if (!(ex instanceof AuthError)) { err.textContent = ex.message; btn.classList.remove('busy'); } }
}

// ---------- shell ----------
async function startApp() {
  D.ref = await get('/api/reference');
  const me = D.ref.me;
  $('#app').innerHTML = `<div class="shell">
    <header class="apphead"><div class="apphead-inner">
      <a class="logo" href="/${SLUG}"><span class="logo-mark">${MARK}</span><span><b>${esc(B.name)}</b><small>${esc(B.app)}</small></span></a>
      <nav class="nav" id="nav" aria-label="Sections"></nav>
      <div class="userbox"><span id="conn" class="live-dot"><i></i>Connecting…</span><div class="avatar" aria-hidden="true">${esc(initials(me.userDisplay))}</div><div class="u"><b>${esc(me.userDisplay)}</b><small>${esc(me.userTitle)}</small></div><button class="btn quiet sm" type="button" id="logout">Sign out</button></div>
    </div></header>
    <div class="pagehead"><div class="pagehead-inner" id="pagehead"></div></div>
    <main id="main"><div class="empty">Loading…</div></main>
    <footer class="foot"><span>${esc(B.foot)}</span><span>Fictional data · <a href="/">All demo websites</a></span></footer>
  </div>`;
  lastSig = '';
  render(); await refresh();
  clearInterval(timer); timer = setInterval(() => { if (!document.hidden) refresh(); }, 1500);
}
function stopApp() { clearInterval(timer); timer = null; D.ref = null; }

const HEAD = {
  submissions: () => [`Submissions to payers`, `Everything ${B.name} has sent through HealthSwitch, with each payer’s decision.`],
  new: () => ['New pre-authorisation', 'One form for every HMO and state scheme connected to HealthSwitch.'],
  recon: () => ['Payments and reconciliation', 'Approved amounts only, matched automatically when a payer pays.'],
  inbox: () => ['Review inbox', isSapphire ? `Requests Sapphire’s rules engine referred to a medical reviewer (₦${D.ref.sapphireAutoLimit.toLocaleString('en-NG')} and over), and documents sent back by hospitals.` : 'Requests from hospitals, delivered by HealthSwitch. Oldest first.'],
  claims: () => ['All claims', isSapphire ? 'Every request received from hospitals, whether the rules engine or a reviewer decided it.' : 'Every request Northstar has received through HealthSwitch.'],
  payments: () => ['Payments', 'Pay a hospital for many approved claims in one transfer.'],
  traffic: () => ['Message flow', 'Every message between hospitals and payers, live from the gateway, queues and adapters.'],
  overview: () => ['National claims overview', 'Last 30 days, computed from the switch database. No patient-level data.']
};
function headRight() {
  if (ROLE === 'hospital') return `<span class="note">Facility code <span class="mono">${esc(ORG())}</span></span>`;
  if (SLUG === 'northstar') return pill('Uses the HealthSwitch portal', 'info');
  if (isSapphire) return D.ref.org.online ? pill('API connection to HealthSwitch: online', 'ok') : pill('API connection to HealthSwitch: offline', 'bad');
  if (ROLE === 'switch') return `<span class="note">Transport: <b>${esc(D.sw ? D.sw.transport : '')}</b></span>`;
  return pill('Read-only · aggregated', 'ok');
}
function render() {
  if (!D.ref || !$('#main')) return;
  const a = document.activeElement, aid = a && a.id, sel = a && a.selectionStart != null ? [a.selectionStart, a.selectionEnd] : null;
  const openD = [...document.querySelectorAll('details[data-mid][open]')].map(d => d.dataset.mid);
  const tw = document.querySelector('#main .tbl-wrap'); const sx = tw ? tw.scrollLeft : 0;
  const tabs = TABS[ROLE];
  $('#nav').innerHTML = tabs.length > 1 ? tabs.map(([k, l]) => `<button type="button" data-tab="${k}" ${S.tab === k ? 'aria-current="page"' : ''}>${l}${k === 'inbox' && D.queue.length ? ` <span class="badge">${D.queue.length}</span>` : ''}</button>`).join('') : '';
  const [h, p] = HEAD[S.tab]();
  $('#pagehead').innerHTML = `<div><h1>${esc(h)}</h1><p>${esc(p)}</p></div><div>${headRight()}</div>`;
  let html;
  if (ROLE === 'hospital') html = S.tab === 'new' ? viewNew() : S.tab === 'recon' ? viewRecon() : viewSubmissions();
  else if (ROLE === 'payer') html = S.tab === 'claims' ? viewHistory() : S.tab === 'payments' ? viewBatch() : viewInbox();
  else if (ROLE === 'switch') html = viewSwitch();
  else html = viewNhia();
  $('#main').innerHTML = html;
  openD.forEach(id => { const d = document.querySelector('details[data-mid="' + id + '"]'); if (d) d.open = true; });
  const tw2 = document.querySelector('#main .tbl-wrap'); if (tw2) tw2.scrollLeft = sx;
  if (aid) { const el = document.getElementById(aid); if (el) { el.focus({ preventScroll: true }); if (sel && el.setSelectionRange) try { el.setSelectionRange(sel[0], sel[1]); } catch (e) {} } }
}

// ---------- hospital: submissions ----------
function viewSubmissions() {
  const f = S.filters, q = f.q.trim().toLowerCase();
  const rows = D.claims.filter(c => (!q || c.patientName.toLowerCase().includes(q) || c.authNo.toLowerCase().includes(q) || c.memberNo.toLowerCase().includes(q))
    && (f.payer === 'all' || c.payerCode === f.payer) && (f.status === 'all' || c.statusLabel === f.status));
  const sel = rows.find(c => c.id === S.selClaim) || rows[0];
  if (sel) S.selClaim = sel.id;
  const statuses = ['Pending', 'Query raised', 'Approved', 'Partially approved', 'Declined', 'Final claim with payer', 'Approved, unpaid', 'Invoiced', 'Paid'];
  return `<div class="grid-2">
  <section class="card"><div class="card-h"><h2>Requests and claims</h2><button class="btn sm" type="button" data-tab="new">New request</button></div>
    <div class="card-b" style="padding-bottom:0"><div class="row">
      <div class="field"><label for="f-q">Search</label><input id="f-q" type="text" placeholder="Patient, auth no or member ID" value="${esc(f.q)}"></div>
      <div class="field"><label for="f-payer">Payer</label><select id="f-payer"><option value="all">All payers</option>${D.ref.payers.map(p => `<option value="${p.code}" ${f.payer === p.code ? 'selected' : ''}>${esc(p.name)}</option>`).join('')}</select></div>
      <div class="field"><label for="f-status">Status</label><select id="f-status"><option value="all">All statuses</option>${statuses.map(s => `<option ${f.status === s ? 'selected' : ''}>${s}</option>`).join('')}</select></div>
    </div></div>
    <div class="tbl-wrap" style="margin-top:14px"><table><thead><tr><th>Auth no</th><th>Patient</th><th>Payer</th><th class="r">Requested</th><th class="r">Approved</th><th>Status</th><th>Delivery</th></tr></thead>
    <tbody>${rows.length ? rows.map(c => `<tr class="click ${sel && c.id === sel.id ? 'sel' : ''}" tabindex="0" data-claim="${c.id}"><td class="mono">${c.authNo}</td><td>${esc(c.patientName)}</td><td>${esc(c.payerName)}</td><td class="r">${naira(c.requestedTotal)}</td><td class="r">${c.items.some(i => i.approved != null) ? naira(c.approvedTotal) : '<span class="muted">–</span>'}</td><td>${pill(c.statusLabel, c.statusKind)}</td><td>${deliveryPill(c.delivery)}</td></tr>`).join('') : '<tr><td colspan="7"><div class="empty">No submissions match these filters.</div></td></tr>'}</tbody></table></div>
  </section>
  ${sel ? claimDetail(sel) : '<section class="card"><div class="empty">Select a submission.</div></section>'}</div>`;
}
function claimDetail(c) {
  const canFinal = c.stage === 'preauth' && (c.rollup === 'approved' || c.rollup === 'partial');
  const hasQuery = c.stage === 'preauth' && c.rollup === 'query';
  return `<section class="card"><div class="card-h"><div><div class="eyebrow">${esc(c.payerName)} · ${c.payerMode === 'api' ? 'connected by API' : 'decides in the HealthSwitch portal'}</div><h2>${esc(c.patientName)}</h2></div>${pill(c.statusLabel, c.statusKind)}</div>
  <div class="card-b">
    <div class="kv"><div><span>Auth no</span><b class="mono">${c.authNo}</b></div><div><span>Member ID</span><b class="mono">${esc(c.memberNo)}</b></div><div><span>NIN</span><b class="mono">${esc(c.nin.slice(0, 3))}•••••${esc(c.nin.slice(-3))}</b></div>
      <div><span>Diagnosis (ICD-10)</span><b>${esc(c.diagnosis.code + ' ' + c.diagnosis.desc)}</b></div><div><span>Approved total</span><b class="num">${naira(c.approvedTotal)}</b></div><div><span>Delivery</span><b>${deliveryPill(c.delivery)}</b></div></div>
    ${c.delivery === 'retrying' ? '<div class="callout c-warn"><b>The payer’s system is not answering.</b><span>HealthSwitch is holding this request and retrying. You do not need to resend it.</span></div>' : ''}
    ${c.delivery === 'dead' ? '<div class="callout c-bad"><b>Not delivered after every retry.</b><span>HealthSwitch operations has been alerted and can replay it.</span></div>' : ''}
    ${c.awaitingPayer && c.stage === 'preauth' ? `<div class="callout c-info"><b>With ${esc(c.payerName)} for review.</b><span>The decision will appear here as soon as the payer submits it.</span></div>` : ''}
    <div class="items">${c.items.map(i => `<div class="item"><div><div class="name">${esc(i.name)}</div><div class="sub"><span class="mono">${i.code}</span> · requested ${naira(i.requested)}${i.reason ? ' · ' + esc(i.reason) : ''}${i.status === 'query' ? ' · needs ' + esc(i.docType) : ''}</div>${i.attachments.length ? filesHtml(i.attachments) : ''}</div><div>${itemPill(i)}</div></div>`).join('')}</div>
    ${remarksHtml(c)}
    ${hasQuery ? queryBlock(c) : ''}
    ${canFinal ? `<div class="callout c-ok"><b>Treat the approved services, then send the final claim.</b><span>Only approved amounts are payable: ${naira(c.approvedTotal)}.</span><div style="margin-top:6px"><button class="btn sm" type="button" data-final="${c.id}">Submit final claim</button></div></div>` : ''}
    ${c.ledger ? `<div class="note">Ledger entry ${naira(c.ledger.amount)} · ${esc(c.ledger.status)}${c.ledger.batchNo ? ' · ' + esc(c.ledger.batchNo) : ''}</div>` : ''}
    ${timelineHtml(c)}
  </div></section>`;
}
function queryBlock(c) {
  const up = S.upload[c.id] || (S.upload[c.id] = { files: {}, note: '' });
  const q = c.items.filter(i => i.status === 'query');
  const ready = q.every(i => (up.files[i.id] || []).length);
  return `<div class="qbox"><div><b>${esc(c.payerName)} needs more information</b><br><span class="note">Upload the requested documents. They go back through HealthSwitch and the services return to the reviewer.</span></div>
  ${q.map(i => `<div style="display:grid;gap:6px;border-top:1px solid var(--line);padding-top:10px"><div><b>${esc(i.name)}</b> · needs <b>${esc(i.docType)}</b></div>${i.note ? `<div class="remark">${esc(i.note)}</div>` : ''}
    <div class="field"><label for="up-${i.id}">Attach ${esc(i.docType.toLowerCase())} (PDF or image, up to 5 MB)</label><input type="file" id="up-${i.id}" data-up="${i.id}" data-c="${c.id}" accept=".pdf,image/*" multiple></div>
    ${(up.files[i.id] || []).length ? filesHtml(up.files[i.id].map(f => ({ local: true, url: f.url, fileName: f.file.name, size: f.file.size, type: f.file.type })), { removable: true, cid: c.id, lid: i.id }) : '<span class="note">No file attached yet.</span>'}</div>`).join('')}
  <div class="field"><label for="resp-${c.id}">Response note to the reviewer (optional)</label><textarea id="resp-${c.id}" data-resp="${c.id}" rows="2">${esc(up.note)}</textarea></div>
  <div class="row" style="justify-content:flex-end"><button class="btn sm" type="button" data-answer="${c.id}" ${ready ? '' : 'disabled'}>Send documents</button></div></div>`;
}

// ---------- hospital: new request ----------
function viewNew() {
  const d = S.draft, m = d.member, e = d.elig;
  let eligHtml = '';
  if (e) {
    if (e.state === 'notfound') eligHtml = `<div class="callout c-bad"><b>No member found.</b><span>${esc(e.message || 'Check the NIN or member number.')}</span></div>`;
    else if (e.state === 'waiting') eligHtml = `<div class="callout c-info"><b>Checking cover with ${esc(payerOf(m.payerCode).name)}…</b><span>Sent through HealthSwitch as coverageeligibility.check</span></div>`;
    else if (e.state === 'active') eligHtml = `<div class="callout c-ok"><b>Covered: ${esc(m.name)}</b><span>${esc(payerOf(m.payerCode).name)} · ${esc(m.plan)} · member ${esc(m.memberNo)}</span><span class="note">${esc(e.via)}</span>${payerOf(m.payerCode).mode === 'api' ? `<span class="note">${esc(payerOf(m.payerCode).name)}’s system decides requests under ${naira(D.ref.sapphireAutoLimit)} automatically; larger ones go to its medical reviewer.</span>` : ''}</div>`;
    else eligHtml = `<div class="callout c-bad"><b>Not covered: ${esc(m.name)}</b><span>${esc(payerOf(m.payerCode).name)} reports no active cover.</span><span class="note">${esc(e.via)}</span></div>`;
  }
  const total = d.lines.reduce((s, l) => s + (Number(l.amount) || 0), 0), ready = e && e.state === 'active';
  return `<div class="grid-2"><section class="card"><div class="card-h"><h2>Patient, consent and services</h2><span class="note">${esc(D.ref.me.userDisplay)}</span></div><div class="card-b">
    <div class="eyebrow">1 · Patient and cover</div>
    <div class="row"><div class="field"><label for="lookup">NIN or member number</label><input id="lookup" type="text" placeholder="e.g. 52937108264" value="${esc(d.lookup)}"></div><button class="btn" type="button" id="btn-check">Check cover</button></div>
    <div class="chips">${D.ref.members.map(x => `<button class="chip" type="button" data-sample="${x.nin}">${esc(x.name)} · ${esc(payerOf(x.payerCode).name)}${x.active ? '' : ' (expired)'}</button>`).join('')}</div>
    ${eligHtml}
    <div class="eyebrow" style="margin-top:6px">2 · Consent and diagnosis</div>
    <label class="check"><input type="checkbox" id="consent" ${d.consent ? 'checked' : ''}><span>The patient agreed to share these details with their payer for this claim. <span class="muted">Required under the Nigeria Data Protection Act.</span></span></label>
    <div class="field"><label for="dx">Diagnosis (ICD-10)</label><select id="dx">${D.ref.diagnoses.map(x => `<option value="${x.code}" ${d.dx === x.code ? 'selected' : ''}>${x.code} · ${esc(x.desc)}</option>`).join('')}</select></div>
    <div class="eyebrow" style="margin-top:6px">3 · Services</div>
    <div class="items">${d.lines.map((l, i) => `<div class="item"><div class="row" style="gap:8px"><div class="field" style="flex:2 1 200px"><label for="ln-${i}">Service</label><select id="ln-${i}" data-line="${i}">${D.ref.services.map(s => `<option value="${s.code}" ${l.code === s.code ? 'selected' : ''}>${esc(s.name)} (${s.code})</option>`).join('')}</select></div><div class="field" style="flex:1 1 120px"><label for="am-${i}">Amount (₦)</label><input id="am-${i}" type="number" min="0" step="500" class="num" data-amount="${i}" value="${l.amount}"></div></div><div class="acts">${d.lines.length > 1 ? `<button class="btn quiet sm" type="button" data-remove="${i}">Remove</button>` : ''}</div></div>`).join('')}</div>
    <div class="row" style="justify-content:space-between;align-items:center"><button class="btn ghost sm" type="button" id="btn-add">Add service</button><b class="num" id="total">Total ${naira(total)}</b></div>
    <div class="row" style="justify-content:flex-end"><button class="btn" type="button" id="btn-submit" ${ready ? '' : 'disabled'}>Send to ${m ? esc(payerOf(m.payerCode).name) : 'payer'}</button></div>
  </div></section>
  <section class="card"><div class="card-h"><h2>What happens when you send</h2></div><div class="card-b"><div class="timeline">
    <div class="tl"><time>1</time><span>HealthSwitch checks the request, saves it and replies <b>202 Accepted</b> at once.</span></div>
    <div class="tl"><time>2</time><span>It is queued and routed to the payer: straight into Sapphire’s own system by API, or into Northstar’s review portal.</span></div>
    <div class="tl"><time>3</time><span>The payer decides each service. The answer comes back through the same queues and appears under Submissions.</span></div>
  </div><p class="note">Sent as a FHIR R4 Claim bundle. You only see ${esc(B.name)}’s own patients and claims.</p></div></section></div>`;
}

// ---------- hospital: payments ----------
function viewRecon() {
  const r = D.hLedger; if (!r) return '<div class="empty">Loading…</div>';
  const total = r.outstanding.reduce((s, x) => s + x.amount, 0), count = r.outstanding.reduce((s, x) => s + x.count, 0);
  return `<div class="grid-4"><div class="tile accent"><span class="l">Owed by all payers</span><span class="v">${naira(total)}</span><span class="l">${count} approved claims not yet paid</span></div>
  ${D.ref.payers.map(p => { const o = r.outstanding.find(x => x.payerCode === p.code) || { amount: 0, count: 0 }; return `<div class="tile"><span class="l">${esc(p.name)}</span><span class="v">${naira(o.amount)}</span><span class="l">${o.count} not yet paid</span></div>`; }).join('')}</div>
  <section class="card"><div class="card-h"><h2>Ledger: approved amounts only</h2></div><div class="tbl-wrap"><table><thead><tr><th>Auth no</th><th>Patient</th><th>Payer</th><th class="r">Approved</th><th>Finalised</th><th>Batch</th><th>Payment</th></tr></thead>
  <tbody>${r.entries.length ? r.entries.map(l => `<tr><td class="mono">${l.authNo}</td><td>${esc(l.patientName)}</td><td>${esc(l.payerName)}</td><td class="r">${naira(l.amount)}</td><td class="mono">${dateOnly(l.finalizedAt)}</td><td class="mono">${esc(l.batchNo || '–')}</td><td>${l.status === 'paid' ? pill(l.batchNo ? 'Paid, matched automatically' : 'Paid', 'paid') : l.status === 'invoiced' ? pill('Invoiced', 'info') : pill('Unpaid', 'warn')}</td></tr>`).join('') : '<tr><td colspan="7"><div class="empty">No approved claims yet.</div></td></tr>'}</tbody></table></div></section>`;
}

// ---------- payer: inbox ----------
function viewInbox() {
  const rows = D.queue, sel = rows.find(c => c.id === S.selPayerClaim) || rows[0];
  if (sel) S.selPayerClaim = sel.id;
  const intro = isSapphire
    ? `<div class="callout c-info"><b>Rules engine: requests under ${naira(D.ref.sapphireAutoLimit)} are decided automatically.</b><span>Each service is approved up to the agreed tariff within seconds, with no reviewer. Larger requests, and documents sent back after a query, wait here for a medical reviewer.</span></div>`
    : `<div class="callout c-info"><b>Northstar has no claims system of its own.</b><span>HealthSwitch delivers every request from every hospital into this inbox. Decisions go back to the hospital through the switch.</span></div>`;
  return `${intro}<div class="grid-2"><section class="card"><div class="card-h"><h2>Waiting for a decision</h2><span class="note">Oldest first</span></div>
  <div class="tbl-wrap"><table><thead><tr><th>Auth no</th><th>Hospital</th><th>Patient</th><th>Type</th><th class="r">Amount</th></tr></thead>
  <tbody>${rows.length ? rows.map(c => `<tr class="click ${sel && sel.id === c.id ? 'sel' : ''}" tabindex="0" data-pclaim="${c.id}"><td class="mono">${c.authNo}</td><td>${esc(c.hospitalShort)}</td><td>${esc(c.patientName)}</td><td>${c.stage === 'claim' ? pill('Final claim', 'info') : c.items.some(i => i.attachments.length && i.status === 'pending') ? pill('Documents in', 'warn') : pill('Pre-auth', 'neutral')}</td><td class="r">${naira(c.stage === 'claim' ? c.approvedTotal : c.requestedTotal)}</td></tr>`).join('') : '<tr><td colspan="5"><div class="empty">Nothing waiting. New requests appear as soon as HealthSwitch delivers them.</div></td></tr>'}</tbody></table></div></section>
  ${sel ? reviewDetail(sel) : '<section class="card"><div class="empty">Select a request to review it.</div></section>'}</div>`;
}
function reviewDetail(c) {
  const dr = S.review[c.id] || (S.review[c.id] = { lines: {}, remark: '' });
  if (c.stage === 'claim') return `<section class="card"><div class="card-h"><div><div class="eyebrow">Final claim · ${esc(c.hospitalName)}</div><h2>${esc(c.patientName)}</h2></div><span class="mono">${c.authNo}</span></div><div class="card-b">
    <div class="items">${c.items.map(i => `<div class="item"><div><div class="name">${esc(i.name)}</div><div class="sub">approved at pre-auth ${naira(i.approved)}</div></div><div>${itemPill(i)}</div></div>`).join('')}</div>
    <p>The final claim matches the approved pre-authorisation: <b>${naira(c.approvedTotal)}</b>. Approving it creates the ledger entry ${esc(c.hospitalShort)} also sees.</p>
    <div class="row" style="justify-content:flex-end"><button class="btn bad" type="button" data-fdec="no" data-c="${c.id}">Decline claim</button><button class="btn" type="button" data-fdec="yes" data-c="${c.id}">Approve claim</button></div></div></section>`;
  return `<section class="card"><div class="card-h"><div><div class="eyebrow">Pre-authorisation · ${esc(c.hospitalName)}</div><h2>${esc(c.patientName)}</h2></div><span class="mono">${c.authNo}</span></div><div class="card-b">
    <div class="kv"><div><span>Member</span><b class="mono">${esc(c.memberNo)}</b></div><div><span>Diagnosis</span><b>${esc(c.diagnosis.code + ' ' + c.diagnosis.desc)}</b></div><div><span>Consent</span><b class="mono">${esc(c.consentRef)}</b></div><div><span>Received</span><b>${when(c.submittedAt)}</b></div></div>
    <div class="items">${c.items.map(i => {
      const over = i.requested - i.tariff, d = dr.lines[i.id] || {}, decided = i.status === 'approved' || i.status === 'declined';
      return `<div class="item"><div><div class="name">${esc(i.name)}</div><div class="sub">requested ${naira(i.requested)} · tariff ${naira(i.tariff)}${over > 0 ? ` · <b style="color:var(--warn)">${naira(over)} above tariff</b>` : ''}${i.reason ? ' · ' + esc(i.reason) : ''}</div></div>
      <div class="acts">${decided ? itemPill(i) : `
        <button class="btn sm ${d.decision === 'approve' ? '' : 'ghost'}" type="button" data-dec="approve" data-c="${c.id}" data-l="${i.id}">Approve</button>
        <button class="btn sm ${d.decision === 'decline' ? 'bad' : 'quiet'}" type="button" data-dec="decline" data-c="${c.id}" data-l="${i.id}">Decline</button>
        <button class="btn sm ${d.decision === 'query' ? 'warn' : 'quiet'}" type="button" data-dec="query" data-c="${c.id}" data-l="${i.id}">Request info</button>
        ${d.decision === 'decline' ? `<select aria-label="Decline reason" data-reason="${i.id}" data-c="${c.id}">${D.ref.declineReasons.map(r => `<option ${d.reason === r ? 'selected' : ''}>${r}</option>`).join('')}</select>` : ''}
        ${d.decision === 'query' ? `<select aria-label="Document needed" data-doc="${i.id}" data-c="${c.id}">${D.ref.docTypes.map(r => `<option ${d.docType === r ? 'selected' : ''}>${r}</option>`).join('')}</select><input type="text" id="qn-${i.id}" data-qnote="${i.id}" data-c="${c.id}" placeholder="Note to the hospital" value="${esc(d.note || '')}" style="width:220px;padding:4px 8px;font-size:12px">` : ''}`}
      </div>${i.attachments.length ? `<div style="grid-column:1 / -1">${i.response ? `<div class="remark" style="border-color:var(--info);background:var(--info-soft)">${esc(c.hospitalShort)}: ${esc(i.response)}</div>` : ''}${filesHtml(i.attachments)}</div>` : ''}</div>`; }).join('')}</div>
    ${remarksHtml(c)}
    <div class="field"><label for="rm-${c.id}">Review remark (shared with ${esc(c.hospitalShort)}, kept in the audit trail)</label><textarea id="rm-${c.id}" data-remark="${c.id}" rows="2">${esc(dr.remark)}</textarea></div>
    <div class="row" style="justify-content:flex-end"><button class="btn" type="button" data-submit-review="${c.id}">Submit decision</button></div></div></section>`;
}

// ---------- payer: all claims ----------
function viewHistory() {
  const q = S.hq.trim().toLowerCase();
  const all = D.hist, rows = all.filter(c => !q || c.patientName.toLowerCase().includes(q) || c.authNo.toLowerCase().includes(q) || c.hospitalName.toLowerCase().includes(q));
  const sel = rows.find(c => c.id === S.selHist) || rows[0];
  if (sel) S.selHist = sel.id;
  const auto = all.filter(c => decidedBy(c) === 'Rules engine').length, byReviewer = all.filter(c => decidedBy(c) === 'Reviewer').length;
  const hospitals = new Set(all.map(c => c.hospitalCode)).size;
  return `<div class="grid-4"><div class="tile accent"><span class="l">Requests received</span><span class="v">${all.length}</span><span class="l">from ${hospitals} hospitals</span></div>
    <div class="tile"><span class="l">Waiting in inbox</span><span class="v">${D.queue.length}</span><span class="l">need a reviewer</span></div>
    ${isSapphire ? `<div class="tile"><span class="l">Decided by the rules engine</span><span class="v">${auto}</span><span class="l">under ${naira(D.ref.sapphireAutoLimit)}, in seconds</span></div>` : `<div class="tile"><span class="l">Approved value</span><span class="v">${naira(all.reduce((s, c) => s + c.approvedTotal, 0))}</span><span class="l">across these requests</span></div>`}
    <div class="tile"><span class="l">Decided by a reviewer</span><span class="v">${byReviewer}</span><span class="l">in this portal</span></div></div>
  <div class="grid-2"><section class="card"><div class="card-h"><h2>Requests and claims</h2><div class="field" style="min-width:220px"><label for="h-q" class="sr">Search</label><input id="h-q" type="text" placeholder="Search patient, hospital or auth no" value="${esc(S.hq)}"></div></div>
    <div class="tbl-wrap"><table><thead><tr><th>Auth no</th><th>Hospital</th><th>Patient</th><th>Received</th><th class="r">Requested</th><th class="r">Approved</th><th>Status</th><th>Decided by</th></tr></thead>
    <tbody>${rows.length ? rows.map(c => `<tr class="click ${sel && sel.id === c.id ? 'sel' : ''}" tabindex="0" data-hclaim="${c.id}"><td class="mono">${c.authNo}</td><td>${esc(c.hospitalShort)}</td><td>${esc(c.patientName)}</td><td class="mono">${when(c.submittedAt)}</td><td class="r">${naira(c.requestedTotal)}</td><td class="r">${c.items.some(i => i.approved != null) ? naira(c.approvedTotal) : '<span class="muted">–</span>'}</td><td>${c.awaitingPayer ? pill('Waiting for you', 'warn') : pill(c.statusLabel, c.statusKind)}</td><td>${esc(decidedBy(c))}</td></tr>`).join('') : '<tr><td colspan="8"><div class="empty">No requests yet.</div></td></tr>'}</tbody></table></div></section>
  ${sel ? `<section class="card"><div class="card-h"><div><div class="eyebrow">${esc(sel.hospitalName)}</div><h2>${esc(sel.patientName)}</h2></div>${sel.awaitingPayer ? `<button class="btn sm" type="button" data-open-inbox="${sel.id}">Open in inbox</button>` : pill(sel.statusLabel, sel.statusKind)}</div><div class="card-b">
    <div class="kv"><div><span>Auth no</span><b class="mono">${sel.authNo}</b></div><div><span>Member</span><b class="mono">${esc(sel.memberNo)}</b></div><div><span>Diagnosis</span><b>${esc(sel.diagnosis.code + ' ' + sel.diagnosis.desc)}</b></div><div><span>Decided by</span><b>${esc(decidedBy(sel))}</b></div></div>
    <div class="items">${sel.items.map(i => `<div class="item"><div><div class="name">${esc(i.name)}</div><div class="sub">requested ${naira(i.requested)} · tariff ${naira(i.tariff)}${i.reason ? ' · ' + esc(i.reason) : ''}</div>${i.attachments.length ? filesHtml(i.attachments) : ''}</div><div>${itemPill(i)}</div></div>`).join('')}</div>
    ${remarksHtml(sel)}${timelineHtml(sel)}</div></section>` : '<section class="card"><div class="empty">Select a request.</div></section>'}</div>`;
}

// ---------- payer: payments ----------
function viewBatch() {
  const r = D.pLedger; if (!r) return '<div class="empty">Loading…</div>';
  const hosps = r.hospitals;
  if (!S.batchHospital && hosps[0]) { S.batchHospital = hosps[0].code; setTimeout(refresh, 0); }
  const rows = S.batchHospital ? r.entries.filter(l => l.hospitalCode === S.batchHospital) : [];
  const chosen = rows.filter(l => S.batchSel[l.authNo]), selTotal = chosen.reduce((s, l) => s + l.amount, 0);
  return `<div class="grid-2"><section class="card"><div class="card-h"><h2>Pay a hospital in one transfer</h2><span class="note">${esc(B.name)} · finance</span></div><div class="card-b">
    ${hosps.length ? `<div class="field"><label for="bh">Hospital (one per batch)</label><select id="bh">${hosps.map(h => `<option value="${h.code}" ${S.batchHospital === h.code ? 'selected' : ''}>${esc(h.name)}</option>`).join('')}</select></div>` : ''}
    <div class="tbl-wrap"><table><thead><tr><th><span class="sr">Select</span></th><th>Auth no</th><th>Patient</th><th class="r">Approved</th><th>Finalised</th></tr></thead>
    <tbody>${rows.length ? rows.map(l => `<tr><td><input type="checkbox" id="b-${l.authNo}" data-bsel="${l.authNo}" ${S.batchSel[l.authNo] ? 'checked' : ''} aria-label="Select ${l.authNo}" style="accent-color:var(--accent)"></td><td class="mono">${l.authNo}</td><td>${esc(l.patientName)}</td><td class="r">${naira(l.amount)}</td><td class="mono">${dateOnly(l.finalizedAt)}</td></tr>`).join('') : '<tr><td colspan="5"><div class="empty">No approved, unpaid claims.</div></td></tr>'}</tbody></table></div>
    <div class="row" style="justify-content:space-between;align-items:center"><span class="num">${chosen.length} selected · <b>${naira(selTotal)}</b></span><button class="btn" type="button" id="btn-batch" ${chosen.length ? '' : 'disabled'}>Generate batch invoice</button></div>
  </div></section>
  <section class="card"><div class="card-h"><h2>Batch invoices</h2></div><div class="card-b">${D.batches.length ? D.batches.map(b => `<div class="card" style="padding:14px;display:grid;gap:10px">
    <div class="row" style="justify-content:space-between;align-items:center"><b class="mono">${b.batchNo}</b>${b.status === 'paid' ? pill('Paid', 'paid') : b.status === 'confirming' ? pill('Confirming payment', 'info') : b.status === 'exception' ? pill('Exception', 'bad') : pill('Awaiting payment', 'warn')}</div>
    <div class="kv"><div><span>Hospital</span><b>${esc(b.hospitalName)}</b></div><div><span>Amount due</span><b class="num">${naira(b.total)}</b></div><div><span>Pay to account</span><b class="mono">${b.virtualAccount}</b></div><div><span>Bank</span><b>${esc(b.bank)}</b></div><div><span>Expires</span><b>${dateOnly(b.expiresAt)}</b></div><div><span>Claims</span><b class="mono">${b.authNos.join(', ')}</b></div></div>
    ${b.status === 'pending' ? `<div class="row" style="justify-content:space-between;align-items:center"><span class="note">Sandbox: plays the payment processor and calls the signed webhook.</span><button class="btn sm" type="button" data-pay="${b.batchNo}">Simulate payment received</button></div>` : ''}
  </div>`).join('') : '<div class="empty">No batches yet.</div>'}</div></section></div>`;
}

// ---------- switch console ----------
function viewSwitch() {
  const w = D.sw; if (!w) return '<div class="empty">Loading…</div>';
  const n = st => (w.stages.find(x => x.stage === st) || { count: 0 }).count;
  const stages = [['q.route', n('q.route'), 'waiting for router'], ['q.deliver.api', n('q.deliver.api'), 'to API payers'], ['q.deliver.portal', n('q.deliver.portal'), 'to portal inboxes'], ['q.retry', n('q.retry'), 'waiting to retry'], ['q.dead', w.dead.length, 'need attention']];
  const statusPill = m => m.status === 'delivered' ? pill('Delivered', 'ok') : m.status === 'retrying' ? pill('Retrying', 'warn') : m.status === 'dead' ? pill('Dead-lettered', 'bad') : pill(m.status === 'queued' ? 'Queued' : 'Dispatched', 'info');
  return `<section class="card"><div class="card-h"><h2>Messages in flight</h2><span class="note">${w.deliveredLast12h} delivered in the last 12 h</span></div><div class="card-b">
    <div class="pipe" style="grid-template-columns:repeat(5,minmax(0,1fr))">${stages.map(([k, c, d]) => `<div class="stage ${k === 'q.retry' ? 'retry' : ''} ${k === 'q.dead' ? 'dead' : ''} ${c ? 'has hot' : ''}"><span class="q">${k}</span><span class="n">${c}</span><span class="d">${d}</span></div>`).join('')}</div>
    <p class="note">The gateway answers every sender at once. Routing, delivery and retries run in background consumers, so a slow payer delays only its own messages.</p></div></section>
  <div class="grid-2"><section class="card"><div class="card-h"><h2>Message log</h2><span class="note">Open a row for the envelope</span></div>
    <div class="tbl-wrap"><table><thead><tr><th>Time</th><th>API</th><th>From → to</th><th>Status</th><th class="r">Tries</th></tr></thead>
    <tbody>${w.recent.map(m => `<tr><td class="mono">${clock(m.createdAt)}</td><td><details class="env" data-mid="${m.id}"><summary class="mono">${esc(m.api)}</summary><pre>${esc(JSON.stringify(m.envelope, null, 2))}</pre></details></td><td>${esc(m.fromName)} → ${esc(m.toName)}</td><td>${statusPill(m)}</td><td class="r">${m.attempts}</td></tr>`).join('')}</tbody></table></div></section>
  <div style="display:grid;gap:16px"><section class="card"><div class="card-h"><h2>Participants</h2></div><div class="card-b" style="gap:0;padding-block:4px">
    ${w.participants.map(p => `<div class="party"><div><b>${esc(p.name)}</b><br><span class="note">${esc(p.type)} · ${p.mode === 'api' ? 'API adapter' : 'portal adapter'} · <span class="mono">${p.code}</span></span></div>${p.mode === 'api' ? `<div class="row" style="align-items:center;gap:8px"><span class="note">${p.online ? 'Online' : 'Offline'}</span><button class="switch" type="button" role="switch" id="sw-${p.code}" data-online="${p.code}" aria-checked="${p.online}" aria-label="${esc(p.name)} system online"></button></div>` : pill('Always available', 'ok')}</div>`).join('')}
  </div></section>
  <section class="card"><div class="card-h"><h2>Dead letters</h2></div><div class="card-b">${w.dead.length ? w.dead.map(m => `<div class="row" style="justify-content:space-between;align-items:center"><span><span class="mono">${m.messageNo}</span> · ${esc(m.api)} → ${esc(m.toName)}<br><span class="note">${esc(m.error || '')}</span></span><button class="btn sm" type="button" data-replay="${m.id}">Replay</button></div>`).join('') : '<p class="note">None. Messages that fail every retry land here, the sender is told, and staff can replay them.</p>'}</div></section>
  <section class="card"><div class="card-h"><h2>Demo controls</h2></div><div class="card-b">
    <p class="note">Reset clears everything done in the demo, in every portal, and reloads the sample claims. Signed-in users stay signed in.</p>
    <div class="row" style="justify-content:flex-end"><button class="btn ${S.resetArmed ? 'bad' : 'quiet'} sm" type="button" id="btn-reset">${S.resetArmed ? 'Click again to reset all demo data' : 'Reset demo data'}</button></div>
  </div></section></div></div>`;
}

// ---------- regulator ----------
function viewNhia() {
  const n = D.nhia; if (!n) return '<div class="empty">Loading…</div>';
  const max = Math.max(1, ...n.byState.map(s => s.count)), totalDec = Math.max(1, n.decisions.reduce((s, d) => s + d.count, 0)), ageMax = Math.max(1, ...n.ageing.map(a => a.count));
  const colors = { approved: 'var(--accent)', partial: 'var(--info)', declined: 'var(--bad)', query: 'var(--warn)' };
  const names = { approved: 'Approved', partial: 'Partially approved', declined: 'Declined', query: 'Query raised' };
  const hrs = h => h < 1 ? Math.round(h * 60) + ' min' : h.toFixed(1) + ' h';
  return `<div class="grid-4"><div class="tile"><span class="l">Claims, last 30 days</span><span class="v">${n.totalClaims.toLocaleString('en-NG')}</span><span class="l">${n.liveClaims} made live in this demo</span></div>
    <div class="tile"><span class="l">Approved in full or part</span><span class="v">${n.approvalRate}%</span><span class="l">of ${n.decidedCount} decided requests</span></div>
    <div class="tile"><span class="l">Median time to decision</span><span class="v">${hrs(n.medianHours)}</span><span class="l">all payers</span></div>
    <div class="tile"><span class="l">Approved claims unpaid over 30 days</span><span class="v">${n.unpaidOver30}</span><span class="l">see ageing below</span></div></div>
  <div class="grid-2"><section class="card"><div class="card-h"><h2>Claims by state</h2></div><div class="card-b"><div class="bars">${n.byState.map(s => `<div class="bar"><span>${esc(s.state)}</span><div class="track"><div class="fill" style="width:${s.count / max * 100}%"></div></div><span class="r num">${s.count}</span></div>`).join('')}</div></div></section>
  <section class="card"><div class="card-h"><h2>Decisions</h2></div><div class="card-b">
    <div class="stack" role="img" aria-label="Decision mix">${n.decisions.map(d => `<div style="width:${d.count / totalDec * 100}%;background:${colors[d.kind]}"></div>`).join('')}</div>
    <div class="legend">${n.decisions.map(d => `<span><i style="background:${colors[d.kind]}"></i>${names[d.kind]} <b class="num">${Math.round(d.count / totalDec * 100)}%</b></span>`).join('')}</div>
    <div class="lbl" style="margin-top:6px">Approved claims awaiting payment, by age</div>
    <div class="bars">${n.ageing.map((a, i) => `<div class="bar"><span>${a.bucket}</span><div class="track"><div class="fill" style="width:${a.count / ageMax * 100}%;background:${['var(--accent)', 'var(--warn)', 'var(--bad)'][i]}"></div></div><span class="r num">${a.count}</span></div>`).join('')}</div></div></section></div>
  <section class="card"><div class="card-h"><h2>By payer type</h2></div><div class="tbl-wrap"><table><thead><tr><th>Payer type</th><th class="r">Claims</th><th class="r">Median time to decision</th><th class="r">Average days to pay</th></tr></thead>
  <tbody>${n.types.map(t => `<tr><td>${esc(t.type)}</td><td class="r">${t.claims}</td><td class="r">${hrs(t.medianHours)}</td><td class="r">${t.avgDaysToPay.toFixed(0)}</td></tr>`).join('')}</tbody></table></div></section>`;
}

// ---------- actions ----------
async function act(btn, fn, okMsg) {
  if (btn) btn.classList.add('busy');
  try { await fn(); if (okMsg) toast(okMsg); } catch (e) { if (!(e instanceof AuthError)) toast(e.message); } finally { if (btn) btn.classList.remove('busy'); await refresh(); }
}
async function checkCover() {
  const d = S.draft;
  try {
    const r = await post('/api/hospitals/' + ORG() + '/eligibility', { lookup: d.lookup });
    d.member = r.member; d.corr = r.correlationId; d.elig = { state: 'waiting' };
  } catch (e) { if (e instanceof AuthError) return; d.member = null; d.elig = { state: 'notfound', message: e.message }; }
  render();
}
async function submitPreauth(btn) {
  const d = S.draft;
  if (!d.consent) { toast('Record the patient’s consent before sending.'); return; }
  await act(btn, async () => {
    const r = await post('/api/hospitals/' + ORG() + '/preauth', { memberNo: d.member.memberNo, consent: true, diagnosisCode: d.dx, lines: d.lines.map(l => ({ code: l.code, amount: Number(l.amount) })) });
    S.selClaim = r.id; S.tab = 'submissions'; S.filters = { q: '', payer: 'all', status: 'all' }; S.draft = newDraft();
    toast(r.authNo + ' accepted by HealthSwitch and on its way to the payer.');
  });
}
async function submitReview(btn, cid) {
  const c = D.queue.find(x => x.id === cid), dr = S.review[cid];
  const decisions = c.items.filter(i => i.status === 'pending' || i.status === 'query').map(i => { const d = dr.lines[i.id] || {}; return { lineId: i.id, decision: d.decision, reason: d.reason, docType: d.docType, note: d.note }; });
  if (decisions.some(d => !d.decision)) { toast('Decide every pending service before submitting.'); return; }
  await act(btn, async () => { await post('/api/claims/' + cid + '/review', { decisions, remark: dr.remark }); delete S.review[cid]; S.selPayerClaim = null; }, 'Decision sent to ' + c.hospitalShort + ' through HealthSwitch.');
}
async function sendDocuments(btn, cid) {
  const up = S.upload[cid], fd = new FormData();
  fd.append('note', up.note || '');
  Object.entries(up.files).forEach(([lid, arr]) => arr.forEach(f => fd.append('item-' + lid, f.file, f.file.name)));
  await act(btn, async () => { await http('POST', '/api/claims/' + cid + '/documents', fd, true); delete S.upload[cid]; }, 'Documents sent. They go back to the payer’s reviewer.');
}
function setTab(t) { S.tab = t; S.resetArmed = false; lastSig = ''; render(); refresh(); window.scrollTo(0, 0); }

// ---------- events ----------
document.addEventListener('submit', e => { if (e.target.id === 'login-form') doLogin(e); });
document.addEventListener('click', e => {
  const t = e.target.closest('button, tr[data-claim], tr[data-pclaim], tr[data-hclaim]'); if (!t) return;
  const ds = t.dataset;
  if (t.id === 'li-fill') { $('#li-user').value = ds.u; $('#li-pass').value = ds.p; $('#li-go').focus(); return; }
  if (t.id === 'logout') { TOKEN = ''; store.set('hs-token-' + SLUG, ''); stopApp(); showLogin('You have signed out.'); return; }
  if (ds.tab) { setTab(ds.tab); return; }
  if (ds.claim) { S.selClaim = ds.claim; render(); return; }
  if (ds.pclaim) { S.selPayerClaim = ds.pclaim; render(); return; }
  if (ds.hclaim) { S.selHist = ds.hclaim; render(); return; }
  if (ds.openInbox) { S.selPayerClaim = ds.openInbox; setTab('inbox'); return; }
  if (ds.sample) { S.draft.lookup = ds.sample; checkCover(); return; }
  if (t.id === 'btn-check') { checkCover(); return; }
  if (t.id === 'btn-add') { S.draft.lines.push({ code: 'LAB-12', amount: 6500 }); render(); return; }
  if (ds.remove) { S.draft.lines.splice(+ds.remove, 1); render(); return; }
  if (t.id === 'btn-submit') { submitPreauth(t); return; }
  if (ds.final) { act(t, () => post('/api/claims/' + ds.final + '/final'), 'Final claim sent.'); return; }
  if (ds.answer) { sendDocuments(t, ds.answer); return; }
  if (ds.unfile) { S.upload[ds.c].files[ds.l].splice(+ds.unfile, 1); render(); return; }
  if (ds.dec) { const dr = S.review[ds.c]; const cur = dr.lines[ds.l] || {}; dr.lines[ds.l] = { decision: ds.dec, reason: cur.reason || D.ref.declineReasons[0], docType: cur.docType || D.ref.docTypes[0], note: cur.note || '' }; render(); return; }
  if (ds.submitReview) { submitReview(t, ds.submitReview); return; }
  if (ds.fdec) { act(t, async () => { await post('/api/claims/' + ds.c + '/final-decision', { approve: ds.fdec === 'yes' }); S.selPayerClaim = null; }, ds.fdec === 'yes' ? 'Final claim approved. The ledger entry is shared with the hospital.' : 'Final claim declined.'); return; }
  if (t.id === 'btn-batch') { const auths = Object.keys(S.batchSel).filter(k => S.batchSel[k]); act(t, async () => { const r = await post('/api/payers/' + ORG() + '/batches', { hospitalCode: S.batchHospital, authNos: auths }); S.batchSel = {}; toast(r.batchNo + ' created for ' + naira(r.total) + '.'); }); return; }
  if (ds.pay) { act(t, () => post('/api/batches/' + ds.pay + '/simulate-payment'), 'Payment webhook sent. The hospital sees the claims as paid in a moment.'); return; }
  if (ds.online) { const on = t.getAttribute('aria-checked') !== 'true'; act(t, () => post('/api/switch/participants/' + ds.online + '/online', { online: on }), on ? 'Back online. Waiting messages are delivered on the next retry.' : 'Offline. New messages will wait and retry.'); return; }
  if (ds.replay) { act(t, () => post('/api/switch/messages/' + ds.replay + '/replay'), 'Replaying the message.'); return; }
  if (t.id === 'btn-reset') {
    if (!S.resetArmed) { S.resetArmed = true; render(); setTimeout(() => { if (S.resetArmed) { S.resetArmed = false; render(); } }, 5000); return; }
    S.resetArmed = false; act(t, () => post('/api/switch/reset'), 'Demo data reset. Every portal now shows the fresh sample claims.'); return;
  }
  if (ds.preview) { openPreview(ds.preview, ds.img === 'true', ds.name); return; }
});
document.addEventListener('keydown', e => {
  if ((e.key === 'Enter' || e.key === ' ') && e.target.matches('tr[data-claim], tr[data-pclaim], tr[data-hclaim]')) { e.preventDefault(); e.target.click(); }
  if (e.key === 'Enter' && e.target.id === 'lookup') checkCover();
  if (e.key === 'Escape') $('#modal').hidden = true;
});
document.addEventListener('input', e => {
  const t = e.target, ds = t.dataset;
  if (t.id === 'f-q') { S.filters.q = t.value; render(); }
  else if (t.id === 'h-q') { S.hq = t.value; render(); }
  else if (t.id === 'lookup') S.draft.lookup = t.value;
  else if (ds.amount != null) { S.draft.lines[+ds.amount].amount = Number(t.value) || 0; $('#total').textContent = 'Total ' + naira(S.draft.lines.reduce((s, l) => s + (Number(l.amount) || 0), 0)); }
  else if (ds.qnote) S.review[ds.c].lines[ds.qnote].note = t.value;
  else if (ds.remark) S.review[ds.remark].remark = t.value;
  else if (ds.resp) S.upload[ds.resp].note = t.value;
});
document.addEventListener('change', e => {
  const t = e.target, ds = t.dataset;
  if (t.id === 'f-payer') { S.filters.payer = t.value; render(); }
  else if (t.id === 'f-status') { S.filters.status = t.value; render(); }
  else if (t.id === 'consent') S.draft.consent = t.checked;
  else if (t.id === 'dx') S.draft.dx = t.value;
  else if (ds.line != null) { const s = D.ref.services.find(x => x.code === t.value); S.draft.lines[+ds.line] = { code: s.code, amount: s.tariff }; render(); }
  else if (ds.reason) S.review[ds.c].lines[ds.reason].reason = t.value;
  else if (ds.doc) S.review[ds.c].lines[ds.doc].docType = t.value;
  else if (t.id === 'bh') { S.batchHospital = t.value; S.batchSel = {}; refresh(); }
  else if (ds.bsel) { S.batchSel[ds.bsel] = t.checked; render(); }
  else if (ds.up && t.files) {
    const up = S.upload[ds.c], arr = up.files[ds.up] || (up.files[ds.up] = []);
    [...t.files].forEach(f => { if (f.size > 5 * 1048576) toast(f.name + ' is over 5 MB.'); else arr.push({ file: f, url: URL.createObjectURL(f) }); });
    render();
  }
});
function openPreview(id, isImg, name) {
  $('#modal-t').textContent = name;
  $('#modal-b').innerHTML = isImg ? `<img src="${fileUrl(id)}" alt="${esc(name)}">` : `<p><a class="filelink" href="${fileUrl(id)}" target="_blank" rel="noopener">Open ${esc(name)} in a new tab</a></p>`;
  $('#modal').hidden = false; $('#modal-x').focus();
}
$('#modal-x').addEventListener('click', () => { $('#modal').hidden = true; });
$('#modal').addEventListener('click', e => { if (e.target.id === 'modal') $('#modal').hidden = true; });

// ---------- start ----------
(async () => {
  try { const meta = await (await fetch('/api/meta')).json(); keyRequired = meta.keyRequired; S.transport = meta.transport; } catch (e) {}
  if (!TOKEN || (keyRequired && !KEY)) { showLogin(); return; }
  try { await startApp(); } catch (e) { if (!(e instanceof AuthError)) showLogin(e.message); }
})();
})();

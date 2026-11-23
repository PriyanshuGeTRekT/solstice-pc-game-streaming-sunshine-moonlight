'use strict';

const $ = (sel, root = document) => root.querySelector(sel);
const main = $('#main');
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

async function api(path, body) {
  const opt = body === undefined ? {} : {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Solstice': '1' },
    body: JSON.stringify(body),
  };
  try {
    const r = await fetch(path, opt);
    return await r.json();
  } catch {
    return { ok: false, error: 'Solstice lost contact with its own background service. Close and reopen the app.' };
  }
}

const app = {
  page: 'home',
  state: null,      // last /api/state
  devices: null,    // last /api/devices
  library: null,    // last /api/library
  picks: null,      // Set of selected library ids (unsaved edits)
  tuning: null,     // edited tuning choices
  drawn: '',        // signature of what is on screen, to skip redundant re-renders
  lastPaired: '',
  dialog: '',
};

/* ---------- small helpers ---------- */

let toastTimer;
function toast(text, bad = false) {
  const t = $('#toast');
  t.textContent = text;
  t.classList.toggle('bad', bad);
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), bad ? 6000 : 3200);
}

async function run(button, work) {
  if (button) { button.classList.add('busy'); button.disabled = true; }
  try { return await work(); }
  finally { if (button) { button.classList.remove('busy'); button.disabled = false; } }
}

function draw(signature, html) {
  if (app.drawn === signature) return false;
  app.drawn = signature;
  main.innerHTML = `<div class="page">${html}</div>`;
  return true;
}

function clock(stamp) {
  // "2026-11-22 21:26:05" -> "21:26"
  const m = /(\d{2}):(\d{2}):\d{2}$/.exec(stamp || '');
  return m ? `${m[1]}:${m[2]}` : '';
}

function day(stamp) {
  const d = new Date((stamp || '').replace(' ', 'T'));
  if (isNaN(d)) return '';
  const today = new Date();
  return d.toDateString() === today.toDateString() ? 'Today' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

const profileNames = { latency: 'Lowest latency', balanced: 'Balanced', picture: 'Best picture' };

/* ---------- dialogs ---------- */

function openDialog(key, html) {
  if (app.dialog === key) return;
  app.dialog = key;
  const s = $('#scrim');
  s.innerHTML = `<div class="dialog" role="dialog" aria-modal="true">${html}</div>`;
  s.hidden = false;
  const first = s.querySelector('input, .btn.primary, .btn');
  if (first) first.focus();
}

function closeDialog() {
  app.dialog = '';
  $('#scrim').hidden = true;
  $('#scrim').innerHTML = '';
}

function loginDialog() {
  openDialog('login', `
    <h2>Sign in to Sunshine</h2>
    <p>Sunshine already has a login that Solstice does not know. Enter it, or let Solstice replace it with a new private one.</p>
    <div style="display:grid;gap:12px;margin-top:18px">
      <label class="field">Username<input type="text" id="lgUser" autocomplete="off" spellcheck="false"></label>
      <label class="field">Password<input type="password" id="lgPass" autocomplete="off"></label>
    </div>
    <div class="error" id="lgErr" role="alert"></div>
    <div class="actions">
      <button class="btn quiet" data-do="login-reset">Replace it for me</button>
      <button class="btn" data-do="close">Cancel</button>
      <button class="btn primary" data-do="login-save">Sign in</button>
    </div>`);
}

async function revealDialog() {
  const r = await api('/api/login/reveal');
  if (!r.ok) return toast(r.error, true);
  openDialog('reveal', `
    <h2>Sunshine login</h2>
    <p>Use this to sign in to Sunshine's own settings page at ${esc(r.url)}. Solstice created it and keeps it encrypted for your Windows account.</p>
    <div class="secret">Username: ${esc(r.user)}<br>Password: ${esc(r.pass)}</div>
    <div class="actions">
      <button class="btn" data-do="close">Close</button>
      <button class="btn primary" data-do="open-webui">Open settings page</button>
    </div>`);
}

function approvalDialog(req) {
  openDialog('approve:' + req.pin + req.name, `
    <h2>${esc(req.name)} wants to pair</h2>
    <p>Check that the handheld is showing this same number. Approve only if it matches.</p>
    <div class="digits num">${esc(req.pin)}</div>
    <p style="font-size:13px">Request from ${esc(req.address)} on your network.</p>
    <div class="actions">
      <button class="btn" data-do="decline">Decline</button>
      <button class="btn primary" data-do="approve">Approve</button>
    </div>`);
}

function watchLink(link) {
  if (!link) return;
  if (link.request) approvalDialog(link.request);
  else if (app.dialog.startsWith('approve:')) closeDialog();
  if (link.lastPaired && link.lastPaired !== app.lastPaired) {
    toast(`${link.lastPaired} is paired and ready to play.`);
    if (app.page === 'devices') loadDevices();
  }
  app.lastPaired = link.lastPaired || '';
  if (link.note) toast(link.note, true);
}

/* ---------- rail ---------- */

function drawRail() {
  const s = app.state;
  if (!s) return;
  const live = s.activity.streaming;
  const label = live ? 'Streaming now' : s.ready ? 'Ready' : s.setupDone ? 'Needs attention' : 'Not set up';
  const cls = live ? 'live' : s.ready ? 'ok' : s.setupDone ? 'bad' : '';
  $('#railFoot').innerHTML = `<b>${esc(s.host.name)}</b><span style="display:inline-flex;align-items:center;gap:8px;margin-top:4px"><span class="dot ${cls}"></span>${label}</span>`;
}

/* ---------- home ---------- */

function setupView(s) {
  const st = s.setup;
  const started = st.running || st.problem || st.steps.some(x => x.state !== 'pending');
  const steps = st.steps.map(x => `
    <li class="${x.state}">
      <span class="mark"></span>
      <div><div class="label">${esc(x.label)}</div>${x.detail ? `<div class="detail">${esc(x.detail)}</div>` : ''}</div>
    </li>`).join('');

  let action = '';
  if (st.running) action = '<button class="btn primary big busy" disabled>Setting up</button>';
  else if (st.problem === 'need-login') action = '<button class="btn primary big" data-do="login">Sign in to Sunshine</button>';
  else if (st.problem) action = '<button class="btn primary big" data-do="setup">Try again</button>';
  else action = '<button class="btn primary big" data-do="setup">Set up everything</button>';

  const sig = 'setup|' + JSON.stringify(st);
  draw(sig, `
    <div class="welcome">
      <h1>${started ? 'Setting up this PC' : 'Play your PC games from anywhere in the house'}</h1>
      <p>${started
        ? 'Leave this window open. If Windows asks for permission, choose Yes.'
        : 'One click installs and configures everything this PC needs to stream games to a handheld: the streaming engine, the controller driver, a private login, tuned settings and your launchers.'}</p>
      <ol class="steps">${steps}</ol>
      ${action}
      <p class="fine">Solstice is a front end for two open-source projects: <button class="link" data-do="src-sunshine">Sunshine</button> on this PC and <button class="link" data-do="src-moonlight">Moonlight</button> on the handheld. Setup installs Sunshine (GPL-3.0) and the ViGEmBus controller driver (BSD-3-Clause) from their official releases through Windows Package Manager, and Windows asks for administrator approval once.</p>
    </div>`);
}

function homeView() {
  const s = app.state;
  if (!s) return draw('loading', '<div class="empty">Reading this PC…</div>');
  drawRail();
  if (!s.setupDone || s.setup.running) return setupView(s);

  const a = s.activity;
  const d = s.host.display;
  const problems = s.checks.filter(c => c.state === 'bad');
  let chip, title, text, actions, cls = '';

  if (a.streaming) {
    cls = 'live';
    chip = '<span class="dot live"></span>Streaming now';
    title = a.app ? `Playing ${esc(a.app)}` : 'A device is connected';
    text = `Connected since ${clock(a.since)}. Your monitor returns to its normal mode when the session ends.`;
    actions = '<button class="btn danger" data-do="end-session">End session</button>';
  } else if (s.ready) {
    chip = '<span class="dot ok"></span>Ready';
    title = 'Ready to stream';
    text = 'Open Solstice Play on your handheld and choose what to play. This PC does not need to be touched.';
    actions = '<button class="btn primary" data-do="go-devices">Add a device</button><button class="btn" data-do="restart">Restart streaming</button>';
  } else {
    chip = '<span class="dot bad"></span>Needs attention';
    title = 'Streaming is not available';
    text = problems.length ? esc(problems[0].detail) : 'Sunshine is still starting. This usually takes about 20 seconds.';
    actions = `<button class="btn primary" data-do="restart">${s.sunshine.service === 'Running' ? 'Restart streaming' : 'Start streaming'}</button>`;
  }

  const checks = s.checks.map(c => `
    <div class="row">
      <span class="dot ${c.state}"></span>
      <div class="grow"><div class="title">${esc(c.title)}</div><div class="detail">${esc(c.detail)}</div></div>
      ${c.action ? `<button class="btn" data-do="fix" data-fix="${esc(c.action)}">${esc(c.actionLabel)}</button>` : ''}
    </div>`).join('');

  const events = a.events.length
    ? a.events.map(e => `
        <div class="row">
          <span class="dot ${e.type === 'connected' ? 'ok' : ''}"></span>
          <div class="grow"><div class="title">${e.type === 'connected' ? 'Device connected' : 'Device disconnected'}</div>
          ${e.app ? `<div class="detail">${esc(e.app)}</div>` : ''}</div>
          <span class="time">${day(e.time)} ${clock(e.time)}</span>
        </div>`).join('')
    : '<div class="empty">No sessions yet. They will show up here once a device connects.</div>';

  const sig = 'home|' + JSON.stringify([s.ready, a, s.checks, s.tuning.profile, s.host.ip]);
  draw(sig, `
    <section class="hero ${cls}">
      <div class="state">${chip}</div>
      <h1>${title}</h1>
      <p>${text}</p>
      <div class="facts">
        <span><b>${esc(s.host.name)}</b> at <span class="num">${esc(s.host.ip || 'no network')}</span></span>
        <span>${esc(s.host.gpu)}</span>
        ${d ? `<span class="num">${d.w}×${d.h} at ${d.hz} Hz</span>` : ''}
        <span>${profileNames[s.tuning.profile]} profile</span>
      </div>
      <div class="actions">${actions}</div>
    </section>
    <h2 class="section-title">Health</h2>
    <div class="list">${checks}</div>
    <h2 class="section-title">Recent sessions</h2>
    <div class="list">${events}</div>`);
}

/* ---------- devices ---------- */

async function loadDevices() {
  const r = await api('/api/devices');
  if (r.ok) { app.devices = r; watchLink(r.link); }
  else app.devices = { error: r.error, clients: [], pending: [], link: app.state?.link || {} };
  if (app.page === 'devices') devicesView();
}

function devicesView() {
  const dv = app.devices;
  if (!dv) return draw('devices|loading', '<div class="head"><h1>Devices</h1></div><div class="empty">Loading…</div>');
  const link = dv.link || {};

  const add = link.active ? `
      <ol class="link-steps">
        <li><div><b>On the handheld, open a browser and go to</b><br><span class="address num">${esc(link.url)}</span></div></li>
        <li><div><b>Download and open Solstice Play</b><br><span style="color:var(--muted)">Windows may show a blue warning because the app comes from your own PC rather than a store. Choose More info, then Run anyway.</span></div></li>
        <li><div><b>Tap Set up, then approve the request that appears here</b><br><span style="color:var(--muted)">Both screens show the same four digits, so you know it is your device.</span></div></li>
      </ol>
      <div class="inline" style="align-items:center">
        <button class="btn" data-do="link-stop">Done</button>
        <span class="countdown num">Open for ${Math.max(1, Math.ceil(link.secondsLeft / 60))} more min, on your home network only</span>
      </div>` : `
      <button class="btn primary" data-do="link-start">Add a device</button>
      ${link.clientAvailable === false ? '<p class="fine">This copy of Solstice does not include the handheld app yet.</p>' : ''}`;

  const clients = dv.clients.length
    ? dv.clients.map(c => `
        <div class="row">
          <span class="dot ok"></span>
          <div class="grow"><div class="title">${esc(c.name || 'Unnamed device')}</div><div class="detail">Paired. It can connect whenever this PC is on.</div></div>
          <button class="btn danger" data-do="unpair" data-uuid="${esc(c.uuid)}" data-name="${esc(c.name || 'this device')}">Remove</button>
        </div>`).join('')
    : `<div class="empty">${dv.error ? esc(dv.error) : 'No devices are paired yet.'}</div>`;

  const waiting = dv.pending.length > 0;
  const sig = 'devices|' + JSON.stringify([link.active, link.url, Math.ceil((link.secondsLeft || 0) / 60), dv.clients, waiting, dv.error]);
  // Keep whatever is being typed into the PIN form across refreshes.
  const typed = { pin: $('#pin')?.value, name: $('#pinName')?.value, focus: document.activeElement?.id };
  if (typed.focus === 'pin' || typed.focus === 'pinName') return;

  if (draw(sig, `
    <div class="head"><h1>Devices</h1><p>Handhelds, laptops and TVs that are allowed to stream from this PC.</p></div>
    <div class="card">
      <h2>Add a handheld</h2>
      <p>Solstice Play does the whole job on the device: it installs Moonlight, finds this PC, pairs and picks the right settings for its screen.</p>
      ${add}
    </div>
    <h2 class="section-title">Paired devices</h2>
    <div class="list">${clients}</div>
    <h2 class="section-title">Pair with a PIN instead</h2>
    <div class="card">
      <p>${waiting
        ? '<b style="color:var(--text)">A device is asking to pair right now.</b> Enter the PIN it is showing.'
        : 'For a device running plain Moonlight (a TV, phone or Steam Deck): select this PC in Moonlight, then enter the 4-digit PIN it shows.'}</p>
      <div class="inline">
        <label class="field">PIN<input type="text" id="pin" class="pin num" inputmode="numeric" maxlength="4" autocomplete="off"></label>
        <label class="field" style="flex:1;min-width:180px">Name for this device<input type="text" id="pinName" maxlength="40" placeholder="Living room TV" autocomplete="off"></label>
        <button class="btn" data-do="pin-pair">Pair</button>
      </div>
      <div class="error" id="pinErr" role="alert"></div>
    </div>`)) {
    if (typed.pin) $('#pin').value = typed.pin;
    if (typed.name) $('#pinName').value = typed.name;
  }
}

/* ---------- library ---------- */

async function loadLibrary() {
  const r = await api('/api/library');
  app.library = r;
  if (r.ok && !app.picks) app.picks = new Set(r.items.filter(i => i.added).map(i => i.id));
  if (app.page === 'library') libraryView(true);
}

function libraryView(force) {
  const lib = app.library;
  if (!lib) return draw('library|loading', '<div class="head"><h1>Library</h1></div><div class="empty">Looking for launchers and games…</div>');
  if (!lib.ok) return draw('library|err', `<div class="head"><h1>Library</h1></div><div class="empty">${esc(lib.error)}</div>`);

  const tile = i => `
    <label class="tile">
      <input type="checkbox" data-pick="${esc(i.id)}" ${app.picks.has(i.id) ? 'checked' : ''}>
      <div class="art"><img src="/file/cover/${encodeURIComponent(i.id)}" alt="" loading="lazy"></div>
      <span class="check"></span>
      <div class="name">${esc(i.name)}</div>
    </label>`;
  const launchers = lib.items.filter(i => i.kind === 'launcher');
  const games = lib.items.filter(i => i.kind === 'game');
  const published = new Set(lib.items.filter(i => i.added).map(i => i.id));
  const dirty = published.size !== app.picks.size || [...published].some(id => !app.picks.has(id));

  if (force) app.drawn = '';
  draw('library|' + lib.items.length, `
    <div class="head"><h1>Library</h1><p>Choose what appears as tiles on your devices. Desktop is always available and reaches anything on this PC, including launchers not listed here.</p></div>
    <h2 class="section-title" style="margin-top:0">Launchers</h2>
    ${launchers.length ? `<div class="shelf">${launchers.map(tile).join('')}</div>` : '<div class="empty">No game launchers were found on this PC.</div>'}
    <h2 class="section-title">Games</h2>
    ${games.length ? `<div class="shelf">${games.map(tile).join('')}</div>` : '<div class="empty">Installed Steam and Epic games show up here.</div>'}
    <div class="savebar" id="savebar" ${dirty ? '' : 'hidden'}>
      <span class="grow" id="saveText"></span>
      <button class="btn quiet" data-do="lib-reset">Discard</button>
      <button class="btn primary" data-do="lib-save">Save changes</button>
    </div>`);
  updateSavebar();
}

function updateSavebar() {
  const lib = app.library;
  if (!lib?.ok) return;
  const published = new Set(lib.items.filter(i => i.added).map(i => i.id));
  const add = [...app.picks].filter(id => !published.has(id)).length;
  const remove = [...published].filter(id => !app.picks.has(id)).length;
  const bar = $('#savebar');
  if (!bar) return;
  bar.hidden = add + remove === 0;
  const parts = [];
  if (add) parts.push(`${add} to add`);
  if (remove) parts.push(`${remove} to remove`);
  $('#saveText').textContent = parts.join(', ');
}

/* ---------- tuning ---------- */

function tuningView(force) {
  const s = app.state;
  if (!s) return;
  if (!app.tuning) app.tuning = { ...s.tuning };
  const t = app.tuning;
  const d = s.host.display;
  const dirty = JSON.stringify(t) !== JSON.stringify(s.tuning);

  const profile = (id, tag, text) => `
    <label class="profile">
      <input type="radio" name="profile" value="${id}" ${t.profile === id ? 'checked' : ''}>
      <div class="box">${tag ? `<span class="tag">${tag}</span>` : ''}<h3>${profileNames[id]}</h3><p>${text}</p></div>
    </label>`;
  const toggle = (key, title, text) => `
    <div class="row">
      <div class="grow"><div class="title">${title}</div><div class="detail">${text}</div></div>
      <label class="switch"><input type="checkbox" data-toggle="${key}" ${t[key] ? 'checked' : ''} aria-label="${title}"><span></span></label>
    </div>`;

  if (force) app.drawn = '';
  draw('tuning|' + JSON.stringify([s.tuning, s.host.display, s.host.vendor]), `
    <div class="head"><h1>Tuning</h1><p>How this PC encodes and presents the stream. Bitrate, resolution and frame rate are chosen on the handheld.</p></div>
    <h2 class="section-title" style="margin-top:0">Encoder profile</h2>
    <div class="profiles">
      ${profile('latency', '', 'Fastest possible encode on your graphics card. Fine detail can soften slightly in fast motion.')}
      ${profile('balanced', 'Recommended', 'Sunshine\'s low-latency defaults: a quick second pass that keeps motion clean at almost no cost.')}
      ${profile('picture', '', 'Slower preset with a full-resolution second pass and adaptive quantisation. Sharper image for a few extra milliseconds.')}
    </div>
    <h2 class="section-title">Behaviour</h2>
    <div class="list">
      ${toggle('matchDisplay', 'Match the handheld\'s screen', `While a device is streaming, switch this monitor ${d ? `from ${d.w}×${d.h} at ${d.hz} Hz ` : ''}to the device's resolution and refresh rate, then switch back. Games render at the handheld's native mode instead of being scaled down.`)}
      ${toggle('guideHold', 'Hold View for the Guide button', 'Holding the View (Back) button for 1.5 seconds sends the Xbox Guide button, which opens Steam\'s overlay. Handhelds have no Guide button of their own.')}
      ${toggle('lockWebUi', 'Keep Sunshine\'s admin page on this PC only', 'Sunshine\'s settings page is reachable from every device on your network by default. This limits it to this computer.')}
    </div>
    <div class="apply">
      <button class="btn primary" data-do="tune-apply" id="tuneApply" ${dirty ? '' : 'disabled'}>Apply</button>
      <span class="note">Applying restarts the streaming service, which takes about 20 seconds.</span>
    </div>
    <h2 class="section-title">Advanced</h2>
    <div class="list">
      <div class="row">
        <div class="grow"><div class="title">Sunshine's own settings</div><div class="detail">Every option Sunshine has, in its web page. Solstice only changes the settings above and leaves the rest as you set them.</div></div>
        <button class="btn" data-do="reveal">Show login</button>
        <button class="btn" data-do="open-webui">Open</button>
      </div>
    </div>`);
}

/* ---------- activity ---------- */

async function activityView() {
  draw('activity', `
    <div class="head"><h1>Activity</h1><p>Sunshine's own log, newest at the bottom. Useful when a session will not start.</p></div>
    <div class="toolbar">
      <button class="btn" data-do="log-refresh">Refresh</button>
      <button class="btn" data-do="open-logs">Open log folder</button>
    </div>
    <div class="log" id="log" tabindex="0">Loading…</div>`);
  const r = await api('/api/logs');
  const box = $('#log');
  if (!box) return;
  if (!r.ok) { box.textContent = r.error; return; }
  box.innerHTML = r.lines.map(l => {
    const cls = /\]: (Error|Fatal):/.test(l) ? 'e' : /\]: Warning:/.test(l) ? 'w' : '';
    return cls ? `<span class="${cls}">${esc(l)}</span>` : esc(l);
  }).join('\n') || 'The log is empty.';
  box.scrollTop = box.scrollHeight;
}

/* ---------- routing and polling ---------- */

function show(page) {
  app.page = page;
  app.drawn = '';
  document.querySelectorAll('#nav button').forEach(b => {
    const on = b.dataset.page === page;
    b.classList.toggle('on', on);
    if (on) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
  main.scrollTop = 0;
  render();
  if (page === 'devices') loadDevices();
  if (page === 'library') { app.picks = null; loadLibrary(); }
  if (page === 'tuning') app.tuning = null, tuningView(true);
}

function render() {
  if (app.page === 'home') homeView();
  else if (app.page === 'devices') devicesView();
  else if (app.page === 'library') libraryView();
  else if (app.page === 'tuning') tuningView();
  else if (app.page === 'activity') activityView();
}

async function refresh() {
  const s = await fetch('/api/state').then(r => r.json()).catch(() => null);
  if (!s) return;
  app.state = s;
  drawRail();
  watchLink(s.link);
  if (app.page === 'home' || app.page === 'tuning') render();
}

async function tick() {
  await refresh();
  const s = app.state;
  const fast = s && (s.setup.running || s.link.active);
  if (app.page === 'devices' && !document.hidden) loadDevices();
  setTimeout(tick, fast ? 900 : 3000);
}

async function fix(kind, button) {
  if (kind === 'setup') { await api('/api/setup/start', {}); return refresh(); }
  if (kind === 'login') return loginDialog();
  if (kind === 'restart') return restart(button);
  const r = await run(button, () => api('/api/fix/' + kind, {}));
  if (!r.ok) return toast(r.error, true);
  await fetch('/api/state?refresh=1').then(x => x.json()).then(s => { app.state = s; render(); });
  toast('Done.');
}

async function restart(button) {
  const r = await run(button, () => api('/api/service/restart', {}));
  toast(r.ok ? 'Streaming restarted.' : r.error, !r.ok);
  refresh();
}

document.addEventListener('click', async e => {
  const nav = e.target.closest('#nav button');
  if (nav) return show(nav.dataset.page);

  const b = e.target.closest('[data-do]');
  if (!b) return;
  switch (b.dataset.do) {
    case 'setup': await api('/api/setup/start', {}); return refresh();
    case 'login': return loginDialog();
    case 'close': return closeDialog();
    case 'fix': return fix(b.dataset.fix, b);
    case 'restart': return restart(b);
    case 'go-devices': show('devices'); return api('/api/link/start', {}).then(r => { if (!r.ok) toast(r.error, true); loadDevices(); });
    case 'src-sunshine': return api('/api/open', { what: 'source-sunshine' });
    case 'src-moonlight': return api('/api/open', { what: 'source-moonlight' });
    case 'open-webui': closeDialog(); return api('/api/open', { what: 'webui' });
    case 'open-logs': return api('/api/open', { what: 'logs' });
    case 'log-refresh': app.drawn = ''; return activityView();
    case 'reveal': return revealDialog();

    case 'end-session': {
      const r = await run(b, () => api('/api/session/end', {}));
      toast(r.ok ? 'Session ended.' : r.error, !r.ok);
      return refresh();
    }

    case 'login-save': {
      const r = await run(b, () => api('/api/login', { user: $('#lgUser').value, pass: $('#lgPass').value }));
      if (!r.ok) { $('#lgErr').textContent = r.error; return; }
      closeDialog(); toast('Signed in.'); return refresh();
    }
    case 'login-reset': {
      const r = await run(b, () => api('/api/login/reset', {}));
      if (!r.ok) { $('#lgErr').textContent = r.error; return; }
      closeDialog(); toast('Sunshine has a new private login.'); return refresh();
    }

    case 'link-start': {
      const r = await run(b, () => api('/api/link/start', {}));
      if (!r.ok) toast(r.error, true);
      return loadDevices();
    }
    case 'link-stop': await api('/api/link/stop', {}); return loadDevices();
    case 'approve': b.classList.add('busy'); await api('/api/link/decide', { approve: true }); return;
    case 'decline': await api('/api/link/decide', { approve: false }); return closeDialog();

    case 'unpair': {
      const r = await run(b, () => api('/api/devices/unpair', { uuid: b.dataset.uuid }));
      toast(r.ok ? `Removed ${b.dataset.name}.` : r.error, !r.ok);
      return loadDevices();
    }
    case 'pin-pair': {
      const r = await run(b, () => api('/api/devices/pair', { pin: $('#pin').value, name: $('#pinName').value }));
      if (!r.ok) { $('#pinErr').textContent = r.error; return; }
      $('#pin').value = ''; $('#pinName').value = ''; $('#pinErr').textContent = '';
      toast('Device paired.');
      return loadDevices();
    }

    case 'lib-reset': app.picks = null; return loadLibrary();
    case 'lib-save': {
      const r = await run(b, () => api('/api/library/sync', { ids: [...app.picks] }));
      if (!r.ok) return toast(r.error, true);
      toast('Library updated on your devices.');
      app.picks = null;
      return loadLibrary();
    }

    case 'tune-apply': {
      const r = await run(b, () => api('/api/tuning', app.tuning));
      if (!r.ok) return toast(r.error, true);
      toast(r.result === 'pending' ? 'Saved. A device is streaming, so this starts with the next session.'
        : r.result === 'unchanged' ? 'Already up to date.' : 'Applied. Streaming is back up.');
      app.tuning = null;
      await refresh();
      return tuningView(true);
    }
  }
});

document.addEventListener('change', e => {
  const t = e.target;
  if (t.dataset.pick) {
    t.checked ? app.picks.add(t.dataset.pick) : app.picks.delete(t.dataset.pick);
    updateSavebar();
  } else if (t.name === 'profile') {
    app.tuning.profile = t.value;
    $('#tuneApply').disabled = JSON.stringify(app.tuning) === JSON.stringify(app.state.tuning);
  } else if (t.dataset.toggle) {
    app.tuning[t.dataset.toggle] = t.checked;
    $('#tuneApply').disabled = JSON.stringify(app.tuning) === JSON.stringify(app.state.tuning);
  }
});

document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && app.dialog && !app.dialog.startsWith('approve:')) closeDialog();
  if (e.key === 'Enter' && e.target.id === 'pin') $('[data-do="pin-pair"]').click();
  if (e.key === 'Enter' && (e.target.id === 'lgUser' || e.target.id === 'lgPass')) $('[data-do="login-save"]').click();
});

render();
tick();

// Deep links (#devices, #library, #tuning, #activity) open that page directly.
const startPage = location.hash.slice(1);
if (['devices', 'library', 'tuning', 'activity'].includes(startPage)) show(startPage);

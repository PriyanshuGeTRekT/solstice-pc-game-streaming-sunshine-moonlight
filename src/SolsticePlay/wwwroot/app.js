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
  try { return await (await fetch(path, opt)).json(); }
  catch { return { ok: false, error: 'Solstice Play lost contact with its background service. Close and reopen the app.' }; }
}

const view = { state: null, apps: null, drawn: '', sheet: false, busy: false };

let toastTimer;
function toast(text, bad = false, ms) {
  const t = $('#toast');
  t.textContent = text;
  t.classList.toggle('bad', bad);
  t.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), ms || (bad ? 7000 : 3200));
}

function draw(signature, html) {
  if (view.drawn === signature) return false;
  const hadFocus = document.activeElement?.dataset?.key;
  view.drawn = signature;
  main.innerHTML = `<div class="wrap">${html}</div>`;
  // Keep the gamepad cursor where it was, or put it on the main action.
  const again = hadFocus && main.querySelector(`[data-key="${CSS.escape(hadFocus)}"]`);
  (again || main.querySelector('[data-autofocus]'))?.focus({ preventScroll: true });
  return true;
}

function summary(st) {
  return `<b class="num">${st.width}×${st.height}</b> · <b class="num">${st.fps} fps</b> · <b class="num">${st.mbps} Mbps</b> · <b>${esc(st.codec)}</b>${st.hdr ? ' · <b>HDR</b>' : ''}`;
}

/* ---------- setup ---------- */

function setupView(s) {
  const st = s.setup;
  const started = st.running || st.problem || st.steps.some(x => x.state !== 'pending');
  const steps = st.steps.map(x => `
    <li class="${x.state}">
      <span class="mark"></span>
      <div><div class="label">${esc(x.label)}</div>${x.detail ? `<div class="detail">${esc(x.detail)}</div>` : ''}</div>
    </li>`).join('');

  let pin = '';
  if (st.running && st.pin) {
    pin = `<div class="pinbox">
      <div class="digits num" aria-label="Pairing number ${esc(st.pin.split('').join(' '))}">${esc(st.pin)}</div>
      <p>${st.pairMode === 'approve'
        ? 'Your PC is asking whether to let this device in. Check that it shows <b>this same number</b>, then choose <b>Approve</b> there.'
        : 'On your PC, open <b>Solstice</b>, go to <b>Devices</b> and type this PIN under "Pair with a PIN instead".'}</p>
    </div>`;
  }

  let action;
  if (st.running) {
    action = '<button class="btn primary huge busy" disabled>Setting up</button>';
  } else if (st.problem === 'choose-host') {
    action = `<div class="choices">${st.hosts.map((h, i) => `
        <button class="btn" data-do="choose" data-host="${esc(h.ip)}" data-key="host${i}" ${i === 0 ? 'data-autofocus' : ''}>${esc(h.name)}<span class="num">${esc(h.ip)}</span></button>`).join('')}</div>`;
  } else if (st.problem === 'no-host') {
    action = `
      <div class="manual">
        <input type="text" id="hostInput" placeholder="PC address, for example 192.168.1.50" inputmode="decimal" autocomplete="off" spellcheck="false" data-key="hostInput">
        <button class="btn" data-do="connect" data-key="connect">Connect</button>
      </div>
      <div class="row-actions"><button class="btn primary" data-do="setup" data-key="setup" data-autofocus>Search again</button></div>`;
  } else if (st.problem === 'no-moonlight') {
    action = `<div class="row-actions">
        <button class="btn primary" data-do="setup" data-key="setup" data-autofocus>Try again</button>
        <button class="btn" data-do="moonlight-site" data-key="site">Open moonlight-stream.org</button>
      </div>`;
  } else if (st.problem) {
    action = '<button class="btn primary huge" data-do="setup" data-key="setup" data-autofocus>Try again</button>';
  } else {
    action = '<button class="btn primary huge" data-do="setup" data-key="setup" data-autofocus>Set up</button>';
  }

  const typing = document.activeElement?.id === 'hostInput';
  if (typing) return; // do not redraw under the on-screen keyboard
  draw('setup|' + JSON.stringify(st), `
    <div class="setup">
      <h1>${st.pin ? 'Approve this device on your PC' : started ? 'Getting this device ready' : 'Play your PC games on this device'}</h1>
      ${st.pin ? '' : `<p>${started
        ? 'Keep this open. If Windows asks for permission while Moonlight installs, choose Yes.'
        : 'One tap installs Moonlight, finds your PC on this network, pairs with it and picks the best stream for this screen.'}</p>`}
      ${pin}
      ${started ? `<ol class="steps">${steps}</ol>` : ''}
      ${action}
    </div>`);
}

/* ---------- home ---------- */

function hue(name) {
  let h = 0;
  for (const c of name) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}

function homeView(s) {
  const host = s.host;
  const apps = view.apps || [];
  const last = localStorage.getItem('lastApp');
  const hero = apps.includes(last) ? last : (apps.includes('Desktop') ? 'Desktop' : apps[0]);

  const tiles = apps.map((a, i) => {
    const h = hue(a);
    return `<button class="tile" data-do="play" data-app="${esc(a)}" data-key="app${i}"
      style="background:linear-gradient(140deg,hsl(${h} 42% 30%),hsl(${(h + 38) % 360} 48% 13%))">
      <span class="name">${esc(a)}</span></button>`;
  }).join('');

  const title = host.online ? `Play on ${esc(host.name)}` : `${esc(host.name)} is asleep`;
  const text = host.online
    ? 'Pick something below, or jump straight in.'
    : host.canWake
      ? 'Tap Play and this device wakes it first. That takes about half a minute.'
      : 'Turn the PC on, then tap Play.';

  draw('home|' + JSON.stringify([host, apps, s.stream, hero, view.busy]), `
    <section class="hero">
      <h1>${title}</h1>
      <p>${text}</p>
      <div class="actions">
        <button class="btn primary huge ${view.busy ? 'busy' : ''}" data-do="play" data-app="${esc(hero || 'Desktop')}" data-key="hero" data-autofocus ${view.busy ? 'disabled' : ''}>
          ${view.busy ? (host.online ? 'Starting' : 'Waking your PC') : `Play ${esc(hero || 'Desktop')}`}
        </button>
        <span class="summary">${summary(s.stream)}</span>
      </div>
    </section>
    ${apps.length ? `<h2 class="section-title">On your PC</h2><div class="shelf">${tiles}</div>` : ''}`);
}

function playingView(s) {
  draw('playing', `
    <div class="playing">
      <h1>Streaming from ${esc(s.host?.name || 'your PC')}</h1>
      <p>To leave the game and come back here, hold these four together:</p>
      <div class="keys"><kbd>Start</kbd><kbd>Select</kbd><kbd>L1</kbd><kbd>R1</kbd></div>
      <p style="font-size:16px;margin-top:14px">On a keyboard: Ctrl + Alt + Shift + Q.</p>
    </div>`);
}

/* ---------- settings sheet ---------- */

function openSheet() {
  view.sheet = true;
  drawSheet();
}

function closeSheet() {
  view.sheet = false;
  $('#scrim').hidden = true;
  $('#scrim').innerHTML = '';
  view.drawn = '';
  render();
}

function drawSheet() {
  const st = view.state.stream;
  const hz = st.screen.hz;
  const seg = (name, options, current) => `<div class="seg" role="group">${options.map(([value, label]) =>
    `<button data-set="${name}" data-value="${esc(value)}" data-key="${name}-${esc(value)}" aria-pressed="${String(value) === String(current)}">${esc(label)}</button>`).join('')}</div>`;
  const toggle = (name, label, hint, on) => `
    <div class="opt toggle">
      <div class="grow"><div class="label">${label}</div><div class="hint">${hint}</div></div>
      <label class="switch"><input type="checkbox" data-toggle="${name}" data-key="tg-${name}" ${on ? 'checked' : ''} aria-label="${label}"><span></span></label>
    </div>`;

  const fpsOptions = [[0, `Auto (${Math.min(hz, 120)})`], [60, '60']];
  if (hz >= 90) fpsOptions.push([90, '90']);
  if (hz >= 120) fpsOptions.push([120, '120']);
  fpsOptions.push([30, '30']);

  const focused = document.activeElement?.dataset?.key;
  const s = $('#scrim');
  s.innerHTML = `
    <div class="sheet" role="dialog" aria-modal="true" aria-label="Stream settings">
      <h2>Stream settings</h2>
      <p>Now: ${summary(st)}. Changes apply to the next game you start.</p>

      <div class="opt"><div class="label">Resolution</div>
        <div class="hint">Native matches this screen (${st.screen.w}×${st.screen.h}), and your PC switches its display to the same mode so nothing is scaled.</div>
        ${seg('resolution', [['native', 'Native'], ['1080p', '1080p'], ['720p', '720p']], st.resolution)}</div>

      <div class="opt"><div class="label">Frame rate</div>
        <div class="hint">This screen is running at ${hz} Hz.${hz < 120 ? ' Set it to 120 Hz in Windows display settings to unlock 120.' : ''} Streams are capped at 120 fps.</div>
        ${seg('fps', fpsOptions, st.fpsChoice)}</div>

      <div class="opt"><div class="label">Bitrate</div>
        <div class="hint">Higher is sharper but needs stronger Wi-Fi. If the picture stutters or breaks up, go lower.</div>
        ${seg('bitrateMbps', [[0, `Auto (${st.autoMbps} Mbps)`], [15, '15'], [25, '25'], [40, '40'], [60, '60'], [80, '80'], [120, '120']], st.bitrateChoice)}</div>

      <div class="opt"><div class="label">Video format</div>
        <div class="hint">HEVC is the best balance. AV1 looks slightly cleaner at low bitrates; H.264 is the fallback if the others misbehave.</div>
        ${seg('codec', [['HEVC', 'HEVC'], ['AV1', 'AV1'], ['H.264', 'H.264']], st.codec)}</div>

      ${toggle('vsync', 'V-Sync', 'Off gives the lowest input delay. Turn it on if you see tearing.', st.vsync)}
      ${toggle('hdr', 'HDR', 'Needs HDR turned on for this screen in Windows.', st.hdr)}
      ${toggle('overlay', 'Show stream stats', 'Latency, bitrate and dropped frames in the corner while playing.', st.overlay)}
      ${toggle('quitAfter', 'Close the game on the PC when I stop', 'Also returns the PC\'s monitor to its normal mode. Turn off to keep a game running and reconnect later.', st.quitAfter)}

      <div class="foot">
        <div class="row-actions">
          <button class="btn quiet" data-do="open-moonlight" data-key="f-ml">Open Moonlight</button>
          <button class="btn quiet" data-do="host-quit" data-key="f-quit">End game on PC</button>
          <button class="btn quiet" data-do="forget" data-key="f-forget">Set up again</button>
        </div>
        <button class="btn primary" data-do="sheet-close" data-key="f-done">Done</button>
      </div>
    </div>`;
  s.hidden = false;
  (s.querySelector(focused ? `[data-key="${CSS.escape(focused)}"]` : '[aria-pressed=true]') || s.querySelector('button'))?.focus({ preventScroll: true });
}

async function saveSetting(name, value) {
  const st = view.state.stream;
  const body = {
    resolution: st.resolution, fps: st.fpsChoice, bitrateMbps: st.bitrateChoice, codec: st.codec,
    vsync: st.vsync, hdr: st.hdr, overlay: st.overlay, quitAfter: st.quitAfter,
  };
  body[name] = value;
  const r = await api('/api/settings', body);
  if (!r.ok) return toast(r.error, true);
  view.state.stream = r.stream;
  drawSheet();
}

/* ---------- loop ---------- */

function render() {
  const s = view.state;
  $('#btnSettings').hidden = !(s && s.setupDone);
  const chip = $('#hostChip');
  if (s?.host && s.setupDone) {
    chip.hidden = false;
    chip.innerHTML = `<span class="dot ${s.host.online ? 'ok' : ''}"></span><b>${esc(s.host.name)}</b>${s.host.online ? 'ready' : 'asleep'}`;
  } else chip.hidden = true;

  if (view.sheet) return;
  if (!s) return draw('loading', '<div class="playing"><p>Starting…</p></div>');
  if (s.playing) return playingView(s);
  if (!s.setupDone || s.setup.running || s.setup.problem) return setupView(s);
  homeView(s);
}

async function loadApps(refresh) {
  const r = await api('/api/apps' + (refresh ? '?refresh=1' : ''));
  if (r.ok) { view.apps = r.apps; render(); }
}

let wasPlaying = false, wasSetup = false;
async function tick() {
  const s = await api('/api/state');
  if (s && s.version) {
    view.state = s;
    if (s.setupDone && (!view.apps || (wasPlaying && !s.playing) || !wasSetup)) loadApps(wasPlaying && !s.playing);
    wasPlaying = s.playing;
    wasSetup = s.setupDone;
    render();
    // Deep link: #settings opens the stream settings sheet once the app is set up.
    if (!view.deepLinked && location.hash === '#settings' && s.setupDone) { view.deepLinked = true; openSheet(); }
  }
  setTimeout(tick, s?.setup?.running ? 700 : s?.playing ? 2000 : 3000);
}

async function play(app) {
  if (view.busy) return;
  view.busy = true;
  render();
  if (!view.state.host.online) toast('Waking your PC…', false, 60000);
  const r = await api('/api/play', { app });
  view.busy = false;
  if (!r.ok) toast(r.error, true);
  else { localStorage.setItem('lastApp', app); $('#toast').classList.remove('show'); }
  view.drawn = '';
  const s = await api('/api/state');
  if (s && s.version) view.state = s;
  render();
}

document.addEventListener('click', async e => {
  if (e.target.closest('#btnSettings')) return openSheet();
  if (e.target.closest('#btnFull')) return window.chrome?.webview?.postMessage('fullscreen');
  if (e.target.id === 'scrim') return closeSheet();

  const set = e.target.closest('[data-set]');
  if (set) {
    const raw = set.dataset.value;
    return saveSetting(set.dataset.set, set.dataset.set === 'fps' || set.dataset.set === 'bitrateMbps' ? Number(raw) : raw);
  }

  const b = e.target.closest('[data-do]');
  if (!b) return;
  switch (b.dataset.do) {
    case 'setup': await api('/api/setup/start', {}); view.drawn = ''; return tick0();
    case 'choose': await api('/api/setup/start', { host: b.dataset.host }); return tick0();
    case 'connect': {
      const host = $('#hostInput').value.trim();
      if (!host) return toast('Type your PC\'s address first. Solstice shows it on the PC\'s Home page.', true);
      $('#hostInput').blur();
      await api('/api/setup/start', { host });
      return tick0();
    }
    case 'moonlight-site': return api('/api/open', { what: 'moonlight-site' });
    case 'open-moonlight': return api('/api/open', { what: 'moonlight' });
    case 'play': return play(b.dataset.app);
    case 'sheet-close': return closeSheet();
    case 'host-quit': {
      b.classList.add('busy');
      const r = await api('/api/host/quit', {});
      b.classList.remove('busy');
      return toast(r.ok ? 'Asked your PC to close the running game.' : r.error, !r.ok);
    }
    case 'forget':
      await api('/api/forget', {});
      view.apps = null;
      closeSheet();
      return tick0();
  }
});

document.addEventListener('change', e => {
  if (e.target.dataset.toggle) saveSetting(e.target.dataset.toggle, e.target.checked);
});

async function tick0() {
  const s = await api('/api/state');
  if (s && s.version) { view.state = s; view.drawn = ''; render(); }
}

/* ---------- gamepad and arrow-key navigation ---------- */

function focusables() {
  const scope = view.sheet ? $('#scrim') : document;
  return [...scope.querySelectorAll('button:not([disabled]), input:not([disabled])')].filter(el => {
    if (!view.sheet && el.closest('#scrim')) return false;
    const r = el.getBoundingClientRect();
    return r.width > 0 && r.height > 0;
  });
}

function move(dir) {
  const els = focusables();
  if (!els.length) return;
  const cur = document.activeElement;
  if (!els.includes(cur)) return els[0].focus();
  const c = cur.getBoundingClientRect();
  const cx = c.left + c.width / 2, cy = c.top + c.height / 2;
  let best = null, bestScore = Infinity;
  for (const el of els) {
    if (el === cur) continue;
    const r = el.getBoundingClientRect();
    const dx = r.left + r.width / 2 - cx, dy = r.top + r.height / 2 - cy;
    const along = dir === 'left' ? -dx : dir === 'right' ? dx : dir === 'up' ? -dy : dy;
    const across = dir === 'left' || dir === 'right' ? Math.abs(dy) : Math.abs(dx);
    if (along < 6) continue;
    const score = along + across * 2.5;
    if (score < bestScore) { best = el; bestScore = score; }
  }
  if (best) { best.focus(); best.scrollIntoView({ block: 'nearest', behavior: 'smooth' }); }
}

document.addEventListener('keydown', e => {
  const typing = e.target.tagName === 'INPUT' && e.target.type === 'text';
  if (e.key === 'Escape' && view.sheet) return closeSheet();
  if (e.key === 'Enter' && e.target.id === 'hostInput') return $('[data-do="connect"]').click();
  if (typing && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) return;
  const dir = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' }[e.key];
  if (dir) { e.preventDefault(); move(dir); }
});

const pad = { held: '', since: 0, repeated: 0, a: false, b: false, y: false };
function padLoop() {
  requestAnimationFrame(padLoop);
  if (!document.hasFocus() || view.state?.playing) return;
  const gp = [...navigator.getGamepads()].find(g => g && g.connected);
  if (!gp) return;
  const btn = i => !!gp.buttons[i]?.pressed;
  const ax = i => gp.axes[i] || 0;
  const dir = btn(12) || ax(1) < -0.6 ? 'up' : btn(13) || ax(1) > 0.6 ? 'down'
    : btn(14) || ax(0) < -0.6 ? 'left' : btn(15) || ax(0) > 0.6 ? 'right' : '';
  const now = performance.now();
  if (dir !== pad.held) {
    pad.held = dir; pad.since = now; pad.repeated = 0;
    if (dir) move(dir);
  } else if (dir && now - pad.since > 420 + pad.repeated * 140) {
    pad.repeated++;
    move(dir);
  }
  if (btn(0) && !pad.a) {
    const el = document.activeElement;
    if (el && el !== document.body) el.type === 'checkbox' ? el.click() : el.click();
  }
  if (btn(1) && !pad.b && view.sheet) closeSheet();
  if (btn(3) && !pad.y && view.state?.setupDone && !view.sheet) openSheet();
  pad.a = btn(0); pad.b = btn(1); pad.y = btn(3);
}

render();
tick();
requestAnimationFrame(padLoop);

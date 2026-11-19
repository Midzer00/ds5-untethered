// Live previews of the app's own UI, built from assets/app-ui/labels.json (generated from the app's sources by
// site-yeni/tools/extract_app_ui.py). Every control is a real button or input; changes bubble as "au:change" events.
import { base } from './assets.js';
import { lang } from './core.js';

let L = {}, PRESETS = [], ICONS = {}, NAV = {}, WEIGHT = 1.6;
export const T = k => L[k] ?? k;
export const presets = () => PRESETS;

export async function loadUi() {
  try {
    const r = await fetch(`${base}app-ui/labels.json`, { cache: 'no-cache' });
    const j = await r.json();
    L = j[lang] || j.en; PRESETS = j.presets || []; ICONS = j.icons || {}; NAV = j.navIcons || {}; WEIGHT = j.iconWeight || 1.6;
    return true;
  } catch { return false; }
}

const SVG = 'http://www.w3.org/2000/svg';
export function h(tag, cls, attrs = {}, kids = []) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  for (const [k, v] of Object.entries(attrs)) if (v !== undefined && v !== null) e.setAttribute(k, v);
  for (const k of [].concat(kids)) if (k !== null && k !== undefined) e.append(k);
  return e;
}
export function icon(name, cls = '') {
  const s = document.createElementNS(SVG, 'svg');
  s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('class', `au-ic ${cls}`.trim()); s.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(SVG, 'path');
  p.setAttribute('d', ICONS[name] || name); p.setAttribute('stroke-width', WEIGHT);
  s.append(p);
  return s;
}
const chevron = () => { const s = icon('M6.5,9.5 L12,15 L17.5,9.5'); s.classList.add('au-chev'); return s; };
export const still = el => { el.inert = true; el.classList.add('au-still'); return el; };
const emit = (el, key, value) => el.dispatchEvent(new CustomEvent('au:change', { bubbles: true, detail: { key, value } }));
export const pct = v => (lang === 'tr' ? `%${Math.round(v)}` : `${Math.round(v)}%`);

// Segmented choice: radio buttons in one track.
export function seg(key, items, index, label) {
  const el = h('div', 'au-seg', { role: 'radiogroup', 'aria-label': label });
  const btns = items.map((t, i) => h('button', '', { type: 'button', role: 'radio' }, t));
  const set = (i, user) => {
    btns.forEach((b, n) => { b.setAttribute('aria-checked', n === i); b.tabIndex = n === i ? 0 : -1; });
    el.value = i; if (user) emit(el, key, i);
  };
  btns.forEach((b, i) => {
    b.addEventListener('click', () => set(i, true));
    b.addEventListener('keydown', e => {
      const d = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key]; if (!d) return;
      e.preventDefault(); const n = (el.value + d + btns.length) % btns.length; set(n, true); btns[n].focus();
    });
  });
  el.append(...btns); el.set = set; set(index);
  return el;
}

// Picker: a button with the chosen text; with options it opens a list above itself (as in the Overview).
export function combo(key, text, { iconName, options, index = 0, label } = {}) {
  const wrap = h('div', 'au-combo-wrap');
  const btn = h('button', 'au-combo', { type: 'button', 'aria-label': label, 'aria-haspopup': options ? 'listbox' : null, 'aria-expanded': options ? 'false' : null });
  const txt = h('span', '', {}, text);
  btn.append(...(iconName ? [icon(iconName)] : []), txt, chevron());
  wrap.append(btn); wrap.button = btn;
  if (!options) return wrap;
  const pop = h('div', 'au-pop', { role: 'listbox', 'aria-label': label });
  const opts = options.map((o, i) => h('button', '', { type: 'button', role: 'option', tabindex: '-1' }, o));
  pop.append(...opts); wrap.append(pop); wrap.pop = pop; wrap.options = opts;
  wrap.open = (o, user) => { pop.classList.toggle('open', o); btn.setAttribute('aria-expanded', o); if (o && user) opts[wrap.value]?.focus({ preventScroll: true }); };
  wrap.select = (i, user) => {
    wrap.value = i; txt.textContent = options[i];
    opts.forEach((b, n) => b.setAttribute('aria-selected', n === i));
    if (user) { wrap.open(false); btn.focus({ preventScroll: true }); emit(wrap, key, i); }
  };
  btn.addEventListener('click', () => wrap.open(!pop.classList.contains('open'), true));
  opts.forEach((b, i) => b.addEventListener('click', () => wrap.select(i, true)));
  pop.addEventListener('keydown', e => {
    const d = { ArrowDown: 1, ArrowUp: -1 }[e.key], cur = opts.indexOf(document.activeElement);
    if (d) { e.preventDefault(); opts[(cur + d + opts.length) % opts.length].focus(); }
    if (e.key === 'Escape') { wrap.open(false); btn.focus(); }
  });
  wrap.select(index);
  return wrap;
}

// Quick toggle tile.
export function tile(key, iconName, label, on, { more, cls = '' } = {}) {
  const b = h('button', `au-tile ${cls}`.trim(), { type: 'button', 'aria-pressed': on, 'aria-label': label });
  const state = h('span', 'au-tile-state');
  b.append(icon(iconName), h('span', 'au-tile-text', {}, [h('span', 'au-tile-label', {}, label), state]));
  if (more) { b.append(h('span', 'au-tile-more', {}, icon('ChevronRight', 'sm'))); b.querySelector('.au-tile-text').style.marginRight = '30px'; }
  b.set = (v, user) => { b.on = v; b.setAttribute('aria-pressed', v); state.textContent = T(v ? 'on' : 'off'); if (user) emit(b, key, v); };
  b.addEventListener('click', () => b.set(!b.on, true));
  b.set(on);
  return b;
}

export function slider(key, label, value, max, fmt = pct) {
  const ctl = h('div', 'au-ctl wide');
  const input = h('input', 'au-slider', { type: 'range', min: 0, max, step: 1, value, 'aria-label': label });
  const out = h('span', 'au-value');
  const paint = () => { input.style.setProperty('--p', `${(input.value / max) * 100}%`); out.textContent = fmt(+input.value); };
  input.addEventListener('input', () => { paint(); emit(input, key, +input.value); });
  ctl.append(input, out); ctl.input = input;
  ctl.set = v => { input.value = v; paint(); };
  paint();
  return ctl;
}

export function toggle(key, label, on) {
  const b = h('button', 'au-switch', { type: 'button', role: 'switch', 'aria-checked': on, 'aria-label': label });
  b.set = (v, user) => { b.on = v; b.setAttribute('aria-checked', v); if (user) emit(b, key, v); };
  b.addEventListener('click', () => b.set(!b.on, true));
  b.set(on);
  return b;
}

function chips(key, items, index, label) {
  const el = h('div', 'au-ctl wrap', { role: 'radiogroup', 'aria-label': label });
  const btns = items.map(t => h('button', 'au-chip', { type: 'button', role: 'radio' }, t));
  el.set = (i, user) => { btns.forEach((b, n) => b.setAttribute('aria-checked', n === i)); el.value = i; if (user) emit(el, key, i); };
  btns.forEach((b, i) => b.addEventListener('click', () => el.set(i, true)));
  el.append(...btns); el.set(index);
  return el;
}

const srow = (title, note, control, tag) => h('div', 'au-srow', {}, [
  h('div', 'au-srow-text', {}, [h('div', 'au-srow-title', {}, [h('span', '', {}, title), tag ? h('span', 'au-tag', {}, tag) : null]), note ? h('span', 'au-note', {}, note) : null]),
  control,
]);
const qrow = (iconName, label, control) => h('div', 'au-qrow', {}, [icon(iconName), h('span', 'au-label', {}, label), control]);
const cardHead = (iconName, title, note) => h('div', 'au-card-head', { style: 'margin-bottom:4px' }, [
  h('div', '', { style: 'display:flex;align-items:center;gap:8px' }, [iconName ? icon(iconName, 'accent') : null, h('span', 'au-h2', {}, title)]),
  note ? h('p', 'au-caption', { style: 'margin-top:2px' }, note) : null,
]);

// ---- Overview: header, Quick Settings, toast ------------------------------------------------------------------------------
export function overview() {
  const back = h('button', 'au-btn lg primary', { type: 'button', 'data-au': 'back' }, [icon('Back'), T('backToGame')]);
  const head = h('div', 'au-head', {}, [
    h('div', 'au-head-title', {}, [h('span', 'au-h1', {}, T('overview')), h('span', 'au-body', {}, T('running'))]),
    back,
    still(h('button', 'au-btn lg', { type: 'button' }, [icon('Power', 'accent'), T('turnOffController')])),
  ]);
  const P = {
    lowPower: tile('lowPower', 'Leaf', T('lowPower'), false, { cls: 'wide' }),
    mouse: tile('mouse', 'Mouse', T('mouseMode'), false),
    shortcuts: tile('shortcuts', 'Shortcut', T('shortcuts'), true, { more: true }),
    speaker: tile('speaker', 'Speaker', T('speaker'), true),
    mic: tile('mic', 'Microphone', T('microphone'), true),
    micDevice: still(combo('micDevice', T('controllerMic'), { iconName: 'Microphone', label: T('microphone') })),
    micButton: seg('micButton', [T('mute'), T('turnOff')], 1, T('micButton')),
    padType: still(combo('padType', T('automatic'), { label: T('controllerType') })),
    haptics: seg('haptics', [T('stepLight'), T('stepMedium'), T('stepStrong')], 2, T('hapticsStrength')),
    trigger: seg('trigger', [T('stepLight'), T('stepMedium'), T('stepStrong')], 2, T('triggerStrength')),
    light: seg('light', [T('stepOff'), T('stepLow'), T('stepHigh')], 2, T('light')),
    profile: combo('profile', T('default'), { options: [T('default'), ...profileNames()], index: 0, label: T('profile') }),
  };
  P.micDevice.button.classList.add('au-combo-dev');
  const card = h('div', 'au-panel au-quick', {}, [
    h('div', 'au-quick-top', {}, [h('span', 'au-h2', {}, T('quickSettings')), still(h('button', 'au-link', { type: 'button' }, T('advanced')))]),
    h('div', 'au-tiles', {}, [P.lowPower, P.mouse, P.shortcuts, P.speaker, P.mic]),
    h('div', 'au-qgap-l'), P.micDevice, h('div', 'au-qgap'),
    qrow('MicButton', T('micButton'), P.micButton), h('div', 'au-qgap-l'),
    qrow('Gamepad', T('controllerType'), P.padType), h('div', 'au-qgap'),
    qrow('Haptics', T('hapticsStrength'), P.haptics), h('div', 'au-qgap'),
    qrow('Trigger', T('triggerStrength'), P.trigger), h('div', 'au-qgap'),
    qrow('Light', T('light'), P.light), h('div', 'au-qgap'),
    qrow('Profile', T('profile'), P.profile),
  ]);
  return { head, card, back, P };
}
// Example profile names, the same ones as the Profiles section of the site
function profileNames() { return lang === 'tr' ? ['Yarış', 'Rahat'] : ['Racing', 'Relaxed']; }

const MARK = () => {
  const s = document.createElementNS(SVG, 'svg'); s.setAttribute('viewBox', '0 0 24 24'); s.setAttribute('aria-hidden', 'true');
  s.innerHTML = '<circle cx="12" cy="12" r="7.5" fill="none" stroke="currentColor" stroke-width="3"/><path d="M19.5 2.5 4.5 21.5" stroke="currentColor" stroke-width="2.6"/>';
  return s;
};
export function toast(base) {
  // As the app draws it: its icon and name, a close cross, then its controller picture beside the title and the body
  const title = h('div', 'au-toast-title'), text = h('div', 'au-toast-text'), art = `${base}app-ui/img/controller.webp`;
  const el = h('div', 'au au-toast', { role: 'status' }, [
    h('div', 'au-toast-head', {}, [h('img', 'au-toast-icon', { src: `${base}app-ui/img/icon.webp`, alt: '' }), h('span', 'au-toast-name', {}, T('appName')), h('span', 'au-toast-x', { 'aria-hidden': 'true' }, icon('M7,7 L17,17 M17,7 L7,17', 'sm'))]),
    h('div', 'au-toast-body', {}, [h('img', 'au-toast-art', { src: art, alt: '' }), h('div', '', { style: 'min-width:0' }, [title, text])]),
  ]);
  el.show = on => { title.textContent = T(on ? 'mouseOnTitle' : 'mouseOffTitle'); text.textContent = T(on ? 'mouseOnBody' : 'mouseOffBody'); };
  el.show(true);
  return el;
}

// ---- Feature cards -----------------------------------------------------------------------------------------------------
export function card(name) {
  const C = {};
  let el;
  if (name === 'effects') {
    C.haptics = slider('gameHaptics', T('gameHaptics'), 150, 200);
    C.trigger = slider('triggerStrength', T('triggerStrength'), 100, 100);
    el = h('div', 'au-panel', {}, [cardHead('Haptics', T('gameEffects'), T('gameEffectsNote')),
      srow(T('gameHaptics'), T('gameHapticsNote'), C.haptics), h('div', 'au-divider'), srow(T('triggerStrength'), T('triggerStrengthNote'), C.trigger)]);
  } else if (name === 'trigger') {
    C.trigger = slider('triggerStrength', T('triggerStrength'), 100, 100);
    el = h('div', 'au-panel', { style: 'padding-top:6px;padding-bottom:6px' }, [srow(T('triggerStrength'), T('triggerStrengthNote'), C.trigger)]);
  } else if (name === 'xbox') {
    C.xbox = toggle('xboxRumble', T('xboxRumble'), false);
    el = h('div', '', {}, [h('p', 'au-section', {}, T('triggers')),
      h('div', 'au-panel', { style: 'padding-top:4px;padding-bottom:4px' }, [srow(T('xboxRumble'), T('xboxRumbleNote'), h('div', 'au-ctl', {}, C.xbox), T('experimental'))])]);
  } else if (name === 'classic') {
    C.rumble = slider('rumble', T('rumble'), 100, 200);
    C.feel = chips('rumbleFeel', [T('soft'), T('punchy')], 0, T('rumbleFeel'));
    el = h('div', 'au-panel', {}, [cardHead('Haptics', T('classicRumble'), T('classicRumbleNote')),
      srow(T('rumble'), T('rumbleNote'), C.rumble), h('div', 'au-divider'), srow(T('rumbleFeel'), T('rumbleFeelNote'), C.feel)]);
  } else if (name === 'light') {
    const name0 = h('span', 'au-body');
    const sw = h('div', 'au-swatches', { role: 'radiogroup', 'aria-label': T('lightbarColour') });
    const btns = PRESETS.map(p => h('button', 'au-swatch', { type: 'button', role: 'radio', 'aria-label': p.name[lang] || p.name.en }, h('i', '', { style: `background:${p.rgb}` })));
    sw.set = (i, user) => {
      btns.forEach((b, n) => { b.setAttribute('aria-checked', n === i); b.tabIndex = n === i ? 0 : -1; });
      name0.textContent = PRESETS[i].name[lang] || PRESETS[i].name.en; sw.value = i;
      if (user) emit(sw, 'colour', i);
    };
    btns.forEach((b, i) => {
      b.addEventListener('click', () => sw.set(i, true));
      b.addEventListener('keydown', e => { const d = { ArrowRight: 1, ArrowLeft: -1 }[e.key]; if (!d) return; e.preventDefault(); const n = (sw.value + d + btns.length) % btns.length; sw.set(n, true); btns[n].focus(); });
    });
    sw.append(...btns); sw.set(0); C.swatches = sw;
    el = h('div', 'au-panel', {}, [
      h('div', 'au-srow', { style: 'min-height:40px' }, [h('span', 'au-h2', {}, T('lightMode')), h('div', 'au-ctl', { style: 'width:200px' }, still(combo('lightMode', T('static'), { label: T('lightMode') })))]),
      h('p', 'au-body', { style: 'margin:6px 0 14px' }, T('staticNote')), h('div', 'au-divider'),
      h('div', '', { style: 'display:flex;justify-content:space-between;align-items:baseline;margin:14px 0 10px' }, [h('span', 'au-h2', {}, T('lightbarColour')), name0]),
      sw,
    ]);
  } else if (name === 'audio') {
    C.speaker = tile('speaker', 'Speaker', T('speaker'), true);
    C.mic = tile('mic', 'Microphone', T('microphone'), true);
    C.micButton = seg('micButton', [T('mute'), T('turnOff')], 1, T('micButton'));
    el = h('div', 'au-panel au-quick', { style: 'width:auto' }, [h('div', 'au-tiles', {}, [C.speaker, C.mic]), h('div', 'au-qgap'),
      still(combo('micDevice', T('controllerMic'), { iconName: 'Microphone', label: T('microphone') })), h('div', 'au-qgap'), qrow('MicButton', T('micButton'), C.micButton)]);
  } else if (name === 'rightClick') {
    C.rc = seg('rightClick', [T('rcOff'), T('rcRightSide'), T('rcTwoFingers')], 0, T('rightClick'));
    el = h('div', 'au-panel', { style: 'padding:4px 14px' }, [h('div', 'au-srow', { style: 'min-height:44px' }, [h('span', '', {}, T('rightClick')), h('div', 'au-ctl', { style: 'width:240px' }, C.rc)])]);
    C.rc.style.width = '100%';
  }
  el.classList.add('au');
  return { el, C };
}

// ---- The whole Overview window (1280 x 720 app px, the app at its 16:9 size) ---------------------------------------------
const battery = (pct = 78) => {
  const s = document.createElementNS(SVG, 'svg'); s.setAttribute('viewBox', '0 0 22 11'); s.setAttribute('class', 'au-batt'); s.setAttribute('aria-hidden', 'true');
  s.innerHTML = `<rect x=".75" y=".75" width="18.5" height="9.5" rx="2.5" fill="none" stroke="currentColor" stroke-width="1.3"/><rect x="20.2" y="3.5" width="1.4" height="4" rx=".6" fill="currentColor"/><rect x="2.6" y="2.6" width="${(14.8 * pct / 100).toFixed(1)}" height="5.8" rx="1.2" fill="currentColor"/>`;
  return s;
};
function graph() {
  const s = document.createElementNS(SVG, 'svg'); s.setAttribute('viewBox', '0 0 370 150'); s.setAttribute('preserveAspectRatio', 'none'); s.setAttribute('class', 'au-graph'); s.setAttribute('aria-hidden', 'true');
  const wave = (a, f, p, y0) => Array.from({ length: 75 }, (_, i) => { const x = i * 5; return `${x},${(y0 + a * Math.sin(x / 370 * Math.PI * 2 * f + p)).toFixed(1)}`; }).join(' ');
  const top = wave(10, 2, 0.6, 92), low = wave(3, 2, 0.2, 128);
  s.innerHTML = `<line x1="0" y1="20" x2="370" y2="20" class="g-l"/><line x1="0" y1="75" x2="370" y2="75" class="g-l"/><line x1="0" y1="148" x2="370" y2="148" class="g-l"/>
    <polygon points="0,150 ${top} 370,150" class="g-f"/><polyline points="${low}" class="g-t"/><polyline points="${top}" class="g-w"/><circle cx="366" cy="${(92 + 10 * Math.sin(Math.PI * 4 * 366 / 370 + 0.6)).toFixed(1)}" r="3.5" class="g-d"/>`;
  return s;
}
const fmtPct = v => (lang === 'tr' ? `%${v}` : `${v}%`);
const meter = (label, pct, cls = '') => h('div', 'au-meterrow', {}, [h('span', 'au-caption', {}, label), h('span', `au-meter ${cls}`, { style: `--v:${pct}%` }), h('span', 'au-mval', {}, fmtPct(pct))]);

export function overviewWindow(base) {
  const O = overview();
  const navKeys = [['Overview', 'navOverview'], ['SupportedGames', 'navGames'], ['Haptics', 'navHaptics'], ['Controller', 'navController'], ['Light', 'navLight'], ['Settings', 'navSettings']];
  const nav = h('div', 'au-nav', {}, navKeys.map(([ic, k], i) => still(h('div', `au-navitem${i ? '' : ' sel'}`, {}, [icon(NAV[ic] || ic), h('span', '', {}, T(k))]))));
  const side = h('aside', 'au-side', {}, [
    h('img', 'au-logo', { src: `${base}app-ui/img/logo.webp`, alt: '' }),
    nav,
    h('div', 'au-side-foot', {}, [
      h('div', 'au-pills', {}, [still(h('span', 'au-pill support', {}, [icon('Heart'), T('support')])), still(h('span', 'au-pill star', {}, [icon('Star'), T('star')]))]),
      h('div', 'au-panel au-dev', {}, [
        h('div', 'au-dev-top', {}, [h('img', 'au-dev-art', { src: `${base}app-ui/img/controller.webp`, alt: '' }),
          h('div', '', {}, [h('div', 'au-dev-name', {}, ['DualSense', icon('Pencil', 'sm')]), h('div', 'au-caption', {}, T('viaBluetooth'))])]),
        h('div', 'au-divider'),
        h('div', 'au-dev-row', {}, [battery(), h('span', '', {}, fmtPct(78))]),
        h('div', 'au-dev-row sp', {}, [h('span', '', {}, T('virtualController')), h('span', 'on', {}, T('on'))]),
        h('div', 'au-dev-row b', {}, T('gameSees')),
        h('div', 'au-caption', {}, T('profileLine')),
      ]),
    ]),
  ]);
  const game = h('div', 'au-panel au-game', {}, [h('img', 'au-game-art', { src: `${base}app-ui/img/controller.webp`, alt: '' }),
    h('div', '', {}, [h('div', 'au-caption', {}, T('gameCaption')), h('div', 'au-display', {}, T('game')), h('div', 'au-dev-row', {}, [battery(), h('span', 'au-body', {}, fmtPct(78))]), h('div', 'au-dev-row b', {}, T('gameSees'))])]);
  const live = h('div', 'au-panel au-live', {}, [
    h('div', 'au-live-top', {}, [h('span', 'au-h2', {}, T('live')), h('span', 'au-caption', {}, T('last30'))]),
    h('div', 'au-live-top', {}, [h('span', 'au-stat', {}, T('delay')), h('span', 'au-good', {}, T('good'))]),
    h('div', 'au-caption', { style: 'margin-top:-2px' }, T('delayWorst')),
    h('div', 'au-graphbox', {}, [graph(), h('span', 'au-gl a', {}, T('underLimit')), h('span', 'au-gl b', {}, '10 ms'), h('span', 'au-gl c', {}, '5'), h('span', 'au-gl d', {}, '0')]),
    h('div', 'au-live-top', {}, [h('span', 'au-body', {}, T('inputRateLabel')), h('span', '', {}, [h('span', 'au-caption', {}, T('rateMin') + '  '), h('b', '', {}, T('inputRate'))])]),
    h('div', 'au-live-cols', {}, [
      h('div', '', {}, [h('div', 'au-section', {}, T('liveTriggers')), meter('L2', 27), meter('R2', 82)]),
      h('div', '', {}, [h('div', 'au-section', {}, T('liveHaptics')), meter(T('left'), 46, 'hap'), meter(T('right'), 38, 'hap')]),
    ]),
  ]);
  const content = h('div', 'au-content', {}, [O.head, h('div', 'au-cols', {}, [h('div', 'au-col', {}, [game, live]), O.card]), h('div', 'au-caption au-health', {}, T('allGood'))]);
  const el = h('div', 'au au-window au-overview', { role: 'group' }, [side, h('div', 'au-hair'), content]);
  return { el, O };
}

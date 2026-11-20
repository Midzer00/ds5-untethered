// Features: one controller, six parts. A list row or a hotspot selects what the stage shows.
import { Overlay, Masks, glow } from './stage.js';
import { clamp, fit, lerp, sstep, MONO, upper, cine, col } from './core.js';
import { presets } from './app-ui.js';

const ADVANCE_S = 9, TRIG_SPAN = 0.22;
const FEEL = [{ amp: 0.86, hz: 1, edge: 0.32, fine: 1 }, { amp: 0.5, hz: 0.5, edge: 0.75, fine: 0 }, { amp: 1, hz: 0.72, edge: 0.04, fine: 0.2 }];
const ENV = [
  t => 0.35 + 0.5 * (0.5 + 0.5 * Math.sin(t * 0.8)),
  t => (((t * 0.34) % 1) < 0.34 ? 0.92 : 0.04),
  t => (((t * 0.34 + 0.08) % 1) < 0.26 ? (((t * 5) % 1) < 0.5 ? 1 : 0.06) : 0.03),
  t => { const v = Math.sin(t * 1.3) * Math.sin(t * 2.9 + 1); return v > 0.2 ? 0.45 + v * 0.55 : 0.03; },
];
const curve = x => (x < 0.28 ? 0.08 + x * 0.1 : x < 0.5 ? lerp(0.108, 0.9, sstep(0.28, 0.5, x)) : x < 0.72 ? 0.9 - (x - 0.5) * 0.3 : lerp(0.834, 0.3, sstep(0.72, 0.84, x)));

export function featuresScene(engine, el, M, cards = {}) {
  const S = M.stage, $ = s => el.querySelector(s), $$ = s => [...el.querySelectorAll(s)];
  const pad = $('.st-pad'), tabs = $$('[role=tab]'), panels = $$('.f-panel'), extras = $$('.st-x'), mats = $$('.st-mats .mat'), hotBox = $('.st-hots');
  const names = panels.map(p => p.dataset.f), ov = new Overlay(pad, S.w);
  const trigCv = $('.trig-cv'), curveCv = $('.st-curve'), meters = $$('.meter canvas'), feels = $$('[data-feel]'), soundCv = $('.sound-cv');
  // A hotspot sits on its part: where a mask exists, its centre wins over the listed point.
  const masks = new Masks(S, () => {
    for (const [id, mask] of [['mic', 'mic'], ['speaker', 'speaker']]) {
      const b = masks.bbox(mask), h = hots.find(e => e.dataset.id === id);
      if (b && h) { h.style.left = `${(b[0] + b[2] / 2) / S.w * 100}%`; h.style.top = `${(b[1] + b[3] / 2) / S.h * 100}%`; }
    }
    engine.refresh(scene);
  });
  const labels = JSON.parse(hotBox.dataset.labels);
  let cur = 0, time = 0, level = 0, auto = cine, wait = 0, seen = false, held = false, feel = 0, led = presets()[0]?.rgb || '--led-blue', shown = { ...FEEL[0] };
  masks.warm([['grip-left', '--powder'], ['grip-right', '--powder'], ['triggers', '--powder'], ['strip', led], ['mic', '--powder'], ['speaker', '--powder']]);
  // Values from the app previews under the stage: they drive the lit parts and the illustrations.
  const fx = { haptics: 1, trigger: 1, xbox: false, speaker: true, mic: true, rumble: 1, punchy: false };
  let xboxTouched = false, xboxTimer = 0;

  const hots = S.hot.map(h => {
    const link = h.f === 'mouse-mode', b = document.createElement(link ? 'a' : 'button');
    if (link) b.href = '#mouse-mode'; else b.type = 'button';
    b.className = 'hs'; b.dataset.f = h.f; b.dataset.id = h.id;
    b.style.left = `${h.x / S.w * 100}%`; b.style.top = `${h.y / S.h * 100}%`;
    b.setAttribute('aria-label', labels[h.f]);
    b.innerHTML = `<i></i><span>${labels[h.f]}</span>`;
    if (h.x > S.w / 2) b.classList.add('flip');
    if (!link) b.addEventListener('click', () => pick(names.indexOf(h.f)));
    hotBox.appendChild(b);
    return b;
  });
  const show = (i, focus) => {
    cur = (i + names.length) % names.length; time = 0; level = cine ? 0 : 1; wait = 0;
    tabs.forEach((t, n) => { t.setAttribute('aria-selected', n === cur); t.tabIndex = n === cur ? 0 : -1; });
    panels.forEach((p, n) => p.classList.toggle('on', n === cur));
    extras.forEach(x => x.classList.toggle('on', x.dataset.f === names[cur]));
    mats.forEach(m => m.classList.toggle('on', m.dataset.f === names[cur]));
    if (names[cur] === 'xbox') demoXbox();
    hots.forEach(h => h.classList.toggle('on', h.dataset.f === names[cur]));
    const t = tabs[cur], list = t.parentElement;
    if (list.scrollWidth > list.clientWidth) list.scrollTo({ left: t.offsetLeft - (list.clientWidth - t.offsetWidth) / 2 });
    if (focus) t.focus({ preventScroll: true });
    fitPhone();
    engine.refresh(scene);
  };
  const pick = (i, focus) => { auto = false; show(i, focus); bringPhone(); };

  // Phones: a picked feature fits one screen from its tab row (just under the top OSD) to above the bottom bar. Where it
  // would not, the illustration goes first, then the app preview is laid out wider and scaled down (like the What works
  // window) to no less than 80%; the text is never cut. Each tab keeps its own height.
  const phone = matchMedia('(max-width:767px)'), calm = matchMedia('(prefers-reduced-motion:reduce)');
  const list = tabs[0].parentElement, matBox = $('.st-mats'), osdLow = document.querySelector('.osd-bottom');
  const probe = document.createElement('div');
  probe.style.cssText = 'position:absolute;top:0;left:0;width:0;height:100svh;visibility:hidden;pointer-events:none';
  el.append(probe);
  const lines = () => {
    const top = parseFloat(getComputedStyle(el).scrollMarginTop) || 64;
    const low = osdLow ? innerHeight - osdLow.getBoundingClientRect().top : 50;
    return [top, (probe.offsetHeight || innerHeight) - low - 16]; // the small viewport: the URL bar may be showing
  };
  const unfit = () => {
    panels.forEach(p => p.classList.remove('f-tight'));
    mats.forEach(m => { const h = m.querySelector('.au-host'); h?.removeAttribute('style'); });
  };
  function fitPhone() {
    unfit();
    if (!phone.matches) return;
    const panel = panels[cur], mat = mats.find(m => m.dataset.f === names[cur]), host = mat?.querySelector('.au-host');
    if (!mat || !host) return;
    const [top, bottom] = lines(), room = () => bottom - top - (matBox.getBoundingClientRect().top - list.getBoundingClientRect().top);
    const cap = mat.offsetHeight - host.offsetHeight, W = host.clientWidth;
    const size = k => {
      if (k >= 1) { host.removeAttribute('style'); return host.offsetHeight; }
      host.style.cssText = `width:${W / k}px;transform:scale(${k});transform-origin:0 0;margin-right:${W - W / k}px`;
      const h = host.offsetHeight;
      host.style.marginBottom = `${h * k - h}px`;
      return h * k;
    };
    // The largest scale that fits, 80% at least (heights fall as the preview widens, so a short search finds it).
    const solve = () => {
      const r = room() - cap;
      if (size(1) <= r) return 1;
      let lo = 0.8, hi = 1;
      for (let n = 0; n < 6; n++) { const k = (lo + hi) / 2; if (size(k) <= r) lo = k; else hi = k; }
      size(lo);
      return lo;
    };
    if (solve() < 0.85 && panel.querySelector('.f-ill')) { panel.classList.add('f-tight'); solve(); }
  }
  // After a tap, the page scrolls down only as far as the content needs, so a short tab keeps the controller in view;
  // it never goes past the tab row sitting under the OSD. A tab row hidden under the OSD is brought back down.
  function bringPhone() {
    if (!phone.matches) return;
    const [top, bottom] = lines(), mat = mats.find(m => m.dataset.f === names[cur]);
    const y = list.getBoundingClientRect().top, end = mat ? mat.getBoundingClientRect().bottom : y;
    if (y >= top && end <= bottom) return;
    const d = y < top ? y - top : Math.min(end - bottom, y - top);
    if (Math.abs(d) < 2) return;
    scrollTo({ top: scrollY + d, behavior: calm.matches ? 'auto' : 'smooth' });
  }
  let fitW = innerWidth, fitH = 0;
  const refit = () => { const h = probe.offsetHeight; if (innerWidth === fitW && h === fitH) return; fitW = innerWidth; fitH = h; fitPhone(); };
  addEventListener('resize', refit);
  phone.addEventListener?.('change', fitPhone);
  document.fonts?.ready.then(fitPhone);

  const drawOverlay = (t, a) => {
    const g = ov.begin(); if (!g) return;
    const name = names[cur], k = ov.k, px = v => v / k, grips = [S.gripLeft, S.gripRight], trig = [S.triggerLeft, S.triggerRight];
    const rings = (p, n = 3) => {
      for (let i = 0; i < n; i++) {
        const u = cine ? (t / 1.2 + i / n) % 1 : (i + 0.5) / n;
        g.strokeStyle = col('--powder', 0.5 * (1 - u) * a); g.lineWidth = px(1);
        g.beginPath(); g.arc(p[0], p[1], px(4 + u * 60), 0, 7); g.stroke();
      }
    };
    const lit = (mask, fallback, level = 0.9) => { if (masks.has(mask)) masks.draw(g, mask, '--powder', level * a); else fallback(); };
    if (name === 'haptics' || name === 'sound') {
      const k = name === 'sound' ? fx.rumble : fx.haptics;
      if (k > 0) grips.forEach((p, i) => { lit(i ? 'grip-right' : 'grip-left', () => glow(g, p[0], p[1], 300, '--powder', 0.42 * a * k), 0.42 * Math.min(1.4, k)); rings(p); });
    }
    if (name === 'triggers' || name === 'xbox') {
      const k = name === 'xbox' ? (fx.xbox ? 1 : 0) : fx.trigger;
      if (k > 0) lit('triggers', () => trig.forEach(p => glow(g, p[0], p[1] + 20, 260, '--powder', 0.55 * a * k)), 0.5 * k);
    }
    if (name === 'light') { if (masks.has('strip')) { masks.draw(g, 'strip', led, a); } }
    if (name === 'audio') {
      if (fx.mic) { lit('mic', () => glow(g, S.mute[0], S.mute[1], 120, '--powder', 0.8 * a)); rings(S.mute, 2); }
      if (fx.speaker) { lit('speaker', () => glow(g, S.speaker[0], S.speaker[1], 170, '--powder', 0.7 * a)); rings(S.speaker, 2); }
    }
    if (name === 'sound') {
      lit('speaker', () => glow(g, S.speaker[0], S.speaker[1], 170, '--powder', 0.6 * a));
      g.strokeStyle = col('--paper', 0.8 * a); g.lineWidth = px(1); g.setLineDash([px(3), px(5)]);
      if (cine) g.lineDashOffset = -px(t * 14);
      for (const p of grips) { g.beginPath(); g.moveTo(S.speaker[0], S.speaker[1]); g.quadraticCurveTo((p[0] + S.speaker[0]) / 2, p[1] - 80, p[0], p[1]); g.stroke(); }
      g.setLineDash([]);
    }
  };
  // The thumbnail is the stage picture itself, cropped to the right trigger; its light follows the resistance curve.
  const drawTrigger = u => {
    const art = pad.querySelector('.art'), { ctx: c, w, h, dpr } = fit(trigCv, 2); if (!w || !h) return;
    c.clearRect(0, 0, w, h);
    if (!art?.complete || !art.naturalWidth) return;
    const k = w / (S.w * TRIG_SPAN), [x, y] = S.triggerRight;
    c.setTransform(dpr * k, 0, 0, dpr * k, dpr * (w / 2 - x * k), dpr * (h / 2 - y * k));
    c.drawImage(art, 0, 0, S.w, S.h);
    masks.draw(c, 'triggers', '--powder', (0.1 + 0.5 * curve(u)) * fx.trigger);
  };
  const drawCurve = u => {
    const { ctx: c, w, h } = fit(curveCv, 2); c.clearRect(0, 0, w, h);
    const X = x => 2 + x * (w - 4), Y = v => h - 3 - v * (h - 8);
    const cv = x => curve(x) * fx.trigger, line = (end, stroke, lw) => { c.strokeStyle = stroke; c.lineWidth = lw; c.beginPath(); for (let i = 0; i <= 80; i++) { const x = i / 80 * end; i ? c.lineTo(X(x), Y(cv(x))) : c.moveTo(X(x), Y(cv(x))); } c.stroke(); };
    line(1, col('--paper', 0.5), 1.5); line(u, col('--powder'), 2);
    c.fillStyle = col('--paper'); c.beginPath(); c.arc(X(u), Y(cv(u)), 4, 0, 7); c.fill();
  };
  const drawMeters = (T, dt) => {
    const k = cine ? 1 - Math.exp(-dt * 7) : 1, F = FEEL[feel];
    for (const n in shown) shown[n] = lerp(shown[n], F[n], k);
    meters.forEach((cv, m) => {
      const { ctx: c, w, h } = fit(cv, 2); if (!w) return; c.clearRect(0, 0, w, h);
      const mid = h / 2; c.strokeStyle = col('--paper', 0.14); c.lineWidth = 1; c.beginPath(); c.moveTo(0, mid); c.lineTo(w, mid); c.stroke();
      c.strokeStyle = col('--powder'); c.lineWidth = 1.5; c.beginPath();
      // The loudest moment in view reaches at least 80% of the half height (40% of the row) for every feel.
      const env = [];
      for (let x = 0; x <= w; x++) { const at = T - (w - x) / w * 2.6; let e = 0; for (let i = 0; i < 6; i++) e += ENV[m](at - i * shown.edge * 0.09); env.push([at, e / 6]); }
      const peak = Math.max(0.35, ...env.map(v => v[1])), gain = (0.8 + 0.2 * shown.amp) / peak;
      env.forEach(([at, e], x) => {
        const y = Math.sin(at * (24 + shown.fine * 54 * e) * shown.hz * 1.571) * e * gain * (mid - 2);
        x ? c.lineTo(x, mid + y) : c.moveTo(x, mid + y);
      });
      c.stroke();
    });
  };
  const drawSound = T => {
    const { ctx: c, w, h } = fit(soundCv, 2); if (!w || !h) return; c.clearRect(0, 0, w, h);
    const [from, to] = soundCv.dataset.labels.split('|').map(upper), mid = h / 2 + 6, split = w * (0.5 - 0.14 * Math.sin(T * 0.4)), bw = Math.max(4, w / 70), A = h * 0.34 * Math.min(1.3, fx.rumble);
    const lvl = x => { const u = x / w * 20; return clamp(0.18 + 0.82 * Math.abs(Math.sin(u * 0.7 + T * 1.5) * Math.sin(u * 1.9 - T * 2.3) + 0.3 * Math.sin(u * 4.3 + T * 4))); };
    c.fillStyle = col('--paper', 0.8);
    for (let x = bw / 2; x < split; x += bw * 1.7) { const v = lvl(x) * A; c.fillRect(x, mid - v, bw, v * 2); }
    c.strokeStyle = col('--powder'); c.lineWidth = 2; c.beginPath();
    for (let x = split; x <= w; x += 1.5) { const y = mid + Math.sin(x * (fx.punchy ? 0.3 : 0.16) - T * (fx.punchy ? 14 : 9)) * lvl(x) * A * sstep(split, split + w * 0.08, x); x === split ? c.moveTo(x, y) : c.lineTo(x, y); }
    c.stroke();
    c.font = `11px ${MONO}`; c.fillStyle = col('--mute'); c.textAlign = 'left'; c.fillText(from, 0, 11); c.textAlign = 'right'; c.fillText(to, w, 11);
  };

  const scene = {
    el, still: 1.6,
    update(T, dt) {
      time += dt;
      level = cine ? Math.min(1, level + dt / 0.32) : 1;
      const a = 1 - (1 - level) ** 3, name = names[cur], at = cine ? T : T;
      drawOverlay(at, a);
      if (name === 'triggers') { const u = 0.5 - 0.5 * Math.cos(at * Math.PI / 2.6); drawTrigger(u); drawCurve(u); }
      if (name === 'xbox') drawMeters(at, dt);
      if (name === 'sound') drawSound(at);
    },
  };
  engine.add(scene);
  pad.querySelector('.art')?.addEventListener('load', () => engine.refresh(scene));

  tabs.forEach((t, i) => t.addEventListener('click', () => pick(i)));
  tabs[0].parentElement.addEventListener('keydown', e => {
    const d = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
    if (d) { e.preventDefault(); pick(cur + d, true); }
    if (e.key === 'Home') { e.preventDefault(); pick(0, true); }
    if (e.key === 'End') { e.preventDefault(); pick(names.length - 1, true); }
  });
  feels.forEach((b, i) => b.addEventListener('click', () => { auto = false; feel = i; feels.forEach((o, n) => o.setAttribute('aria-pressed', n === i)); engine.refresh(scene); }));
  // The previews: each change is shown on the stage at once.
  el.addEventListener('au:change', e => {
    const { key, value } = e.detail;
    auto = false;
    if (key === 'gameHaptics') fx.haptics = value / 150;
    if (key === 'triggerStrength') { fx.trigger = value / 100; for (const c of [cards.effects, cards.trigger]) c?.trigger?.set(value); }
    if (key === 'xboxRumble') { fx.xbox = value; xboxTouched = true; }
    if (key === 'colour') led = presets()[value]?.rgb || led;
    if (key === 'speaker') fx.speaker = value;
    if (key === 'mic') fx.mic = value;
    if (key === 'rumble') fx.rumble = value / 100;
    if (key === 'rumbleFeel') fx.punchy = value === 1;
    engine.refresh(scene);
  });
  // Xbox Trigger Rumble is off by default; the demo switches it on once, so the stage shows what it does.
  const demoXbox = () => { clearTimeout(xboxTimer); if (cine && !xboxTouched && !fx.xbox) xboxTimer = setTimeout(() => { if (names[cur] === 'xbox' && !xboxTouched) { cards.xbox?.xbox?.set(true); fx.xbox = true; engine.refresh(scene); } }, 1400); };
  el.addEventListener('pointerenter', () => { held = true; }); el.addEventListener('pointerleave', () => { held = false; });
  el.addEventListener('focusin', () => { held = true; }); el.addEventListener('focusout', () => { held = false; });
  new IntersectionObserver(([e]) => { seen = e.isIntersecting; }, { rootMargin: '-25% 0px' }).observe(el);
  if (cine) setInterval(() => {
    if (!auto || !seen || held || document.hidden) return;
    wait += 0.25; if (wait >= ADVANCE_S) show(cur + 1);
  }, 250);
  show(0);
}

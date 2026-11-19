// Mouse Mode: a live copy of the app's whole Overview window on the TV beside the controller. A short timeline (about 12 s)
// loops while the section is in view: the TV shows the whole window and zooms toward the part each step is about; the pointer
// goes to the real elements, the Profile picker really opens and Back To Game is pressed. While the demo is paused, visitors
// can use the window themselves; playing again restores the step's state.
import { base, quadMatrix, quadPixels } from './assets.js';
import { Overlay, Masks, glow } from './stage.js';
import { clamp, lerp, sstep, pulse, cine, col } from './core.js';
import { overviewWindow, toast as makeToast } from './app-ui.js';
import { lightbox } from './app-shots.js';

// Step lengths in seconds: in game, short ø press, touchpad, a press is a click, profile, Back To Game, ø + Options.
// Steps after the first run 25 % slower than their timings below (STRETCH); the road opens the loop in 1 s.
const STRETCH = 1.25, DUR = [1.0, ...[1.8, 1.8, 1.2, 1.6, 2.0, 2.9].map(d => d * STRETCH)], START = DUR.reduce((a, d, i) => [...a, a[i] + d], [0]), LEN = START[DUR.length], LOOP_PAUSE = 0.8;
const TOAST_SCALE = 0.8, W = 800, APP_W = 1280, APP_H = 720, ZOOM = 1.8, ZOOM_PHONE = 2.73, OPTIONS = [0.757, 0.319]; // Options button on the stage picture
const mix = (p, q, e) => p.map((v, n) => lerp(v, q[n], e));
const ease = (a, b, s) => sstep(a, b, s);

export function mouseScene(engine, el, M, cards) {
  const $ = s => el.querySelector(s), S = M.stage, TV = M.tv;
  const tv = $('.tv'), mtv = $('.m-tv'), screen = $('.tv-screen'), over = $('.tv-over'), road = $('.tv-road').getContext('2d');
  const cur = $('.tv-cur'), ring = $('.tv-ring'), inset = $('.m-inset'), play = $('.m-play'), linkPath = $('.m-link path'), linkSvg = $('.m-link');
  const btns = [...el.querySelectorAll('.m-row button')], says = [...el.querySelectorAll('.m-step')];
  const padEl = $('.pad[data-main]'), hit = $('.pad-hit'), ov = new Overlay(padEl, S.w);
  const fine = matchMedia('(hover:hover) and (pointer:fine)').matches, steer = { on: false, u: 0.5, v: 0.5, x: 0, y: 0 };
  let [tx, ty, tw, th] = S.touchSurface || [S.w * 0.3, S.h * 0.08, S.w * 0.4, S.h * 0.3];
  const placeHit = () => { hit.style.cssText = `left:${tx / S.w * 100}%;top:${ty / S.h * 100}%;width:${tw / S.w * 100}%;height:${th / S.h * 100}%`; };
  const masks = new Masks(S, () => { const b = masks.bbox('touchpad'); if (b) { [tx, ty, tw, th] = b; placeHit(); } });
  masks.warm([['touchpad', '--powder'], ['strip', '--powder'], ['ps', '--powder'], ['touchpad', '--raven'], ['touchpad', '--powder-deep']]);
  const quad = quadPixels(TV.screenQuad, TV.w, TV.h), qw = quad[1][0] - quad[0][0], H = Math.round(W * (quad[3][1] - quad[0][1]) / qw);
  let curStep = -1, clock = cine ? 0 : START[3] + 0.5, mode = 'auto', hold = 0, narrow = innerWidth <= 860, user = false, rest = 0;

  // The app window; the toast lives in its own layer over the screen (it is its own window in the app too).
  const { el: app, O } = overviewWindow(base);
  app.classList.add('tv-app'); app.setAttribute('aria-label', el.dataset.preview || '');
  const toast = makeToast(base);
  toast.classList.add('tv-toast'); toast.style.right = toast.style.bottom = '0px';
  over.prepend(app); over.append(toast);
  const rc = cards.rightClick?.rc;

  const art = new Image(); art.alt = ''; art.className = 'art'; art.src = base + (innerWidth <= 820 && TV.fileSm ? TV.fileSm : TV.file); $('[data-tv-art]').appendChild(art);
  if (TV.reflection) { const r = new Image(); r.alt = ''; r.className = 'tv-refl'; r.src = base + TV.reflection; tv.appendChild(r); }
  for (const e of [tv, mtv]) e.style.setProperty('--ar', TV.w / TV.h);
  tv.style.setProperty('--glass-r', `${(TV.screenCornerRadius || 0) * TV.w / qw * W}px`);
  for (const e of [screen, over]) e.style.height = `${H}px`;
  placeHit();

  // Geometry in app pixels: element centres and the frames the TV shows (whole window, and the zoomed views).
  let G = null;
  const box = e => { let x = 0, y = 0, n = e; while (n && n !== app) { x += n.offsetLeft; y += n.offsetTop; n = n.offsetParent; } return [x, y, e.offsetWidth, e.offsetHeight]; };
  const mid = e => { const [x, y, w, hh] = box(e); return [x + w / 2, y + hh / 2]; };
  const zoomTo = (cx, cy, z) => { const fw = APP_W / z, fh = APP_H / z; return [clamp(cx - fw / 2, 0, APP_W - fw), clamp(cy - fh / 2, 0, APP_H - fh), fw]; };
  const measure = () => {
    const z = narrow ? ZOOM_PHONE : ZOOM, card = box(O.card), rows = box(O.P.padType), prof = box(O.P.profile), head = box(O.head);
    G = {
      whole: [0, 0, APP_W],
      rows: zoomTo(card[0] + card[2] / 2, (rows[1] + prof[1] + prof[3]) / 2 - 10, z),
      top: zoomTo(card[0] + card[2] / 2 - 40, head[1] + head[3] / 2 + 90, z),
      A: mid(O.card.querySelectorAll('.au-qrow .au-label')[1]),
      R: (() => { const [x, y, w, hh] = box(O.P.haptics); return [x + w * 0.35, y + hh / 2]; })(),
      P: mid(O.P.profile.button),
      C: (() => { const [x, y] = box(O.P.profile), o = O.P.profile.options[2]; return [x + o.offsetLeft + o.offsetWidth * 0.3, y + O.P.profile.pop.offsetTop + o.offsetTop + o.offsetHeight / 2]; })(),
      D: mid(O.back),
    };
  };

  const layout = () => {
    narrow = innerWidth <= 860;
    if (narrow) { // the TV's screen fills the column; its housing is cropped away
      const cw = mtv.clientWidth, tvW = cw * TV.w / qw;
      Object.assign(tv.style, { width: `${tvW}px`, left: `${-quad[0][0] / TV.w * tvW}px`, top: `${-quad[0][1] / TV.w * tvW}px` });
    } else { tv.style.width = tv.style.left = tv.style.top = ''; }
    const k = tv.clientWidth / TV.w; if (!k) return;
    screen.style.transform = over.style.transform = quadMatrix(W, H, quad.map(([x, y]) => [x * k, y * k]));
    measure();
    if (!narrow) { // the Bluetooth link: touchpad to the TV's indicator light
      const b = linkSvg.getBoundingClientRect(), p = padEl.getBoundingClientRect(), t = tv.getBoundingClientRect();
      const ax = p.left + p.width * (tx + tw / 2) / S.w - b.left, ay = p.top + p.height * (ty + th / 2) / S.h - b.top;
      const led = quadPixels([TV.indicator || [0.9, 0.9]], TV.w, TV.h)[0], bx = t.left + led[0] * k - b.left, by = t.top + led[1] * k - b.top;
      linkPath.setAttribute('d', `M${ax.toFixed(0)} ${ay.toFixed(0)}C${ax.toFixed(0)} ${(ay + 160).toFixed(0)} ${(bx + 120).toFixed(0)} ${(by + 50).toFixed(0)} ${bx.toFixed(0)} ${by.toFixed(0)}`);
    }
  };
  const label = () => { play.textContent = play.dataset[mode === 'auto' ? 'pause' : 'play']; };
  hit.addEventListener('pointermove', e => {
    const r = hit.getBoundingClientRect();
    steer.u = clamp((e.clientX - r.left) / r.width); steer.v = clamp((e.clientY - r.top) / r.height); steer.on = true;
    if (mode === 'auto') { mode = 'hold'; hold = 2; label(); }
  });
  hit.addEventListener('pointerleave', () => { steer.on = false; });
  // A visitor using the window pauses the demo; Play hands it back to the timeline.
  const takeOver = () => { if (!user) { user = true; mode = 'paused'; label(); engine.refresh(scene); } };
  app.addEventListener('pointerdown', takeOver); app.addEventListener('keydown', takeOver); app.addEventListener('focusin', takeOver);
  app.addEventListener('au:change', e => { if (e.detail.key === 'mouse') toastOn(e.detail.value); });
  O.back.addEventListener('click', () => { takeOver(); O.P.mouse.set(false); toastOn(false); });
  rc?.closest('.mat')?.addEventListener('au:change', takeOver);
  let toastTimer = 0;
  const toastOn = on => { toast.show(on); toast.classList.add('on'); clearTimeout(toastTimer); toastTimer = setTimeout(() => toast.classList.remove('on'), 2200); };
  $('.m-tv-cap .mat-full')?.addEventListener('click', () => lightbox(O.P.profile.pop.classList.contains('open') ? 'overview-profile-open' : O.P.profile.value === 2 ? 'overview-profile-relaxed' : O.P.mouse.on ? 'overview-mouse' : 'overview'));

  // Timeline: what the window shows at step i, t seconds into it.
  // Each toast stays at least 3 s; the On toast leaves 2 s into the Touchpad step, the Off toast 1.75 s into the last step.
  const stateAt = (i, t) => ({
    show: (i === 1 && t >= 0.5) || (i > 1 && i < 5) || (i === 5 && t < 0.95) || (i === 6 && t >= 2.25),
    mouse: (i === 1 && t >= 0.5) || (i > 1 && i < 5) || (i === 5 && t < 0.8) || (i === 6 && t >= 2.25),
    open: (i === 3 && t >= 0.65) || (i === 4 && t < 0.75),
    profile: i === 5 || (i === 4 && t >= 0.75) ? 2 : 0,
    pressed: i === 5 && t >= 0.72 && t < 0.86,
    toast: (i === 1 && t >= 0.5) || (i === 2 && t < 1.6) || (i === 6 && t >= 2.25) ? 'on' : (i === 5 && t >= 1.0) || (i === 6 && t < 1.4) ? 'off' : '',
  });
  const frameAt = (i, t) => {
    const { whole, rows, top } = G;
    if (i === 2) return mix(whole, rows, ease(0, 0.56, t));
    if (i === 3 || i === 4) return rows;
    if (i === 5) return t < 0.56 ? mix(rows, top, ease(0, 0.56, t)) : top;
    return whole;
  };
  const pointerAt = (i, t) => {
    const { A, R, P, C, D } = G;
    if (i <= 1 || i === 6) return A;
    // The pointer waits above the toast while it is up, then goes to the Profile picker
    if (i === 2) { const e = ease(0.4, 1.0, t), q = mix(A, R, e); return [q[0], q[1] + 12 * Math.sin(e * Math.PI)]; }
    if (i === 3) return mix(R, P, ease(0, 0.5, t));
    if (i === 4) return mix(P, C, ease(0.1, 0.6, t));
    return mix(C, D, ease(0.1, 0.6, t));
  };
  const clickAt = i => (i === 3 ? 0.6 : i === 4 ? 0.7 : i === 5 ? 0.75 : -1);
  let shown = '';
  const apply = st => {
    const key = JSON.stringify(st); if (key === shown) return; shown = key;
    O.P.mouse.set(st.mouse); O.P.profile.select(st.profile); O.P.profile.open(st.open);
    O.back.classList.toggle('is-pressed', st.pressed);
    toast.classList.toggle('on', !!st.toast); if (st.toast) toast.show(st.toast === 'on');
  };
  const drawRoad = t => {
    const c = road, w = 800, hh = 600, hz = 225, cx = 400 + Math.sin(t * 0.5) * 34;
    c.fillStyle = col('--raven'); c.fillRect(0, 0, w, hh);
    c.strokeStyle = col('--powder', 0.75); c.lineWidth = 1.5; c.beginPath(); c.moveTo(0, hz); c.lineTo(w, hz); c.stroke();
    c.strokeStyle = col('--paper', 0.2); c.lineWidth = 1.2;
    for (let n = 0; n < 12; n++) { const z = (n / 12 + t * 0.22) % 1, y = hz + (hh - hz) * z * z, half = 40 + 720 * z * z, x = lerp(cx, 400, z); c.beginPath(); c.moveTo(x - half, y); c.lineTo(x + half, y); c.stroke(); }
    c.lineWidth = 2;
    for (const o of [-1, -0.5, 0.5, 1]) { c.strokeStyle = col('--paper', Math.abs(o) === 1 ? 0.7 : 0.26); c.beginPath(); c.moveTo(cx + o * 40, hz); c.lineTo(400 + o * 760, hh); c.stroke(); }
    c.strokeStyle = col('--powder'); c.lineWidth = 4; c.setLineDash([26, 26]); c.lineDashOffset = -t * 90;
    c.beginPath(); c.moveTo(cx, hz); c.lineTo(400, hh); c.stroke(); c.setLineDash([]);
  };
  const stepOf = c => { let i = 0; while (i < DUR.length - 1 && c >= START[i + 1]) i++; return [i, c - START[i]]; };

  const scene = {
    el, near: layout, resize: layout,
    update(time, dt) {
      if (!G) layout();
      if (!G) return;
      if (cine && mode !== 'paused') {
        if (mode === 'hold') clock = Math.min(START[hold + 1] - 0.01, clock + dt);
        else if (clock < LEN) clock = Math.min(LEN, clock + dt);
        else if ((rest += dt) > LOOP_PAUSE) { rest = 0; clock = 0; shown = ''; }
      }
      if (mode !== 'paused' && user) { user = false; shown = ''; }
      const [i, t0] = stepOf(Math.min(clock, LEN - 0.001)), t = i === 0 ? t0 : t0 / STRETCH; // t: the step's own timing
      if (i !== curStep) {
        curStep = i;
        says.forEach((li, n) => li.classList.toggle('on', n === i));
        btns.forEach((b, n) => { b.classList.toggle('on', n === i); n === i ? b.setAttribute('aria-current', 'step') : b.removeAttribute('aria-current'); });
        const b = btns[i], row = b.closest('.m-row'); if (row.scrollWidth > row.clientWidth) row.scrollTo({ left: b.parentElement.offsetLeft - 16 });
        hit.classList.toggle('live', fine && i === 2);
        if (i !== 2) steer.on = false;
      }
      const st = stateAt(i, t), showApp = user || st.show;
      tv.classList.toggle('app', showApp);
      if (!showApp) drawRoad(cine ? time : 1.2);
      if (!user) apply(st);
      const rcMode = i === 4 ? (t < 1.0 ? 0 : t < 1.3 ? 1 : 2) : 0;
      inset?.classList.toggle('on', user || (i === 4 && t > 0.85));
      if (!user && rc && rc.value !== rcMode) rc.set(rcMode);

      // The TV's view of the window and the pointer on it.
      const [fx, fy, fw] = user ? G.whole : frameAt(i, t), k = W / fw;
      app.style.transform = `translate3d(${(-fx * k).toFixed(1)}px,${(-fy * k).toFixed(1)}px,0) scale(${k.toFixed(4)})`;
      // The real toast is its own 380 x 132 window drawn at 100 %; beside the app window at 125 % it is 0.8 of the window's scale.
      // One transform (pinned to the corner, then 12 px in at the window's scale) instead of right/bottom and --k: no layout per frame.
      toast.style.transform = `translate(${(-12 * k).toFixed(1)}px,${(-12 * k).toFixed(1)}px) scale(${(k * TOAST_SCALE).toFixed(4)})`;
      let [x, y] = pointerAt(i, t);
      const toScreen = (a, b) => [(a - fx) * k, (b - fy) * k];
      if (steer.on && i === 2) { steer.x = lerp(steer.x, fx + steer.u * fw, 0.3); steer.y = lerp(steer.y, fy + steer.v * fw * H / W, 0.3); x = steer.x; y = steer.y; } else { steer.x = x; steer.y = y; }
      const [sx, sy] = toScreen(x, y);
      cur.style.opacity = showApp && !user && i > 1 && i < 6 ? 1 : 0; cur.style.transform = `translate3d(${sx.toFixed(1)}px,${sy.toFixed(1)}px,0)`;
      const c = clickAt(i), press = c < 0 || user ? 0 : pulse(t, c, 0.12), ringT = c < 0 || user ? -1 : (t - c) / 0.35;
      ring.style.opacity = ringT > 0 && ringT < 1 ? (1 - ringT).toFixed(2) : 0;
      if (ringT > 0 && ringT < 1) ring.style.transform = `translate3d(${sx}px,${sy}px,0) scale(${(0.3 + ringT * 1.2).toFixed(2)})`;

      const g = ov.begin(); if (!g) return;
      const px = d => d / ov.k, mode_ = rc ? rc.value : rcMode;
      if (!user && ((i >= 2 && i <= 4) || (i === 5 && t < 0.7))) {
        const toTouch = q => { const [a, b] = toScreen(q[0], q[1]), u = clamp(a / W), w = clamp(b / H); return [tx + tw * (0.5 + (u - 0.5) * (1 - 0.22 * w)), ty + w * th]; };
        const finger = (a, b) => { glow(g, a, b, px(28), '--powder', 0.6); g.fillStyle = col('--powder-core'); g.beginPath(); g.arc(a, b, px(7), 0, 7); g.fill(); };
        masks.draw(g, 'touchpad', '--powder', 0.6 - press * 0.15); masks.draw(g, 'strip', '--powder', 0.7);
        if (press) masks.draw(g, 'touchpad', '--raven', press * 0.2);
        if (i === 4 && mode_ === 1) { g.save(); g.beginPath(); g.rect(tx + tw / 2, ty, tw / 2, th); g.clip(); masks.draw(g, 'touchpad', '--powder-deep', 0.8); g.restore(); }
        if (!(i === 4 && mode_)) finger(...toTouch([x, y]));
        else if (mode_ === 1) finger(tx + tw * 0.76, ty + th * 0.52);
        else { finger(tx + tw * 0.4, ty + th * 0.52); finger(tx + tw * 0.6, ty + th * 0.46); }
      }
      // ø, and ø with Options: the button lights with rings around it
      const lightPs = (kk, s) => {
        const [a, b, r] = S.ps;
        if (masks.has('ps')) masks.draw(g, 'ps', '--powder', 0.9 * kk); else { g.fillStyle = col('--powder', 0.6 * kk); g.beginPath(); g.arc(a, b, r, 0, 7); g.fill(); }
        for (let n = 0; n < 3; n++) { const u = (s * 2 + n / 3) % 1; g.strokeStyle = col('--powder', (1 - u) * kk); g.lineWidth = px(1.5); g.beginPath(); g.arc(a, b, r + u * S.w * 0.06, 0, 7); g.stroke(); }
      };
      if (!user && i === 1 && t < 0.7) lightPs(1 - sstep(0.45, 0.7, t), t);
      if (!user && i === 6 && t > 1.5 && t < 2.25) {
        const kk = sstep(1.5, 1.7, t) * (1 - sstep(2.0, 2.25, t)), [ox, oy] = [OPTIONS[0] * S.w, OPTIONS[1] * S.h];
        lightPs(kk, t);
        glow(g, ox, oy, S.w * 0.035, '--powder', 0.95 * kk);
        g.strokeStyle = col('--powder', 0.8 * kk); g.lineWidth = px(1.5); g.beginPath(); g.arc(ox, oy, S.w * 0.022, 0, 7); g.stroke();
      }
    },
  };
  engine.add(scene);
  btns.forEach((b, i) => b.addEventListener('click', () => { user = false; shown = ''; hold = i; mode = 'hold'; clock = START[i] + (cine ? 0 : DUR[i] * 0.6); label(); engine.refresh(scene); }));
  play.addEventListener('click', () => { mode = mode === 'auto' ? 'paused' : 'auto'; if (mode === 'auto') { user = false; shown = ''; if (clock >= LEN) clock = 0; } label(); engine.run(); engine.refresh(scene); });
  addEventListener('resize', () => { G = null; });
  document.fonts?.ready.then(() => { G = null; engine.refresh(scene); });
  label();
}

// Shared helpers and the scene clock: animation runs by time while a scene is on screen. Scrolling only moves the page.
export const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
export const lerp = (a, b, t) => a + (b - a) * t;
export const sstep = (a, b, v) => { const t = clamp((v - a) / (b - a)); return t * t * (3 - 2 * t); };
export const pulse = (v, c, w) => { const d = Math.abs(v - c) / w; return d >= 1 ? 0 : 0.5 + 0.5 * Math.cos(Math.PI * d); };
export const MONO = '"Cascadia Mono","SF Mono",Consolas,"Liberation Mono",monospace';
export const root = document.documentElement;
export const lang = root.lang || 'en';
export const cine = root.classList.contains('cine');
export const upper = s => s.toLocaleUpperCase(lang);
// Colours come from the CSS tokens; col('--wave', .4) gives a canvas colour with alpha.
const rgbCache = new Map(), probe = document.createElement('canvas').getContext('2d', { willReadFrequently: true });
export function col(token, a = 1) {
  let c = rgbCache.get(token);
  if (!c) {
    const el = document.createElement('i'); el.style.color = token.startsWith('--') ? `var(${token})` : token; document.body.appendChild(el);
    probe.fillStyle = getComputedStyle(el).color; el.remove();
    probe.clearRect(0, 0, 1, 1); probe.fillRect(0, 0, 1, 1);
    c = [...probe.getImageData(0, 0, 1, 1).data].slice(0, 3).join(','); rgbCache.set(token, c);
  }
  return `rgba(${c},${a})`;
}
export const fxOn = () => !root.classList.contains('fx-off');

// Sizes a canvas to its CSS box with a device-pixel-ratio cap. Returns the context scaled to CSS pixels.
export function fit(canvas, cap = 1.5) {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  const dpr = Math.min(window.devicePixelRatio || 1, cap);
  const bw = Math.max(1, Math.round(w * dpr)), bh = Math.max(1, Math.round(h * dpr));
  if (canvas.width !== bw || canvas.height !== bh) { canvas.width = bw; canvas.height = bh; }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { ctx, w, h, dpr };
}

export class Engine {
  scenes = [];
  raf = 0;
  last = 0;

  // cfg: el, update(time, dt), and optional near, far, enter, leave, resize, gate() and still (time for the static render).
  add(cfg) {
    const s = { time: 0, still: 2.4, inView: false, isNear: false, isVis: false, ...cfg };
    this.scenes.push(s);
    return s;
  }
  sync(s) {
    const vis = s.inView && (s.gate ? s.gate() : true);
    if (vis !== s.isVis) { s.isVis = vis; vis ? s.enter?.() : s.leave?.(); }
    if (cine) this.run(); else this.refresh(s);
  }
  refresh(s) {
    if (!cine) { if (s.isNear) s.update?.(s.still, 0); } else if (s.isVis) s.update?.(s.time, 0);
  }
  run() {
    if (!this.raf && this.scenes.some(s => s.isVis)) { this.last = 0; this.raf = requestAnimationFrame(t => this.tick(t)); }
  }
  tick(now) {
    this.raf = 0;
    const dt = this.last ? Math.min(0.1, (now - this.last) / 1000) : 0.016;
    this.last = now;
    let any = false;
    for (const s of this.scenes) if (s.isVis) { any = true; s.time += dt; s.update?.(s.time, dt); }
    if (any && !document.hidden) { const l = this.last; this.raf = requestAnimationFrame(t => this.tick(t)); this.last = l; }
  }
  start() {
    const of = new Map(this.scenes.map(s => [s.el, s]));
    const near = new IntersectionObserver(es => es.forEach(e => {
      const s = of.get(e.target);
      if (e.isIntersecting !== s.isNear) { s.isNear = e.isIntersecting; s.isNear ? s.near?.() : s.far?.(); if (s.isNear && !cine) this.refresh(s); }
    }), { rootMargin: '100% 0px' });
    const view = new IntersectionObserver(es => es.forEach(e => {
      const s = of.get(e.target); s.inView = e.isIntersecting; this.sync(s);
    }), { rootMargin: '-10% 0px' });
    this.scenes.forEach(s => { near.observe(s.el); view.observe(s.el); });
    let pending = false;
    addEventListener('resize', () => {
      if (pending) return; pending = true;
      requestAnimationFrame(() => { pending = false; this.scenes.forEach(s => { s.resize?.(); this.refresh(s); }); });
    });
    document.addEventListener('visibilitychange', () => { if (!document.hidden) this.run(); });
    document.fonts?.ready.then(() => this.scenes.forEach(s => { s.resize?.(); this.refresh(s); }));
  }
}

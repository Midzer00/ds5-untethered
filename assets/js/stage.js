// The controller stage: one picture with lit parts. All positions and masks come from the manifest's `stage` entry.
import { base } from './assets.js';
import { col, fit } from './core.js';

const small = () => innerWidth <= 820;
// The grips' light falls off from the middle of the grip (share of the mask's height), so it has no hard upper edge.
const GRIP_FADE = { 'grip-left': 0.62, 'grip-right': 0.62 };

export function mountPads(M) {
  const S = M.stage;
  document.querySelectorAll('[data-pad]').forEach(p => {
    p.style.setProperty('--ar', S.w / S.h);
    const img = new Image(); img.alt = ''; img.className = 'art'; img.decoding = 'async'; img.draggable = false;
    img.src = base + (small() && S.artSm ? S.artSm : S.art);
    p.insertBefore(img, p.firstChild);
  });
}

// Canvas lying over the controller picture, drawn in the picture's coordinate system.
export class Overlay {
  constructor(pad, w) {
    this.w = w;
    this.cv = document.createElement('canvas'); this.cv.className = 'ov'; this.cv.setAttribute('aria-hidden', 'true');
    pad.appendChild(this.cv);
  }
  begin() {
    const { ctx, w, h, dpr } = fit(this.cv, 2);
    if (!w || !h) return null;
    ctx.clearRect(0, 0, w, h);
    this.k = w / this.w;
    ctx.setTransform(dpr * this.k, 0, 0, dpr * this.k, 0, 0);
    return ctx;
  }
}

// Emission masks, tinted on demand. A mask may cover the whole picture or the box given in `stage.maskBox`.
export class Masks {
  constructor(S, onload) {
    this.S = S; this.img = {}; this.cache = {};
    for (const [name, path] of Object.entries(S.masks || {})) {
      const i = new Image(); i.onload = () => { this.img[name] = i; onload?.(); }; i.src = base + path;
    }
  }
  has(name) { return !!this.img[name]; }
  // Bounding box of a mask in picture pixels.
  bbox(name) {
    const m = this.img[name]; if (!m) return null;
    const c = document.createElement('canvas'), w = c.width = 250, h = c.height = Math.round(250 * this.S.h / this.S.w), x = c.getContext('2d', { willReadFrequently: true });
    x.drawImage(m, 0, 0, w, h);
    const d = x.getImageData(0, 0, w, h).data; let a = w, b = h, e = 0, f = 0;
    for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) if (d[(i + j * w) * 4 + 3] > 128) { a = Math.min(a, i); b = Math.min(b, j); e = Math.max(e, i + 1); f = Math.max(f, j + 1); }
    const k = this.S.w / w; return e ? [a * k, b * k, (e - a) * k, (f - b) * k] : null;
  }
  draw(g, name, token, alpha) {
    const m = this.img[name]; if (!m || alpha <= 0) return;
    const key = `${name}|${token}`;
    let c = this.cache[key];
    if (!c) {
      c = this.cache[key] = document.createElement('canvas');
      c.width = 1000; c.height = Math.round(1000 * this.S.h / this.S.w);
      const x = c.getContext('2d');
      x.drawImage(m, 0, 0, c.width, c.height);
      x.globalCompositeOperation = 'multiply'; x.fillStyle = col(token); x.fillRect(0, 0, c.width, c.height);
      x.globalCompositeOperation = 'destination-in'; x.drawImage(m, 0, 0, c.width, c.height);
      const fade = GRIP_FADE[name], box = fade && this.bbox(name);
      if (box) {
        const k = c.width / this.S.w, [bx, by, bw, bh] = box.map(v => v * k), cx = bx + bw / 2, cy = by + bh * 0.64;
        const f = x.createRadialGradient(cx, cy, bh * fade * 0.35, cx, cy, bh * fade); f.addColorStop(0, '#000'); f.addColorStop(1, 'rgba(0,0,0,0)');
        x.fillStyle = f; x.fillRect(0, 0, c.width, c.height);
      }
      x.globalCompositeOperation = 'lighter'; x.filter = 'blur(10px)'; x.drawImage(c, 0, 0); x.filter = 'none';
    }
    g.save(); g.globalAlpha = alpha; g.drawImage(c, 0, 0, this.S.w, this.S.h); g.restore();
  }
}

export function glow(g, x, y, r, token, a) {
  const f = g.createRadialGradient(x, y, 0, x, y, r);
  f.addColorStop(0, col(token, a)); f.addColorStop(1, col(token, 0));
  g.fillStyle = f; g.beginPath(); g.arc(x, y, r, 0, 7); g.fill();
}

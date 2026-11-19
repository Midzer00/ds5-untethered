// CH 02: the signal path runs by itself while it is on screen.
import { clamp, fit, MONO, upper, cine, col } from './core.js';

export const setSwap = (box, i) => { [...box.children].forEach((c, n) => c.classList.toggle('on', n === i)); };

export function howScene(engine, el) {
  const cv = el.querySelector('.path-cv'), dots = [...el.querySelectorAll('.path i')];
  el.querySelectorAll('.path li').forEach(n => n.classList.add('lit'));
  const labels = cv.dataset.labels.split('|').map(upper);
  let pts = [];
  const place = () => {
    const r = cv.getBoundingClientRect();
    pts = dots.map(d => { const b = d.getBoundingClientRect(); return [b.left + b.width / 2 - r.left, b.top + b.height / 2 - r.top]; });
  };
  const at = u => {
    const s = clamp(u) * (pts.length - 1), i = Math.min(pts.length - 2, Math.floor(s)), f = s - i;
    return [pts[i][0] + (pts[i + 1][0] - pts[i][0]) * f, pts[i][1] + (pts[i + 1][1] - pts[i][1]) * f];
  };
  engine.add({
    el, near: place, enter: place, resize: place,
    update(t) {
      if (!pts.length) place();
      const { ctx: c, w, h } = fit(cv, 2);
      c.clearRect(0, 0, w, h);
      c.lineCap = 'round'; c.lineWidth = 1.5;
      pts.slice(1).forEach((q, i) => { c.strokeStyle = col(i === 0 ? '--poppy' : '--powder'); c.beginPath(); c.moveTo(...pts[i]); c.lineTo(...q); c.stroke(); });
      c.font = `10px ${MONO}`; c.textAlign = 'center';
      const move = cine ? t * 0.11 : 0.08;
      const pulseAt = (u, tone) => { const [x, y] = at(u); c.fillStyle = col(tone); c.beginPath(); c.arc(x, y, 3.5, 0, 7); c.fill(); return [x, y]; };
      for (let i = 0; i < 3; i++) {
        const [x, y] = pulseAt(1 / 3 + ((i / 3 + move) % 1) * 2 / 3, '--powder');
        if (w > 560) { c.fillStyle = col('--paper'); c.fillText(labels[i], x, y - 13); }
      }
    },
  });
}

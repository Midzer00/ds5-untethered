// Profiles: three example sleeves. Drag, arrow keys or a click bring one forward; its settings appear beside it.
import { base } from './assets.js';
import { clamp, lerp } from './core.js';

const CW = 400, CH = 690;
const WIDE = { ref: 200, slots: [[0, 1, 0, 1, 1], [230, 0.6, 1, 0.8, 28]] };
const NARROW = { ref: 150, slots: [[0, 1, 0, 1, 1], [128, 0.6, 1, 0.8, 30]] };

export function profilesScene(el) {
  const list = el.querySelector('.sleeves'), items = [...list.children], btns = items.map(li => li.querySelector('button')), now = el.querySelector('.p-now');
  const n = items.length, small = innerWidth <= 820 ? '-sm' : '';
  let cur = 1, pos = 1, drag = null, moved = false;
  items.forEach(li => {
    const front = li.querySelector('.sl-front');
    for (const [name, cls] of [['wear', 'sl-wear'], ['gloss', 'sl-gloss']]) { const i = new Image(); i.alt = ''; i.className = cls; i.draggable = false; i.onerror = () => i.remove(); i.src = `${base}img/sleeve-${name}${small}.webp`; front.appendChild(i); }
  });
  const fit = () => {
    const w = btns[0].clientWidth; if (!w) return;
    list.style.setProperty('--k', w / CW);
    el.querySelectorAll('.c-title').forEach(t => {
      t.style.fontSize = '100px';
      const widest = Math.max(...[...t.children].map(s => s.offsetWidth));
      if (widest) t.style.fontSize = `${Math.min(150, 100 * (CW - 24) / widest).toFixed(1)}px`;
    });
  };
  const place = () => {
    const set = innerWidth <= 860 ? NARROW : WIDE, k = btns[0].clientWidth / set.ref;
    items.forEach((li, i) => {
      const d = i - pos, a = Math.min(1, Math.abs(d)), s = Math.sign(d), [A, B] = set.slots, v = A.map((x, m) => lerp(x, B[m], a));
      li.style.transform = `translate3d(${(s * v[0] * k * Math.max(1, Math.abs(d))).toFixed(1)}px,0,${(-a * 120).toFixed(0)}px) rotateY(${(-s * v[4] * (a ? 1 : 0)).toFixed(1)}deg)`;
      li.style.zIndex = 10 - Math.round(a * 3);
      li.style.setProperty('--b', v[1].toFixed(2)); li.style.setProperty('--bl', `${v[2].toFixed(1)}px`); li.style.setProperty('--s', v[3].toFixed(2));
    });
  };
  const select = (i, focus) => {
    cur = clamp(i, 0, n - 1); pos = cur;
    items.forEach((li, k) => { li.classList.toggle('cur', k === cur); btns[k].setAttribute('aria-pressed', k === cur); });
    now.textContent = `${items[cur].querySelector('.p-name').textContent}: ${[...items[cur].querySelectorAll('.c-set i')].map(i => i.textContent).join(', ')}`;
    place();
    if (focus) btns[cur].focus({ preventScroll: true });
  };
  btns.forEach((b, i) => b.addEventListener('click', () => { if (!moved) select(i); }));
  list.addEventListener('keydown', e => {
    const d = { ArrowLeft: -1, ArrowRight: 1 }[e.key];
    if (d) { e.preventDefault(); select(cur + d, true); }
  });
  list.addEventListener('pointerdown', e => { drag = { x: e.clientX, pos, id: e.pointerId }; moved = false; });
  addEventListener('pointermove', e => {
    if (!drag || e.pointerId !== drag.id) return;
    const dx = e.clientX - drag.x;
    if (!moved && Math.abs(dx) > 8) { moved = true; list.classList.add('drag'); }
    if (moved) { pos = clamp(drag.pos - dx / (btns[0].clientWidth * 1.15), -0.3, n - 0.7); place(); }
  }, { passive: true });
  const end = () => { if (!drag) return; drag = null; list.classList.remove('drag'); if (moved) { select(Math.round(pos)); setTimeout(() => { moved = false; }, 0); } };
  addEventListener('pointerup', end); addEventListener('pointercancel', end);
  addEventListener('resize', () => { fit(); place(); });
  document.fonts?.ready.then(() => { fit(); place(); });
  fit(); select(1);
}

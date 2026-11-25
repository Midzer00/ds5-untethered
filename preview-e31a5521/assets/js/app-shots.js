// The app previews in the Features cards and beside Mouse Mode, and the drawn Overview window in What works.
import { card, overviewWindow } from './app-ui.js';
import { base } from './assets.js';

// Builds each figure's live preview. Returns the controls of each card by its data-ui name.
export function mountMats() {
  const cards = {};
  document.querySelectorAll('.mat[data-ui]').forEach(m => {
    const { el, C } = card(m.dataset.ui), host = m.querySelector('.au-host');
    el.setAttribute('role', 'group'); el.setAttribute('aria-label', m.dataset.label || '');
    host.append(el);
    cards[m.dataset.ui] = C;
  });
  return cards;
}

// What works: a second, independent copy of the whole Overview window, the full 1280 x 720 app px the Mouse Mode TV's whole
// view frames. Static: inert, no pointer, no timers; the host is one image to assistive tech.
// It is built when the section comes near, and scaled (not rasterised) to the figure's width, so the text stays sharp.
const FRAME_W = 1280; // the frame's 16:9 height (720) is the host's aspect-ratio in site.css
export function mountOverview() {
  const host = document.querySelector('#what-works .wk-host');
  if (!host) return;
  const mount = () => {
    const { el } = overviewWindow(base);
    el.classList.add('wk-app');
    el.removeAttribute('role'); el.setAttribute('aria-hidden', 'true'); el.inert = true;
    host.append(el);
    const fit = () => { const w = host.clientWidth; if (w) el.style.transform = `scale(${(w / FRAME_W).toFixed(5)})`; };
    fit();
    new ResizeObserver(fit).observe(host);
  };
  if (!('IntersectionObserver' in window)) { mount(); return; }
  const io = new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) { io.disconnect(); mount(); } }, { rootMargin: '600px 0px' });
  io.observe(host);
}

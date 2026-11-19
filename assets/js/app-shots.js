// The app previews in the Features cards and beside Mouse Mode, and the lightbox with the real screenshot as proof.
import { base } from './assets.js';
import { lang } from './core.js';
import { card } from './app-ui.js';

const url = view => `${base}app/${lang}/${view}.webp`;
let openBox = () => {};
export const lightbox = view => openBox(view);

// Builds each figure's live preview. Returns the controls of each card by its data-ui name.
export function mountMats() {
  const box = document.getElementById('lightbox'), big = box.querySelector('img');
  openBox = view => { big.src = url(view); box.showModal(); };
  box.querySelector('.lb-close').addEventListener('click', () => box.close());
  box.addEventListener('click', e => { if (e.target === box) box.close(); });
  const cards = {};
  document.querySelectorAll('.mat[data-ui]').forEach(m => {
    const { el, C } = card(m.dataset.ui), host = m.querySelector('.au-host');
    el.setAttribute('role', 'group'); el.setAttribute('aria-label', m.dataset.label || '');
    host.append(el);
    cards[m.dataset.ui] = C;
    m.querySelector('.mat-full')?.addEventListener('click', () => openBox(m.dataset.view));
  });
  return cards;
}

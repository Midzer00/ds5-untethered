// The app previews in the Features cards and beside Mouse Mode.
import { card } from './app-ui.js';

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

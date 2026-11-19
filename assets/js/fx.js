// On-screen display: timecode, clock, FX toggle and menu.
import { root, fxOn } from './core.js';

const $ = id => document.getElementById(id);
const two = n => String(n).padStart(2, '0');

let showFilmTime = () => {};
export const filmTime = s => showFilmTime(s);

export function initOsd() {
  const code = $('timecode');
  let film = '00:00:00';
  const secs = [...document.querySelectorAll('main > section')], home = $('home');
  let inHero = true, homeH = 0, tops = [], past, pending = false;
  // Geometry is read when it changes (ResizeObserver runs after layout), so scrolling itself never forces a layout.
  const top = e => { let y = 0; for (let n = e; n; n = n.offsetParent) y += n.offsetTop; return y; };
  const measure = () => { homeH = home.offsetHeight; tops = secs.map(top); };
  const show = t => { if (code.textContent !== t) code.textContent = t; };
  const where = () => {
    pending = false;
    const y = scrollY; inHero = y < homeH * 0.5;
    if ((y > 4) !== past) { past = y > 4; root.classList.toggle('past-hero', past); } // the fades cover anything that scrolls under the OSD, from the first pixel
    if (inHero) { show(film); return; }
    let i = 0; tops.forEach((t, n) => { if (t - y <= innerHeight * 0.45) i = n; });
    show(`${two(i + 1)} ${secs[i].dataset.osd}`);
  };
  showFilmTime = t => {
    const s = Math.floor(t);
    film = `${two(Math.floor(s / 3600))}:${two(Math.floor(s / 60) % 60)}:${two(s % 60)}`;
    if (inHero) show(film);
  };
  measure(); where();
  if (window.ResizeObserver) { const ro = new ResizeObserver(() => { measure(); where(); }); [document.body, home, ...secs].forEach(e => ro.observe(e)); }
  addEventListener('scroll', () => { if (!pending) { pending = true; requestAnimationFrame(where); } }, { passive: true });
  addEventListener('resize', () => { measure(); where(); });

  const fx = $('fx'), word = fx.querySelector('span');
  const paint = () => { const on = fxOn(); fx.setAttribute('aria-pressed', on); word.textContent = fx.dataset[on ? 'on' : 'off']; };
  paint();
  fx.addEventListener('click', () => {
    root.classList.toggle('fx-off');
    try { localStorage.setItem('ds5-fx', fxOn() ? 'on' : 'off'); } catch { /* storage may be blocked */ }
    paint();
  });

  const menu = $('menu'), nav = $('osd-nav');
  const setMenu = o => { menu.setAttribute('aria-expanded', o); nav.classList.toggle('open', o); };
  menu.addEventListener('click', () => setMenu(menu.getAttribute('aria-expanded') !== 'true'));
  nav.addEventListener('click', e => { if (e.target.closest('a')) setMenu(false); });
  addEventListener('keydown', e => { if (e.key === 'Escape') setMenu(false); });

  // One short entrance for the sections below the home screen.
  const io = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } }), { rootMargin: '0px 0px -8% 0px' });
  document.querySelectorAll('.rise').forEach(s => io.observe(s));
}

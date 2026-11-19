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
  let inHero = true;
  const where = () => {
    const y = scrollY, h = home.offsetHeight; inHero = y < h * 0.5;
    root.classList.toggle('past-hero', y > 4); // the fades cover anything that scrolls under the OSD, from the first pixel
    if (inHero) { code.textContent = film; return; }
    let i = 0; secs.forEach((s, n) => { if (s.getBoundingClientRect().top <= innerHeight * 0.45) i = n; });
    code.textContent = `${two(i + 1)} ${secs[i].dataset.osd}`;
  };
  showFilmTime = t => {
    const s = Math.floor(t);
    film = `${two(Math.floor(s / 3600))}:${two(Math.floor(s / 60) % 60)}:${two(s % 60)}`;
    if (inHero && code.textContent !== film) code.textContent = film;
  };
  where();
  addEventListener('scroll', () => requestAnimationFrame(where), { passive: true }); addEventListener('resize', where);

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

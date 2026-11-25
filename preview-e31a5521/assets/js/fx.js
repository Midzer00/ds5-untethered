// On-screen display: timecode, clock, FX and sound toggles, and menu.
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
  initSound();

  const menu = $('menu'), nav = $('osd-nav');
  const setMenu = o => { menu.setAttribute('aria-expanded', o); nav.classList.toggle('open', o); };
  menu.addEventListener('click', () => setMenu(menu.getAttribute('aria-expanded') !== 'true'));
  nav.addEventListener('click', e => { if (e.target.closest('a')) setMenu(false); });
  addEventListener('keydown', e => { if (e.key === 'Escape') setMenu(false); });

  // One short entrance for the sections below the home screen.
  const io = new IntersectionObserver(es => es.forEach(e => { if (e.isIntersecting) { e.target.classList.add('in'); io.unobserve(e.target); } }), { rootMargin: '0px 0px -8% 0px' });
  document.querySelectorAll('.rise').forEach(s => io.observe(s));
}

// Background music: on by default. Browsers never let a page start sound by itself, so the music starts, with a fade-in,
// on the visitor's first click, tap or key press anywhere on the page, and nothing is downloaded before that. Until then
// the toggle already reads On, because that is the setting; a first click on the toggle itself turns it off instead.
// Turning it off is remembered ('off') and later visits stay silent; turning it on again stores 'on'. With Data Saver
// on, the music waits for the toggle.
const SOUND_KEY = 'ds5-sound';
const GESTURES = ['pointerdown', 'click', 'keydown', 'touchend'];
function initSound() {
  const btn = $('snd');
  if (!btn) return;
  const word = btn.querySelector('span');
  let stored = null;
  try { stored = localStorage.getItem(SOUND_KEY); } catch { /* storage may be blocked */ }
  let audio = null, on = stored !== 'off' && !navigator.connection?.saveData, raf = 0, armed = false;
  const paint = () => { btn.setAttribute('aria-pressed', on); word.textContent = btn.dataset[on ? 'on' : 'off']; };
  const save = () => { try { localStorage.setItem(SOUND_KEY, on ? 'on' : 'off'); } catch { /* storage may be blocked */ } };
  // Volume ramps run on animation frames; iOS ignores element volume, so there the music simply starts and stops.
  const ramp = (to, ms, done) => {
    cancelAnimationFrame(raf);
    const from = audio.volume, t0 = performance.now();
    const step = now => {
      const k = Math.min(1, Math.max(0, (now - t0) / ms)); // a frame's timestamp can precede t0
      audio.volume = from + (to - from) * k;
      if (k < 1) raf = requestAnimationFrame(step); else if (done) done();
    };
    raf = requestAnimationFrame(step);
  };
  const create = () => {
    audio = document.createElement('audio');
    audio.loop = true; audio.preload = 'auto'; audio.volume = 0;
    for (const [file, type] of [['ambience.webm', 'audio/webm; codecs="opus"'], ['ambience.m4a', 'audio/mp4; codecs="mp4a.40.2"']]) {
      const s = document.createElement('source');
      s.src = new URL(`../audio/${file}`, import.meta.url).href; s.type = type;
      audio.appendChild(s);
    }
  };
  // A refused start leaves the setting on and waits for the next gesture.
  const play = ms => {
    if (!audio) create();
    const p = audio.play();
    ramp(1, ms);
    if (p) p.catch(() => { if (on && audio.paused && !document.hidden) { cancelAnimationFrame(raf); audio.volume = 0; arm(); } });
  };
  // The first activation anywhere starts the music. The toggle's own events are left to its click handler, and a
  // touch that only scrolls (no activation yet) keeps the listeners waiting.
  const onGesture = e => {
    if (e.target instanceof Element && e.target.closest('#snd')) return;
    if (!on || document.hidden || (navigator.userActivation && !navigator.userActivation.isActive)) return;
    disarm(); play(1500);
  };
  const arm = () => { if (armed || !on) return; armed = true; GESTURES.forEach(t => addEventListener(t, onGesture, { capture: true, passive: true })); };
  const disarm = () => { if (!armed) return; armed = false; GESTURES.forEach(t => removeEventListener(t, onGesture, { capture: true })); };
  paint(); arm();
  btn.addEventListener('click', () => {
    on = !on; paint(); save(); disarm();
    if (on) play(1500);
    else if (audio) ramp(0, 500, () => audio.pause());
  });
  document.addEventListener('visibilitychange', () => {
    if (!audio) return;
    if (document.hidden) { cancelAnimationFrame(raf); audio.pause(); audio.volume = 0; }
    else if (on && !armed) play(800);
  });
}

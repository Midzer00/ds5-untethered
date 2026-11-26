// The hero film plays once by itself and rests on its last frame. Beats are fractions of its duration.
import { base } from './assets.js';
import { root, sstep, cine } from './core.js';
import { filmTime } from './fx.js';

const DEFAULTS = { release: 0.6, igniteStart: 0.6, igniteEnd: 0.8 };
// Where the controller rests in the film's frame: centre x, centre y and width as fractions of the frame (manifest: settleBox).
const SETTLE_BOX = { desktop: [0.5, 0.315, 0.4], mobile: [0.5, 0.27, 0.8] }, CTRL_ASPECT = 0.6;
// The film starts only once it can play through; if that takes longer than LOAD_LIMIT_MS it is skipped for the still.
const BEAT_MS = 350, LOAD_LIMIT_MS = 2500, SEEN = 'ds5-intro-seen';

// Development only: ?proxy=1 plays the proxy film in assets/dev/hero-proxy/ with its events.json; the manifest is not changed.
async function withProxy(M) {
  if (new URLSearchParams(location.search).get('proxy') !== '1') return M;
  let j = {};
  try { j = await (await fetch(`${base}dev/hero-proxy/events.json`, { cache: 'no-cache' })).json(); } catch { /* defaults */ }
  const hero = {};
  for (const k of ['desktop', 'mobile']) {
    const p = `dev/hero-proxy/hero-${k}`;
    hero[k] = { ...M.hero[k], mp4: `${p}.mp4`, webm: null, types: { mp4: 'video/mp4' }, first: `${p}-first.webp`, last: `${p}-last.webp`,
      frames: j.frames || M.hero[k].frames, fps: j.fps || M.hero[k].fps, events: j.events || M.hero[k].events, w: 0, h: 0 };
  }
  return { ...M, hero };
}
const remember = () => { try { sessionStorage.setItem(SEEN, '1'); } catch { /* storage may be blocked */ } };

export async function heroFilm(M) {
  M = await withProxy(M);
  const home = document.getElementById('home'), video = home.querySelector('.hero-video'), still = home.querySelector('.hero-still');
  const skip = document.getElementById('skip'), replay = document.getElementById('replay'), source = document.getElementById('source');
  let key = '', set = null, E = DEFAULTS, frac = 0, ready = false, started = false, playing = false, vfc = false, timer = 0, inView = true, past = false;

  const pick = () => (innerWidth <= 820 || innerWidth / innerHeight < 0.9 ? 'mobile' : 'desktop');
  const use = k => { key = k; set = M.hero[k]; E = { ...DEFAULTS, ...set.events }; E.settle ??= E.igniteEnd; placeTitle(); };
  // The name sits under the controller's resting place, wherever the film's frame lands on this screen.
  const placeTitle = () => {
    if (!set) return;
    const fw = video.videoWidth || still.naturalWidth || set.w || 16, fh = video.videoHeight || still.naturalHeight || set.h || 9, vw = home.clientWidth, vh = home.clientHeight;
    const s = (getComputedStyle(video).objectFit === 'contain' ? Math.min : Math.max)(vw / fw, vh / fh), oy = (vh - fh * s) / 2;
    const [, cy, w] = set.settleBox || SETTLE_BOX[key], bottom = oy + (cy * fh + (w * fw * CTRL_ASPECT) / 2) * s;
    // The stack (name, buttons, hint) clears the controller and ends above the bottom OSD; if the screen is too short the hint's line goes first.
    const stack = home.querySelector('.hero-title'), want = bottom + Math.max(52, vh * 0.06), room = vh - 72;
    home.classList.remove('tight');
    let top = Math.min(want, room - stack.offsetHeight);
    if (top < bottom + 28) { home.classList.add('tight'); top = Math.max(bottom + 28, room - stack.offsetHeight); }
    home.style.setProperty('--title-top', `${Math.round(top)}px`);
  };
  // Phones get H.264 first (hardware decoding, same size); on desktop the VP9 file is about a fifth smaller.
  const sources = () => (key === 'mobile' ? ['mp4', 'webm'] : ['webm', 'mp4'])
    .map(f => ({ src: set[f], type: set.types?.[f] || `video/${f}` }))
    .filter(s => s.src && video.canPlayType(s.type))
    .sort((a, b) => (video.canPlayType(b.type) === 'probably') - (video.canPlayType(a.type) === 'probably'));
  // Stops a film that will not be shown from downloading further.
  const unload = () => { ready = false; video.removeAttribute('poster'); video.replaceChildren(); video.load(); };
  const status = () => {
    const usb = !past && frac < E.release;
    if (source) { const t = source.dataset[usb ? 'on' : 'off']; if (source.textContent !== t) source.textContent = t; }
    root.classList.toggle('usb', usb);
  };
  const render = () => {
    status();
    filmTime(frac * set.frames / set.fps);
    home.style.setProperty('--ig', sstep(E.igniteStart, E.igniteEnd, frac).toFixed(3));
    const end = frac >= 1 ? 1 : 0; // at rest both are shown, whatever the film's timing
    const t1 = Math.max(end, sstep(E.settle, E.settle + 0.05, frac));
    home.style.setProperty('--t1', t1.toFixed(3));
    home.classList.toggle('titles-off', t1 < 0.5); // the buttons can only be clicked once they show
    home.style.setProperty('--t2', Math.max(end, sstep(E.settle + 0.06, E.settle + 0.12, frac)).toFixed(3));
  };
  // The still replaces the film once it is decoded, so a skip never shows an empty frame.
  const showStill = drop => {
    const want = base + set.last; still.src = want;
    still.decode().catch(() => {}).finally(() => { if (still.src === want && home.classList.contains('done')) { still.hidden = false; video.hidden = true; if (drop) unload(); } });
  };
  // Rests on the last frame: the film's own, or the still when the film is skipped or cannot play.
  const finish = (useStill, drop) => {
    clearTimeout(timer); playing = false; started = false; video.pause();
    frac = 1; render(); remember();
    home.classList.remove('intro'); home.classList.add('done'); root.classList.remove('intro-on');
    if (useStill) showStill(drop);
  };
  const watch = () => {
    if (vfc || !video.requestVideoFrameCallback || video.paused || !playing) return;
    vfc = true;
    video.requestVideoFrameCallback((now, meta) => { vfc = false; if (playing && video.duration > 0) { frac = Math.min(1, meta.mediaTime / video.duration); render(); } watch(); });
  };
  const sync = () => {
    if (!started) return;
    if (inView && !document.hidden) { playing = true; video.play()?.then(watch).catch(e => { if (e.name !== 'AbortError') finish(true, true); }); } else video.pause();
  };
  const load = () => {
    video.poster = base + set.first;
    video.replaceChildren();
    const list = sources(), giveUp = () => { if (!ready) finish(true, true); };
    if (!list.length) { finish(true); return; }
    list.forEach(({ src, type }, i) => {
      const s = document.createElement('source'); s.src = base + src; s.type = type;
      if (i === list.length - 1) s.addEventListener('error', giveUp);
      video.appendChild(s);
    });
    video.load();
    timer = setTimeout(giveUp, LOAD_LIMIT_MS);
  };
  const intro = () => {
    home.classList.remove('done'); home.classList.add('intro'); root.classList.add('intro-on');
    still.hidden = true; video.hidden = false; frac = 0; render();
    if (ready) { video.currentTime = 0; started = true; sync(); } else load();
  };

  use(pick());
  const film = root.classList.contains('film'); // decided in the page head, together with the poster preload
  video.muted = true; video.defaultMuted = true; video.playsInline = true;
  // A backdrop, not a player: no picture-in-picture, casting or full-screen overlays from the browser.
  video.disablePictureInPicture = true; video.disableRemotePlayback = true; video.controls = false;
  // Ready when the browser expects to play to the end without stalling, or the whole film is already buffered; until then the first frame shows.
  const buffered = () => {
    const b = video.buffered, d = video.duration;
    if (!(d > 0) || video.readyState < 2) return false;
    for (let i = 0; i < b.length; i++) if (b.start(i) <= 0.1 && b.end(i) >= d - 0.1) return true;
    return false;
  };
  const onReady = () => {
    if (ready || !video.currentSrc) return;
    ready = true; clearTimeout(timer);
    if (home.classList.contains('intro')) timer = setTimeout(() => { started = true; sync(); }, BEAT_MS);
  };
  video.addEventListener('canplaythrough', onReady);
  for (const e of ['progress', 'loadeddata']) video.addEventListener(e, () => { if (buffered()) onReady(); });
  video.addEventListener('timeupdate', () => { if (playing && video.duration > 0) { frac = Math.min(1, video.currentTime / video.duration); render(); } });
  video.addEventListener('play', watch);
  video.addEventListener('loadedmetadata', placeTitle);
  still.addEventListener('load', placeTitle);
  document.fonts?.ready.then(placeTitle);
  video.addEventListener('ended', () => finish(false));
  document.addEventListener('visibilitychange', sync);
  new IntersectionObserver(([e]) => { inView = e.isIntersecting; sync(); }, { threshold: 0.2 }).observe(home);
  new IntersectionObserver(([e]) => home.classList.toggle('away', !e.isIntersecting)).observe(home);
  const onScroll = () => { const p = scrollY > innerHeight * 0.5; if (p !== past) { past = p; status(); } };
  addEventListener('scroll', onScroll, { passive: true });
  skip.addEventListener('click', () => { replay.focus({ preventScroll: true }); finish(true); });
  replay.addEventListener('click', () => { if (cine) { scrollTo(0, 0); intro(); skip.focus({ preventScroll: true }); } });
  addEventListener('resize', () => {
    placeTitle();
    if (pick() === key) return;
    use(pick()); ready = false;
    if (home.classList.contains('done')) showStill(); else { started = false; clearTimeout(timer); load(); }
  });

  onScroll();
  root.classList.remove('film');
  if (film) intro(); else { home.classList.add('intro'); finish(true); }
}

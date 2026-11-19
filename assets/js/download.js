// Every Download button. The installer URL is resolved only after a click; nothing is requested on page load.
const REPO = 'Midzer00/ds5-untethered';
const API = `https://api.github.com/repos/${REPO}/releases/latest`;
const UPDATE = `https://raw.githubusercontent.com/${REPO}/main/update.json`;
const PAGE = `https://github.com/${REPO}/releases/latest`;
const SETUP = /^DS5-Untethered-(.+)-Setup\.exe$/;
const API_MS = 2000, TOTAL_MS = 2500;

const isWindows = () => {
  const p = navigator.userAgentData?.platform;
  return p ? /windows/i.test(p) : /Windows NT/i.test(navigator.userAgent) && !/Windows Phone/i.test(navigator.userAgent);
};
async function getJson(url, ms, outer) {
  const c = new AbortController(), t = setTimeout(() => c.abort(), ms), stop = () => c.abort();
  outer.addEventListener('abort', stop);
  try {
    const r = await fetch(url, { credentials: 'omit', cache: 'no-store', signal: c.signal });
    if (!r.ok) throw new Error(String(r.status));
    return await r.json();
  } finally { clearTimeout(t); outer.removeEventListener('abort', stop); }
}
async function resolve() {
  const all = new AbortController(), end = Date.now() + TOTAL_MS, t = setTimeout(() => all.abort(), TOTAL_MS);
  try {
    try {
      const j = await getJson(API, API_MS, all.signal);
      const a = j.assets?.find(x => SETUP.test(x.name));
      if (a?.browser_download_url) return { url: a.browser_download_url, name: a.name, version: a.name.match(SETUP)[1] };
    } catch { /* falls through to update.json */ }
    const left = end - Date.now();
    if (left <= 0) return null;
    const { version } = await getJson(UPDATE, left, all.signal);
    if (!/^[\w.-]+$/.test(version || '')) return null;
    // update.json's own link is the -Update.exe build reserved for in-app updates, so it is not used.
    const name = `DS5-Untethered-${version}-Setup.exe`;
    return { url: `https://github.com/${REPO}/releases/download/v${version}/${name}`, name, version };
  } catch { return null; } finally { clearTimeout(t); }
}

export function initDownload() {
  const panel = document.getElementById('dl-panel'), live = document.getElementById('dl-live'), T = panel.dataset;
  const buttons = [...document.querySelectorAll('[data-dl]')];
  const label = b => b.querySelector('.dl-label');
  buttons.forEach(b => { b.dataset.word = label(b).textContent; });
  panel.querySelector('.dl-close').addEventListener('click', () => { panel.hidden = true; });
  addEventListener('keydown', e => { if (e.key === 'Escape') panel.hidden = true; });

  // Other systems still get the latest Setup.exe on a click (to copy to a PC); the note says it runs on Windows.
  if (!isWindows()) {
    document.documentElement.classList.add('not-win');
    document.querySelectorAll('.dl-note').forEach(n => { n.hidden = false; });
  }
  let busy = false;
  const say = (b, text) => { label(b).textContent = text; live.textContent = text; };
  buttons.forEach(b => b.addEventListener('click', async e => {
    if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    if (busy) return;
    busy = true; say(b, T.prep);
    const hit = await resolve();
    if (hit) {
      location.assign(hit.url);
      say(b, T.done);
      panel.querySelector('.dl-file').textContent = hit.name;
      panel.querySelector('.dl-ver').textContent = hit.version;
      panel.querySelector('.dl-direct').href = hit.url;
      panel.hidden = false;
      setTimeout(() => { label(b).textContent = b.dataset.word; }, 6000);
    } else {
      label(b).textContent = b.dataset.word; live.textContent = '';
      location.assign(PAGE);
    }
    busy = false;
  }));
}

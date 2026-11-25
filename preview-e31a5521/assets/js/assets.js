// Manifest loading and still images. manifest.json wins over manifest.dev.json.

export const base = new URL('../', import.meta.url).href;

async function getJson(name) {
  try {
    const r = await fetch(base + name, { cache: 'no-cache' });
    return r.ok ? await r.json() : null;
  } catch { return null; }
}

export async function loadManifest() {
  return normalise((await getJson('manifest.json')) || (await getJson('manifest.dev.json')));
}

// The stage entry uses fractions; scenes work in picture pixels.
function normalise(M) {
  const S = M.stage, at = k => { const h = S.hotspots[k]; return [h[0] * S.w, h[1] * S.h]; };
  S.art = S.file; S.artSm = S.fileSm;
  S.gripLeft = at('gripLeft'); S.gripRight = at('gripRight'); S.triggerLeft = at('triggerLeft'); S.triggerRight = at('triggerRight');
  S.speaker = at('speaker'); S.mute = at('mic'); S.ps = [...at('ps'), S.w * 0.028];
  S.hot = [['gripLeft', 'haptics'], ['gripRight', 'haptics'], ['triggerLeft', 'triggers'], ['triggerRight', 'xbox'], ['strip', 'light'], ['touchpad', 'mouse-mode'], ['speaker', 'audio'], ['mic', 'audio']]
    .map(([k, f]) => { const [x, y] = at(k); return { id: k, x, y, f }; });
  return M;
}

// Quads may be normalised (0..1) or in image pixels.
export function quadPixels(quad, w, h) {
  const unit = quad.flat().every(v => v <= 1.0001);
  return quad.map(([x, y]) => (unit ? [x * w, y * h] : [x, y]));
}

// CSS matrix3d mapping the rectangle w*h onto four points (clockwise from top-left).
export function quadMatrix(w, h, q) {
  const src = [[0, 0], [w, 0], [w, h], [0, h]], A = [];
  for (let i = 0; i < 4; i++) {
    const [x, y] = src[i], [u, v] = q[i];
    A.push([x, y, 1, 0, 0, 0, -u * x, -u * y, u], [0, 0, 0, x, y, 1, -v * x, -v * y, v]);
  }
  for (let c = 0; c < 8; c++) {
    let best = c;
    for (let r = c + 1; r < 8; r++) if (Math.abs(A[r][c]) > Math.abs(A[best][c])) best = r;
    [A[c], A[best]] = [A[best], A[c]];
    for (let r = 0; r < 8; r++) {
      if (r === c) continue;
      const f = A[r][c] / A[c][c];
      for (let k = c; k < 9; k++) A[r][k] -= f * A[c][k];
    }
  }
  const [a, b, c, d, e, f, g, k] = A.map((row, i) => row[8] / row[i]);
  return `matrix3d(${[a, d, 0, g, b, e, 0, k, 0, 0, 1, 0, c, f, 0, 1].map(n => +n.toFixed(8)).join(',')})`;
}

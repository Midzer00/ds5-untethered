# SPDX-License-Identifier: GPL-3.0-or-later
"""The light-strip masks as a real DualSense shows its light: the strip itself, and a short soft halo hugging it.

The glow used to be a render with the strip as the only light source, which also had what that light does in odd places
(a bright patch on the shell beside each top end, light over the body's edge, spots on the core) and render noise on
the touchpad. Now the glow is drawn from the strip's outline (layout.pad_and_band, the band ControllerArt has): full
GLOW_PEAK at the strip's edge, falling off smoothly to nothing LIGHT_REACH pixels out, the same all along the strip.
Every mask is then cut to the body (the art's alpha, shrunk by two pixels so the halved masks the app uses stay inside
it too). Running it again gives the same masks.

Used by build_controller.py on every render (numpy only); run on its own to redo the shipped glow masks:
    python tools/art/controller/light_clip.py [ASSETS_DIR]
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import layout as L  # noqa: E402

GLOW_PEAK = 0.45  # the halo right at the strip's edge


def edge_distance():
    """Distance of each pixel centre of L.LIGHT_RECT from the strip's outline (0 inside the strip)."""
    _, band, _ = L.pad_and_band()
    pts = np.array(band + [band[0]], np.float64)
    x0, y0, w, h = L.LIGHT_RECT
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    xx += x0 + 0.5
    yy += y0 + 0.5
    d = np.full((h, w), np.inf)
    inside = np.zeros((h, w), bool)
    for a, b in zip(pts[:-1], pts[1:]):
        ab = b - a
        l2 = ab @ ab
        if l2 == 0:
            continue
        t = np.clip(((xx - a[0]) * ab[0] + (yy - a[1]) * ab[1]) / l2, 0, 1)
        d = np.minimum(d, np.hypot(xx - a[0] - t * ab[0], yy - a[1] - t * ab[1]))
        # Even-odd crossing test for inside.
        crosses = ((a[1] > yy) != (b[1] > yy)) & (xx < a[0] + (yy - a[1]) * ab[0] / np.where(ab[1] == 0, 1, ab[1]))
        inside ^= crosses
    return np.where(inside, 0.0, d)


def halo(cover):
    """The glow: GLOW_PEAK at the strip's edge easing to 0 at L.LIGHT_REACH (smoothstep), none on the strip itself."""
    t = np.clip(edge_distance() / L.LIGHT_REACH, 0, 1)
    return GLOW_PEAK * (1 - t * t * (3 - 2 * t)) * (1 - cover)


def body_inside(art_alpha, shrink=2):
    """The art's alpha (0..1, cut to L.LIGHT_RECT) shrunk by `shrink` pixels: the lowest alpha in each (2k+1)^2 box."""
    pad = np.pad(art_alpha, shrink, mode="constant", constant_values=0)
    h, w = art_alpha.shape
    out = np.ones_like(art_alpha)
    for dy in range(2 * shrink + 1):
        for dx in range(2 * shrink + 1):
            out = np.minimum(out, pad[dy:dy + h, dx:dx + w])
    return out


def finish_masks(strip, core, art_alpha):
    """(glow, strip, core), 0..1 over L.LIGHT_RECT: the halo drawn round the strip, all three cut to the body."""
    body = body_inside(art_alpha)
    return np.minimum(halo(strip), body), np.minimum(strip, body), np.minimum(core, body)


def main():
    from PIL import Image

    assets = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..",
                                                                   "src", "Kopru.App", "Assets")
    x, y, w, h = L.LIGHT_RECT
    for prefix in ("controller", "controller-edge"):
        art = np.asarray(Image.open(os.path.join(assets, f"{prefix}.png")).convert("RGBA"), np.float64)[y:y + h, x:x + w, 3] / 255
        paths = [os.path.join(assets, f"{prefix}-light-{part}.png") for part in ("glow", "strip", "core")]
        strip, core = (np.asarray(Image.open(p).convert("RGBA"), np.float64)[..., 3] / 255 for p in paths[1:])
        for path, mask in zip(paths, finish_masks(strip, core, art)):
            rgba = np.full((h, w, 4), 255, np.uint8)
            rgba[..., 3] = np.round(np.clip(mask, 0, 1) * 255).astype(np.uint8)
            Image.fromarray(rgba, "RGBA").save(path, optimize=True)
            print(path)


if __name__ == "__main__":
    main()

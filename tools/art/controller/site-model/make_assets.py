# SPDX-License-Identifier: GPL-3.0-or-later
"""The app's DualSense sprite set from the site top-view passes (site_top_passes.py).

    python make_assets.py PASS_DIR OUT_DIR [--name controller] [--no-masks]   (OUT_DIR: e.g. src/Kopru.App/Assets)

--name sets the files' prefix ({name}.png, {name}-well-left/right.png, {name}-cap.png); --no-masks leaves out the light
masks (a colour variant shares the default's: same shape) and needs no cover.png. --alpha-like REF.png --fill #RRGGBB
(translucent variants): where the render lets the background through (see-through triggers, the touchpad's edge) it is
laid over that colour, so the art keeps the default's outline, which the shared light masks are cut to.

Same post-processing as tools/art/controller/build_controller.py (render_sprites, render_light_masks) and light_clip,
except the glow: the site's strip is lit along the sides and round the bottom corners and fades out along the bottom
middle (its coverage pass carries the fade), so the halo round the band outline is weighted by the strip's light at the
nearest point of the band, not drawn all round it.
"""
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import layout as L  # noqa: E402
import light_clip as LC  # noqa: E402


def read(folder, name):
    return np.asarray(Image.open(os.path.join(folder, name)).convert("RGBA"), np.float32) / 255


def write(folder, name, rgba):
    a = np.clip(rgba, 0, 1)
    Image.fromarray((a * 255 + 0.5).astype(np.uint8), "RGBA").save(os.path.join(folder, name), optimize=True)


def box_blur(a, k):
    pad = np.pad(a, k, mode="edge")
    cs = np.pad(pad.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    n = 2 * k + 1
    return (cs[n:, n:] - cs[:-n, n:] - cs[n:, :-n] + cs[:-n, :-n]) / n ** 2


def keep_outline(img, ref_alpha, fill):
    """Where img lets more background through than ref_alpha, lays it over fill (straight colour), in place."""
    a = img[..., 3:4]
    gap = np.clip(ref_alpha[..., None] - a, 0, 1)
    img[..., :3] = np.where(a + gap > 1e-4, (img[..., :3] * a + fill * gap) / np.maximum(a + gap, 1e-4), 0)
    img[..., 3:4] = np.maximum(a, ref_alpha[..., None])


def sprites(src, alpha_like=None, fill=None):
    """{"body", "well-left", "well-right", "cap"}: float RGBA arrays (0..1, straight) cut from the passes in src."""
    art, nocap, capc = read(src, "beauty.png"), read(src, "nocap.png"), read(src, "capcover.png")
    if alpha_like is not None:
        ref = np.asarray(Image.open(alpha_like).convert("RGBA"), np.float32)[..., 3] / 255
        for img in (art, nocap):
            keep_outline(img, ref, np.asarray(fill if fill is not None else (0.12, 0.12, 0.12), np.float32))
    out = {"body": art}
    # ---- stick sprites (build_controller.render_sprites)
    half = L.SPRITE_HALF
    yy, xx = np.mgrid[-half:half, -half:half]
    r = np.hypot(xx + 0.5, yy + 0.5)
    for name, (cx, cy) in L.STICKS.items():
        box = (slice(cy - half, cy + half), slice(cx - half, cx + half))
        well = nocap[box].copy()
        well[..., 3] *= np.clip((L.WELL_SPRITE_R - r) / 10, 0, 1)
        out[f"well-{name}"] = well
        if name != "left":
            continue
        a, n, c = art[box], nocap[box], capc[box][..., 3:4]
        lum = np.array([0.2126, 0.7152, 0.0722], np.float32)
        la, ln = (a[..., :3] * lum).sum(-1, keepdims=True), (n[..., :3] * lum).sum(-1, keepdims=True)
        shadow = np.where(ln > 0.02, np.clip(1 - la / np.maximum(ln, 1e-4), 0, 1), 0)[..., 0]
        shadow = box_blur(shadow, 9)
        shadow = (shadow * np.clip((L.STICK_CAP_R + 34 - r) / 24, 0, 1))[..., None]
        alpha = c + (1 - c) * shadow
        rgb = np.where(alpha > 1e-4, a[..., :3] * c / np.maximum(alpha, 1e-4), 0)
        out["cap"] = np.concatenate([rgb, alpha], -1)
    return out


def light_masks(src, art_alpha):
    """(strip, core, glow) over L.LIGHT_RECT from the coverage pass, cut to the body (art_alpha: the full art's)."""
    cov = read(src, "cover.png")
    # (build_controller.render_light_masks + light_clip.finish_masks, the glow weighted by the strip's light)
    x, y, w, h = L.LIGHT_RECT
    lb = (slice(y, y + h), slice(x, x + w))
    d_band = LC.edge_distance()
    # The coverage pass also has the slit along the pad's top edge (pad-slit), which is not part of the app's band: the
    # strip is kept to the band (plus 2 px of anti-aliasing).
    cover = (cov[..., :3].max(-1) * cov[..., 3])[lb] * np.clip(3 - d_band, 0, 1)
    core = np.clip((box_blur(cover, 5) - 0.86) / 0.12, 0, 1) * cover
    band = d_band == 0
    _, (iy, ix) = ndi.distance_transform_edt(~band, return_indices=True)
    light_here = ndi.maximum_filter(cover, 5)[iy, ix]          # the strip's light at the nearest band point
    t = np.clip(d_band / L.LIGHT_REACH, 0, 1)
    glow = LC.GLOW_PEAK * (1 - t * t * (3 - 2 * t)) * (1 - cover) * light_here
    body = LC.body_inside(art_alpha[lb])
    return np.minimum(cover, body), np.minimum(core, body), np.minimum(glow, body)


def main():
    args = sys.argv[1:]
    opts = {k: args[args.index(k) + 1] for k in ("--name", "--alpha-like", "--fill") if k in args}
    name = opts.get("--name", "controller")
    src, out = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] not in opts)][:2]
    os.makedirs(out, exist_ok=True)
    fill = None
    if "--fill" in opts:
        h = opts["--fill"].lstrip("#")
        fill = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    parts = sprites(src, opts.get("--alpha-like"), fill)
    write(out, f"{name}.png", parts["body"])
    for k in ("well-left", "well-right", "cap"):
        write(out, f"{name}-{k}.png", parts[k])
    if "--no-masks" not in args:
        for k, m in zip(("strip", "core", "glow"), light_masks(src, parts["body"][..., 3])):
            rgba = np.ones(m.shape + (4,), np.float32)
            rgba[..., 3] = m
            write(out, f"{name}-light-{k}.png", rgba)
    print("written to", out)


if __name__ == "__main__":
    main()

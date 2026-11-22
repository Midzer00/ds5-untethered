# SPDX-License-Identifier: GPL-3.0-or-later
"""Printed patterns as an RGBA layer over the art (numpy only; 2290 x 1520, y down), drawn from simple generated
shapes: our own patterns (stripes, hatching, rays, dashed outlines, dots, speckle, shards, brush strokes), nothing traced.

    layer(ops) -> float32 array (1520, 2290, 4): straight colour and coverage

Each op is a dict with "op" and "colour" (hex), and optionally "mirror": true (also drawn mirrored about the body's
axis), "alpha" (coverage scale) and "wear" (0..1: the print chipped away in rough patches; "seed" picks them):

    {"op": "poly", "pts": [[x, y], ...]}                            a filled polygon
    {"op": "line", "pts": [[x, y], ...], "width": px, "dash": [on, off]}   a thick (optionally dashed) polyline
    {"op": "rays", "centre": [x, y], "count": n, "width": px, "inner": r, "seed": s}   thin lines fanning out
    {"op": "hatch", "pts": [...], "spacing": px, "width": px, "angle": deg, "phase": px}   parallel lines in a polygon
    {"op": "dots", "pts": [...], "spacing": px, "size": px}        a dot grid inside a polygon
    {"op": "speckle", "count": n, "size": [min, max], "seed": s, "colours": [...]}   scattered round glints
    {"op": "shards", "count": n, "size": [min, max], "seed": s, "colours": [...], "region": [x0, y0, x1, y1]}
                                                                     thin angular slivers
    {"op": "strokes", ...brush.coverage parameters}                 dry-brush strokes (brush.py)
"""
import math

import numpy as np

import brush

ART_W, ART_H = 2290, 1520
AXIS = 1144.5


def rgb(hexstr):
    h = hexstr.lstrip("#")
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)], np.float32)


def _grid(bbox):
    x0, y0, x1, y1 = (int(max(0, math.floor(bbox[0]) - 2)), int(max(0, math.floor(bbox[1]) - 2)),
                      int(min(ART_W, math.ceil(bbox[2]) + 2)), int(min(ART_H, math.ceil(bbox[3]) + 2)))
    if x0 >= x1 or y0 >= y1:
        return None
    yy, xx = np.mgrid[y0:y1, x0:x1].astype(np.float32)
    return (slice(y0, y1), slice(x0, x1)), xx + 0.5, yy + 0.5


def _poly_cover(pts, out):
    """Anti-aliased polygon coverage (4 sub-samples per pixel), max into out."""
    p = np.asarray(pts, np.float32)
    g = _grid((p[:, 0].min(), p[:, 1].min(), p[:, 0].max(), p[:, 1].max()))
    if g is None:
        return
    sl, xx, yy = g
    acc = np.zeros(xx.shape, np.float32)
    for ox, oy in ((-0.25, -0.25), (0.25, -0.25), (-0.25, 0.25), (0.25, 0.25)):
        qx, qy = xx + ox, yy + oy
        inside = np.zeros(xx.shape, bool)
        for (ax, ay), (bx, by) in zip(p, np.roll(p, -1, axis=0)):
            cond = (ay > qy) != (by > qy)
            with np.errstate(divide="ignore", invalid="ignore"):
                xi = (bx - ax) * (qy - ay) / (by - ay) + ax
            inside ^= cond & (qx < xi)
        acc += inside
    np.maximum(out[sl], acc / 4, out=out[sl])


def _seg_cover(a, b, width, out):
    a, b = np.asarray(a, np.float32), np.asarray(b, np.float32)
    r = width / 2
    g = _grid((min(a[0], b[0]) - r, min(a[1], b[1]) - r, max(a[0], b[0]) + r, max(a[1], b[1]) + r))
    if g is None:
        return
    sl, xx, yy = g
    ab = b - a
    l2 = max(float(ab @ ab), 1e-6)
    t = np.clip(((xx - a[0]) * ab[0] + (yy - a[1]) * ab[1]) / l2, 0, 1)
    d = np.hypot(xx - a[0] - t * ab[0], yy - a[1] - t * ab[1])
    np.maximum(out[sl], np.clip(r - d + 0.5, 0, 1), out=out[sl])


def _polyline(pts, width, dash, out):
    pts = [np.asarray(p, np.float32) for p in pts]
    if not dash:
        for a, b in zip(pts, pts[1:]):
            _seg_cover(a, b, width, out)
        return
    on, off = dash
    pos, drawing, left = 0.0, True, on
    for a, b in zip(pts, pts[1:]):
        seg = float(np.linalg.norm(b - a))
        d = (b - a) / max(seg, 1e-6)
        s = 0.0
        while s < seg:
            step = min(left, seg - s)
            if drawing:
                _seg_cover(a + d * s, a + d * (s + step), width, out)
            s += step
            left -= step
            if left <= 1e-6:
                drawing = not drawing
                left = on if drawing else off
        pos += seg


def _smooth_field(rng, scale):
    """A smooth random field 0..1 over the art (blurred white noise, FFT)."""
    f = np.fft.fft2(rng.random((ART_H, ART_W)))
    ky, kx = np.fft.fftfreq(ART_H)[:, None], np.fft.fftfreq(ART_W)[None, :]
    v = np.real(np.fft.ifft2(f * np.exp(-2 * (math.pi * scale) ** 2 * (kx ** 2 + ky ** 2))))
    return ((v - v.min()) / max(v.max() - v.min(), 1e-6)).astype(np.float32)


def _clip_poly(pts, cover):
    mask = np.zeros_like(cover)
    _poly_cover(pts, mask)
    cover *= mask


def _op_cover(op, rng):
    cov = np.zeros((ART_H, ART_W), np.float32)
    kind = op["op"]
    if kind == "poly":
        _poly_cover(op["pts"], cov)
    elif kind == "line":
        _polyline(op["pts"], op.get("width", 4), op.get("dash"), cov)
    elif kind == "rays":
        cx, cy = op["centre"]
        n = op.get("count", 40)
        for k in range(n):
            ang = 2 * math.pi * (k + rng.uniform(-0.3, 0.3)) / n
            d = (math.cos(ang), math.sin(ang))
            r0 = op.get("inner", 0)
            _seg_cover((cx + d[0] * r0, cy + d[1] * r0), (cx + d[0] * 3000, cy + d[1] * 3000), op.get("width", 2), cov)
    elif kind == "hatch":
        sp, w, ang = op.get("spacing", 24), op.get("width", 6), math.radians(op.get("angle", 45))
        p = np.asarray(op["pts"], np.float32)
        g = _grid((p[:, 0].min(), p[:, 1].min(), p[:, 0].max(), p[:, 1].max()))
        if g:
            sl, xx, yy = g
            t = (xx * math.cos(ang) + yy * math.sin(ang) + op.get("phase", 0)) % sp
            band = np.clip(w / 2 - np.abs(t - sp / 2) + 0.5, 0, 1)
            cov[sl] = band
            _clip_poly(op["pts"], cov)
    elif kind == "dots":
        sp, size = op.get("spacing", 20), op.get("size", 4)
        p = np.asarray(op["pts"], np.float32)
        g = _grid((p[:, 0].min(), p[:, 1].min(), p[:, 0].max(), p[:, 1].max()))
        if g:
            sl, xx, yy = g
            dx, dy = (xx % sp) - sp / 2, (yy % sp) - sp / 2
            cov[sl] = np.clip(size / 2 - np.hypot(dx, dy) + 0.5, 0, 1)
            _clip_poly(op["pts"], cov)
    elif kind == "strokes":
        params = {k: v for k, v in op.items() if k in ("seed", "count", "width", "dry", "region", "length", "angle",
                                                        "spread", "overlap")}
        cov = brush.coverage(**params)
    elif kind in ("speckle", "shards"):
        raise ValueError("multi-colour ops are drawn by layer()")
    else:
        raise ValueError(f"unknown op {kind}")
    if op.get("mirror"):
        cov = np.maximum(cov, cov[:, ::-1])          # the art's centre (1144.5) is the body's axis
    if op.get("wear"):
        field = _smooth_field(np.random.default_rng(op.get("seed", 5) + 101), op.get("wearScale", 5))
        cov *= np.clip((field - op["wear"]) / 0.04 + 0.5, 0, 1)
    return cov * op.get("alpha", 1.0)


def layer(ops):
    out = np.zeros((ART_H, ART_W, 4), np.float32)
    for i, op in enumerate(ops):
        rng = np.random.default_rng(op.get("seed", i + 1))
        if op["op"] in ("speckle", "shards"):
            colours = [rgb(c) for c in op["colours"]]
            maps = [np.zeros((ART_H, ART_W), np.float32) for _ in colours]
            x0, y0, x1, y1 = op.get("region", (60, 20, ART_W - 60, ART_H - 60))
            for _ in range(op.get("count", 200)):
                cov = maps[rng.integers(len(colours))]
                x, y = rng.uniform(x0, x1), rng.uniform(y0, y1)
                s = rng.uniform(*op.get("size", (2, 5)))
                if op["op"] == "speckle":
                    _seg_cover((x, y), (x, y), s, cov)
                else:
                    # a long thin crystal: a narrow diamond, its widest point off centre
                    a = rng.uniform(0, math.pi)
                    d, n = np.array([math.cos(a), math.sin(a)]), np.array([-math.sin(a), math.cos(a)])
                    w = s * rng.uniform(0.07, 0.14)
                    c0 = np.array([x, y])
                    k = rng.uniform(-0.3, 0.3)
                    _poly_cover([c0 - d * s * 0.6, c0 + d * s * k + n * w, c0 + d * s, c0 + d * s * k - n * w], cov)
            for c, cov in zip(colours, maps):
                if op.get("mirror"):
                    cov = np.maximum(cov, cov[:, ::-1])
                _over(out, c, cov * op.get("alpha", 1.0))
            continue
        _over(out, rgb(op["colour"]), _op_cover(op, rng))
    return out


def _over(out, c, cov):
    """Lays colour c over out (straight colour) by coverage cov."""
    a_new = cov + out[..., 3] * (1 - cov)
    out[..., :3] = np.where(a_new[..., None] > 1e-6,
                            (c * cov[..., None] + out[..., :3] * (out[..., 3] * (1 - cov))[..., None]) /
                            np.maximum(a_new[..., None], 1e-6), 0)
    out[..., 3] = a_new

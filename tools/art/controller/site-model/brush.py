# SPDX-License-Identifier: GPL-3.0-or-later
"""Our own random brush strokes (numpy only): a coverage map over the art (2290 x 1520, y down) for a "strokes" pattern.

Each stroke is a broad sweeping cubic curve laid down as a run of overlapping stamps: a blunt loaded start, a body that
thins a little as the brush lifts, and dry-brush streaks: a fixed random bristle profile across the brush, which breaks
the ink up more and more towards the end, so a stroke ends in split streaks rather than a point. A seed makes it repeatable; nothing is traced from anything.

    coverage(seed, count, width, dry, region) -> float32 array (1520, 2290), 0..1
"""
import math

import numpy as np

ART_W, ART_H = 2290, 1520


def _smooth_noise(rng, n, k):
    v = rng.random(n + 2 * k)
    kern = np.hanning(2 * k + 1)
    v = np.convolve(v, kern / kern.sum(), mode="same")[k:-k]
    return (v - v.min()) / max(v.max() - v.min(), 1e-6)


def _stroke(rng, p0, ang, width, dry, length):
    """One stroke's coverage stamps: [(slice_y, slice_x, alpha), ...]."""
    d = np.array([math.cos(ang), math.sin(ang)])
    n = np.array([-d[1], d[0]])
    ln = rng.uniform(*length)
    bend = rng.choice([-1, 1]) * rng.uniform(0.1, 0.3) * ln
    p1 = p0 + d * ln / 3 + n * bend
    p2 = p0 + d * 2 * ln / 3 + n * bend * rng.uniform(0.4, 1.1)
    p3 = p0 + d * ln
    w0 = width * rng.uniform(0.6, 1.3)
    bristle = _smooth_noise(rng, 96, 2)                 # across the brush, -1..1 -> 96 samples
    speckle = _smooth_noise(rng, 512, 6)                # along the stroke: the ink running out unevenly
    stamps = []
    steps = max(8, int(ln / 1.5))
    for i, t in enumerate(np.linspace(0, 1, steps)):
        u = 1 - t
        c = u ** 3 * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t ** 3 * p3
        tan = 3 * u * u * (p1 - p0) + 6 * u * t * (p2 - p1) + 3 * t * t * (p3 - p2)
        tan /= max(np.linalg.norm(tan), 1e-6)
        nor = np.array([-tan[1], tan[0]])
        pressure = 0.8 + 0.2 * min(1.0, t / 0.06) if t < 0.06 else 1 - 0.45 * (t - 0.06) / 0.94
        hw = max(1.5, w0 / 2 * pressure)
        dryness = dry * max(0.0, (t - 0.2) / 0.8) ** 1.2 * (0.6 + 0.8 * speckle[int(t * 511)]) + 0.04
        r = int(hw + 3)
        cx, cy = int(c[0]), int(c[1])
        xa, xb, ya, yb = max(cx - r, 0), min(cx + r + 1, ART_W), max(cy - r, 0), min(cy + r + 1, ART_H)
        if xa >= xb or ya >= yb:
            continue
        yy, xx = np.mgrid[ya:yb, xa:xb].astype(np.float32)
        dx, dy = xx + 0.5 - c[0], yy + 0.5 - c[1]
        across = (dx * nor[0] + dy * nor[1]) / hw
        along = dx * tan[0] + dy * tan[1]
        body = np.clip((1 - np.abs(across)) * hw / 1.2, 0, 1) * np.clip(2.5 - np.abs(along) / 1.2, 0, 1)
        if i == 0:   # a round, loaded start
            body = np.maximum(body, np.clip((1 - np.hypot(across, along / hw)) * hw / 1.2, 0, 1) * (along < 0))
        b = np.interp(np.clip(across, -1, 1), np.linspace(-1, 1, bristle.size), bristle)
        stamps.append((slice(ya, yb), slice(xa, xb), body * np.clip((b - dryness) / 0.08 + 0.5, 0, 1)))
    return stamps


def coverage(seed=1, count=9, width=120.0, dry=0.75, region=(120, 40, 2170, 1450), length=(600, 1200), angle=-25.0,
             spread=30.0, overlap=0.12):
    """Strokes sweep roughly one way (angle, degrees, y down: -25 rises to the right, give or take spread), spread over
    the region (one start per cell of a grid) and barely cross (a stroke covering more than `overlap` of its own ink
    with earlier ones is drawn again elsewhere), so no crossings build shapes that read as marks or letters."""
    rng = np.random.default_rng(seed)
    acc = np.zeros((ART_H, ART_W), np.float32)
    x0, y0, x1, y1 = region
    cols = max(1, round(math.sqrt(count * (x1 - x0) / (y1 - y0))))
    rows = math.ceil(count / cols)
    for cell in rng.permutation(cols * rows)[:count]:
        gx, gy = cell % cols, cell // cols
        for _ in range(40):
            p0 = np.array([x0 + (gx + rng.uniform(0.05, 0.95)) * (x1 - x0) / cols,
                           y0 + (gy + rng.uniform(0.05, 0.95)) * (y1 - y0) / rows])
            ang = math.radians(angle + rng.uniform(-spread, spread)) + (math.pi if rng.random() < 0.5 else 0)
            stamps = _stroke(rng, p0, ang, width, dry, length)
            own = np.zeros_like(acc)
            for sy, sx, a in stamps:
                np.maximum(own[sy, sx], a, out=own[sy, sx])
            if (own * acc).sum() <= overlap * max(own.sum(), 1):
                np.maximum(acc, own, out=acc)
                break
    return acc

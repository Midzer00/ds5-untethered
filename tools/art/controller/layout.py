# SPDX-License-Identifier: GPL-3.0-or-later
"""2D geometry of the controller front view, in the art's own pixels (2290 x 1520, x right, y down).

The app draws its overlays from the same numbers: ControllerLayout.cs (button regions, sticks, touch surface) and
ControllerArt.cs (touchpad, light strip, player LEDs). The shapes those files define are ported here one to one, so the
modelled parts land exactly under them. Everything else (the body silhouette, the shell split) is drawn here.
Plain Python (math only), so it can be imported by Blender and by the check script alike.
"""
import math

ART_W, ART_H = 2290, 1520
MIRROR_X = 1145.0      # ControllerLayout.MirrorX (regions)
BODY_CX = 1144.5       # the body's own axis: half way between the sticks (778, 1511)

# ---- ControllerLayout.cs ----
FACE = {  # centre, region radius (the hole in the shell, plus a pixel)
    "triangle": ((1846, 265), 78),
    "square": ((1702.5, 416), 78),
    "circle": ((1992, 416), 78),
    "cross": ((1846.5, 569), 78),
}
STICKS = {"left": (778, 743), "right": (1511, 743)}
STICK_CAP_R = 140
STICK_WELL_R = 176     # how far a moved cap may reach (cap + travel)
STICK_OPENING_R = 182  # the hole in the body the stick sits in
SPRITE_HALF = 270      # moved-stick sprites: squares of 2 x this around the stick centre
LIGHT_RECT = (560, 0, 1170, 640)  # x, y, w, h of the light-strip masks (the strip and its glow)
WELL_SPRITE_R = 232    # the no-cap sprite fades out by here (the light strip is 239 px from a stick centre)
DPAD_CENTRE = (436, 438)
DPAD_REACH = {0: 197, 90: 199, 180: 202, 270: 198}
CREATE = (615.5, 189, 27, 54, -10)
OPTIONS = (1671.5, 189, 27, 54, 10)
PS = ((1144, 770), 55)
MUTE = (1143.5, 871, 15, 56, 90)
FN_LEFT = (905, 940, 18, 40, 90)
FN_RIGHT = (1385, 940, 18, 40, 90)
TOUCH_SURFACE = (712, 40, 864, 472)

TRIGGER_FACE = [
    (285, 94), (291, 80), (297, 72), (310, 61), (330, 49), (350, 40), (370, 32), (400, 22),
    (430, 15), (460, 10), (490, 6), (530, 6), (550, 8), (560, 11), (570, 14), (581, 19),
    (570, 24), (560, 27), (550, 30), (530, 34), (500, 41), (470, 47), (440, 53), (400, 61),
    (370, 68), (340, 73), (310, 78), (300, 78), (292, 85),
]
BUMPER_FACE = [
    (292, 85), (300, 78), (310, 78), (340, 73), (370, 68), (400, 61), (440, 53), (470, 47),
    (500, 41), (530, 34), (550, 30), (560, 27), (570, 24), (581, 19), (585, 26), (588, 39),
    (590, 49), (591, 63), (580, 63), (560, 64), (540, 66), (520, 68), (500, 72), (480, 75),
    (460, 78), (440, 82), (420, 86), (400, 90), (380, 95), (360, 99), (340, 104), (320, 108),
    (300, 113), (285, 115),
]
ARM_PROFILE = [
    (241, 0), (242, 20), (244, 34.5), (248, 43), (252, 49), (260, 57), (268, 63), (276, 66), (284, 68), (292, 69),
    (308, 69), (324, 67.5), (340, 65.5), (348, 64), (356, 61.5), (364, 57.5), (372, 51.5), (380, 44), (390, 34),
    (400, 23.5), (408, 14.5), (413, 7), (416, 0),
]
ARM_TIP = 22

# ---- ControllerArt.cs ----
LED_CENTRE_X, LED_Y, LED_SPACING = 1144, 552, 34


def mirror_x(points, axis=MIRROR_X):
    return [(2 * axis - x, y) for x, y in reversed(points)]


def circle(cx, cy, r, segments=96):
    return [(cx + r * math.cos(2 * math.pi * i / segments), cy + r * math.sin(2 * math.pi * i / segments)) for i in range(segments)]


def pill(cx, cy, half_w, half_l, degrees, n=24):
    """ControllerLayout.Pill: a vertical pill turned clockwise on screen around its centre."""
    local = []
    straight = max(0.0, half_l - half_w)
    for i in range(n + 1):
        a = math.pi + math.pi * i / n
        local.append((half_w * math.cos(a), -straight + half_w * math.sin(a)))
    for i in range(n + 1):
        a = math.pi * i / n
        local.append((half_w * math.cos(a), straight + half_w * math.sin(a)))
    t = math.radians(degrees)
    c, s = math.cos(t), math.sin(t)
    return [(cx + x * c - y * s, cy + x * s + y * c) for x, y in local]


def dpad_arm(degrees, reach):
    """ControllerLayout.DpadArm."""
    cx, cy = DPAD_CENTRE
    up = [(cx + half, y) for y, half in ARM_PROFILE]
    for i in range(len(ARM_PROFILE) - 2, 0, -1):
        up.append((cx - ARM_PROFILE[i][1], ARM_PROFILE[i][0]))
    a = math.radians(degrees)
    c, s = math.cos(a), math.sin(a)
    out = []
    for x, y in up:
        dx = x - cx
        dy = -(ARM_TIP + (cy - y - ARM_TIP) * (reach - ARM_TIP) / (cy - ARM_PROFILE[0][0] - ARM_TIP))
        out.append((cx + dx * c - dy * s, cy + dx * s + dy * c))
    return out


def _cubic(p0, c1, c2, p3, t):
    u = 1 - t
    return (u * u * u * p0[0] + 3 * u * u * t * c1[0] + 3 * u * t * t * c2[0] + t * t * t * p3[0],
            u * u * u * p0[1] + 3 * u * u * t * c1[1] + 3 * u * t * t * c2[1] + t * t * t * p3[1])


def _dist(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _ieee_remainder(x, y):
    q = x / y
    r = round(q)  # Python rounds half to even, like Math.IEEERemainder
    return x - r * y


def pad_and_band():
    """ControllerArt.BuildShapes: (touchpad polygon, light strip polygon, strip centre line of the left half top to middle)."""
    centre, slope, top, corner_start, corner_end_x, bottom, handle_k = LED_CENTRE_X, 0.197, 60, 400, 880, 527, 0.45
    side_w, bottom_w, outward = 21, 26, 3

    def side_offset(y):
        return 456.5 - slope * (y - 148)

    line = []
    for i in range(39):
        y = top + (corner_start - top) * i / 39
        line.append(((centre - side_offset(y), y), 0.0))
    p0 = (centre - side_offset(corner_start), corner_start)
    p3 = (corner_end_x, bottom)
    tl = math.sqrt(slope * slope + 1)
    handle = handle_k * _dist(p0, p3)
    c1 = (p0[0] + slope / tl * handle, p0[1] + 1 / tl * handle)
    c2 = (p3[0] - handle, p3[1])
    for i in range(39):
        t = i / 39.0
        line.append((_cubic(p0, c1, c2, p3, t), t))
    for i in range(20):
        line.append(((corner_end_x + (centre - corner_end_x) * i / 19, bottom), 1.0))

    def offset(sign):
        res = []
        for i in range(len(line)):
            a = line[max(0, i - 1)][0]
            b = line[min(len(line) - 1, i + 1)][0]
            dx, dy = b[0] - a[0], b[1] - a[1]
            ln = math.hypot(dx, dy)
            half = (side_w + (bottom_w - side_w) * line[i][1]) / 2 + (outward if sign > 0 else 0)
            p = line[i][0]
            res.append((p[0] + sign * dy / ln * half, p[1] - sign * dx / ln * half))
        return res

    def mirror(half):
        return half + [(2 * centre - x, y) for x, y in reversed(half)]

    outer, inner, middle = mirror(offset(1)), mirror(offset(-1)), mirror(offset(0))

    def cap(frm, to):
        mid = ((frm[0] + to[0]) / 2, (frm[1] + to[1]) / 2)
        r = _dist(frm, to) / 2
        a0 = math.atan2(frm[1] - mid[1], frm[0] - mid[0])
        a1 = math.atan2(to[1] - mid[1], to[0] - mid[0])
        sweep = _ieee_remainder(a1 - a0, 2 * math.pi)
        if math.sin(a0 + sweep / 2) > 0:
            sweep -= math.copysign(2 * math.pi, sweep)
        return [(mid[0] + r * math.cos(a0 + sweep * i / 12), mid[1] + r * math.sin(a0 + sweep * i / 12)) for i in range(1, 12)]

    band = cap(inner[0], outer[0]) + outer + cap(outer[-1], inner[-1]) + list(reversed(inner))
    corner = middle[0]
    shoulder = (corner[0] + 34, 44)
    top_left = [_cubic(corner, (corner[0] + 4, corner[1] - 14), (corner[0] + 14, 47), shoulder, i / 16.0) for i in range(17)]
    top_left += [_cubic(shoulder, (900, 33), (1000, 29), (centre, 29), i / 16.0) for i in range(1, 17)]
    pad = top_left + [(2 * centre - x, y) for x, y in list(reversed(top_left))[1:]] + list(reversed(middle))
    left_centre_line = [p for p, _ in line]
    return pad, band, left_centre_line


# ---- Drawn here: body silhouette and the white shell ----

def catmull_rom(points, samples_per_segment=12, closed=False):
    """Centripetal-free (uniform) Catmull-Rom through the points."""
    pts = list(points)
    n = len(pts)
    out = []
    rng = range(n) if closed else range(n - 1)
    for i in rng:
        p0 = pts[(i - 1) % n] if closed or i > 0 else pts[0]
        p1 = pts[i]
        p2 = pts[(i + 1) % n]
        p3 = pts[(i + 2) % n] if closed or i + 2 < n else pts[-1]
        for k in range(samples_per_segment):
            t = k / samples_per_segment
            t2, t3 = t * t, t * t * t
            out.append(tuple(0.5 * ((2 * p1[j]) + (-p0[j] + p2[j]) * t + (2 * p0[j] - 5 * p1[j] + 4 * p2[j] - p3[j]) * t2
                                    + (-p0[j] + 3 * p1[j] - 3 * p2[j] + p3[j]) * t3) for j in range(2)))
    if not closed:
        out.append(pts[-1])
    return out


# The left half of the body outline, clockwise on screen from the top centre round the left grip to the bottom centre.
# Hand-placed control points: the top edge behind the touchpad, the bumper line, the outer flank down to the grip, the
# rounded grip end, the inner edge of the grip and the arch between the grips.
BODY_TOP = [(BODY_CX, 36), (1000, 37), (860, 40), (760, 44), (700, 49), (640, 57)]
BODY_FLANK = [(591, 63), (440, 87), (300, 112), (262, 128), (232, 160), (206, 214), (181, 280), (150, 362), (120, 445),
              (90, 540), (60, 648), (35, 760), (17, 860), (8, 950), (5, 1040), (7, 1130), (14, 1220), (28, 1300), (52, 1380),
              (92, 1448), (150, 1497), (215, 1517), (275, 1514), (325, 1490), (358, 1446), (385, 1380), (412, 1310),
              (440, 1238), (468, 1166), (497, 1100), (530, 1048), (575, 1008), (640, 985), (760, 973), (900, 970), (BODY_CX, 970)]

# The white shell (left): its outer edge follows the body, its inner edge runs beside the light strip and then down the
# inside of the grip, leaving a dark strip of the core visible there.
SHELL_INNER = [(745, 425), (722, 470), (695, 520), (650, 588), (600, 660), (552, 740), (512, 820), (485, 900),
               (465, 980), (450, 1060), (432, 1140), (410, 1220), (386, 1300), (360, 1376), (334, 1436), (312, 1476),
               (296, 1502)]


def body_left():
    top = catmull_rom(BODY_TOP, 10)
    flank = catmull_rom(BODY_FLANK, 14)
    return top[:-1] + flank


def body_outline():
    left = body_left()
    right = [(2 * BODY_CX - x, y) for x, y in reversed(left)]
    return left + right[1:-1]


def _resample(points, step):
    out = [points[0]]
    acc = 0.0
    for a, b in zip(points, points[1:]):
        d = _dist(a, b)
        if d == 0:
            continue
        t = step - acc
        while t <= d:
            out.append((a[0] + (b[0] - a[0]) * t / d, a[1] + (b[1] - a[1]) * t / d))
            t += step
        acc = (acc + d) % step
    return out


def shell_span():
    """Indices into body_left() of the stretch of body edge the white shell covers (top inner corner to grip end)."""
    left = body_left()
    start = min(range(len(left)), key=lambda i: _dist(left[i], (640, 57)))
    end = min(range(len(left)), key=lambda i: _dist(left[i], SHELL_INNER[-1]))
    return start, end


def body_loop():
    """The body outline tagged per point: 'hidden' where the shell covers the edge, 'out' where the core shows."""
    left = body_left()
    start, end = shell_span()
    n = len(left)
    loop = [(p, "hidden" if start <= i <= end else "out") for i, p in enumerate(left)]
    for j in range(1, n - 1):
        i = n - 1 - j
        x, y = left[i]
        loop.append(((2 * BODY_CX - x, y), "hidden" if start <= i <= end else "out"))
    return loop


def shell_left():
    """(outline, classes): the left white shell as one closed loop, each point tagged 'outer' (shared with the body edge,
    softly rounded) or 'inner' (the shell's own edge over the core, a short crisp step)."""
    _, _, centre_line = pad_and_band()
    left = body_left()
    # Outer: from the shell's top inner corner along the body edge (bumper line, flank, grip end) to where the inner edge
    # meets it at the grip tip.
    start, end = shell_span()
    outer = left[start:end + 1]
    # Inner: up the grip, up beside the strip (the strip's centre line, so the step hides under the painted strip).
    # (starting on the body edge itself, so the two edges meet in a clean point at the grip end)
    inner_curve = catmull_rom([left[end]] + list(reversed(SHELL_INNER[:-1])), 12)
    strip = [p for p in centre_line if p[1] <= 425]
    strip_up = list(reversed(strip))  # bottom to top
    loop = [(p, "outer") for p in outer]
    loop += [(p, "inner") for p in inner_curve[1:]]
    loop += [(p, "inner") for p in strip_up]
    # Close over the top: from the strip's top end back to the start, still the shell's own edge.
    loop += [((655, 56), "inner")]
    return loop


def smooth_closed(points, step=3.0, iterations=16):
    """A measured point list made smooth: resampled evenly, then relaxed (moves the outline by a pixel or two)."""
    pts = _resample(list(points) + [points[0]], step)[:-1]
    n = len(pts)
    for _ in range(iterations):
        pts = [((pts[i - 1][0] + 2 * pts[i][0] + pts[(i + 1) % n][0]) / 4, (pts[i - 1][1] + 2 * pts[i][1] + pts[(i + 1) % n][1]) / 4)
               for i in range(n)]
    return pts


def dpad_recess(offset=7.0):
    """One hole per arm: the arm grown by offset (convex, so the Minkowski sum is the hull of grown corners)."""
    holes = []
    for deg, reach in DPAD_REACH.items():
        arm = dpad_arm(deg, reach)
        pts = []
        for x, y in arm:
            for k in range(12):
                a = 2 * math.pi * k / 12
                pts.append((x + offset * math.cos(a), y + offset * math.sin(a)))
        holes.append(_convex_hull(pts))
    return holes


def _convex_hull(points):
    pts = sorted(set(points))
    if len(pts) <= 2:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


SPEAKER_HOLES = [(BODY_CX + 26 * (i - 3), 598) for i in range(7)] + [(BODY_CX + 26 * (i - 2.5), 625) for i in range(6)]
SPEAKER_HOLE_R = 6.5


def regions(edge=False):
    """Every overlay region (name, polygon) as ControllerLayout builds it, for the alignment check."""
    out = [(k, circle(c[0], c[1], r, 72)) for k, (c, r) in FACE.items()]
    out += [(f"dpad{d}", dpad_arm(d, r)) for d, r in DPAD_REACH.items()]
    out += [("create", pill(*CREATE)), ("options", pill(*OPTIONS))]
    out += [("L1", BUMPER_FACE), ("R1", mirror_x(BUMPER_FACE)), ("L2", TRIGGER_FACE), ("R2", mirror_x(TRIGGER_FACE))]
    for name, c in STICKS.items():
        out.append((name, circle(c[0], c[1], STICK_WELL_R, 72)))
    out += [("ps", circle(PS[0][0], PS[0][1], PS[1], 72)), ("mute", pill(*MUTE))]
    if edge:
        out += [("fnL", pill(*FN_LEFT)), ("fnR", pill(*FN_RIGHT))]
    return out

# SPDX-License-Identifier: GPL-3.0-or-later
"""Models the controller front view from scratch and renders it (Blender 5.3, Cycles).

    blender -b --factory-startup -P build_controller.py -- --variant standard --out controller.png
    blender -b --factory-startup -P build_controller.py -- --variant edge --out controller-edge.png

Options: --samples N (default 384), --scale F (render size factor, default 1 = 2290 x 1520), --cpu, --blend FILE
(also save the scene).

How it is built: every part is a height field over its 2D outline in the art's pixels (layout.py). The outline is
triangulated (constrained Delaunay, with denser rings near the edges), each vertex is lifted by a profile of its
distance to the outline (a quarter round of a set radius per edge, a gentle dome, the grips falling away) and gets its
normal from the same function analytically, so the shading is smooth whatever the triangles. An orthographic camera
looks straight down, one world unit per 100 px, so the render lines up with the app's overlays pixel for pixel.
The touchpad and the light strip are modelled but the app paints its own strip over them (live colour); no logos.
"""
import math
import os
import sys
import time

import bpy
import numpy as np
from mathutils import Vector, kdtree
from mathutils.geometry import delaunay_2d_cdt

sys.dont_write_bytecode = True  # no __pycache__ next to the script
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import layout as L  # noqa: E402

S = 0.01  # world units per art pixel
SMOOTH_MIN = 12.0  # px: blend width where two edge profiles meet


# ---------------------------------------------------------------- options

def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    opts = {"variant": "standard", "out": None, "samples": 384, "scale": 1.0, "cpu": False, "blend": None, "sprites": None}
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--variant":
            opts["variant"] = argv[i + 1]; i += 2
        elif a == "--out":
            opts["out"] = argv[i + 1]; i += 2
        elif a == "--samples":
            opts["samples"] = int(argv[i + 1]); i += 2
        elif a == "--scale":
            opts["scale"] = float(argv[i + 1]); i += 2
        elif a == "--sprites":
            opts["sprites"] = argv[i + 1]; i += 2
        elif a == "--blend":
            opts["blend"] = argv[i + 1]; i += 2
        elif a == "--cpu":
            opts["cpu"] = True; i += 1
        else:
            raise SystemExit(f"unknown option {a}")
    if opts["variant"] not in ("standard", "edge"):
        raise SystemExit("--variant standard|edge")
    return opts


# ---------------------------------------------------------------- height-field parts

def _profile(t):
    """Quarter round: 0 at the edge, 1 from t = 1 inwards, and its slope."""
    if t >= 1:
        return 1.0, 0.0
    t = max(t, 0.0015)
    u = 1 - t
    root = math.sqrt(max(1 - u * u, 1e-9))
    return root, u / root


def _smooth(t):
    if t >= 1:
        return 1.0, 0.0
    return t * t * (3 - 2 * t), 6 * t * (1 - t)


def _resample_closed(points, classes, step):
    pts, cls = [], []
    n = len(points)
    for i in range(n):
        a, b = points[i], points[(i + 1) % n]
        d = math.hypot(b[0] - a[0], b[1] - a[1])
        k = max(1, int(round(d / step)))
        for j in range(k):
            t = j / k
            pts.append((a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t))
            cls.append(classes[i])
    return pts, cls


def _inside(px, py, loops):
    """Even-odd test of many points against all loops (outer and holes)."""
    res = np.zeros(px.shape, dtype=bool)
    for loop in loops:
        a = np.asarray(loop, dtype=np.float64)
        x1, y1 = a[:, 0], a[:, 1]
        x2, y2 = np.roll(x1, -1), np.roll(y1, -1)
        for s in range(0, px.size, 4096):
            qx, qy = px[s:s + 4096, None], py[s:s + 4096, None]
            cond = (y1 > qy) != (y2 > qy)
            with np.errstate(divide="ignore", invalid="ignore"):
                xi = (x2 - x1) * (qy - y1) / (y2 - y1) + x1
            res[s:s + 4096] ^= (np.count_nonzero(cond & (qx < xi), axis=1) % 2).astype(bool)
    return res


class DomeField:
    """A smooth bump over an outline: the outline's filled mask blurred (Gaussian), so it rises from the edge and levels
    off inside with no creases (a distance-based bump would crease along the middle of every narrow part)."""

    def __init__(self, outline, sigma, res=3.0):
        a = np.asarray(outline, dtype=np.float64)
        pad = 3 * sigma
        self.x0, self.y0 = a[:, 0].min() - pad, a[:, 1].min() - pad
        nx = int((a[:, 0].max() + pad - self.x0) / res) + 1
        ny = int((a[:, 1].max() + pad - self.y0) / res) + 1
        self.res = res
        gx = self.x0 + np.arange(nx) * res
        gy = self.y0 + np.arange(ny) * res
        mx, my = np.meshgrid(gx, gy)
        mask = _inside(mx.ravel(), my.ravel(), [outline]).reshape(ny, nx).astype(np.float64)
        # Gaussian blur through the FFT (the padding keeps it from wrapping round).
        ky = np.fft.fftfreq(ny)[:, None]
        kx = np.fft.fftfreq(nx)[None, :]
        s = sigma / res
        blur = np.real(np.fft.ifft2(np.fft.fft2(mask) * np.exp(-2 * (math.pi * s) ** 2 * (kx ** 2 + ky ** 2))))
        t = np.clip((blur - 0.5) / 0.5, 0.0, 1.0)
        self.f = t * t * (3 - 2 * t)
        self.fv, self.fu = np.gradient(self.f, res)

    def sample(self, u, v):
        x = (u - self.x0) / self.res
        y = (v - self.y0) / self.res
        i, j = int(math.floor(x)), int(math.floor(y))
        ny, nx = self.f.shape
        if i < 0 or j < 0 or i >= nx - 1 or j >= ny - 1:
            return 0.0, 0.0, 0.0
        fx, fy = x - i, y - j

        def bil(g):
            return (g[j, i] * (1 - fx) * (1 - fy) + g[j, i + 1] * fx * (1 - fy) + g[j + 1, i] * (1 - fx) * fy
                    + g[j + 1, i + 1] * fx * fy)
        return bil(self.f), bil(self.fu), bil(self.fv)


class Part:
    """A height field over a 2D outline with holes.

    loops: [outer, hole, hole, ...], each a list of (point, class name).
    classes: name -> ("edge", radius, base, top): within radius of that edge the surface rounds down from top to base;
             the surface is the lowest of all edge classes.
             name -> ("lip", radius, depth): a rounded drop of depth into a hole (subtracted).
    dome: (height, reach): a smooth bump over the outer outline, levelling off about reach in from the edge.
    extra: f(u, v) -> z added everywhere (e.g. the surface the part sits on, the grips falling away).
    """

    def __init__(self, name, loops, classes, material, dome=(0.0, 1.0), extra=None, grid=7.0, rings=None, z_shift=0.0):
        self.name, self.classes, self.material = name, classes, material
        self.dome_h, self.dome_l = dome
        self.dome_field = DomeField([p for p, _ in loops[0]], self.dome_l / 3) if self.dome_h else None
        self.extra = extra
        self.grid = grid
        self.z_shift = z_shift
        self.rings = rings or [0.45, 1.2, 2.4, 4.0, 6.2, 9.0, 12.5, 17.0]
        self.loops_dense = []   # (points, classes, normals) at 1 px
        dense_all, cls_all, nrm_all = [], [], []
        coarse = []
        for loop in loops:
            pts = [p for p, _ in loop]
            cls = [c for _, c in loop]
            dp, dc = _resample_closed(pts, cls, 1.0)
            coarse.append(dp[::3])
            self.loops_dense.append([dp, dc])
        self.coarse = coarse
        # Inward normals: the side where the part is, decided per loop by majority.
        for k, (dp, dc) in enumerate(self.loops_dense):
            n = len(dp)
            nr = []
            for i in range(n):
                a, b = dp[(i - 1) % n], dp[(i + 1) % n]
                tx, ty = b[0] - a[0], b[1] - a[1]
                ln = math.hypot(tx, ty) or 1.0
                nr.append((-ty / ln, tx / ln))
            idx = list(range(0, n, max(1, n // 41)))
            qx = np.array([dp[i][0] + 1.5 * nr[i][0] for i in idx])
            qy = np.array([dp[i][1] + 1.5 * nr[i][1] for i in idx])
            if np.count_nonzero(_inside(qx, qy, coarse)) * 2 < len(idx):
                nr = [(-x, -y) for x, y in nr]
            self.loops_dense[k].append(nr)
            dense_all += dp
            cls_all += dc
            nrm_all += nr
        self.dense, self.dense_cls, self.dense_nrm = dense_all, cls_all, nrm_all
        self.trees = {}
        for c in classes:
            ids = [i for i, cc in enumerate(cls_all) if cc == c]
            if not ids:
                continue
            t = kdtree.KDTree(len(ids))
            for i in ids:
                t.insert((dense_all[i][0], dense_all[i][1], 0.0), i)
            t.balance()
            self.trees[c] = t
        self.all_tree = kdtree.KDTree(len(dense_all))
        for i, p in enumerate(dense_all):
            self.all_tree.insert((p[0], p[1], 0.0), i)
        self.all_tree.balance()

    def _dist(self, tree, u, v):
        co, i, d = tree.find((u, v, 0.0))
        if d > 1.0:
            gx, gy = (u - co[0]) / d, (v - co[1]) / d
        else:
            gx, gy = self.dense_nrm[i]
        return d, gx, gy

    def height(self, u, v, grad=False):
        z_edge, gu_e, gv_e = None, 0.0, 0.0
        z_lip, gu_l, gv_l = 0.0, 0.0, 0.0
        for c, tree in self.trees.items():
            spec = self.classes[c]
            d, gx, gy = self._dist(tree, u, v)
            if spec[0] == "edge":
                _, r, base, top = spec
                g, dg = _profile(d / r)
                z = base + (top - base) * g
                k = (top - base) * dg / r
                if z_edge is None:
                    z_edge, gu_e, gv_e = z, k * gx, k * gy
                else:
                    # Smooth minimum, so where two edges' roundings meet there is no crease.
                    h = min(max(0.5 + 0.5 * (z_edge - z) / SMOOTH_MIN, 0.0), 1.0)
                    z_edge = z_edge + (z - z_edge) * h - SMOOTH_MIN * h * (1 - h)
                    gu_e = gu_e + (k * gx - gu_e) * h
                    gv_e = gv_e + (k * gy - gv_e) * h
            else:
                _, r, depth = spec
                g, dg = _profile(d / r)
                z_lip -= depth * (1 - g)
                k = depth * dg / r
                gu_l += k * gx
                gv_l += k * gy
        z = (z_edge or 0.0) + z_lip
        gu, gv = gu_e + gu_l, gv_e + gv_l
        if self.dome_field:
            f, fu, fv = self.dome_field.sample(u, v)
            z += self.dome_h * f
            gu += self.dome_h * fu
            gv += self.dome_h * fv
        if self.extra:
            z += self.extra(u, v)
            if grad:
                e = 0.5
                gu += (self.extra(u + e, v) - self.extra(u - e, v)) / (2 * e)
                gv += (self.extra(u, v + e) - self.extra(u, v - e)) / (2 * e)
        z += self.z_shift
        return (z, gu, gv) if grad else z

    def points(self):
        """Mesh vertices: the outline every 2 px, rings just inside it, a grid further in."""
        pts, loops_idx = [], []
        for dp, dc, nr in self.loops_dense:
            idx = []
            for i in range(0, len(dp), 2):
                idx.append(len(pts))
                pts.append(dp[i])
            loops_idx.append(idx)
        ring = []
        for dp, dc, nr in self.loops_dense:
            for k, o in enumerate(self.rings):
                every = 2 if o < 5 else (4 if o < 10 else 6)
                for i in range(0, len(dp), every):
                    ring.append((dp[i][0] + nr[i][0] * o, dp[i][1] + nr[i][1] * o, o))
        if ring:
            r = np.array(ring)
            keep = _inside(r[:, 0], r[:, 1], self.coarse)
            for (x, y, o), ok in zip(ring, keep):
                if ok and self.all_tree.find((x, y, 0.0))[2] >= 0.6 * o:
                    pts.append((x, y))
        xs = [p[0] for p in self.dense]
        ys = [p[1] for p in self.dense]
        gx = np.arange(min(xs) + self.grid / 2, max(xs), self.grid)
        gy = np.arange(min(ys) + self.grid / 2, max(ys), self.grid)
        mx, my = np.meshgrid(gx, gy)
        mx, my = mx.ravel(), my.ravel()
        keep = _inside(mx, my, self.coarse)
        limit = self.rings[-1] + self.grid * 0.6
        for x, y in zip(mx[keep], my[keep]):
            if self.all_tree.find((x, y, 0.0))[2] >= limit:
                pts.append((x, y))
        return pts, loops_idx

    def build(self, collection):
        pts, loops_idx = self.points()
        outer = loops_idx[0]
        # CDT wants the outer face counter-clockwise (in its own x/y; ours is y-down, so check the signed area).
        area = sum(pts[outer[i]][0] * pts[outer[(i + 1) % len(outer)]][1] - pts[outer[(i + 1) % len(outer)]][0] * pts[outer[i]][1]
                   for i in range(len(outer)))
        if area < 0:
            outer = list(reversed(outer))
        edges = []
        for idx in loops_idx[1:]:
            edges += [(idx[i], idx[(i + 1) % len(idx)]) for i in range(len(idx))]
        verts2, _, faces, _, _, _ = delaunay_2d_cdt([Vector(p) for p in pts], edges, [outer], 1, 1e-4, False)
        # Drop triangles in holes.
        cx = np.array([sum(verts2[i][0] for i in f) / len(f) for f in faces])
        cy = np.array([sum(verts2[i][1] for i in f) / len(f) for f in faces])
        keep = _inside(cx, cy, self.coarse)
        # Counter-clockwise in y-down pixels is clockwise seen from above: flip so the faces look up at the camera.
        faces = [list(reversed(f)) for f, k in zip(faces, keep) if k]
        coords, normals = [], []
        for p in verts2:
            u, v = p[0], p[1]
            z, gu, gv = self.height(u, v, grad=True)
            coords.append(((u - L.ART_W / 2) * S, (L.ART_H / 2 - v) * S, z * S))
            n = Vector((-gu, gv, 1.0)).normalized()  # world Y = -v
            normals.append(n)
        mesh = bpy.data.meshes.new(self.name)
        mesh.from_pydata(coords, [], faces)
        mesh.update()
        mesh.shade_smooth()
        mesh.normals_split_custom_set_from_vertices(normals)
        mesh.materials.append(self.material)
        obj = bpy.data.objects.new(self.name, mesh)
        # Smooth normals on a faceted surface: keep shadow rays clear of the flat triangles.
        obj.shadow_terminator_geometry_offset = 1.0
        collection.objects.link(obj)
        return obj


def one(points, cls):
    return [(p, cls) for p in points]


def mirrored_loop(loop, axis=L.BODY_CX):
    return [((2 * axis - p[0], p[1]), c) for p, c in reversed(loop)]


def centroid(points):
    return (sum(p[0] for p in points) / len(points), sum(p[1] for p in points) / len(points))


# ---------------------------------------------------------------- materials

def srgb(hexstr):
    h = hexstr.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return tuple(x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c) + (1.0,)


def material(name, color, rough, coat=0.0, spec=0.5, subsurface=0.0, dots=0.0):
    """Principled plastic. dots > 0 adds a fine raised dot pattern (bump), as on the Edge's touchpad."""
    m = bpy.data.materials.new(name)
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = srgb(color)
    bsdf.inputs["Roughness"].default_value = rough
    bsdf.inputs["Specular IOR Level"].default_value = spec
    bsdf.inputs["Coat Weight"].default_value = coat
    bsdf.inputs["Coat Roughness"].default_value = 0.12
    bsdf.inputs["Subsurface Weight"].default_value = subsurface
    bsdf.inputs["Subsurface Radius"].default_value = (0.02, 0.02, 0.02)
    if dots:
        coord = nt.nodes.new("ShaderNodeTexCoord")
        vor = nt.nodes.new("ShaderNodeTexVoronoi")
        vor.feature = "F1"
        vor.inputs["Scale"].default_value = 100 / 13.0  # a dot every ~13 px
        vor.inputs["Randomness"].default_value = 0.0
        ramp = nt.nodes.new("ShaderNodeMapRange")
        ramp.inputs["From Min"].default_value = 0.22
        ramp.inputs["From Max"].default_value = 0.34
        ramp.inputs["To Min"].default_value = 1.0
        ramp.inputs["To Max"].default_value = 0.0
        bump = nt.nodes.new("ShaderNodeBump")
        bump.inputs["Strength"].default_value = dots
        bump.inputs["Distance"].default_value = 0.01
        nt.links.new(coord.outputs["Object"], vor.inputs["Vector"])
        nt.links.new(vor.outputs["Distance"], ramp.inputs["Value"])
        nt.links.new(ramp.outputs["Result"], bump.inputs["Height"])
        nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


# name -> (colour, roughness, coat, subsurface, dots)
PALETTES = {
    # White wings, matte black centre, white touchpad, translucent grey face buttons and d-pad.
    "standard": {
        "shell": ("#F1F2F2", 0.36, 0.0, 0.0, 0), "core": ("#0E0F11", 0.42, 0.0, 0.0, 0),
        "pad": ("#EDEEEF", 0.5, 0.0, 0.0, 0), "face": ("#A3A8B0", 0.18, 0.6, 0.15, 0),
        "dpad": ("#B9BDC4", 0.2, 0.5, 0.15, 0), "pill": ("#EEEFF0", 0.3, 0.2, 0.0, 0),
        "mark": ("#7C828C", 0.45, 0.0, 0.0, 0), "bumper": ("#16171A", 0.42, 0.2, 0.0, 0),
        "trigger": ("#1E2023", 0.48, 0.0, 0.0, 0), "gloss": ("#0C0C0E", 0.12, 0.6, 0.0, 0),
        "mute": ("#B5B8BE", 0.2, 0.5, 0.2, 0), "floor": ("#050506", 0.9, 0.0, 0.0, 0),
        "boot": ("#1D1E21", 0.42, 0.0, 0.0, 0), "cap": ("#1C1D1F", 0.68, 0.0, 0.0, 0),
        "rim": ("#3A3B3F", 0.62, 0.0, 0.0, 0), "strip": ("#C9CCD2", 0.3, 0.0, 0.5, 0),
    },
    # The Edge: the same white wings, a glossy black centre, a black dotted touchpad, black buttons and sticks.
    "edge": {
        "shell": ("#F1F2F2", 0.36, 0.0, 0.0, 0), "core": ("#0B0B0C", 0.14, 0.5, 0.0, 0),
        "pad": ("#08090A", 0.5, 0.0, 0.0, 0.35), "face": ("#0A0A0B", 0.22, 0.25, 0.0, 0),
        "dpad": ("#0A0A0B", 0.22, 0.25, 0.0, 0), "pill": ("#EEEFF0", 0.3, 0.2, 0.0, 0),
        "mark": ("#8A9099", 0.45, 0.0, 0.0, 0), "bumper": ("#141517", 0.3, 0.3, 0.0, 0),
        "trigger": ("#1B1C1F", 0.4, 0.0, 0.0, 0), "gloss": ("#0C0C0E", 0.12, 0.6, 0.0, 0),
        "mute": ("#B5B8BE", 0.2, 0.5, 0.2, 0), "floor": ("#050506", 0.9, 0.0, 0.0, 0),
        "boot": ("#1A1B1D", 0.4, 0.0, 0.0, 0), "cap": ("#18191B", 0.66, 0.0, 0.0, 0),
        "rim": ("#34353A", 0.6, 0.0, 0.0, 0), "strip": ("#C9CCD2", 0.3, 0.0, 0.5, 0),
    },
}


# ---------------------------------------------------------------- the controller

def grip_fall(u, v):
    """The grips fall away from the viewer towards their ends."""
    t = min(max((v - 620) / 900, 0.0), 1.0)
    return -190 * t * t


def build(variant, col):
    """Builds every part; returns the stick cap objects (the moved-stick sprites need them apart)."""
    pal = PALETTES[variant]
    mats = {k: material(f"{variant}-{k}", c, r, coat=co, subsurface=ss, dots=dt) for k, (c, r, co, ss, dt) in pal.items()}
    edge = variant == "edge"
    parts, caps = [], []
    mx, my, mw, ml, md = L.MUTE
    (pcx, pcy), pr = L.PS

    # Core: the whole body, dark; holes for the sticks, PS, mute, speaker (and the Edge's Fn buttons). Where the shell
    # covers its edge it rounds off gently (hidden anyway); where it shows (between the grips, along their inner edge,
    # behind the touchpad) it has a tighter round.
    core_loops = [L.body_loop()]
    for c in L.STICKS.values():
        core_loops.append(one(L.circle(c[0], c[1], L.STICK_OPENING_R, 180), "well"))
    core_loops.append(one(L.circle(pcx, pcy, pr + 5, 96), "small"))
    core_loops.append(one(L.pill(mx, my, mw + 4, ml + 4, md), "small"))
    for x, y in L.SPEAKER_HOLES:
        core_loops.append(one(L.circle(x, y, L.SPEAKER_HOLE_R, 24), "speaker"))
    if edge:
        for fx, fy, fw, fl, fd in (L.FN_LEFT, L.FN_RIGHT):
            core_loops.append(one(L.pill(fx, fy, fw + 4, fl + 4, fd), "small"))
    core = Part("core", core_loops, {
        "hidden": ("edge", 120, 0, 100),
        "out": ("edge", 46, 0, 100),
        "well": ("lip", 10, 14),
        "small": ("lip", 5, 20),
        "speaker": ("lip", 2.5, 24),
    }, mats["core"], dome=(12, 220), extra=grip_fall)
    parts.append(core)

    # Floor: seen through the holes.
    parts.append(Part("floor", [one(L.body_outline(), "out")], {"out": ("edge", 2, -8, -6)}, mats["floor"], extra=grip_fall,
                      grid=24, rings=[1.0]))

    # Shells, left and right (mirror), with holes for the buttons: a thin gap around each button, no more.
    shell_l = L.shell_left()
    left_holes = [one(h, "btn") for h in L.dpad_recess(2.5)]
    cx_, cy_, w_, l_, d_ = L.CREATE
    left_holes.append(one(L.pill(cx_, cy_, w_ - 1, l_ - 1, d_), "btn"))
    right_holes = [one(L.circle(c[0], c[1], r - 1, 140), "btn") for c, r in L.FACE.values()]
    ox, oy, ow, ol, od = L.OPTIONS
    right_holes.append(one(L.pill(ox, oy, ow - 1, ol - 1, od), "btn"))
    shell_classes = {
        "outer": ("edge", 105, 0, 140),
        "inner": ("edge", 13, 92, 140),
        "btn": ("lip", 4, 16),
    }
    shell_left_part = Part("shell-left", [shell_l] + left_holes, shell_classes, mats["shell"], dome=(36, 300), extra=grip_fall,
                           grid=4.5)
    shell_right_part = Part("shell-right", [mirrored_loop(shell_l)] + right_holes, shell_classes, mats["shell"], dome=(36, 300),
                            extra=grip_fall, grid=4.5)
    parts += [shell_left_part, shell_right_part]

    def on(host, lift):
        return lambda u, v: host.height(u, v) + lift

    # Face buttons: plain, slightly proud of the shell.
    for name, (c, r) in L.FACE.items():
        top = shell_right_part.height(c[0], c[1])
        parts.append(Part(f"face-{name}", [one(L.circle(c[0], c[1], r - 3.5, 140), "e")], {"e": ("edge", 13, -24, 0)},
                          mats["face"], dome=(4, 40), extra=lambda u, v, t=top: t + 5, grid=4))
    # D-pad arms with a small engraved arrow each.
    cxd, cyd = L.DPAD_CENTRE
    for deg, reach in L.DPAD_REACH.items():
        arm = L.dpad_arm(deg, reach)
        ac = centroid(arm)
        top = shell_left_part.height(ac[0], ac[1])
        arm_part = Part(f"dpad-{deg}", [one(arm, "e")], {"e": ("edge", 12, -24, 0)}, mats["dpad"], dome=(3, 40),
                        extra=lambda u, v, t=top: t + 4, grid=4)
        parts.append(arm_part)
        a = math.radians(deg)
        dist, size = 139, 13
        ux, uy = math.sin(a), -math.cos(a)        # outwards from the centre
        px, py = cxd + ux * dist, cyd + uy * dist
        tri = [(px + ux * size, py + uy * size),
               (px - ux * size * 0.6 - uy * size, py - uy * size * 0.6 + ux * size),
               (px - ux * size * 0.6 + uy * size, py - uy * size * 0.6 - ux * size)]
        parts.append(Part(f"dpad-mark-{deg}", [one(_rounded_poly(tri, 2.0), "e")], {"e": ("edge", 1.5, -0.6, 0.5)},
                          mats["mark"], extra=on(arm_part, 0.0), grid=3, rings=[0.4, 1.0]))
    # Create / Options: slim white pills and their marks.
    for (x, y, w, l, d), host in ((L.CREATE, shell_left_part), (L.OPTIONS, shell_right_part)):
        top = host.height(x, y)
        parts.append(Part(f"pill-{x}", [one(L.pill(x, y, w - 3.5, l - 3.5, d), "e")], {"e": ("edge", 10, -20, 0)}, mats["pill"],
                          extra=lambda u, v, t=top: t + 3, grid=3))
    for i, mark in enumerate(_create_mark() + _options_mark()):
        host = shell_left_part if i < 3 else shell_right_part
        parts.append(Part(f"mark-{i}", [one(mark, "e")], {"e": ("edge", 1.4, -0.8, 0.4)}, mats["mark"],
                          extra=on(host, 0.0), grid=3, rings=[0.4, 1.0]))

    # Bumpers and triggers (behind the shell's top edge).
    # The overlay's polygons are measured point lists; a closed spline through them gives smooth edges to round off.
    bumper = L.smooth_closed(L.BUMPER_FACE)
    trigger = L.smooth_closed(L.TRIGGER_FACE)
    parts.append(Part("L1", [one(bumper, "e")], {"e": ("edge", 11, 0, 66)}, mats["bumper"], grid=4))
    parts.append(Part("R1", [one(L.mirror_x(bumper), "e")], {"e": ("edge", 11, 0, 66)}, mats["bumper"], grid=4))
    parts.append(Part("L2", [one(trigger, "e")], {"e": ("edge", 10, 0, 52)}, mats["trigger"], grid=4))
    parts.append(Part("R2", [one(L.mirror_x(trigger), "e")], {"e": ("edge", 10, 0, 52)}, mats["trigger"], grid=4))

    # Touchpad (the app paints the light strip over its sides and bottom): a separate plate with its own rounded edge,
    # a little below the shell's top.
    pad, _, _ = L.pad_and_band()
    parts.append(Part("touchpad", [one(pad, "e")], {"e": ("edge", 9, 104, 130)}, mats["pad"], dome=(5, 260), grid=6))

    # Sticks: a shallow well, the rubber boot round the shaft, and a large cap standing well above the body: a smooth
    # dished centre and a raised rim with fine grip bumps (two parts, two materials, one surface).
    cap_r = L.STICK_CAP_R
    dish_r = cap_r - 34
    for name, c in L.STICKS.items():
        parts.append(Part(f"well-{name}", [one(L.circle(c[0], c[1], L.STICK_OPENING_R + 4, 180), "e")],
                          {"e": ("edge", 4, 38, 40)}, mats["floor"], grid=10))
        parts.append(Part(f"boot-{name}", [one(L.circle(c[0], c[1], L.STICK_OPENING_R - 8, 180), "e")],
                          {"e": ("edge", 80, 40, 140)}, mats["boot"], grid=5))

        def cap_height(u, v, c=c):
            r = math.hypot(u - c[0], v - c[1])
            g, _ = _profile((cap_r - r) / 26)                 # the rounded outer edge
            z = 160 + 84 * g
            s_ = min(r / (dish_r + 6), 1.0)
            z -= 40 * (1 - s_) ** 2 * (1 + 2 * s_)            # the dish: flat in the middle, smooth into the rim
            band = (r - dish_r) / (cap_r - 6 - dish_r)
            if 0 < band < 1:                                  # grip bumps on the rim
                theta = math.atan2(v - c[1], u - c[0])
                z += 0.7 * math.sin(math.pi * band) ** 2 * math.cos(84 * theta) * math.cos(2 * math.pi * (r - dish_r) / 9)
            return z
        flat = {"e": ("edge", 0.3, 0, 0)}
        # (the dish reaches a little under the rim and sits a hair lower, so the two never leave a gap between them)
        dish = Part(f"cap-dish-{name}", [one(L.circle(c[0], c[1], dish_r + 3, 200), "e")], flat, mats["cap"],
                    extra=lambda u, v, f=cap_height: f(u, v) - 0.4, grid=3, rings=[0.4])
        rim = Part(f"cap-rim-{name}", [one(L.circle(c[0], c[1], cap_r, 240), "e"), one(L.circle(c[0], c[1], dish_r, 200), "e")],
                   flat, mats["rim"], extra=cap_height, grid=2, rings=[0.4, 1.2, 2.5, 4.5, 7, 10])
        caps += [dish, rim]
        parts += [dish, rim]

    # The light-strip channels round the touchpad (the app paints the live colour over them).
    _, band, _ = L.pad_and_band()
    parts.append(Part("light-strip", [one(band, "e")], {"e": ("edge", 6, 100, 128)}, mats["strip"], grid=4))

    # PS (round, glossy), mute (translucent), and the Edge's Fn buttons (ridged).
    parts.append(Part("ps", [one(L.circle(pcx, pcy, pr, 120), "e")], {"e": ("edge", 22, 72, 114)}, mats["gloss"],
                      dome=(4, 40), extra=lambda u, v: grip_fall(u, v), grid=3))
    parts.append(Part("mute", [one(L.pill(mx, my, mw, ml, md), "e")], {"e": ("edge", 9, 74, 102)}, mats["mute"],
                      extra=lambda u, v: grip_fall(u, v), grid=3))
    if edge:
        for fx, fy, fw, fl, fd in (L.FN_LEFT, L.FN_RIGHT):
            parts.append(Part("fn", [one(L.pill(fx, fy, fw, fl, fd), "e")], {"e": ("edge", 8, 66, 96)}, mats["gloss"],
                              extra=lambda u, v, fx=fx: grip_fall(u, v) + 1.6 * math.cos(2 * math.pi * (u - fx) / 11),
                              grid=2.5))

    objects = {}
    for p in parts:
        objects[p.name] = p.build(col)
    return [objects[c.name] for c in caps]


def _rounded_poly(points, r):
    """A polygon with its corners rounded by r (enough for small marks)."""
    out = []
    n = len(points)
    for i in range(n):
        p0, p1, p2 = points[i - 1], points[i], points[(i + 1) % n]
        a = (p0[0] - p1[0], p0[1] - p1[1])
        b = (p2[0] - p1[0], p2[1] - p1[1])
        la, lb = math.hypot(*a), math.hypot(*b)
        s = min(r, la / 2, lb / 2)
        q0 = (p1[0] + a[0] / la * s, p1[1] + a[1] / la * s)
        q2 = (p1[0] + b[0] / lb * s, p1[1] + b[1] / lb * s)
        for k in range(5):
            t = k / 4
            out.append(((1 - t) ** 2 * q0[0] + 2 * (1 - t) * t * p1[0] + t * t * q2[0],
                        (1 - t) ** 2 * q0[1] + 2 * (1 - t) * t * p1[1] + t * t * q2[1]))
    return out


def _bar(cx, cy, length, width, degrees):
    return L.pill(cx, cy, width / 2, length / 2, degrees, 8)


def _create_mark():
    """Three short rays above the Create button (tilted with it)."""
    x, y, _, l, d = L.CREATE
    t = math.radians(d)
    base = (x + math.sin(t) * (l + 14), y - math.cos(t) * (l + 14))
    out = []
    for k in (-1, 0, 1):
        a = t + math.radians(38 * k)
        r0, r1 = 7, 21
        mx_ = base[0] + math.sin(a) * (r0 + r1) / 2
        my_ = base[1] - math.cos(a) * (r0 + r1) / 2
        out.append(_bar(mx_, my_, r1 - r0, 4.2, math.degrees(a)))
    return out


def _options_mark():
    """Three short lines above the Options button."""
    x, y, _, l, d = L.OPTIONS
    t = math.radians(d)
    base = (x + math.sin(t) * (l + 22), y - math.cos(t) * (l + 22))
    out = []
    for k in (-1, 0, 1):
        cx = base[0] + math.sin(t) * (k * 8.5)
        cy = base[1] - math.cos(t) * (k * 8.5)
        out.append(_bar(cx, cy, 25, 4.2, d + 90))
    return out


# ---------------------------------------------------------------- scene

def setup_scene(opts):
    scene = bpy.context.scene
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    scene.render.engine = "CYCLES"
    cyc = scene.cycles
    if not opts["cpu"]:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
            try:
                prefs.compute_device_type = kind
            except TypeError:
                continue
            prefs.get_devices()
            gpus = [d for d in prefs.devices if d.type == kind]
            if gpus:
                for d in prefs.devices:
                    d.use = d.type == kind
                cyc.device = "GPU"
                print(f"render device: {kind} {[d.name for d in gpus]}")
                break
    cyc.samples = opts["samples"]
    cyc.use_denoising = os.environ.get("ART_DENOISE", "1") == "1"
    cyc.use_adaptive_sampling = True
    if os.environ.get("ART_BORDER"):  # debugging: render a region only, "xmin,ymin,xmax,ymax" in 0..1 (y up)
        b = [float(x) for x in os.environ["ART_BORDER"].split(",")]
        scene.render.use_border = True
        scene.render.use_crop_to_border = True
        scene.render.border_min_x, scene.render.border_min_y, scene.render.border_max_x, scene.render.border_max_y = b
    cyc.max_bounces = 8
    cyc.seed = 7
    r = scene.render
    r.resolution_x = round(L.ART_W * opts["scale"])
    r.resolution_y = round(L.ART_H * opts["scale"])
    r.resolution_percentage = 100
    r.film_transparent = True
    r.image_settings.file_format = "PNG"
    r.image_settings.color_mode = "RGBA"
    r.image_settings.color_depth = "8"
    r.image_settings.compression = 90
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Base Contrast"
    scene.view_settings.exposure = float(os.environ.get("ART_EXPOSURE", "0.5"))

    cam_data = bpy.data.cameras.new("camera")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = L.ART_W * S
    cam_data.sensor_fit = "HORIZONTAL"
    cam_data.clip_start = 1
    cam_data.clip_end = 100
    cam = bpy.data.objects.new("camera", cam_data)
    cam.location = (0, 0, 50)
    scene.collection.objects.link(cam)
    scene.camera = cam

    world = bpy.data.worlds.new("studio")
    scene.world = world
    bg = world.node_tree.nodes["Background"]
    bg.inputs["Color"].default_value = (1, 1, 1, 1)
    bg.inputs["Strength"].default_value = float(os.environ.get("ART_WORLD", "0.2"))

    def light(name, kind, direction, energy, **kw):
        data = bpy.data.lights.new(name, kind)
        data.energy = energy
        for k, v in kw.items():
            setattr(data, k, v)
        obj = bpy.data.objects.new(name, data)
        d = Vector(direction).normalized()
        obj.location = d * 40
        obj.rotation_euler = d.to_track_quat("Z", "Y").to_euler()
        scene.collection.objects.link(obj)
        return obj

    # Key: a soft sun from the top left, high enough that shadows stay short; a grazing light from the top for a
    # highlight along the upper edges; a broad softbox above; a faint fill from the lower right.
    light("key", "SUN", (-0.3, 0.5, 0.81), float(os.environ.get("ART_KEY", "3.0")), angle=math.radians(20))
    light("rim", "SUN", (0.05, 0.95, 0.3), float(os.environ.get("ART_RIM", "1.1")), angle=math.radians(8))
    light("fill", "SUN", (0.5, -0.45, 0.74), 0.5, angle=math.radians(40))
    soft = light("softbox", "AREA", (-0.1, 0.25, 0.96), 4500.0, shape="RECTANGLE", size=34.0, size_y=16.0)
    soft.data.use_shadow = True
    return scene


def _read(path):
    """A rendered PNG as a float array (rows top down, values as stored: 0..1 sRGB, straight alpha)."""
    img = bpy.data.images.load(path)
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)[::-1]


def _write(path, rgba):
    h, w, _ = rgba.shape
    out = bpy.data.images.new(os.path.basename(path), w, h, alpha=True)
    out.pixels.foreach_set(np.ascontiguousarray(rgba[::-1], dtype=np.float32).ravel())
    out.filepath_raw = path
    out.file_format = "PNG"
    out.save()
    bpy.data.images.remove(out)


def _box_blur(a, k):
    """Mean over a (2k+1)^2 box, edges extended."""
    pad = np.pad(a, k, mode="edge")
    cs = np.pad(pad.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
    n = 2 * k + 1
    return (cs[n:, n:] - cs[:-n, n:] - cs[n:, :-n] + cs[:-n, :-n]) / n ** 2


def _write_mask(path, alpha):
    rgba = np.ones(alpha.shape + (4,), dtype=np.float32)
    rgba[..., 3] = np.clip(alpha, 0, 1)
    _write(path, rgba)


def render_light_masks(scene, prefix, folder):
    """Masks the app paints the live light colour through (all cut to L.LIGHT_RECT):
    {prefix}-light-strip.png  the diffuser channel itself (anti-aliased coverage),
    {prefix}-light-core.png   its bright middle,
    {prefix}-light-glow.png   the light it throws on the shell and touchpad around it: a render with the strip as the
                              only light source (white emission, no lamps, no world), outside the strip itself."""
    strip = scene.objects["light-strip"]
    lamps = [o for o in scene.objects if o.type == "LIGHT"]
    others = [o for o in scene.objects if o.type == "MESH" and o != strip]
    strength = scene.world.node_tree.nodes["Background"].inputs["Strength"]
    keep = (strength.default_value, scene.view_settings.view_transform, scene.view_settings.look, scene.view_settings.exposure,
            strip.data.materials[0], scene.cycles.samples)
    emit = bpy.data.materials.new("strip-emission")
    bsdf = emit.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
    bsdf.inputs["Emission Color"].default_value = (1, 1, 1, 1)
    bsdf.inputs["Emission Strength"].default_value = float(os.environ.get("ART_STRIP_EMISSION", "10"))
    strength.default_value = 0
    scene.view_settings.view_transform, scene.view_settings.look, scene.view_settings.exposure = "Standard", "None", 0
    for o in lamps:
        o.hide_render = True
    strip.data.materials[0] = emit
    spill_png = os.path.join(folder, f"{prefix}-spill.tmp.png")
    scene.render.filepath = spill_png
    bpy.ops.render.render(write_still=True)
    for o in others:  # the strip alone, for its coverage
        o.visible_camera = False
    cover_png = os.path.join(folder, f"{prefix}-stripcover.tmp.png")
    scene.cycles.samples = 32
    scene.render.filepath = cover_png
    bpy.ops.render.render(write_still=True)
    for o in others:
        o.visible_camera = True
    for o in lamps:
        o.hide_render = False
    (strength.default_value, scene.view_settings.view_transform, scene.view_settings.look, scene.view_settings.exposure,
     strip.data.materials[0], scene.cycles.samples) = keep
    x, y, w, h = L.LIGHT_RECT
    box = (slice(y, y + h), slice(x, x + w))
    spill, cover = _read(spill_png)[box], _read(cover_png)[box][..., 3]
    os.remove(spill_png)
    os.remove(cover_png)
    lum = (spill[..., :3] * np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)).sum(-1) * spill[..., 3]
    # The spill near the strip, cleaned of render noise and faded out within ~50 px (further off it is only a faint
    # general brightening, which as a mask would show the rectangle's edge), then lifted a little so it carries.
    glow = _box_blur(np.clip((lum - 0.015) / 0.985, 0, 1), 3)
    window = np.clip(_box_blur(cover, 45) * 6, 0, 1)
    glow = (glow * window) ** 0.7 * (1 - cover)
    core = np.clip((_box_blur(cover, 5) - 0.86) / 0.12, 0, 1) * cover
    _write_mask(os.path.join(folder, f"{prefix}-light-strip.png"), cover)
    _write_mask(os.path.join(folder, f"{prefix}-light-core.png"), core)
    _write_mask(os.path.join(folder, f"{prefix}-light-glow.png"), glow)


def render_sprites(scene, caps, prefix, folder, main_png):
    """The moved stick in the app: the stick's surroundings with no cap (drawn over the art's stick at rest) and the cap
    alone with the shadow it casts (drawn where the stick is pushed). The cap and its shadow come from the art itself:
    the cap's pixels where it covers, and outside it the darkening it causes (art against the no-cap render), so the
    two sprites put back together give the art again."""
    others = [o for o in scene.objects if o.type == "MESH" and o not in caps]
    for o in caps:  # no caps: they neither show nor cast shadows
        o.visible_camera = o.visible_shadow = o.visible_diffuse = o.visible_glossy = False
    nocap_png = os.path.join(folder, f"{prefix}-nocap.tmp.png")
    scene.render.filepath = nocap_png
    bpy.ops.render.render(write_still=True)
    for o in caps:  # caps only, for their coverage
        o.visible_camera = o.visible_shadow = o.visible_diffuse = o.visible_glossy = True
    for o in others:
        o.visible_camera = False
    cover_png = os.path.join(folder, f"{prefix}-cover.tmp.png")
    samples = scene.cycles.samples
    scene.cycles.samples = 32
    scene.render.filepath = cover_png
    bpy.ops.render.render(write_still=True)
    scene.cycles.samples = samples
    for o in others:
        o.visible_camera = True
    art, nocap, cover = _read(main_png), _read(nocap_png), _read(cover_png)
    os.remove(nocap_png)
    os.remove(cover_png)
    half = L.SPRITE_HALF
    yy, xx = np.mgrid[-half:half, -half:half]
    r = np.hypot(xx + 0.5, yy + 0.5)
    for name, (cx, cy) in L.STICKS.items():
        box = (slice(cy - half, cy + half), slice(cx - half, cx + half))
        # Round, with a soft edge well inside the light strip (the app paints the strip under the overlay).
        well = nocap[box].copy()
        well[..., 3] *= np.clip((L.WELL_SPRITE_R - r) / 10, 0, 1)
        _write(os.path.join(folder, f"{prefix}-well-{name}.png"), well)
        if name != "left":
            continue
        a, n, cov = art[box], nocap[box], cover[box][..., 3:4]
        lum = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
        la, ln = (a[..., :3] * lum).sum(-1, keepdims=True), (n[..., :3] * lum).sum(-1, keepdims=True)
        shadow = np.where(ln > 0.02, np.clip(1 - la / np.maximum(ln, 1e-4), 0, 1), 0)[..., 0]
        # Only the soft shadow right round the cap travels with it: blurred (the lip and floor of the well have sharp
        # edges the ratio picks up) and faded out a little beyond the cap's edge.
        shadow = _box_blur(shadow, 9)
        shadow = (shadow * np.clip((L.STICK_CAP_R + 34 - r) / 24, 0, 1))[..., None]
        alpha = cov + (1 - cov) * shadow
        rgb = np.where(alpha > 1e-4, a[..., :3] * cov / np.maximum(alpha, 1e-4), 0)
        _write(os.path.join(folder, f"{prefix}-cap.png"), np.concatenate([rgb, alpha], axis=-1))


def main():
    opts = parse_args()
    t0 = time.time()
    scene = setup_scene(opts)
    col = bpy.data.collections.new("controller")
    scene.collection.children.link(col)
    caps = build(opts["variant"], col)
    t1 = time.time()
    print(f"model built in {t1 - t0:.1f} s")
    if opts["blend"]:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(opts["blend"]))
    if opts["out"]:
        scene.render.filepath = os.path.abspath(opts["out"])
        bpy.ops.render.render(write_still=True)
        print(f"rendered in {time.time() - t1:.1f} s -> {scene.render.filepath}")
    if opts["sprites"]:
        t2 = time.time()
        prefix = "controller" if opts["variant"] == "standard" else "controller-edge"
        if not opts["out"]:
            raise SystemExit("--sprites needs --out (the sprites are cut from the art)")
        render_sprites(scene, caps, prefix, os.path.abspath(opts["sprites"]), os.path.abspath(opts["out"]))
        render_light_masks(scene, prefix, os.path.abspath(opts["sprites"]))
        print(f"sprites in {time.time() - t2:.1f} s -> {opts['sprites']}")


if __name__ == "__main__":
    main()

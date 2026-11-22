# SPDX-License-Identifier: GPL-3.0-or-later
"""Controller colour variants on the site model: a recipe (variants.json) recolours the model's own materials.

A recipe names colours per part and a finish per part; a colour is a hex string or a pattern:

    "#RRGGBB"                                                 plain
    {"gradient": ["#top", "#bottom"], "y": [y0, y1]}          vertical blend, art px (smooth between y0 and y1);
                                                              with "x": [x0, x1] instead, left to right
    {"camo": ["#base", "#a", "#b", "#c"], "size": px, "seed": n}   rounded blotches in three layers over the base
    {"stripes": "#base", "colour": "#c", "x": [[x0, x1], ...]}       vertical bands, art px
    {"carbon": ["#a", "#b"], "cell": px}                      diagonal twill checker (carbon-fibre look)
    {"shift": ["#a", "#b"]}                                   colour that turns with the surface (pearl, iridescent)
    {"strokes": "#base", "ink": "#c", "seed": n, ...}         our own random dry-brush strokes (brush.py; its other
                                                              parameters pass through: count, width, dry, angle...)
    {"base": <colour spec>, "overlay": [op, ...], "metal": m}  a printed layer (overlay.py: stripes, hatching, rays,
                                                              dashed outlines, dots, speckle, shards, strokes) over a
                                                              base; metal > 0 makes the print metallic (gold, silver)
    {"brushed": "#base", "amount": a, "angle": deg}           fine brushed-metal streaks
    {"internals": ["#core", "#board", "#metal"]}              for the core under a translucent shell: its own colour
                                                              with what lies under the wings as darker shapes (circuit
                                                              boards under the buttons, the rumble motors in the grips,
                                                              the trigger motors); none of it reaches the visible centre

Parts: shell (wings and grips), touchpad, core (centre), face (face buttons), dpad, sticks (caps), triggers (L1/R1/L2/R2),
pill (Create/Options; by default the shell's colour on a light shell, else the core's), rear (the back, seen at the
edges and through a translucent shell; by default the shell's colour), emblem (the ø button's chrome tint: a darker
chrome where the centre is light, so it still reads). Not recoloured: the light strip, the mute button, the stick
boots, the speaker grille and the player-LED line.

Finishes: matte (the model's own), satin, glossy, metallic, pearl, translucent.

Recipe colours are estimates from product photos, which lift the darks; they are brought onto the model's own
calibration (its white and black match the white controller's photo estimate) by c' = 0.988 c^1.138 in linear light.
"""
import json
import math

import bpy
import numpy as np

import brush
import overlay

S = 0.01            # world units per art pixel
ART_W, ART_H = 2290, 1520

# part -> the model's material names it recolours
PART_MATERIALS = {
    "shell": ("shell",), "touchpad": ("pad",), "core": ("core",), "face": ("face",), "dpad": ("dpad",),
    "sticks": ("cap", "rim"), "triggers": ("bumper", "trigger"), "pill": ("pill",), "rear": ("rear",),
    "emblem": ("ps-silver",),
}

FINISHES = {
    "matte": {},
    # (the body's surfaces are smooth but not mirror-true: a sharp clear coat shows their slow waves as a wavy streak
    # on the grips, so the shell's shading normals are calmed (smooth_normals.py) and even the glossy finishes keep
    # their highlights broad and soft: metal reads by its sheen, not by a sharp reflection)
    "satin": {"Roughness": 0.4, "Coat Weight": 0.1, "Coat Roughness": 0.35},
    "glossy": {"Roughness": 0.3, "Coat Weight": 0.4, "Coat Roughness": 0.22},
    "metallic": {"Metallic": 0.8, "Roughness": 0.48, "Coat Weight": 0.15, "Coat Roughness": 0.3},
    "pearl": {"Roughness": 0.34, "Coat Weight": 0.4, "Coat Roughness": 0.22},
    "translucent": {"Roughness": 0.26, "Coat Weight": 0.4, "Coat Roughness": 0.2},
}
TRANSLUCENT_SEE = 0.7      # how much of what lies under a translucent part shows through


def linear(hexstr):
    """A recipe hex (sRGB, photo estimate) -> the model's linear colour."""
    h = hexstr.strip().lstrip("#")[:6]
    out = []
    for i in (0, 2, 4):
        c = int(h[i:i + 2], 16) / 255
        c = c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
        out.append(min(1.0, 0.988 * c ** 1.138))
    return (*out, 1.0)


def luminance(rgba):
    return 0.2126 * rgba[0] + 0.7152 * rgba[1] + 0.0722 * rgba[2]


class Nodes:
    def __init__(self, mat):
        self.nt = mat.node_tree
        self.n, self.l = self.nt.nodes, self.nt.links

    def new(self, kind, **inputs):
        node = self.n.new(kind)
        for k, v in inputs.items():
            if isinstance(v, bpy.types.NodeSocket):
                self.l.new(v, node.inputs[k])
            else:
                node.inputs[k].default_value = v
        return node

    def math(self, op, a, b=None, clamp=False):
        m = self.n.new("ShaderNodeMath")
        m.operation = op
        m.use_clamp = clamp
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, bpy.types.NodeSocket):
                self.l.new(v, m.inputs[i])
            else:
                m.inputs[i].default_value = v
        return m.outputs[0]

    def mix(self, fac, a, b):
        m = self.n.new("ShaderNodeMix")
        m.data_type = "RGBA"
        for name, v in (("Factor", fac), ("A", a), ("B", b)):
            if isinstance(v, bpy.types.NodeSocket):
                self.l.new(v, m.inputs[name])
            else:
                m.inputs[name].default_value = v
        return m.outputs["Result"]

    def art_uv(self):
        """(u, v) in art pixels from the object coordinates (the parts sit at the origin, art plane at z 0)."""
        tc = self.n.new("ShaderNodeTexCoord")
        sep = self.new("ShaderNodeSeparateXYZ", Vector=tc.outputs["Object"])
        u = self.math("MULTIPLY_ADD", sep.outputs["X"], 1 / S)
        u.node.inputs[2].default_value = ART_W / 2
        v = self.math("MULTIPLY_ADD", sep.outputs["Y"], -1 / S)
        v.node.inputs[2].default_value = ART_H / 2
        return u, v, tc

    def step(self, x, edge, soft):
        """0 below edge, 1 above, over soft."""
        r = self.new("ShaderNodeMapRange", Value=x)
        r.inputs["From Min"].default_value = edge - soft / 2
        r.inputs["From Max"].default_value = edge + soft / 2
        r.interpolation_type = "SMOOTHSTEP"
        return r.outputs["Result"]


def colour(nodes, spec):
    """A colour spec as an RGBA constant (tuple) or a node socket."""
    if isinstance(spec, str):
        return linear(spec)
    if "gradient" in spec:
        u, v, _ = nodes.art_uv()
        y0, y1 = spec["y"] if "y" in spec else spec["x"]
        r = nodes.new("ShaderNodeMapRange", Value=v if "y" in spec else u)
        r.inputs["From Min"].default_value, r.inputs["From Max"].default_value = y0, y1
        r.interpolation_type = "SMOOTHSTEP"
        return nodes.mix(r.outputs["Result"], linear(spec["gradient"][0]), linear(spec["gradient"][1]))
    if "camo" in spec:
        base, *layers = spec["camo"]
        tc = nodes.n.new("ShaderNodeTexCoord")
        size = spec.get("size", 50) * S
        out = linear(base)
        for i, c in enumerate(layers):
            noise = nodes.new("ShaderNodeTexNoise", Vector=tc.outputs["Object"])
            noise.noise_dimensions = "4D"
            noise.inputs["W"].default_value = spec.get("seed", 1) * 7.31 + i * 3.17
            noise.inputs["Scale"].default_value = 0.55 / size
            noise.inputs["Detail"].default_value = 1.5
            noise.inputs["Roughness"].default_value = 0.45
            noise.inputs["Distortion"].default_value = 0.25
            out = nodes.mix(nodes.step(noise.outputs["Fac"], 0.58 - 0.02 * i, 0.012), out, linear(c))
        return out
    if "stripes" in spec:
        u, _, _ = nodes.art_uv()
        fac = None
        for x0, x1 in spec["x"]:
            band = nodes.math("MULTIPLY", nodes.step(u, x0, 1.5), nodes.math("SUBTRACT", 1.0, nodes.step(u, x1, 1.5)))
            fac = band if fac is None else nodes.math("ADD", fac, band, clamp=True)
        return nodes.mix(fac, linear(spec["stripes"]), linear(spec["colour"]))
    if "carbon" in spec:
        u, v, _ = nodes.art_uv()
        cell = spec.get("cell", 9)
        a = nodes.math("FLOOR", nodes.math("DIVIDE", nodes.math("ADD", u, v), cell))
        b = nodes.math("FLOOR", nodes.math("DIVIDE", nodes.math("SUBTRACT", u, v), cell * 2))
        check = nodes.math("FLOORED_MODULO", nodes.math("ADD", a, b), 2.0)
        # a soft sheen across each weave cell, as the fibres catch the light
        frac = nodes.math("FRACT", nodes.math("DIVIDE", nodes.math("ADD", u, v), cell))
        sheen = nodes.math("MULTIPLY_ADD", nodes.math("SINE", nodes.math("MULTIPLY", frac, math.pi)), 0.35)
        sheen.node.inputs[2].default_value = 0.75
        c = nodes.mix(check, linear(spec["carbon"][0]), linear(spec["carbon"][1]))
        m = nodes.n.new("ShaderNodeMix")
        m.data_type = "RGBA"
        m.blend_type = "MULTIPLY"
        m.inputs["Factor"].default_value = 1.0
        nodes.l.new(c, m.inputs["A"])
        nodes.l.new(sheen, m.inputs["B"])
        return m.outputs["Result"]
    if "overlay" in spec:
        base = colour(nodes, spec.get("base", "#808080"))
        tex = _art_texture(nodes, _overlay_image(spec["overlay"]))
        nodes.last_overlay_alpha = tex.outputs["Alpha"]
        return nodes.mix(tex.outputs["Alpha"], base, tex.outputs["Color"])
    if "brushed" in spec:
        tc = nodes.n.new("ShaderNodeTexCoord")
        ang = math.radians(spec.get("angle", 0))
        # streaks along the angle: noise squeezed across it
        rot = nodes.new("ShaderNodeVectorRotate", Vector=tc.outputs["Object"])
        rot.rotation_type = "Z_AXIS"
        rot.inputs["Angle"].default_value = -ang
        scale = nodes.new("ShaderNodeVectorMath", Vector=rot.outputs["Vector"])
        scale.operation = "MULTIPLY"
        scale.inputs[1].default_value = (0.6, 60.0, 1.0)
        noise = nodes.new("ShaderNodeTexNoise", Vector=scale.outputs["Vector"])
        noise.inputs["Scale"].default_value = 6.0
        noise.inputs["Detail"].default_value = 4.0
        amt = spec.get("amount", 0.15)
        shade = nodes.new("ShaderNodeMapRange", Value=noise.outputs["Fac"])
        shade.inputs["To Min"].default_value, shade.inputs["To Max"].default_value = 1 - amt, 1 + amt
        m = nodes.n.new("ShaderNodeMix")
        m.data_type = "RGBA"
        m.blend_type = "MULTIPLY"
        m.inputs["Factor"].default_value = 1.0
        m.inputs["A"].default_value = linear(spec["brushed"])
        nodes.l.new(shade.outputs["Result"], m.inputs["B"])
        return m.outputs["Result"]
    if "strokes" in spec:
        u, v, _ = nodes.art_uv()
        x = nodes.math("DIVIDE", u, ART_W)
        y = nodes.math("SUBTRACT", 1.0, nodes.math("DIVIDE", v, ART_H))
        vec = nodes.new("ShaderNodeCombineXYZ", X=x, Y=y)
        tex = nodes.new("ShaderNodeTexImage", Vector=vec.outputs[0])
        tex.image = _stroke_image(spec)
        tex.extension = "CLIP"
        return nodes.mix(tex.outputs["Color"], linear(spec["strokes"]), linear(spec["ink"]))
    if "internals" in spec:
        inside, board, metal = (linear(c) for c in spec["internals"])
        u, v, _ = nodes.art_uv()
        out = inside
        for mirror in (False, True):
            uu = nodes.math("SUBTRACT", ART_W - 1, u) if mirror else u
            # the board under the buttons, the rumble motor in the grip, the trigger motor under the shoulder
            out = nodes.mix(_rect(nodes, uu, v, 180, 200, 640, 640, 110), out, board)
            out = nodes.mix(_disc(nodes, uu, v, 380, 1060, 125), out, metal)
            out = nodes.mix(_disc(nodes, uu, v, 380, 1060, 70), out, board)
            out = nodes.mix(_disc(nodes, uu, v, 330, 120, 75), out, metal)
        return out
    if "shift" in spec:
        lw = nodes.new("ShaderNodeLayerWeight", Blend=spec.get("blend", 0.45))
        return nodes.mix(lw.outputs["Facing"], linear(spec["shift"][0]), linear(spec["shift"][1]))
    raise ValueError(f"unknown colour spec {spec}")


_IMAGES = {}


def _art_texture(nodes, image):
    """An image laid over the art, looked up by the art position (straight down)."""
    u, v, _ = nodes.art_uv()
    x = nodes.math("DIVIDE", u, ART_W)
    y = nodes.math("SUBTRACT", 1.0, nodes.math("DIVIDE", v, ART_H))
    vec = nodes.new("ShaderNodeCombineXYZ", X=x, Y=y)
    tex = nodes.new("ShaderNodeTexImage", Vector=vec.outputs[0])
    tex.image = image
    tex.extension = "CLIP"
    return tex


def _overlay_image(ops):
    key = "overlay:" + json.dumps(ops, sort_keys=True)
    if key not in _IMAGES:
        rgba = overlay.layer(ops)
        lin = np.where(rgba[..., :3] <= 0.04045, rgba[..., :3] / 12.92, ((rgba[..., :3] + 0.055) / 1.055) ** 2.4)
        lin = np.clip(0.988 * lin ** 1.138, 0, 1)               # the same calibration as linear()
        img = bpy.data.images.new(f"overlay-{len(_IMAGES)}", ART_W, ART_H, alpha=True, float_buffer=True)
        img.colorspace_settings.name = "Non-Color"
        img.alpha_mode = "STRAIGHT"
        px = np.concatenate([lin, rgba[..., 3:]], -1)[::-1]
        img.pixels.foreach_set(np.ascontiguousarray(px, np.float32).ravel())
        _IMAGES[key] = img
    return _IMAGES[key]


def _stroke_image(spec):
    """The strokes' coverage as an image over the art (non-colour, made once per set of parameters)."""
    params = {k: v for k, v in spec.items() if k not in ("strokes", "ink")}
    key = json.dumps(params, sort_keys=True)
    if key not in _IMAGES:
        a = brush.coverage(**params)[::-1]          # Blender's rows run bottom up
        img = bpy.data.images.new(f"strokes-{len(_IMAGES)}", ART_W, ART_H, alpha=False, float_buffer=True)
        img.colorspace_settings.name = "Non-Color"
        px = np.empty((ART_H, ART_W, 4), np.float32)
        px[..., 0] = px[..., 1] = px[..., 2] = a
        px[..., 3] = 1
        img.pixels.foreach_set(px.ravel())
        _IMAGES[key] = img
    return _IMAGES[key]


def _disc(nodes, u, v, cx, cy, r):
    d = nodes.math("SQRT", nodes.math("ADD", nodes.math("POWER", nodes.math("SUBTRACT", u, cx), 2.0),
                                      nodes.math("POWER", nodes.math("SUBTRACT", v, cy), 2.0)))
    return nodes.math("SUBTRACT", 1.0, nodes.step(d, r, 3.0))


def _rect(nodes, u, v, x0, y0, x1, y1, radius):
    """A rounded rectangle's coverage."""
    cx, cy, hx, hy = (x0 + x1) / 2, (y0 + y1) / 2, (x1 - x0) / 2 - radius, (y1 - y0) / 2 - radius
    dx = nodes.math("MAXIMUM", nodes.math("SUBTRACT", nodes.math("ABSOLUTE", nodes.math("SUBTRACT", u, cx)), hx), 0.0)
    dy = nodes.math("MAXIMUM", nodes.math("SUBTRACT", nodes.math("ABSOLUTE", nodes.math("SUBTRACT", v, cy)), hy), 0.0)
    d = nodes.math("SQRT", nodes.math("ADD", nodes.math("POWER", dx, 2.0), nodes.math("POWER", dy, 2.0)))
    return nodes.math("SUBTRACT", 1.0, nodes.step(d, radius, 3.0))


def representative(spec):
    """One plain colour for a spec (for derived parts)."""
    if isinstance(spec, str):
        return spec
    for k in ("camo", "carbon", "shift", "gradient", "internals"):
        if k in spec:
            return spec[k][-1] if k == "gradient" else spec[k][0]
    if "overlay" in spec:
        return representative(spec.get("base", "#808080"))
    return spec.get("stripes") or spec.get("strokes") or spec.get("brushed")


def _bsdf(mat):
    return mat.node_tree.nodes["Principled BSDF"]


def _set(sock, value):
    if isinstance(value, bpy.types.NodeSocket):
        mat_links = sock.node.id_data.links
        mat_links.new(value, sock)
    else:
        for link in list(sock.links):
            sock.node.id_data.links.remove(link)
        sock.default_value = value


def _finish(mat, name, tint):
    b = _bsdf(mat)
    for k, v in FINISHES[name].items():
        b.inputs[k].default_value = v
    if name == "translucent":
        nodes = Nodes(mat)
        out = next(n for n in mat.node_tree.nodes if n.bl_idname == "ShaderNodeOutputMaterial")
        clear = nodes.new("ShaderNodeBsdfTransparent")
        _set(clear.inputs["Color"], tint)
        mix = nodes.new("ShaderNodeMixShader", Fac=TRANSLUCENT_SEE)
        nodes.l.new(b.outputs[0], mix.inputs[1])
        nodes.l.new(clear.outputs[0], mix.inputs[2])
        nodes.l.new(mix.outputs[0], out.inputs["Surface"])


def recolour(mat, part, spec, finish, recipe):
    """Edits mat (a copy of the model's material) for one part."""
    nodes = Nodes(mat)
    b = _bsdf(mat)
    c = colour(nodes, spec)
    core = linear(representative(recipe["parts"].get("core", "#1E1F22")))
    mixes = [n for n in mat.node_tree.nodes if n.bl_idname == "ShaderNodeMix" and n.name.startswith("Mix")]
    if mat.name.startswith(("shell", "rear")):
        # the model's own two-tone mask: the shell's colour (A) and the dark band where it meets the core (B)
        m = mat.node_tree.nodes["Mix"]
        _set(m.inputs["A"], c)
        _set(m.inputs["B"], core)
    elif mat.name.startswith(("face", "dpad")):
        # the buttons' sides darker than their tops, as in the model
        m = mat.node_tree.nodes["Mix"]
        top = linear(representative(spec))
        _set(m.inputs["B"], c)
        _set(m.inputs["A"], tuple(x * 0.43 for x in top[:3]) + (1.0,))
    elif mat.name.startswith("rim"):
        lit = linear(representative(spec))
        _set(b.inputs["Base Color"], tuple(min(1.0, x * 1.6 + 0.012) for x in lit[:3]) + (1.0,))
    else:
        _set(b.inputs["Base Color"], c)
    if mat.name.startswith("ps-silver"):
        return                                  # the chrome keeps its own finish; only its tint changes
    tint = linear(representative(spec))
    _finish(mat, finish, tuple(min(1.0, x * 1.4 + 0.08) for x in tint[:3]) + (1.0,))
    alpha = getattr(nodes, "last_overlay_alpha", None)
    if alpha is not None and isinstance(spec, dict) and spec.get("metal"):
        # a metallic print: metallic and a little smoother where the print is
        base_m = b.inputs["Metallic"].default_value
        metal = nodes.math("MULTIPLY_ADD", alpha, spec["metal"] - base_m)
        metal.node.inputs[2].default_value = base_m
        nodes.l.new(metal, b.inputs["Metallic"])
        base_r = b.inputs["Roughness"].default_value
        rough = nodes.math("MULTIPLY_ADD", alpha, 0.3 - base_r)
        rough.node.inputs[2].default_value = base_r
        nodes.l.new(rough, b.inputs["Roughness"])
    del mixes


class Painter:
    """Keeps the model's materials and applies recipes on copies (apply() again for the next variant)."""

    def __init__(self, objects):
        self.objects = [o for o in objects if o.type in ("MESH", "CURVE") and o.data and o.data.materials]
        self.original = {o.name: [s.material for s in o.material_slots] for o in self.objects}

    def reset(self):
        for o in self.objects:
            for slot, m in zip(o.material_slots, self.original[o.name]):
                slot.material = m

    def apply(self, recipe):
        self.reset()
        parts = dict(recipe.get("parts", {}))
        finishes = recipe.get("finish", {})
        if parts:
            shell = parts.get("shell")
            if shell and "pill" not in parts:
                rep = linear(representative(shell))
                parts["pill"] = representative(shell) if luminance(rep) > 0.3 else representative(parts.get("core", "#1E1F22"))
            if shell and "rear" not in parts:
                parts["rear"] = parts.get("rearColour") or shell
        made = {}
        for part, spec in parts.items():
            if part not in PART_MATERIALS:
                continue
            finish = finishes.get(part, finishes.get("default", "matte"))
            for base in PART_MATERIALS[part]:
                for o in self.objects:
                    for slot, orig in zip(o.material_slots, self.original[o.name]):
                        if orig is None or orig.name.split(".")[0] != base:
                            continue
                        key = (part, base)
                        if key not in made:
                            m = orig.copy()
                            m.name = f"{base}-{recipe['id']}"
                            recolour(m, part, spec, "matte" if part == "rear" else finish, recipe)
                            made[key] = m
                        slot.material = made[key]
        return made

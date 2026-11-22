# SPDX-License-Identifier: GPL-3.0-or-later
"""The site project's top view (its blender/top.py: same scene, studio, camera and model) plus the passes the app's
DualSense art needs. The site project is only read: nothing is written there (bytecode off, output under --out).

    set SITE_BLENDER=<the site project's blender folder>
    blender -b --factory-startup -P site_top_passes.py -- --samples 256 --out DIR

SITE_MODEL (optional) is the model file to load; by default the site's cache/controller-v7.blend (the scripted body
with the ring + slash centre button; studio.ensure_controller would pick the scanned body instead).

DIR/beauty.png    top.py's beauty (the app's controller.png)
DIR/nocap.png     the same with the stick caps (and shafts) hidden: no camera, no shadow, no bounces
DIR/capcover.png  the caps alone (coverage), 32 samples
DIR/cover.png     top.py's strip coverage pass (light-strip + pad-slit as plain emission, no lamps)
"""
import os
import sys

sys.dont_write_bytecode = True
SITE = os.environ.get("SITE_BLENDER")
if not SITE or not os.path.isfile(os.path.join(SITE, "top.py")):
    raise SystemExit("set SITE_BLENDER to the site project's blender folder")
sys.path.insert(0, SITE)
import bpy  # noqa: E402

import studio as ST  # noqa: E402
import layout as L  # noqa: E402

MODEL = os.environ.get("SITE_MODEL") or os.path.join(SITE, "cache", "controller-v7.blend")
ST.ensure_controller = lambda: MODEL      # instead of what studio.ensure_controller picks (and might build into the cache)
CAPS = ("cap-dish-", "cap-rim-", "cap-under-", "shaft-")


def check_out(out):
    assert out and not os.path.abspath(out).lower().startswith(os.path.abspath(SITE).lower()), "output inside the site project"


def setup(o):
    """top.py's scene, studio, camera and the model, posed flat: (scene, studio, controller)."""
    scene = ST.new_scene(o, L.ART_W, L.ART_H, transparent=True, look="AgX - High Contrast", exposure=0.15)
    st = ST.Studio(scene)
    target = (0, 0, 0)
    st.box("box-top", (0.0, 0.5, 0.86), (52, 15), 7.5, dist=44, target=target)
    st.box("box-left", (-0.8, 0.0, 0.6), (9, 40), 6.5, dist=44, target=target)
    st.box("box-right", (0.82, -0.1, 0.56), (6, 40), 3.2, dist=44, target=target)
    st.box("box-front", (0.1, -0.86, 0.5), (46, 12), 3.0, dist=44, target=target)
    st.box("box-rim", (0.3, 0.86, 0.42), (46, 5), 7.0, dist=44, target=target)
    st.key((-0.18, -0.08, 0.98), (14, 9), 4200.0, dist=44, target=target)
    ctl = ST.Controller(scene, hide=("port-",))
    cam = ST.camera(scene)
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = L.ART_W * ST.S
    cam.data.sensor_fit = "HORIZONTAL"
    cam.data.clip_end = 200
    cam.location = (0, 0, 50)
    ctl.pose((0, 0, 0), (1, 0, 0, 0))
    return scene, st, ctl


def _border(scene, box):
    """Render only box (art px: x0, y0, x1, y1) of the full frame, or the whole frame with None."""
    r = scene.render
    r.use_border = box is not None
    r.use_crop_to_border = False
    if box:
        x0, y0, x1, y1 = box
        r.border_min_x, r.border_max_x = x0 / L.ART_W, x1 / L.ART_W
        r.border_min_y, r.border_max_y = 1 - y1 / L.ART_H, 1 - y0 / L.ART_H


# The stick sprites' squares (make_assets.py cuts 2 x SPRITE_HALF round each stick) and a margin for the denoiser.
STICK_BOX = (min(x for x, _ in L.STICKS.values()) - L.SPRITE_HALF - 16, min(y for _, y in L.STICKS.values()) - L.SPRITE_HALF - 16,
             max(x for x, _ in L.STICKS.values()) + L.SPRITE_HALF + 16, max(y for _, y in L.STICKS.values()) + L.SPRITE_HALF + 16)


def render_art(scene, ctl, out, sprites_only_round_sticks=True):
    """beauty.png, nocap.png and capcover.png into out. The last two are only needed round the sticks: rendered there
    alone (the rest of the frame transparent) unless sprites_only_round_sticks is False."""
    ST.render_to(scene, os.path.join(out, "beauty.png"))
    caps = [ob for ob in ctl.objects if ob.name.startswith(CAPS)]
    for c in caps:
        c.visible_camera = c.visible_shadow = c.visible_diffuse = c.visible_glossy = c.visible_transmission = False
    _border(scene, STICK_BOX if sprites_only_round_sticks else None)
    ST.render_to(scene, os.path.join(out, "nocap.png"))
    for c in caps:
        c.visible_camera = c.visible_shadow = c.visible_diffuse = c.visible_glossy = c.visible_transmission = True
    others = [ob for ob in ctl.objects if ob not in caps]
    for ob in others:
        ob.visible_camera = False
    samples = scene.cycles.samples
    scene.cycles.samples = 32
    ST.render_to(scene, os.path.join(out, "capcover.png"))
    scene.cycles.samples = samples
    for ob in others:
        ob.visible_camera = True
    _border(scene, None)


def render_cover(scene, st, ctl, out):
    """top.py's strip coverage pass, unchanged (it changes the scene: call it last)."""
    for ob in scene.objects:
        if ob.type == "LIGHT" or ob.name.startswith("box-"):
            ob.hide_render = True
    st.world_mul.default_value = 0.0
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0
    scene.render.dither_intensity = 0
    strip_n = ctl.strip_material.node_tree.nodes
    slit_mat = next(o for o in ctl.objects if o.name == "pad-slit").data.materials[0]
    for m in (ctl.strip_material, slit_mat):
        b = m.node_tree.nodes["Principled BSDF"]
        b.inputs["Base Color"].default_value = (0, 0, 0, 1)
        b.inputs["Emission Color"].default_value = (1, 1, 1, 1)
    keep = ("light-strip", "pad-slit")
    for ob in ctl.objects:
        ob.visible_camera = ob.name in keep
    strip_n["use-profile"].outputs[0].default_value = 0.0
    ctl.strip(1.0)
    scene.cycles.samples = 32
    scene.cycles.use_denoising = False
    ST.render_to(scene, os.path.join(out, "cover.png"))


def main():
    o = ST.parse(default_samples=256)
    check_out(o.out)
    scene, st, ctl = setup(o)
    render_art(scene, ctl, o.out, sprites_only_round_sticks=False)
    render_cover(scene, st, ctl, o.out)


if __name__ == "__main__":
    main()

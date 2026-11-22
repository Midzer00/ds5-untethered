# SPDX-License-Identifier: GPL-3.0-or-later
"""Controller colour variants: the site top view (site_top_passes.py) once per recipe in variants.json.

    set SITE_BLENDER=<the site project's blender folder>
    blender -b --factory-startup -P site_variants.py -- --ids jet-black,crimson-red [--recipes variants.json] --out DIR
                                                     [--samples 256]

For each id: DIR/<id>/beauty.png, nocap.png and capcover.png (the last two only round the sticks), for make_assets.py
(--name controller-<id> --no-masks). The light masks are the default white's: the variants share the model's shape.
The model loads once; its shell's shading normals are calmed once (smooth_normals.py: no wavy reflections on the
grips) and each variant recolours copies of its materials (variant_materials.py).
"""
import json
import os
import sys
import time

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def own_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    ids, recipes, rest = None, os.path.join(HERE, "variants.json"), []
    i = 0
    while i < len(argv):
        if argv[i] == "--ids":
            ids = argv[i + 1].split(","); i += 2
        elif argv[i] == "--recipes":
            recipes = argv[i + 1]; i += 2
        else:
            rest.append(argv[i]); i += 1
    sys.argv = sys.argv[:sys.argv.index("--") + 1] + rest if "--" in sys.argv else sys.argv
    return ids, recipes


IDS, RECIPES = own_args()
import site_top_passes as TP  # noqa: E402
import variant_materials as VM  # noqa: E402
import smooth_normals as SN  # noqa: E402


def main():
    o = TP.ST.parse(default_samples=256)
    TP.check_out(o.out)
    with open(RECIPES, encoding="utf-8") as f:
        recipes = {r["id"]: r for r in json.load(f)["variants"]}
    ids = IDS or list(recipes)
    scene, st, ctl = TP.setup(o)
    t = time.time()
    smoothed = SN.apply(ctl.objects)
    print(f"SMOOTHED {', '.join(smoothed)} in {time.time() - t:.1f} s", flush=True)
    painter = VM.Painter(ctl.objects)
    for vid in ids:
        t = time.time()
        painter.apply(recipes[vid])
        TP.render_art(scene, ctl, os.path.join(o.out, vid))
        print(f"VARIANT {vid} in {time.time() - t:.1f} s", flush=True)
    painter.reset()
    SN.restore(ctl.objects, smoothed)


main()

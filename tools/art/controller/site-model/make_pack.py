# SPDX-License-Identifier: GPL-3.0-or-later
"""The app's controller colour pack from the variant passes (site_variants.py).

    python make_pack.py PASSES_DIR [OUT_DIR] [--recipes variants.json]
        OUT_DIR defaults to src/Kopru.App/Assets/Pads

For every recipe, in variants.json's order with the standard colours first: OUT_DIR/<id>/body.webp (the art, half size:
1145 x 760), well-left.webp, well-right.webp and cap.webp (the stick sprites, half size: 270 x 270), WebP quality 90 with
lossless alpha; and OUT_DIR/pads.json, an array in display order of
{"id", "group": "standard" | "special", "nameEn", "nameTr", "swatch": "#RRGGBB"}. The swatch is the median colour of
the variant's body where the default art shows its white shell and touchpad. The default white is not in the pack (the
app keeps its own PNG set and light masks, which every variant shares). A translucent variant keeps the default's
outline (make_assets.keep_outline), laid over a dark tint of its own shell.
"""
import json
import os
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.dont_write_bytecode = True
sys.path.insert(0, HERE)
import make_assets as MA  # noqa: E402

REPO = os.path.normpath(os.path.join(HERE, "..", "..", "..", ".."))
DEFAULT_ART = os.path.join(REPO, "src", "Kopru.App", "Assets", "controller.png")


def representative(spec):
    if isinstance(spec, str):
        return spec
    for k in ("camo", "carbon", "shift", "internals"):
        if k in spec:
            return spec[k][0]
    if "gradient" in spec:
        return spec["gradient"][-1]
    if "base" in spec:
        return representative(spec["base"])
    return spec.get("stripes") or spec.get("strokes") or spec.get("brushed")


def hexrgb(h):
    h = h.lstrip("#")
    return np.array([int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)], np.float32)


def save_webp(rgba, size, path):
    im = Image.fromarray((np.clip(rgba, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")
    im = im.resize(size, Image.LANCZOS)               # (Pillow resamples RGBA with premultiplied alpha)
    im.save(path, "WEBP", quality=90, alpha_quality=100, method=6)


def main():
    args = sys.argv[1:]
    recipes_path = args[args.index("--recipes") + 1] if "--recipes" in args else os.path.join(HERE, "variants.json")
    pos = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] != "--recipes")]
    src = pos[0]
    out = pos[1] if len(pos) > 1 else os.path.join(REPO, "src", "Kopru.App", "Assets", "Pads")
    with open(recipes_path, encoding="utf-8") as f:
        recipes = json.load(f)["variants"]
    recipes = [r for r in recipes if r.get("group") == "standard"] + [r for r in recipes if r.get("group") != "standard"]
    default = np.asarray(Image.open(DEFAULT_ART).convert("RGBA"), np.float32) / 255
    shell_mask = (default[..., 3] > 0.99) & (default[..., :3].mean(-1) > 0.75)
    pads = []
    for r in recipes:
        translucent = "translucent" in r.get("finish", {}).values()
        fill = hexrgb(representative(r["parts"]["shell"])) * 0.35 if translucent else None
        parts = MA.sprites(os.path.join(src, r["id"]), DEFAULT_ART if translucent else None, fill)
        folder = os.path.join(out, r["id"])
        os.makedirs(folder, exist_ok=True)
        save_webp(parts["body"], (1145, 760), os.path.join(folder, "body.webp"))
        for k in ("well-left", "well-right", "cap"):
            save_webp(parts[k], (270, 270), os.path.join(folder, f"{k}.webp"))
        swatch = np.median(parts["body"][..., :3][shell_mask], 0)
        pads.append({"id": r["id"], "group": r["group"], "nameEn": r["name"]["en"], "nameTr": r["name"]["tr"],
                     "swatch": "#" + "".join(f"{round(float(v) * 255):02X}" for v in swatch)})
        print(r["id"], pads[-1]["swatch"], flush=True)
    with open(os.path.join(out, "pads.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(pads, f, ensure_ascii=False, indent=1)
        f.write("\n")
    print(len(pads), "pads ->", out)


if __name__ == "__main__":
    main()

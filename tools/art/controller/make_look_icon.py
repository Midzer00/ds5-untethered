# SPDX-License-Identifier: GPL-3.0-or-later
"""The Controller page's colour tile icon: a DualSense in four flat colour bands.

Takes the body picture of four colour variants of the built-in pack (src/Kopru.App/Assets/Pads/<id>/body.webp, all
rendered with the same framing), scales each to the icon's size first and then cuts it into four equal vertical strips
at whole pixel columns, left to right one variant each, so the seams are hard edges (nothing is resampled across them).
Writes src/Kopru.App/Assets/look-icon.png (RGBA, transparent background) at three times its display size (60 x 40 DIP),
sharp up to 300 % scaling.

    python tools/art/controller/make_look_icon.py

Needs Pillow. Run it again when the pack's body pictures change.
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
PADS = ROOT / "src" / "Kopru.App" / "Assets" / "Pads"
OUT = ROOT / "src" / "Kopru.App" / "Assets" / "look-icon.png"

# Left to right: Crimson Red, Bright Pink, Deep Violet, Sky Blue.
VARIANTS = ["crimson-red", "bright-pink", "deep-violet", "sky-blue"]
WIDTH = 180  # 3 x 60 DIP


def main() -> None:
    bodies = [Image.open(PADS / v / "body.webp").convert("RGBA") for v in VARIANTS]
    size = bodies[0].size
    if any(b.size != size for b in bodies):
        raise SystemExit("the body pictures differ in size")
    height = round(WIDTH * size[1] / size[0])
    scaled = [b.resize((WIDTH, height), Image.LANCZOS) for b in bodies]
    icon = Image.new("RGBA", (WIDTH, height), (0, 0, 0, 0))
    for i, body in enumerate(scaled):
        left, right = round(i * WIDTH / len(scaled)), round((i + 1) * WIDTH / len(scaled))
        icon.paste(body.crop((left, 0, right, height)), (left, 0))
    icon.save(OUT, optimize=True)
    print(f"{OUT.relative_to(ROOT)}: {WIDTH} x {height}")


if __name__ == "__main__":
    main()

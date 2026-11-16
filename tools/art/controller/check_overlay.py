# SPDX-License-Identifier: GPL-3.0-or-later
"""Draws the app's overlay shapes (button regions, stick travel, touchpad, light strip, player LEDs) over a render, for
checking alignment by eye. Needs Pillow:  python check_overlay.py render.png out.png [--edge] [--plan]
--plan also draws the body and shell outlines this script models from."""
import sys

from PIL import Image, ImageDraw

sys.dont_write_bytecode = True
import layout as L  # noqa: E402


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    edge = "--edge" in sys.argv
    plan = "--plan" in sys.argv
    src, dst = args
    img = Image.open(src).convert("RGBA")
    if img.size != (L.ART_W, L.ART_H):
        img = img.resize((L.ART_W, L.ART_H))
    bg = Image.new("RGBA", img.size, (26, 28, 32, 255))
    bg.alpha_composite(img)
    d = ImageDraw.Draw(bg)
    pad, band, _ = L.pad_and_band()
    d.line(pad + [pad[0]], fill=(255, 80, 80, 255), width=2)
    d.line(band + [band[0]], fill=(80, 255, 120, 255), width=2)
    for name, poly in L.regions(edge):
        d.line(list(poly) + [poly[0]], fill=(60, 160, 255, 255), width=2)
    for name, c in L.STICKS.items():
        r = L.STICK_WELL_R
        d.ellipse([c[0] - r, c[1] - r, c[0] + r, c[1] + r], outline=(255, 200, 0, 255), width=2)
        r = L.STICK_CAP_R
        d.ellipse([c[0] - r, c[1] - r, c[0] + r, c[1] + r], outline=(255, 200, 0, 255), width=2)
    x, y, w, h = L.TOUCH_SURFACE
    d.rectangle([x, y, x + w, y + h], outline=(255, 0, 255, 255), width=1)
    for i in range(5):
        cx = L.LED_CENTRE_X + (i - 2) * L.LED_SPACING
        d.rounded_rectangle([cx - 10, L.LED_Y - 4, cx + 10, L.LED_Y + 4], radius=4, outline=(255, 255, 255, 255), width=1)
    if plan:
        body = L.body_outline()
        d.line(body + [body[0]], fill=(255, 140, 0, 255), width=2)
        shell = [p for p, _ in L.shell_left()]
        d.line(shell + [shell[0]], fill=(200, 0, 255, 255), width=2)
        for hole in L.dpad_recess():
            d.line(hole + [hole[0]], fill=(0, 255, 255, 255), width=1)
    bg.save(dst)


if __name__ == "__main__":
    main()

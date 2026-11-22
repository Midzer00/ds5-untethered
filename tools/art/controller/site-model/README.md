# DualSense art from the site model

The DualSense pictures in `src/Kopru.App/Assets` (`controller.png`, `controller-light-strip/-core/-glow.png`,
`controller-well-left/right.png`, `controller-cap.png`) are a top view of the model from the site project's Blender
scenes: a model grown from `../build_controller.py`'s, with the app's emblem (the logo's ring + slash, in
chrome) as the centre button in place of the PS button. The Edge pictures (`controller-edge*.png`) still come from
`../build_controller.py --variant edge`; the site scene has no Edge.

These scripts need the site project's `blender` folder (its `studio.py`, `layout.py`, `top.py` and model caches). They
only read from it.

## Files used

- The model: the site project's `blender/cache/controller-v7.blend` (the scripted body; its loader would otherwise pick
  the scanned body in `blender/scan/cache/controller-scan.blend`, which looks lumpy seen straight down). `SITE_MODEL`
  overrides it.
- Scene, studio, camera and exposure: as the site's `blender/top.py` (orthographic, straight down, pixel-aligned with
  `../layout.py` at 2290 x 1520, transparent background, AgX High Contrast, exposure +0.15, 256 samples).

## Render

```
set SITE_BLENDER=<the site project's blender folder>
blender -b --factory-startup -P tools/art/controller/site-model/site_top_passes.py -- --samples 256 --out <passes folder>
python tools/art/controller/site-model/make_assets.py <passes folder> src/Kopru.App/Assets
```

`site_top_passes.py` writes `beauty.png` (the art), `nocap.png` (stick caps and shafts hidden from camera, shadows and
bounces), `capcover.png` (the caps alone, for their coverage) and `cover.png` (the site's strip coverage pass: the
strip and the pad's slit as plain emission). About 55 s on an RTX 3090.

`make_assets.py` cuts the app's sprites from them as `../build_controller.py` does: the well sprites from the no-cap
render, the cap with its shadow (art against no-cap), the strip and its bright core from the coverage pass, all cut to
the body (`../light_clip.py`). Two differences: the strip is kept to the app's band (`ControllerArt`'s light strip, plus
2 px), since the coverage pass also has the slit along the pad's top edge; and the glow, a halo round the band as
`light_clip` draws it, is weighted by the strip's light at the nearest point of the band, because the site's strip
fades out under the middle of the pad.

## The centre button

The emblem's ring is 57 px in radius, centred at (1144, 728): 42 px above where the PS button sits (`../layout.py`
`PS`, which the Edge art and `../build_controller.py` still use). `ControllerLayout.cs` has the PS press region there for
the DualSense art (a 58 px circle) and at the PS button for the Edge art.

## Colour variants

`variants.json` holds one recipe per controller colour, in display order (standard colours, then special editions):
per-part colours (estimates from product photos, brought onto the model's calibration; the standard colours were tuned
by rendering and comparing with the product photos), a finish per part and, where the edition has one, a pattern.
`approach` records how each was made: `solid` (the colour's own plain colours and finish), `scheme-only` (a printed
edition reduced to its colour scheme) or `original-pattern` (our own procedural pattern evoking the edition's look in
colour, density and placement: camo blotches, fades, two-tone splits, stripes, hatching, rays, dashed outlines, dots,
speckle, crystal slivers, carbon twill, brushed metal, random brush strokes; `overlay.py`, `brush.py`; nothing traced).
No logos, artwork, characters, symbols, lettering or brand marks; the ø button and the plain face buttons stay on every
variant (on a light centre the ø gets a darker chrome so it still reads). `variant_materials.py` documents the parts,
patterns and finishes. The default white has no recipe: it is the art above.

```
blender -b --factory-startup -P tools/art/controller/site-model/site_variants.py -- --out <passes> [--ids jet-black,grey-camo]
python tools/art/controller/site-model/make_pack.py <passes>
```

`site_variants.py` loads the model once (about 10 s) and calms the shell's shading normals (`smooth_normals.py`, about
25 s): the shell is smooth but not mirror-true, and its grips' slow ripples, a few degrees deep, which the white
default's bright diffuse hides, showed on darker, saturated, glossy and metallic colours as a wavy S-shaped reflection
of the studio's soft boxes. The vertex normals are low-passed over the mesh at three scales and each corner takes the
low-passed normal where it faces the camera and stays within 15 to 35 degrees of the model's; the vertices do not move
(the outline and the masks stay the default's), and the side walls, the rims of the button holes and the open edges keep
the model's normals. The finishes' highlights are broad too (`FINISHES`: metal by its soft sheen). Each variant then
takes about 21 s on an RTX 3090 (30 s translucent): the full beauty, and the no-cap and cap passes only round the
sticks. `make_pack.py` writes the pack the app loads: `src/Kopru.App/Assets/Pads/<id>/body.webp` (1145 x 760),
`well-left.webp`, `well-right.webp`, `cap.webp` (270 x 270; all half size, WebP quality 90 with lossless alpha) and
`Pads/pads.json` (display order: `{"id", "group", "nameEn", "nameTr", "swatch"}`). Every variant keeps the model's
outline, so the light masks are the default's; a translucent variant, which would let the background through its
triggers and the pad's edge, is laid over a dark tint of its own shell there (`make_assets.keep_outline`).
`make_assets.py <passes>/<id> <out> --name controller-<id> --no-masks` writes one variant at full size as PNG instead.

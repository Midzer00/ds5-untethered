# Controller art

`src/Kopru.App/Assets/controller.png` (DualSense, white) and `controller-edge.png` (DualSense Edge, dark) are renders of
a model built from scratch by `build_controller.py` in Blender. No outside models, textures or logos are used.

## Render

Blender 5.3 (Cycles; an NVIDIA GPU is used through OptiX if there is one, otherwise pass `--cpu`):

```
blender -b --factory-startup -P tools/art/controller/build_controller.py -- --variant standard --out src/Kopru.App/Assets/controller.png --sprites src/Kopru.App/Assets
blender -b --factory-startup -P tools/art/controller/build_controller.py -- --variant edge --out src/Kopru.App/Assets/controller-edge.png --sprites src/Kopru.App/Assets
```

`--sprites` also writes what the app draws for a moved stick: `controller[-edge]-well-left/right.png` (each stick's
surroundings without its cap, 540 x 540 around the stick) and `controller[-edge]-cap.png` (the cap with its shadow, cut
from the art: the cap's pixels plus the darkening it causes, measured against the no-cap render), and the light-strip
masks the app paints the live colour through, each covering `ControllerLayout.LightRect`:
`controller[-edge]-light-strip.png` (the diffuser channel), `-light-core.png` (its bright middle) and `-light-glow.png`
(a short soft halo hugging the strip, drawn by `light_clip.py` from the strip's outline: strongest at its edge, gone
`LIGHT_REACH` px out). All three are cut to the body; `python tools/art/controller/light_clip.py` redoes the glow of the
shipped masks.

Defaults: 2290 x 1520 px, transparent background (RGBA PNG), 384 samples (adaptive) with denoising, AgX (Base Contrast,
exposure +0.5), orthographic camera straight down at 100 px per world unit (the art's 2290 px are the controller's
160 mm). Each variant takes about 35 s to build the model, 23 s to render and 60 s for the sprites and masks on an RTX 3090. `--samples N`, `--scale F` (smaller previews) and `--blend FILE` (save the scene) are optional. The
result is deterministic (fixed seed). To keep the repository small, optionally re-save with Pillow `optimize=True`.

## How it is built

- `layout.py`: the 2D shapes in the art's pixels. The overlay geometry the app draws on top (button regions, sticks,
  touch surface, bumpers and triggers from `ControllerLayout.cs`; touchpad, light strip and player LEDs from
  `ControllerArt.cs`) is ported here one to one, so each modelled part lands exactly under its overlay. The body
  silhouette and the two-tone split (white shell over the dark core) are drawn here as spline control points.
- `build_controller.py`: every part (core, the two shell halves, touchpad, light-strip channels, translucent face
  buttons, d-pad, Create/Options, sticks with boot, dished cap and textured rim, PS, mute, the Edge's Fn buttons,
  bumpers, triggers) is a separate height field over its outline: triangulated with denser rings at the edge,
  lifted by a quarter-round profile per edge (a soft round on the outside, a short step where the shell meets the core,
  a rounded lip into each button hole), a smooth dome (a blurred mask) and the grips falling away. Normals come from the
  same function, so the shading is smooth. Lighting: a soft key sun from the top left, a large softbox above, a faint
  fill and a dim white world.
- The app paints the light strip (live colour) and the player LEDs over the render; the touchpad is part of the render.
- The Edge is the same model with its own colours: white wings, a glossy black centre, a black dotted touchpad, black
  buttons and the two Fn buttons. No logos or text on either.

## Changing the art

Positions are shared with the app: if a shape moves, change it in `ControllerLayout.cs`/`ControllerArt.cs` and in
`layout.py`, render both variants again and check the overlay:

```
python tools/art/controller/check_overlay.py src/Kopru.App/Assets/controller.png overlay.png
python tools/art/controller/check_overlay.py src/Kopru.App/Assets/controller-edge.png overlay-edge.png --edge
```

Always render with `--sprites` so the moved-stick sprites match the art. `ControllerArtRenderTests` saves the places in the app that show the art when
`KOPRU_PAGE_RENDER_DIR` is set.

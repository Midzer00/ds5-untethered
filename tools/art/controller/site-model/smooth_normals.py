# SPDX-License-Identifier: GPL-3.0-or-later
"""Calmer shading normals for the shell (the wings and grips) in the colour variants.

The model's shell is smooth but not mirror-true: its grips carry slow ripples a few degrees deep (a few hundred art px
long), which the white default's bright diffuse hides but every darker, saturated, glossy or metallic variant shows as a
wavy S-shaped reflection of the studio's soft boxes. Only the shading normals change (the vertices stay where they are,
so the outline, the masks and the sprites' cut lines are the model's): the vertex normals are low-passed over the mesh
(repeated neighbour averaging, at three scales up to about a grip's width), and each corner's normal moves to the
low-passed ones where the surface faces the camera and they stay close to the model's; the side walls (seen edge on),
the corners round the button holes and the rims next to the mesh's open edges keep the model's normals, with soft
transitions.
"""
import numpy as np

SHELLS = ("shell-left", "shell-right")
# neighbour-averaging rounds (the edges are ~0.05 units; the ripples ~1.5 units long), finest first: each scale's
# normal is taken where it stays within DEVIATION of the model's, so the coarse one calms the broad grips and the finer
# ones the tighter curves (the shoulders) where the coarse one strays too far
SCALES = (300, 1000, 3000)
DEVIATION = (15.0, 35.0)   # degrees from the low-passed normal: below the first it moves fully, above the second not
FACING = (0.3, 0.6)        # the corner normal's z (towards the camera): below the first it keeps the model's
EDGE_HOPS = 10             # edges from the mesh's open edges over which the move fades in


def _smoothstep(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


def _unit(v):
    return v / np.maximum(np.linalg.norm(v, axis=1, keepdims=True), 1e-12)


def smooth(me, scales=SCALES):
    """Replaces the mesh's custom normals; returns the old corner normals (for restore())."""
    nl, nv = len(me.loops), len(me.vertices)
    ln = np.empty(nl * 3, np.float32)
    me.corner_normals.foreach_get("vector", ln)
    ln = ln.reshape(-1, 3).astype(np.float64)
    lv = np.empty(nl, np.int64)
    me.loops.foreach_get("vertex_index", lv)
    le = np.empty(nl, np.int64)
    me.loops.foreach_get("edge_index", le)
    ev = np.empty(len(me.edges) * 2, np.int64)
    me.edges.foreach_get("vertices", ev)
    ev = ev.reshape(-1, 2)
    a, b = ev[:, 0], ev[:, 1]

    vn = np.zeros((nv, 3))
    for k in range(3):
        vn[:, k] = np.bincount(lv, ln[:, k], nv)
    s = _unit(vn)
    smoothed = []
    for i in range(max(scales)):
        acc = s.copy()
        for k in range(3):
            acc[:, k] += np.bincount(a, s[b, k], nv) + np.bincount(b, s[a, k], nv)
        s = _unit(acc)
        if i + 1 in scales:
            smoothed.append(s)

    # hops from the open edges (an edge used by one face)
    hop = np.full(nv, float(EDGE_HOPS))
    hop[np.unique(ev[np.bincount(le, minlength=len(me.edges)) == 1].ravel())] = 0
    for _ in range(EDGE_HOPS):
        nb = np.minimum(hop[a], hop[b]) + 1
        np.minimum.at(hop, a, nb)
        np.minimum.at(hop, b, nb)

    keep = _smoothstep((ln[:, 2] - FACING[0]) / (FACING[1] - FACING[0])) * np.clip(hop[lv] / EDGE_HOPS, 0, 1)
    out = ln
    for s in smoothed:
        sl = s[lv]
        dev = np.degrees(np.arccos(np.clip((ln * sl).sum(1), -1, 1)))
        w = keep * _smoothstep((DEVIATION[1] - dev) / (DEVIATION[1] - DEVIATION[0]))
        out = _unit(out * (1 - w[:, None]) + sl * w[:, None])
    me.normals_split_custom_set(out.astype(np.float32).tolist())
    return ln


def apply(objects, names=SHELLS):
    """Smooths the named meshes' normals; returns {object name: old corner normals}."""
    return {o.name: smooth(o.data) for o in objects if o.type == "MESH" and o.name.startswith(names)}


def restore(objects, saved):
    for o in objects:
        if o.name in saved:
            o.data.normals_split_custom_set(saved[o.name].astype(np.float32).tolist())

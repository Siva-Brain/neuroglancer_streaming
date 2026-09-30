"""LOD mesh generation and binary serialization.

A "chunk" here is one anatomical region. Each chunk can be produced at three
levels of detail (LOD0/1/2) by changing the tessellation of its ellipsoid.

Binary chunk format (little-endian), see docs/protocol.md:

    offset  type              meaning
    0       char[4]           magic  'BRN1'
    4       uint32            vertex_count  (V)
    8       uint32            index_count   (I)
    12      float32[V*3]      positions  (x, y, z) ...
    12+V*12 uint32[I]         triangle indices

Normals and colors are intentionally NOT in the payload: the client computes
vertex normals locally and takes the flat region color from chunk metadata.
This keeps payloads small and the parser trivial.
"""
from __future__ import annotations

import struct
import numpy as np

# slices (longitude) x stacks (latitude) per LOD
LOD_CONFIGS = {
    0: (8, 6),    # very low
    1: (18, 12),  # medium
    2: (36, 24),  # high
}
MAX_LOD = max(LOD_CONFIGS)
MAGIC = b"BRN1"


def make_ellipsoid(center, radii, slices, stacks):
    """Return (positions [V,3] float32, indices [I] uint32) for a UV ellipsoid."""
    center = np.asarray(center, dtype=np.float64)
    radii = np.asarray(radii, dtype=np.float64)

    u = np.linspace(0.0, 2.0 * np.pi, slices + 1)      # longitude
    v = np.linspace(0.0, np.pi, stacks + 1)            # latitude
    uu, vv = np.meshgrid(u, v)                          # (stacks+1, slices+1)

    x = np.sin(vv) * np.cos(uu)
    y = np.cos(vv)
    z = np.sin(vv) * np.sin(uu)
    pos = np.stack([x, y, z], axis=-1) * radii          # broadcast (3,)
    pos = pos.reshape(-1, 3) + center

    # triangle indices over the (stacks x slices) grid
    row = slices + 1
    i = np.arange(stacks)[:, None]
    j = np.arange(slices)[None, :]
    a = (i * row + j).ravel()
    b = a + row
    tris = np.empty((a.size, 6), dtype=np.uint32)
    tris[:, 0] = a
    tris[:, 1] = b
    tris[:, 2] = a + 1
    tris[:, 3] = a + 1
    tris[:, 4] = b
    tris[:, 5] = b + 1
    return pos.astype(np.float32), tris.ravel().astype(np.uint32)


def serialize_mesh(pos: np.ndarray, idx: np.ndarray) -> bytes:
    header = MAGIC + struct.pack("<II", len(pos), len(idx))
    return header + pos.astype("<f4").tobytes() + idx.astype("<u4").tobytes()


def bbox(pos: np.ndarray):
    mn = pos.min(axis=0).tolist()
    mx = pos.max(axis=0).tolist()
    return {"min": mn, "max": mx}

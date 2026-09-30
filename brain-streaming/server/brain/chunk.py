"""Chunk catalog: builds all region meshes at all LODs and answers queries.

Everything is generated once at startup and cached in memory as ready-to-send
binary blobs, so request handlers only look up bytes (plus an optional delay).
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Dict, List

import numpy as np

from .lod import LOD_CONFIGS, MAX_LOD, make_ellipsoid, serialize_mesh, bbox
from .synthetic_brain import REGIONS, Region

# how many nearest visible chunks get promoted to the top LOD
TOP_LOD_BUDGET = 4


@dataclass
class ChunkMeta:
    chunk_id: str
    center: list
    rotation: list          # quaternion [x,y,z,w]; identity for now
    color: list
    dependencies: list
    lods: Dict[int, dict]   # lod -> {"bounds":..., "vertex_count":.., "bytes":..}


class Catalog:
    def __init__(self):
        self._meta: Dict[str, ChunkMeta] = {}
        self._blobs: Dict[tuple, bytes] = {}   # (chunk_id, lod) -> bytes
        self._build()

    def _build(self):
        for r in REGIONS:
            lods = {}
            for lod, (slices, stacks) in LOD_CONFIGS.items():
                pos, idx = make_ellipsoid(r.center, r.radii, slices, stacks)
                blob = serialize_mesh(pos, idx)
                self._blobs[(r.chunk_id, lod)] = blob
                lods[lod] = {
                    "bounds": bbox(pos),
                    "vertex_count": int(len(pos)),
                    "triangle_count": int(len(idx) // 3),
                    "bytes": len(blob),
                }
            self._meta[r.chunk_id] = ChunkMeta(
                chunk_id=r.chunk_id,
                center=list(r.center),
                rotation=[0.0, 0.0, 0.0, 1.0],
                color=list(r.color),
                dependencies=list(r.dependencies),
                lods=lods,
            )

    # ---- queries -------------------------------------------------------
    def info(self) -> dict:
        allmin = np.array([m["bounds"]["min"] for meta in self._meta.values()
                           for m in [meta.lods[MAX_LOD]]])
        allmax = np.array([meta.lods[MAX_LOD]["bounds"]["max"]
                           for meta in self._meta.values()])
        return {
            "name": "synthetic-hello-world-brain",
            "coordinate_frame": "+X right, +Y up, +Z forward (mm-ish)",
            "lods": sorted(LOD_CONFIGS.keys()),
            "max_lod": MAX_LOD,
            "chunk_count": len(self._meta),
            "chunk_ids": list(self._meta.keys()),
            "bounds": {"min": allmin.min(axis=0).tolist(),
                       "max": allmax.max(axis=0).tolist()},
        }

    def chunks(self) -> List[dict]:
        return [self._meta_dict(m) for m in self._meta.values()]

    def _meta_dict(self, m: ChunkMeta) -> dict:
        return {
            "chunk_id": m.chunk_id,
            "center": m.center,
            "rotation": m.rotation,
            "color": m.color,
            "dependencies": m.dependencies,
            "lods": {str(k): v for k, v in m.lods.items()},
        }

    def has(self, chunk_id: str, lod: int) -> bool:
        return (chunk_id, lod) in self._blobs

    def blob(self, chunk_id: str, lod: int) -> bytes:
        return self._blobs[(chunk_id, lod)]

    # ---- camera-driven prioritization ---------------------------------
    def prioritize(self, position, forward, fov_deg) -> List[dict]:
        """Given a camera, return chunks ordered by importance with a target LOD.

        Rule (kept deliberately simple for the prototype):
          - a chunk is "visible" if it's within the forward cone
          - visible chunks come first, nearest-first
          - the nearest few visible chunks target the top LOD, the rest LOD1
          - non-visible chunks target LOD0 (kept coarse)
        """
        pos = np.asarray(position, dtype=np.float64)
        fwd = np.asarray(forward, dtype=np.float64)
        n = np.linalg.norm(fwd)
        fwd = fwd / n if n > 1e-6 else np.array([0.0, 0.0, -1.0])
        half = math.radians(min(max(fov_deg, 10.0), 120.0)) * 0.5
        cos_cone = math.cos(min(half * 1.6, math.radians(85.0)))  # generous cone

        rows = []
        for m in self._meta.values():
            to = np.asarray(m.center, dtype=np.float64) - pos
            dist = float(np.linalg.norm(to))
            d = to / dist if dist > 1e-6 else fwd
            visible = float(np.dot(fwd, d)) >= cos_cone
            rows.append({"chunk_id": m.chunk_id, "distance": dist, "visible": visible})

        visible = sorted([r for r in rows if r["visible"]], key=lambda r: r["distance"])
        hidden = sorted([r for r in rows if not r["visible"]], key=lambda r: r["distance"])

        out = []
        for rank, r in enumerate(visible):
            target = MAX_LOD if rank < TOP_LOD_BUDGET else 1
            out.append({**r, "target_lod": target, "priority": rank})
        base = len(visible)
        for k, r in enumerate(hidden):
            out.append({**r, "target_lod": 0, "priority": base + k})
        return out


catalog = Catalog()

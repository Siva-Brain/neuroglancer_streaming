"""Camera-driven chunk selection over the real dataset.

World space = millimetres, brain centred at the origin. Data axes (z,y,x) map to
world (z,y,x); the physical extent comes from the OME voxel sizes, so every
pyramid level lands in the same world box.

Priority (kept deliberately simple for the prototype):
  1. chunks inside the current view cone
  2. chunks closest to the camera
  3. a finer target LOD when the camera is near, coarser when far
  4. neighbouring (out-of-cone) chunks at lower priority

`min_level` clamps how fine we will stream: level 0 has 2088 shards (~1 TB
full), so the prototype streams no finer than `min_level` (default 3). Raise it
later once bandwidth/rendering are proven.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

import numpy as np

from datasource.base import BrainDataSource, DatasetInfo, LevelInfo


@dataclass
class SelectedChunk:
    chunk_id: str
    level: int
    coords: Tuple[int, int, int, int]
    center_mm: Tuple[float, float, float]   # world (x,y,z)
    distance: float
    visible: bool
    priority: int


class CameraViewSelector:
    def __init__(self, source: BrainDataSource, min_level: int = 3, budget: int = 24):
        self.source = source
        self.info: DatasetInfo = source.get_metadata()
        self.min_level = max(min_level, self.info.finest_level)
        self.budget = budget
        ez, ey, ex = (e * 1000.0 for e in self.info.extent_m)  # mm
        self.extent_mm = (ez, ey, ex)
        self.diag_mm = max(ex, ey, ez)

    # ---- geometry -----------------------------------------------------------
    def _level(self, lvl: int) -> LevelInfo:
        for l in self.info.levels:
            if l.level == lvl:
                return l
        raise KeyError(lvl)

    def shard_center_mm(self, li: LevelInfo, coords) -> Tuple[float, float, float]:
        cz, cy, cx, _ = li.chunk_shape
        vz, vy, vx, _ = li.voxel_size_m
        zi, yi, xi, _ = coords
        sz, sy, sx, _ = li.shape
        # voxel-space center of the shard (clipped to array bounds)
        cvz = min((zi + 0.5) * cz, sz - 0.5)
        cvy = min((yi + 0.5) * cy, sy - 0.5)
        cvx = min((xi + 0.5) * cx, sx - 0.5)
        # metres -> mm, centred
        z = cvz * vz * 1000.0 - self.extent_mm[0] / 2
        y = cvy * vy * 1000.0 - self.extent_mm[1] / 2
        x = cvx * vx * 1000.0 - self.extent_mm[2] / 2
        return (x, y, z)   # world (x,y,z)

    def target_level(self, dist_mm: float) -> int:
        t = min(dist_mm / (2.0 * self.diag_mm), 1.0)
        lvl = round(self.min_level + t * (self.info.coarsest_level - self.min_level))
        return int(max(self.min_level, min(self.info.coarsest_level, lvl)))

    # ---- selection ----------------------------------------------------------
    def select(self, position, forward, fov_deg=60.0, aspect=1.777) -> List[SelectedChunk]:
        pos = np.asarray(position, float)
        fwd = np.asarray(forward, float)
        n = np.linalg.norm(fwd)
        fwd = fwd / n if n > 1e-6 else np.array([0, 0, -1.0])

        dist_center = float(np.linalg.norm(pos))  # brain center at origin
        lvl = self.target_level(dist_center)
        li = self._level(lvl)
        gz, gy, gx, gc = li.grid

        half = math.radians(min(max(fov_deg, 20.0), 120.0)) * 0.5
        cos_cone = math.cos(min(half * 1.7, math.radians(85.0)))

        rows: List[SelectedChunk] = []
        for zi in range(gz):
            for yi in range(gy):
                for xi in range(gx):
                    coords = (zi, yi, xi, 0)
                    c = np.array(self.shard_center_mm(li, coords))
                    to = c - pos
                    d = float(np.linalg.norm(to))
                    dirn = to / d if d > 1e-6 else fwd
                    visible = float(np.dot(fwd, dirn)) >= cos_cone
                    cid = f"L{lvl}.{zi}.{yi}.{xi}.0"
                    rows.append(SelectedChunk(cid, lvl, coords, tuple(c), d, visible, 0))

        vis = sorted([r for r in rows if r.visible], key=lambda r: r.distance)
        hid = sorted([r for r in rows if not r.visible], key=lambda r: r.distance)
        ordered = vis + hid
        for i, r in enumerate(ordered):
            r.priority = i
        return ordered[: self.budget]

    def baseline_chunks(self) -> List[str]:
        """The coarse full-brain set the client loads first (whole coarsest level)."""
        li = self._level(self.info.coarsest_level)
        gz, gy, gx, _ = li.grid
        return [f"L{li.level}.{zi}.{yi}.{xi}.0"
                for zi in range(gz) for yi in range(gy) for xi in range(gx)]

"""Camera-driven chunk selection over the real dataset.

World space = millimetres, brain centred at the origin. Data axes (z,y,x) map to
world (z,y,x); the physical extent comes from the OME voxel sizes, so every
pyramid level lands in the same world box.

Selection is **Neuroglancer-style screen-space**:

  1. LOD is chosen so that one voxel projects to ~`target_px` screen pixels
     (`screen_space_level`): zoom in -> finer level, zoom out -> coarser. This
     replaces the old distance-bucketed `target_level` heuristic.
  2. Visibility is an **aspect-correct view frustum** test with a per-shard
     bounding sphere (`_frustum`), replacing the old single forward-cone.
  3. Within the chosen level, visible shards are ordered nearest-first, then
     out-of-frustum neighbours (lower priority) so the client can prefetch.

`min_level` clamps how fine we will stream: level 0 is ~1 TB full, so the
prototype streams no finer than `min_level`. Lower it (e.g. to 0/1) to inspect
Nissl grain in an ROI.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import List, Optional, Tuple

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


def _unit(v: np.ndarray, fallback: np.ndarray) -> np.ndarray:
    n = float(np.linalg.norm(v))
    return v / n if n > 1e-9 else fallback


class CameraViewSelector:
    def __init__(self, source: BrainDataSource, min_level: int = 3, budget: int = 24,
                 target_px: float = 1.0):
        self.source = source
        self.info: DatasetInfo = source.get_metadata()
        self.min_level = max(min_level, self.info.finest_level)
        self.budget = budget
        # desired on-screen size of one voxel, in pixels. ~1 = voxel≈pixel (crisp,
        # most data); raise (2-4) to stream less; lower (<1) to oversample.
        self.target_px = max(target_px, 0.1)
        ez, ey, ex = (e * 1000.0 for e in self.info.extent_m)  # mm
        self.extent_mm = (ez, ey, ex)
        self.diag_mm = max(ex, ey, ez)

    # ---- geometry -----------------------------------------------------------
    def _level(self, lvl: int) -> LevelInfo:
        for l in self.info.levels:
            if l.level == lvl:
                return l
        raise KeyError(lvl)

    def _level_or_none(self, lvl: int) -> Optional[LevelInfo]:
        for l in self.info.levels:
            if l.level == lvl:
                return l
        return None

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

    def shard_radius_mm(self, li: LevelInfo) -> float:
        """Bounding-sphere radius (mm) of one shard at this level."""
        cz, cy, cx, _ = li.chunk_shape
        vz, vy, vx, _ = li.voxel_size_m
        dz, dy, dx = cz * vz * 1000.0, cy * vy * 1000.0, cx * vx * 1000.0
        return 0.5 * math.sqrt(dz * dz + dy * dy + dx * dx)

    # ---- screen-space LOD ---------------------------------------------------
    def screen_space_level(self, dist_mm: float, viewport_h: float, fov_deg: float) -> int:
        """Coarsest level whose voxel still projects to <= target_px at `dist_mm`.

        proj_px(voxel) = voxel_mm * focal_px / dist, focal_px = 0.5*H / tan(fov/2).
        Scan coarse -> fine and stop at the first level fine enough (least data);
        if even the finest allowed level is too coarse, clamp to min_level.
        """
        half_v = math.tan(math.radians(min(max(fov_deg, 20.0), 120.0)) * 0.5)
        focal_px = 0.5 * max(viewport_h, 1.0) / max(half_v, 1e-6)
        dist = max(dist_mm, 1e-3)
        chosen = self.info.coarsest_level
        for lvl in range(self.info.coarsest_level, self.min_level - 1, -1):
            li = self._level_or_none(lvl)
            if li is None:
                continue
            voxel_mm = li.voxel_size_m[2] * 1000.0        # finest (x) axis
            proj_px = voxel_mm * focal_px / dist
            chosen = lvl
            if proj_px <= self.target_px:
                break
        return int(max(self.min_level, min(self.info.coarsest_level, chosen)))

    # ---- frustum ------------------------------------------------------------
    def _basis(self, forward):
        fwd = _unit(np.asarray(forward, float), np.array([0, 0, -1.0]))
        up_world = np.array([0.0, 1.0, 0.0])
        if abs(float(np.dot(fwd, up_world))) > 0.99:
            up_world = np.array([0.0, 0.0, 1.0])
        right = _unit(np.cross(fwd, up_world), np.array([1.0, 0.0, 0.0]))
        up = np.cross(right, fwd)
        return fwd, right, up

    @staticmethod
    def _in_frustum(to, fwd, right, up, half_h, half_v, radius, far):
        """Aspect-correct frustum test in view space with a bounding-sphere slack."""
        z = float(np.dot(to, fwd))
        if z < -radius or z > far + radius:
            return False
        x = float(np.dot(to, right))
        y = float(np.dot(to, up))
        sx = half_h * z + radius * math.sqrt(1.0 + half_h * half_h)
        sy = half_v * z + radius * math.sqrt(1.0 + half_v * half_v)
        return abs(x) <= sx and abs(y) <= sy

    # ---- selection ----------------------------------------------------------
    def select(self, position, forward, fov_deg: float = 60.0, aspect: float = 1.777,
               viewport_h: float = 1080.0, level_min: Optional[int] = None,
               level_max: Optional[int] = None, whole: bool = False) -> List[SelectedChunk]:
        pos = np.asarray(position, float)
        fwd, right, up = self._basis(forward)
        half_v = math.tan(math.radians(min(max(fov_deg, 20.0), 120.0)) * 0.5)
        half_h = half_v * max(aspect, 1e-3)
        # far plane must clear the camera: the brain sits at the origin, so a
        # camera `|pos|` away needs far >= |pos| + brain reach (not just diag).
        far = float(np.linalg.norm(pos)) + self.diag_mm * 2.0

        # 1) nearest visible distance, estimated on the coarsest grid (shard
        #    centres barely move between levels, so this is a cheap proxy that
        #    then drives the screen-space level choice).
        coarse = self._level(self.info.coarsest_level)
        crad = self.shard_radius_mm(coarse)
        gz, gy, gx, _ = coarse.grid
        near_dist = float(np.linalg.norm(pos)) or self.diag_mm
        any_vis = False
        for zi in range(gz):
            for yi in range(gy):
                for xi in range(gx):
                    c = np.array(self.shard_center_mm(coarse, (zi, yi, xi, 0)))
                    to = c - pos
                    if self._in_frustum(to, fwd, right, up, half_h, half_v, crad, far):
                        d = float(np.linalg.norm(to))
                        if not any_vis or d < near_dist:
                            near_dist = d
                            any_vis = True
        if not any_vis:
            near_dist = float(np.linalg.norm(pos)) or self.diag_mm

        # 2) screen-space level for that distance, then the optional UI clamp:
        #    restrict to [level_min, level_max]; equal bounds pin a single layer.
        lvl = self.screen_space_level(near_dist, viewport_h, fov_deg)
        lo = self.min_level if level_min is None else int(level_min)
        hi = self.info.coarsest_level if level_max is None else int(level_max)
        lo = max(self.min_level, min(lo, self.info.coarsest_level))
        hi = max(self.min_level, min(hi, self.info.coarsest_level))
        if lo > hi:
            lo, hi = hi, lo
        lvl = max(lo, min(hi, lvl))
        li = self._level(lvl)
        rad = self.shard_radius_mm(li)
        gz, gy, gx, gc = li.grid

        # 3) enumerate the chosen level, frustum-cull, order nearest-first.
        rows: List[SelectedChunk] = []
        for zi in range(gz):
            for yi in range(gy):
                for xi in range(gx):
                    coords = (zi, yi, xi, 0)
                    c = np.array(self.shard_center_mm(li, coords))
                    to = c - pos
                    d = float(np.linalg.norm(to))
                    visible = self._in_frustum(to, fwd, right, up, half_h, half_v, rad, far)
                    cid = f"L{lvl}.{zi}.{yi}.{xi}.0"
                    rows.append(SelectedChunk(cid, lvl, coords, tuple(c), d, visible, 0))

        vis = sorted([r for r in rows if r.visible], key=lambda r: r.distance)
        hid = sorted([r for r in rows if not r.visible], key=lambda r: r.distance)
        ordered = vis + hid
        for i, r in enumerate(ordered):
            r.priority = i
        # `whole` = load the entire level (every shard), not just the frustum set,
        # so the whole brain stays resident while orbiting. Still ordered
        # nearest-first so the in-view part arrives before the rest fills in.
        # Only sane at coarse levels; pair with a coarse level clamp.
        return ordered if whole else ordered[: self.budget]

    def baseline_chunks(self) -> List[str]:
        """The coarse full-brain set the client loads first (whole coarsest level)."""
        li = self._level(self.info.coarsest_level)
        gz, gy, gx, _ = li.grid
        return [f"L{li.level}.{zi}.{yi}.{xi}.0"
                for zi in range(gz) for yi in range(gy) for xi in range(gx)]

"""ChunkManager: the DGX-side owner of chunk state.

- fetches chunks through the BrainDataSource (never Zarr directly),
- caches decoded chunks (LRU), de-duplicates concurrent requests,
- tracks per-chunk records (status/size/last_access),
- serializes a chunk to the compact 'BVX1' voxel-brick payload for the client.

Payload 'BVX1' (little-endian) -- a raw voxel brick, channel-selectable:
    char[4] 'BVX1'
    u8   level
    u8   n_channels
    u16  reserved(0)
    i32  z0, y0, x0          voxel origin in the level array
    u16  dz, dy, dx          brick voxel dimensions
    u8[dz*dy*dx*n_channels]  voxels in (z,y,x,c) order
The client uploads this straight into a 3D texture brick (Phase 8/9).
"""
from __future__ import annotations

import struct
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Tuple

import numpy as np

from datasource.base import BrainDataSource, ChunkData
from .cache import LruChunkCache

_MAGIC = b"BVX2"   # v2: adds world bbox (6 f32 mm) so resampled bricks stay correct


@dataclass
class ChunkRecord:
    chunk_id: str
    level: int
    coords: Tuple[int, int, int, int]
    status: str = "unknown"          # unknown|fetching|ready|error|empty
    size_bytes: int = 0              # compressed source bytes
    voxel_bytes: int = 0             # decoded bytes
    last_access: float = 0.0
    error: Optional[str] = None


class ChunkManager:
    def __init__(self, source: BrainDataSource, cache: Optional[LruChunkCache] = None,
                 workers: int = 32, processor=None, max_xy: int = 0):
        self.source = source
        # NB: LruChunkCache defines __len__, so an empty one is falsy -> must
        # test `is not None`, not `cache or ...` (that would drop a passed cache).
        self.cache = cache if cache is not None else LruChunkCache()
        self.processor = processor          # optional GpuProcessor
        self.max_xy = max_xy                # 0 = no server-side resample
        # Nissl glass-brain preprocessing (per-block intensity window for the
        # gray channel; None until computed at startup -> identity/no-normalize).
        self.norm_lohi: Optional[Tuple[float, float]] = None
        self._records: Dict[str, ChunkRecord] = {}
        self._locks: Dict[str, threading.Lock] = {}
        self._glock = threading.Lock()
        self.total_source_bytes = 0
        self._pool = ThreadPoolExecutor(max_workers=workers)
        self.prefetch_submitted = 0
        # geometry for world-bbox (read once)
        info = source.get_metadata()
        self._extent_m = info.extent_m                                  # (z,y,x)
        self._voxel = {l.level: l.voxel_size_m for l in info.levels}    # level -> (z,y,x,c)

    # ---- record bookkeeping -------------------------------------------------
    def _record(self, chunk_id: str, level: int, coords) -> ChunkRecord:
        with self._glock:
            r = self._records.get(chunk_id)
            if r is None:
                r = ChunkRecord(chunk_id, level, coords)
                self._records[chunk_id] = r
            return r

    def _lock_for(self, chunk_id: str) -> threading.Lock:
        with self._glock:
            lk = self._locks.get(chunk_id)
            if lk is None:
                lk = threading.Lock()
                self._locks[chunk_id] = lk
            return lk

    # ---- fetch (dedup + cache) ---------------------------------------------
    def get(self, level: int, coords: Tuple[int, int, int, int]) -> ChunkData:
        chunk_id = f"L{level}.{coords[0]}.{coords[1]}.{coords[2]}.{coords[3]}"
        r = self._record(chunk_id, level, coords)
        r.last_access = time.time()

        cached = self.cache.get(chunk_id)
        if cached is not None:
            r.status = "ready" if cached.array.any() else "empty"
            return cached

        # single-flight per chunk_id
        with self._lock_for(chunk_id):
            cached = self.cache.get(chunk_id)
            if cached is not None:
                return cached
            r.status = "fetching"
            try:
                cd = self.source.get_chunk(level, coords)
            except Exception as e:  # noqa
                r.status = "error"; r.error = str(e)
                raise
            r.status = "empty" if cd.source_bytes == 0 else "ready"
            r.size_bytes = cd.source_bytes
            r.voxel_bytes = int(cd.array.nbytes)
            self.total_source_bytes += cd.source_bytes
            self.cache.put(chunk_id, cd)
            return cd

    def is_cached(self, chunk_id: str) -> bool:
        return self.cache.get(chunk_id) is not None

    # ---- parallel prefetch (hides HTTP latency using many workers) ----------
    def get_many(self, specs: List[Tuple[int, Tuple[int, int, int, int]]]) -> List[ChunkData]:
        """Fetch/cache many chunks concurrently; returns them in order."""
        return list(self._pool.map(lambda s: self.get(*s), specs))

    def prefetch(self, specs: List[Tuple[int, Tuple[int, int, int, int]]]) -> int:
        """Fire-and-forget warm the cache for these chunks. Returns #submitted."""
        n = 0
        for level, coords in specs:
            cid = f"L{level}.{coords[0]}.{coords[1]}.{coords[2]}.{coords[3]}"
            if self.is_cached(cid):
                continue
            self._pool.submit(self._safe_get, level, coords)
            n += 1
        self.prefetch_submitted += n
        return n

    def _safe_get(self, level, coords):
        try:
            self.get(level, coords)
        except Exception:  # noqa
            pass

    # ---- world geometry -----------------------------------------------------
    def world_bbox_mm(self, level: int, origin_vox, dims_vox) -> Tuple[float, ...]:
        """Return (xmin,ymin,zmin,xmax,ymax,zmax) mm for the (unresampled) brick."""
        vz, vy, vx, _ = self._voxel[level]
        Ez, Ey, Ex = self._extent_m
        z0, y0, x0, _ = origin_vox
        dz, dy, dx = dims_vox
        xmin = (x0 * vx - Ex / 2) * 1000.0; xmax = ((x0 + dx) * vx - Ex / 2) * 1000.0
        ymin = (y0 * vy - Ey / 2) * 1000.0; ymax = ((y0 + dy) * vy - Ey / 2) * 1000.0
        zmin = (z0 * vz - Ez / 2) * 1000.0; zmax = ((z0 + dz) * vz - Ez / 2) * 1000.0
        return (xmin, ymin, zmin, xmax, ymax, zmax)

    # ---- serialization (BVX2: world bbox + optional GPU resample) -----------
    def serialize(self, cd: ChunkData, channels: Optional[List[int]] = None,
                  max_xy: Optional[int] = None, glass: bool = False) -> bytes:
        arr = cd.array
        odz, ody, odx, _ = arr.shape                      # ORIGINAL dims -> world bbox
        if channels is not None:
            arr = arr[:, :, :, channels]
        arr = np.ascontiguousarray(arr, dtype=np.uint8)

        cap = self.max_xy if max_xy is None else max_xy
        if cap and self.processor is not None and max(ody, odx) > cap:
            arr = np.ascontiguousarray(self.processor.resample_xy_max(arr, cap), dtype=np.uint8)

        # Nissl see-through DVR: normalize gray + append precomputed gradient so
        # the client gets [gray, mask, grad] (3 ch). Done after resample so the
        # gradient is taken at the resolution actually displayed. Needs the gray
        # and mask channels (the viewer requests channels=[0,3]).
        if glass and self.processor is not None and arr.shape[-1] >= 2:
            arr = np.ascontiguousarray(
                self.processor.glass_pack(arr, gray_idx=0, mask_idx=1,
                                          lohi=self.norm_lohi), dtype=np.uint8)

        dz, dy, dx, nc = arr.shape
        oz, oy, ox, _ = cd.origin_vox
        bbox = self.world_bbox_mm(cd.level, cd.origin_vox, (odz, ody, odx))
        header = (_MAGIC + struct.pack("<BBH", cd.level, nc, 0)
                  + struct.pack("<iii", oz, oy, ox)
                  + struct.pack("<HHH", dz, dy, dx)
                  + struct.pack("<6f", *bbox))
        return header + arr.tobytes()

    # ---- introspection ------------------------------------------------------
    def records(self) -> List[ChunkRecord]:
        with self._glock:
            return list(self._records.values())

    def stats(self) -> dict:
        by_status: Dict[str, int] = {}
        for r in self._records.values():
            by_status[r.status] = by_status.get(r.status, 0) + 1
        return {
            "tracked": len(self._records),
            "by_status": by_status,
            "total_source_bytes": self.total_source_bytes,
            "cache": self.cache.stats(),
        }

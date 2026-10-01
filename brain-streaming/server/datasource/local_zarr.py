"""LocalZarrBrainSource -- reads a sharded Zarr v3 dataset from a locally-mounted
path (e.g. DDN) via file I/O instead of HTTP range GETs.

Handles BOTH layouts we serve:
  * 4D intensity volumes (z,y,x,c)  -- fused RGB, Nissl gray+mask  (chunk key .../c/z/y/x/c)
  * 3D label volumes    (z,y,x)     -- segmentation / region ids   (chunk key .../c/z/y/x)

The on-disk layout matches the HTTP server's URL layout, so for 4D volumes we
inherit HttpZarrBrainSource's metadata/sharding/decode unchanged and only swap
the four I/O primitives for file reads. For 3D label volumes we present a
synthetic channel axis of size 1 so the rest of the server (ChunkManager,
serialize, world geometry) treats them uniformly as (z,y,x,1) uint8.

See docs/performance-plan.md (Item 1): file pread is sub-ms vs ~170-350 ms HTTP.
"""
from __future__ import annotations

import json
import math
import os
import time
from typing import List, Optional, Tuple

import numpy as np
from numcodecs import Blosc

from .http_zarr import HttpZarrBrainSource
from .base import DatasetInfo, LevelInfo, ChunkData

_blosc = Blosc()


class LocalZarrBrainSource(HttpZarrBrainSource):
    def __init__(self, root_path: str,
                 voxel_um_finest: Optional[Tuple[float, float, float]] = None):
        self.root = os.path.abspath(os.path.expanduser(root_path)).rstrip("/")
        self.timeout = 0.0
        self.voxel_um_finest = voxel_um_finest
        self._session = None
        self._info = None
        self._level_meta = {}
        self._cdim = True                    # does the array have an explicit channel axis?

    # ---- I/O primitives (file instead of HTTP) ------------------------------
    def _get_json(self, path: str) -> dict:
        with open(path, "r") as f:
            return json.load(f)

    def _range_tail(self, path: str, n: int) -> Optional[bytes]:
        if not os.path.exists(path):
            return None                      # missing shard == all fill (like 404)
        with open(path, "rb") as f:
            size = os.fstat(f.fileno()).st_size
            f.seek(max(0, size - n))
            return f.read(n)

    def _range(self, path: str, off: int, n: int) -> bytes:
        with open(path, "rb") as f:
            f.seek(off)
            return f.read(n)

    # ---- metadata (adds a size-1 channel axis for 3D label volumes) ---------
    def get_metadata(self) -> DatasetInfo:
        if self._info is not None:
            return self._info
        root = self._get_json(f"{self.root}/zarr.json")
        ome = root["attributes"]["ome"]["multiscales"][0]
        axes = [a["name"] for a in ome["axes"]]
        self._cdim = "c" in axes
        datasets = ome["datasets"]

        levels: List[LevelInfo] = []
        for ds in datasets:
            lvl = int(ds["path"])
            meta = self._get_json(f"{self.root}/{lvl}/zarr.json")
            self._level_meta[lvl] = meta
            shape = tuple(meta["shape"])
            cshape = tuple(meta["chunk_grid"]["configuration"]["chunk_shape"])
            scale = list(ds["coordinateTransformations"][0]["scale"])  # (z,y,x[,c]) metres
            if not self._cdim:               # 3D -> synthesize channel axis of size 1
                shape = shape + (1,)
                cshape = cshape + (1,)
                scale = scale + [1.0]
            grid = tuple(int(math.ceil(s / c)) for s, c in zip(shape, cshape))
            levels.append(LevelInfo(lvl, shape, cshape, tuple(scale), grid))

        levels.sort(key=lambda l: l.level)
        finest = min(levels, key=lambda l: l.voxel_size_m[2]).level
        coarsest = max(levels, key=lambda l: l.voxel_size_m[2]).level

        if self.voxel_um_finest is not None:
            fin = next(l for l in levels if l.level == finest)
            tz, ty, tx = (v * 1e-6 for v in self.voxel_um_finest)
            fz, fy, fx, _ = fin.voxel_size_m
            kz = tz / fz if fz else 1.0
            ky = ty / fy if fy else 1.0
            kx = tx / fx if fx else 1.0
            levels = [LevelInfo(l.level, l.shape, l.chunk_shape,
                                (l.voxel_size_m[0] * kz, l.voxel_size_m[1] * ky,
                                 l.voxel_size_m[2] * kx, l.voxel_size_m[3]), l.grid)
                      for l in levels]

        z, y, x, c = levels[0].shape
        vz, vy, vx, _ = levels[0].voxel_size_m
        self._info = DatasetInfo(
            name=ome.get("name", "brain"),
            dtype=self._level_meta[finest]["data_type"],
            axes=axes if self._cdim else axes + ["c"],
            channels=levels[0].shape[3],
            levels=levels, finest_level=finest, coarsest_level=coarsest,
            extent_m=(z * vz, y * vy, x * vx),
            attrs={k: v for k, v in root["attributes"].items() if k != "ome"},
        )
        return self._info

    # ---- shard path + decode (channel coord omitted for 3D) -----------------
    def _chunk_path(self, level: int, zi: int, yi: int, xi: int, ci: int) -> str:
        if self._cdim:
            return f"{self.root}/{level}/c/{zi}/{yi}/{xi}/{ci}"
        return f"{self.root}/{level}/c/{zi}/{yi}/{xi}"

    def get_chunk(self, level: int, coords: Tuple[int, int, int, int]) -> ChunkData:
        li = self.level_info(level)
        z, y, x, c = coords
        path = self._chunk_path(level, z, y, x, c)

        t0 = time.time()
        if not os.path.exists(path):
            arr = self._empty_block(li, coords)
            return ChunkData(level, coords, arr, self._origin(li, coords), 0,
                             time.time() - t0, 0.0)
        with open(path, "rb") as f:
            shard = f.read()
        fetch_s = time.time() - t0

        t1 = time.time()
        arr = self._decode_shard(shard, li, coords)
        decode_s = time.time() - t1
        return ChunkData(level, coords, arr, self._origin(li, coords),
                         len(shard), fetch_s, decode_s)

    def _inner_shape(self, li: LevelInfo) -> Tuple[int, int, int, int]:
        for codec in self._level_meta[li.level].get("codecs", []):
            if codec.get("name") == "sharding_indexed":
                sh = tuple(codec["configuration"]["chunk_shape"])
                return sh if self._cdim else sh + (1,)     # pad 3D inner -> 4D
        return li.chunk_shape

    def _decode_shard(self, shard: bytes, li: LevelInfo,
                      coords: Tuple[int, int, int, int]) -> np.ndarray:
        inner = self._inner_shape(li)                      # (iz,iy,ix,ic) (ic=1 for 3D)
        grid = tuple(o // i for o, i in zip(li.chunk_shape, inner))
        ninner = int(np.prod(grid))
        index = np.frombuffer(shard[-(ninner * 16 + 4):-4], dtype="<u8").reshape(ninner, 2)
        inner_read = inner if self._cdim else inner[:3]    # bytes on disk are 3D for labels

        arr = self._empty_block(li, coords)
        bz, by, bx, bc = arr.shape
        for l in range(ninner):
            off, n = int(index[l, 0]), int(index[l, 1])
            if off == (1 << 64) - 1:
                continue
            zi, yi, xi, ci = self._lin_to_grid(l, grid)
            dec = np.frombuffer(_blosc.decode(shard[off:off + n]), np.uint8).reshape(inner_read)
            if not self._cdim:
                dec = dec[..., None]                       # (iz,iy,ix) -> (iz,iy,ix,1)
            z0, y0, x0, c0 = zi * inner[0], yi * inner[1], xi * inner[2], ci * inner[3]
            zs, ys, xs, cs = (min(inner[0], bz - z0), min(inner[1], by - y0),
                              min(inner[2], bx - x0), min(inner[3], bc - c0))
            if zs <= 0 or ys <= 0 or xs <= 0 or cs <= 0:
                continue
            arr[z0:z0 + zs, y0:y0 + ys, x0:x0 + xs, c0:c0 + cs] = dec[:zs, :ys, :xs, :cs]
        return arr

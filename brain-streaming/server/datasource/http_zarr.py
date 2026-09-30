"""HttpZarrBrainSource -- reads the read-only HTTP Zarr v3 (sharded) dataset.

Verified against docs/dataset.md. Uses a direct manual sharded reader
(range GETs + numcodecs blosc) because tensorstore's http kvstore hung here;
the format is fully specified so this is deterministic and fast (~20 ms/shard).

Only the chunks needed are ever fetched. The full dataset is never downloaded.
"""
from __future__ import annotations

import math
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Dict, List, Optional, Tuple

import numpy as np
import requests
from numcodecs import Blosc

from .base import BrainDataSource, DatasetInfo, LevelInfo, ChunkData

_EMPTY = (1 << 64) - 1
_blosc = Blosc()


class HttpZarrBrainSource(BrainDataSource):
    def __init__(self, root_url: str, timeout: float = 30.0,
                 voxel_um_finest: Optional[Tuple[float, float, float]] = None):
        """`voxel_um_finest` (z,y,x microns): true acquisition voxel size of the
        FINEST level, overriding the OME `scale` metadata. The 5 block Zarrs ship
        a placeholder scale (8 um x/y, ~62 um z) that would make each block a
        192 mm pancake; the real voxels are 0.5 um x/y, 20 um z (~12 mm block).
        We rescale every level by a per-axis factor so pyramid ratios are kept
        and all downstream geometry (extent, world bbox, layout) is physical.
        Pass None to trust the metadata verbatim.
        """
        self.root = root_url.rstrip("/")
        self.timeout = timeout
        self.voxel_um_finest = voxel_um_finest
        self._session = requests.Session()
        # DDN + the HTTP Zarr server handle heavy parallel access; the default
        # requests pool caps at 10 connections, which throttled ROI reads. Open a
        # large pool so hundreds of inner-chunk range GETs run truly concurrently.
        _adapter = requests.adapters.HTTPAdapter(
            pool_connections=512, pool_maxsize=512, max_retries=2)
        self._session.mount("http://", _adapter)
        self._session.mount("https://", _adapter)
        self._info: Optional[DatasetInfo] = None
        self._level_meta: Dict[int, dict] = {}   # raw array zarr.json per level

    # ---- metadata -----------------------------------------------------------
    def get_metadata(self) -> DatasetInfo:
        if self._info is not None:
            return self._info
        root = self._get_json(f"{self.root}/zarr.json")
        ome = root["attributes"]["ome"]["multiscales"][0]
        axes = [a["name"] for a in ome["axes"]]
        datasets = ome["datasets"]

        levels: List[LevelInfo] = []
        for ds in datasets:
            lvl = int(ds["path"])
            meta = self._get_json(f"{self.root}/{lvl}/zarr.json")
            self._level_meta[lvl] = meta
            shape = tuple(meta["shape"])
            cshape = tuple(meta["chunk_grid"]["configuration"]["chunk_shape"])
            scale = ds["coordinateTransformations"][0]["scale"]  # (z,y,x,c) metres
            grid = tuple(int(math.ceil(s / c)) for s, c in zip(shape, cshape))
            levels.append(LevelInfo(lvl, shape, cshape, tuple(scale), grid))

        levels.sort(key=lambda l: l.level)
        # finest = smallest x/y voxel
        finest = min(levels, key=lambda l: l.voxel_size_m[2]).level
        coarsest = max(levels, key=lambda l: l.voxel_size_m[2]).level

        # override placeholder OME scale with the true acquisition voxel size,
        # applied as a per-axis factor so the pyramid's level ratios are kept.
        if self.voxel_um_finest is not None:
            fin = next(l for l in levels if l.level == finest)
            tz, ty, tx = (v * 1e-6 for v in self.voxel_um_finest)   # um -> m
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
            axes=axes,
            channels=levels[0].shape[3],
            levels=levels,
            finest_level=finest,
            coarsest_level=coarsest,
            extent_m=(z * vz, y * vy, x * vx),
            attrs={k: v for k, v in root["attributes"].items() if k != "ome"},
        )
        return self._info

    def level_info(self, level: int) -> LevelInfo:
        for l in self.get_metadata().levels:
            if l.level == level:
                return l
        raise KeyError(f"no level {level}")

    # ---- one chunk (shard) --------------------------------------------------
    def get_chunk(self, level: int, coords: Tuple[int, int, int, int]) -> ChunkData:
        li = self.level_info(level)
        z, y, x, c = coords
        url = f"{self.root}/{level}/c/{z}/{y}/{x}/{c}"

        t0 = time.time()
        resp = self._session.get(url, timeout=self.timeout)
        fetch_s = time.time() - t0
        if resp.status_code == 404:
            # missing shard == all fill (empty region)
            arr = self._empty_block(li, coords)
            return ChunkData(level, coords, arr, self._origin(li, coords), 0, fetch_s, 0.0)
        resp.raise_for_status()
        shard = resp.content

        t1 = time.time()
        arr = self._decode_shard(shard, li, coords)
        decode_s = time.time() - t1
        return ChunkData(level, coords, arr, self._origin(li, coords),
                         len(shard), fetch_s, decode_s)

    # ---- arbitrary region ---------------------------------------------------
    def get_region(self, level: int, channel: Optional[int],
                   bounds: Tuple[slice, slice, slice]) -> np.ndarray:
        li = self.level_info(level)
        zc, yc, xc, cc = li.chunk_shape
        zs, ys, xs = bounds
        z0, z1 = zs.start or 0, zs.stop or li.shape[0]
        y0, y1 = ys.start or 0, ys.stop or li.shape[1]
        x0, x1 = xs.start or 0, xs.stop or li.shape[2]
        cch = li.shape[3] if channel is None else 1
        out = np.zeros((z1 - z0, y1 - y0, x1 - x0, cch), np.uint8)

        for zi in range(z0 // zc, math.ceil(z1 / zc)):
            for yi in range(y0 // yc, math.ceil(y1 / yc)):
                for xi in range(x0 // xc, math.ceil(x1 / xc)):
                    ch = self.get_chunk(level, (zi, yi, xi, 0)).array  # c-grid always 0
                    oz, oy, ox, _ = self._origin(li, (zi, yi, xi, 0))
                    sub = ch if channel is None else ch[:, :, :, channel:channel + 1]
                    self._blit(out, sub, (oz, oy, ox), (z0, y0, x0))
        return out

    # ---- partial ROI (reads ONLY the inner chunks overlapping the box) -------
    def get_roi(self, level: int, z0: int, z1: int, y0: int, y1: int,
                x0: int, x1: int, channels: Optional[List[int]] = None,
                workers: int = 32) -> np.ndarray:
        """Read a sub-box at `level` fetching only the inner (256^2) chunks that
        overlap it, via HTTP range requests + the shard index. This makes even a
        fine-level ROI cheap: whole 512 MB shards are never decoded.

        Returns (z,y,x,len(channels)) uint8.
        """
        li = self.level_info(level)
        cz, cy, cx, cc = li.chunk_shape
        iz, iy, ix, ic = self._inner_shape(li)
        gz, gy, gx, gc = cz // iz, cy // iy, cx // ix, cc // ic
        ninner = gz * gy * gx * gc
        tail_n = ninner * 16 + 4                       # index (u64 pairs) + crc32c
        sz, sy, sx, sc = li.shape

        z0 = max(0, z0); z1 = min(sz, z1)
        y0 = max(0, y0); y1 = min(sy, y1)
        x0 = max(0, x0); x1 = min(sx, x1)
        chans = list(range(sc)) if channels is None else channels
        out = np.zeros((z1 - z0, y1 - y0, x1 - x0, len(chans)), np.uint8)

        # enumerate the shards overlapping the box
        shard_coords = [(zi, yi, xi)
                        for zi in range(z0 // cz, math.ceil(z1 / cz))
                        for yi in range(y0 // cy, math.ceil(y1 / cy))
                        for xi in range(x0 // cx, math.ceil(x1 / cx))]

        # STEP 1: fetch every shard's index IN PARALLEL (these were serial before
        # and dominated fine-level ROI latency: ~100 shards x ~350 ms round-trip).
        def get_index(sc):
            zi, yi, xi = sc
            url = f"{self.root}/{level}/c/{zi}/{yi}/{xi}/0"
            return sc, url, self._range_tail(url, tail_n)

        with ThreadPoolExecutor(max_workers=workers) as ex:
            idx_results = list(ex.map(get_index, shard_coords))

        # STEP 2: build the inner-chunk task list from the indexes.
        tasks = []                                     # (url, off, n, gzo, gyo, gxo)
        for (zi, yi, xi), url, idx in idx_results:
            if idx is None:
                continue                               # missing shard -> all fill
            index = np.frombuffer(idx[:-4], dtype="<u8").reshape(ninner, 2)
            lz0 = max(z0, zi * cz) - zi * cz; lz1 = min(z1, zi * cz + cz) - zi * cz
            ly0 = max(y0, yi * cy) - yi * cy; ly1 = min(y1, yi * cy + cy) - yi * cy
            lx0 = max(x0, xi * cx) - xi * cx; lx1 = min(x1, xi * cx + cx) - xi * cx
            for zin in range(lz0 // iz, math.ceil(lz1 / iz)):
                for yin in range(ly0 // iy, math.ceil(ly1 / iy)):
                    for xin in range(lx0 // ix, math.ceil(lx1 / ix)):
                        l = ((zin * gy + yin) * gx + xin) * gc
                        off, n = int(index[l, 0]), int(index[l, 1])
                        if off == _EMPTY:
                            continue
                        tasks.append((url, off, n,
                                      zi * cz + zin * iz,
                                      yi * cy + yin * iy,
                                      xi * cx + xin * ix))

        # STEP 3: fetch + blosc-decode every inner chunk in parallel.
        def fetch(t):
            url, off, n, gzo, gyo, gxo = t
            dec = np.frombuffer(_blosc.decode(self._range(url, off, n)), np.uint8)
            return gzo, gyo, gxo, dec.reshape((iz, iy, ix, ic))

        if tasks:
            with ThreadPoolExecutor(max_workers=workers) as ex:
                for gzo, gyo, gxo, dec in ex.map(fetch, tasks):
                    zz0 = max(z0, gzo); zz1 = min(z1, gzo + iz)
                    yy0 = max(y0, gyo); yy1 = min(y1, gyo + iy)
                    xx0 = max(x0, gxo); xx1 = min(x1, gxo + ix)
                    if zz1 <= zz0 or yy1 <= yy0 or xx1 <= xx0:
                        continue
                    sub = dec[zz0 - gzo:zz1 - gzo, yy0 - gyo:yy1 - gyo,
                              xx0 - gxo:xx1 - gxo, :][:, :, :, chans]
                    out[zz0 - z0:zz1 - z0, yy0 - y0:yy1 - y0, xx0 - x0:xx1 - x0, :] = sub
        return out

    def _range_tail(self, url: str, n: int) -> Optional[bytes]:
        r = self._session.get(url, headers={"Range": f"bytes=-{n}"}, timeout=self.timeout)
        if r.status_code == 404:
            return None
        r.raise_for_status()
        return r.content

    def _range(self, url: str, off: int, n: int) -> bytes:
        r = self._session.get(url, headers={"Range": f"bytes={off}-{off + n - 1}"},
                              timeout=self.timeout)
        r.raise_for_status()
        return r.content

    # ---- internals ----------------------------------------------------------
    def _decode_shard(self, shard: bytes, li: LevelInfo,
                      coords: Tuple[int, int, int, int]) -> np.ndarray:
        inner = self._inner_shape(li)               # [8,256,256,4]
        grid = tuple(o // i for o, i in zip(li.chunk_shape, inner))  # e.g. (1,16,16,1)
        ninner = int(np.prod(grid))
        index = np.frombuffer(shard[-(ninner * 16 + 4):-4], dtype="<u8").reshape(ninner, 2)

        arr = self._empty_block(li, coords)          # (bz,by,bx,c) cropped to array
        bz, by, bx, bc = arr.shape
        for l in range(ninner):
            off, n = int(index[l, 0]), int(index[l, 1])
            if off == _EMPTY:
                continue
            zi, yi, xi, ci = self._lin_to_grid(l, grid)
            dec = np.frombuffer(_blosc.decode(shard[off:off + n]), np.uint8).reshape(inner)
            z0, y0, x0, c0 = zi * inner[0], yi * inner[1], xi * inner[2], ci * inner[3]
            zs, ys, xs, cs = (min(inner[0], bz - z0), min(inner[1], by - y0),
                              min(inner[2], bx - x0), min(inner[3], bc - c0))
            if zs <= 0 or ys <= 0 or xs <= 0 or cs <= 0:
                continue
            arr[z0:z0 + zs, y0:y0 + ys, x0:x0 + xs, c0:c0 + cs] = dec[:zs, :ys, :xs, :cs]
        return arr

    def _inner_shape(self, li: LevelInfo) -> Tuple[int, int, int, int]:
        # read the sharding_indexed inner chunk_shape from the level's codecs
        for codec in self._level_meta[li.level].get("codecs", []):
            if codec.get("name") == "sharding_indexed":
                return tuple(codec["configuration"]["chunk_shape"])
        # no sharding -> the chunk itself is the unit
        return li.chunk_shape

    def _empty_block(self, li: LevelInfo, coords) -> np.ndarray:
        z, y, x, c = coords
        cz, cy, cx, cc = li.chunk_shape
        sz, sy, sx, sc = li.shape
        bz = min(cz, sz - z * cz); by = min(cy, sy - y * cy)
        bx = min(cx, sx - x * cx); bc = min(cc, sc - c * cc)
        return np.zeros((bz, by, bx, bc), np.uint8)

    @staticmethod
    def _origin(li: LevelInfo, coords) -> Tuple[int, int, int, int]:
        cz, cy, cx, cc = li.chunk_shape
        z, y, x, c = coords
        return (z * cz, y * cy, x * cx, c * cc)

    @staticmethod
    def _lin_to_grid(l: int, grid) -> Tuple[int, int, int, int]:
        gz, gy, gx, gc = grid
        c = l % gc; l //= gc
        x = l % gx; l //= gx
        y = l % gy; l //= gy
        z = l % gz
        return z, y, x, c

    @staticmethod
    def _blit(dst, src, origin, dst_origin):
        oz, oy, ox = origin
        dz, dy, dx = dst_origin
        z0 = max(oz, dz); y0 = max(oy, dy); x0 = max(ox, dx)
        z1 = min(oz + src.shape[0], dz + dst.shape[0])
        y1 = min(oy + src.shape[1], dy + dst.shape[1])
        x1 = min(ox + src.shape[2], dx + dst.shape[2])
        if z1 <= z0 or y1 <= y0 or x1 <= x0:
            return
        dst[z0 - dz:z1 - dz, y0 - dy:y1 - dy, x0 - dx:x1 - dx, :] = \
            src[z0 - oz:z1 - oz, y0 - oy:y1 - oy, x0 - ox:x1 - ox, :]

    def _get_json(self, url: str) -> dict:
        r = self._session.get(url, timeout=self.timeout)
        r.raise_for_status()
        return r.json()

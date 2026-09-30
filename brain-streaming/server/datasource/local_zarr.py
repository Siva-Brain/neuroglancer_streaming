"""LocalZarrBrainSource -- reads a Zarr v3 (sharded) dataset from the LOCAL
filesystem instead of over HTTP.

The on-disk format is identical to the HTTP one (OME-Zarr 0.5 multiscales,
`sharding_indexed` codec with an inner blosc/zstd stage, chunk key
`{level}/c/{z}/{y}/{x}/{c}`), so this reuses every bit of the sharded decode
logic in HttpZarrBrainSource and only swaps the four byte-access primitives
(JSON read, whole-shard read, index tail range, inner-chunk range) for plain
file reads. That keeps a single, tested sharding implementation for both brains.

Used for brain 2 (`hb02_fused.zarr`), a single fused RGB whole-brain volume
sitting on the DDN filesystem; its OME `scale` is REAL (8 um x/y, 320 um z) so
NO voxel override is applied (voxel_um_finest=None).
"""
from __future__ import annotations

import os
import time
from typing import Optional, Tuple

from .base import DatasetInfo, ChunkData
from .http_zarr import HttpZarrBrainSource


class LocalZarrBrainSource(HttpZarrBrainSource):
    def __init__(self, root_path: str,
                 voxel_um_finest: Optional[Tuple[float, float, float]] = None):
        # deliberately DO NOT call super().__init__ (it opens an HTTP session).
        self.root = os.path.abspath(os.path.expanduser(root_path)).rstrip("/")
        self.timeout = 0.0
        self.voxel_um_finest = voxel_um_finest
        self._session = None
        self._info: Optional[DatasetInfo] = None
        self._level_meta = {}

    # ---- byte-access primitives (filesystem instead of HTTP) ----------------
    def _get_json(self, path: str) -> dict:
        import json
        with open(path) as f:
            return json.load(f)

    def get_chunk(self, level: int, coords: Tuple[int, int, int, int]) -> ChunkData:
        li = self.level_info(level)
        z, y, x, c = coords
        path = f"{self.root}/{level}/c/{z}/{y}/{x}/{c}"
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
        return ChunkData(level, coords, arr, self._origin(li, coords),
                         len(shard), fetch_s, time.time() - t1)

    def _range_tail(self, path: str, n: int) -> Optional[bytes]:
        if not os.path.exists(path):
            return None
        size = os.path.getsize(path)
        with open(path, "rb") as f:
            f.seek(max(0, size - n))
            return f.read(n)

    def _range(self, path: str, off: int, n: int) -> bytes:
        with open(path, "rb") as f:
            f.seek(off)
            return f.read(n)

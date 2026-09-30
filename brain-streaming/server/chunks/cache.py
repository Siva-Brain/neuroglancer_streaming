"""Bounded LRU cache for decoded chunks (DGX-side memory cache).

Keyed by chunk_id. Evicts least-recently-used when the byte budget is exceeded.
Prevents re-decoding/re-downloading the same chunk.
"""
from __future__ import annotations

import threading
import time
from collections import OrderedDict
from typing import Optional

from datasource.base import ChunkData


class LruChunkCache:
    def __init__(self, max_bytes: int = 512 * 1024 * 1024):
        self.max_bytes = max_bytes
        self._store: "OrderedDict[str, ChunkData]" = OrderedDict()
        self._bytes = 0
        self._lock = threading.Lock()
        self.hits = 0
        self.misses = 0

    @property
    def bytes_used(self) -> int:
        return self._bytes

    def __len__(self) -> int:
        return len(self._store)

    def get(self, chunk_id: str) -> Optional[ChunkData]:
        with self._lock:
            cd = self._store.get(chunk_id)
            if cd is None:
                self.misses += 1
                return None
            self._store.move_to_end(chunk_id)
            self.hits += 1
            return cd

    def put(self, chunk_id: str, cd: ChunkData) -> None:
        nbytes = int(cd.array.nbytes)
        with self._lock:
            if chunk_id in self._store:
                self._bytes -= int(self._store[chunk_id].array.nbytes)
            self._store[chunk_id] = cd
            self._store.move_to_end(chunk_id)
            self._bytes += nbytes
            while self._bytes > self.max_bytes and len(self._store) > 1:
                _, old = self._store.popitem(last=False)
                self._bytes -= int(old.array.nbytes)

    def stats(self) -> dict:
        total = self.hits + self.misses
        return {
            "entries": len(self._store),
            "bytes_used": self._bytes,
            "max_bytes": self.max_bytes,
            "hits": self.hits,
            "misses": self.misses,
            "hit_rate": round(self.hits / total, 3) if total else 0.0,
        }

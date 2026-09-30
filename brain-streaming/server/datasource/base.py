"""BrainDataSource: the interface that hides Zarr (or anything else) from the
rest of the DGX application. The streaming server, chunk manager and priority
logic depend ONLY on this interface -- never on Zarr/HTTP details.

Later, a mesh/segmentation source can implement the same interface (Phase 17)
without touching the streaming or Unity layers.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import List, Optional, Tuple

import numpy as np


@dataclass
class LevelInfo:
    level: int
    shape: Tuple[int, int, int, int]      # (z, y, x, c)
    chunk_shape: Tuple[int, int, int, int]
    voxel_size_m: Tuple[float, float, float, float]  # (z, y, x, c) metres
    grid: Tuple[int, int, int, int]       # number of shards per axis


@dataclass
class DatasetInfo:
    name: str
    dtype: str
    axes: List[str]                       # ["z","y","x","c"]
    channels: int
    levels: List[LevelInfo]
    finest_level: int
    coarsest_level: int
    extent_m: Tuple[float, float, float]  # physical (z, y, x) metres
    attrs: dict = field(default_factory=dict)


@dataclass
class ChunkData:
    """A decoded chunk: geometry-agnostic ND block plus identity."""
    level: int
    coords: Tuple[int, int, int, int]     # shard grid index (z,y,x,c)
    array: np.ndarray                     # (z,y,x,c) uint8 for this shard
    origin_vox: Tuple[int, int, int, int] # voxel offset in the level array
    source_bytes: int                     # compressed bytes fetched
    fetch_s: float
    decode_s: float

    @property
    def chunk_id(self) -> str:
        z, y, x, c = self.coords
        return f"L{self.level}.{z}.{y}.{x}.{c}"


class BrainDataSource(ABC):
    """Everything the rest of the system is allowed to know about the data."""

    @abstractmethod
    def get_metadata(self) -> DatasetInfo: ...

    @abstractmethod
    def get_chunk(self, level: int, coords: Tuple[int, int, int, int]) -> ChunkData:
        """Fetch + decode exactly one shard-sized chunk."""

    @abstractmethod
    def get_region(self, level: int, channel: Optional[int],
                   bounds: Tuple[slice, slice, slice]) -> np.ndarray:
        """Read an arbitrary (z,y,x) region at a level, only touching needed chunks."""

    # ---- helpers usable by all sources --------------------------------------
    @staticmethod
    def parse_chunk_id(chunk_id: str) -> Tuple[int, Tuple[int, int, int, int]]:
        # "L5.19.0.0.0" -> (5, (19,0,0,0))
        lvl, z, y, x, c = chunk_id.replace("L", "").split(".")
        return int(lvl), (int(z), int(y), int(x), int(c))

"""Procedural synthetic brain.

Each region is a coloured ellipsoid and becomes one streamable chunk. The shapes
are anatomically suggestive, not accurate -- the point is recognizable regions
for demonstrating chunking / LOD / streaming.

Coordinate frame: millimetre-ish, +X right, +Y up, +Z forward (toward face).
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import List


@dataclass
class Region:
    chunk_id: str
    center: tuple
    radii: tuple
    color: tuple           # rgb 0..1
    dependencies: List[str] = field(default_factory=list)


# left/right hemispheres, cerebellum, brain stem, plus paired deep structures.
REGIONS: List[Region] = [
    Region("left_hemisphere",  (-42,  25,   0), (46, 58, 64), (0.90, 0.72, 0.75)),
    Region("right_hemisphere", ( 42,  25,   0), (46, 58, 64), (0.88, 0.70, 0.73)),
    Region("cerebellum",       (  0, -40, -58), (48, 28, 30), (0.78, 0.60, 0.86)),
    Region("brain_stem",       (  0, -55,  -8), (11, 40, 12), (0.70, 0.75, 0.92),
           dependencies=["cerebellum"]),
    Region("thalamus_left",    (-16,   8,   2), (12, 12, 16), (0.96, 0.85, 0.55),
           dependencies=["left_hemisphere"]),
    Region("thalamus_right",   ( 16,   8,   2), (12, 12, 16), (0.96, 0.85, 0.55),
           dependencies=["right_hemisphere"]),
    Region("hippocampus_left", (-30,  -8,  10), ( 9,  9, 26), (0.55, 0.85, 0.70),
           dependencies=["left_hemisphere"]),
    Region("hippocampus_right",( 30,  -8,  10), ( 9,  9, 26), (0.55, 0.85, 0.70),
           dependencies=["right_hemisphere"]),
]

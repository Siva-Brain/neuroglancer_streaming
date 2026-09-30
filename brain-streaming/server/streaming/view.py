"""View + request/response models for the real-Zarr streaming API.

Transport-agnostic shapes (HTTP+JSON today). See docs/protocol.md.
"""
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel, Field


class ViewRequest(BaseModel):
    """Unity/browser -> DGX :: VIEW_UPDATE.

    `position`/`forward` are given in the target BLOCK's local millimetre frame
    (the client inverse-transforms the world camera by that block's placement
    matrix before sending), so the selector always reasons in block-local space
    with the block centred at its own origin. `block` names which block this
    view is for; omitted -> the first/default block (single-block back-compat).
    """
    position: List[float] = Field(..., min_length=3, max_length=3)
    rotation: List[float] = Field(default=[0, 0, 0, 1], min_length=4, max_length=4)
    forward: List[float] = Field(..., min_length=3, max_length=3)
    fov: float = 60.0
    viewportWidth: int = 1920
    viewportHeight: int = 1080
    block: Optional[str] = None


class SelectedChunkDto(BaseModel):
    chunk_id: str
    level: int
    coords: List[int]
    center_mm: List[float]
    distance: float
    visible: bool
    priority: int


class ViewResponse(BaseModel):
    target_level: int
    chunks: List[SelectedChunkDto]


class ChunkRequestBody(BaseModel):
    """POST /api/chunks/request -- explicit multi-chunk request."""
    chunk_ids: List[str]


class ChunkCancelBody(BaseModel):
    """POST /api/chunks/cancel -- client tells DGX these are no longer needed."""
    chunk_ids: List[str]


PROTOCOL_DOC = {
    "version": "3.0-zarr-http-multiblock",
    "world": "each block is baked in its OWN local millimetres, centred at its "
             "own origin; the client places every block in the shared world with "
             "an editable transform (translation/rotation/scale) and shows/hides "
             "it. Data axes z,y,x map to local z,y,x.",
    "blocks": "the brain was block-cut into several slabs, each its own Zarr "
              "pyramid. GET /api/dataset/info returns `blocks:[...]`, each with a "
              "`block_id`, per-block metadata and a `default_transform`. All other "
              "endpoints take `?block=<id>` (POST /api/view: `block` field); "
              "omitted -> the first block (single-block back-compat).",
    "messages": {
        "DATASET_INFO":  "GET  /api/dataset/info                 -> {blocks:[...]}",
        "VIEW_UPDATE":   "POST /api/view            (ViewRequest, camera in block-local mm) -> ViewResponse",
        "REQUEST_CHUNK": "GET  /api/chunk/{chunk_id}?channels=0,3&block=<id> -> binary BVX2",
        "REQUEST_MANY":  "POST /api/chunks/request?block=<id>  (chunk_ids) -> per-chunk status",
        "CANCEL_CHUNK":  "POST /api/chunks/cancel   (chunk_ids)",
        "PREFETCH":      "POST /api/prefetch?block=<id>        (chunk_ids) -> warms DGX cache",
        "HEARTBEAT":     "GET  /api/health                       -> {..., blocks:[...]}",
    },
    "chunk_id": "L{level}.{z}.{y}.{x}.{c}  (shard grid indices, per block)",
    "binary_brick_format":
        "magic 'BVX2'(4) | u8 level | u8 n_channels | u16 reserved | i32 z0,y0,x0 "
        "| u16 dz,dy,dx | f32[6] world_bbox_mm(xmin,ymin,zmin,xmax,ymax,zmax) "
        "| u8[dz*dy*dx*n_channels] voxels (z,y,x,c order). Header = 50 bytes.",
}

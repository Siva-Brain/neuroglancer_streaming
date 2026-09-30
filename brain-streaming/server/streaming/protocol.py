"""Transport-agnostic protocol definitions.

The message *shapes* here are what matter. Today they ride on HTTP+JSON (and raw
binary for chunk payloads), but nothing in the client/server rendering logic
depends on HTTP: swapping in WebSocket / QUIC / gRPC later means re-binding these
same messages to a new transport. See docs/protocol.md.
"""
from __future__ import annotations

from typing import List, Optional
from pydantic import BaseModel, Field


class Viewport(BaseModel):
    width: int = 1280
    height: int = 720


class ViewRequest(BaseModel):
    """Unity/browser -> DGX :: VIEW_UPDATE"""
    camera_position: List[float] = Field(..., min_length=3, max_length=3)
    # rotation as a quaternion [x,y,z,w]; forward is derived client-side and sent
    # explicitly so the server stays math-light and transport-agnostic.
    camera_rotation: List[float] = Field(default=[0, 0, 0, 1], min_length=4, max_length=4)
    camera_forward: List[float] = Field(..., min_length=3, max_length=3)
    fov: float = 60.0
    viewport: Viewport = Viewport()


class PrioritizedChunk(BaseModel):
    """DGX -> Unity :: entry of the priority list (a CHUNK_AVAILABLE hint)."""
    chunk_id: str
    target_lod: int
    visible: bool
    distance: float
    priority: int


class ViewResponse(BaseModel):
    chunks: List[PrioritizedChunk]


# Human-readable protocol summary, echoed at GET /api/protocol.
PROTOCOL_DOC = {
    "version": "1.0-http",
    "messages": {
        "VIEW_UPDATE":     "POST /api/brain/view    (ViewRequest) -> ViewResponse",
        "REQUEST_CHUNK":   "GET  /api/brain/chunk/{chunk_id}?lod=N -> binary BRN1",
        "CHUNK_AVAILABLE": "response to REQUEST_CHUNK / entry in ViewResponse",
        "CANCEL_CHUNK":    "client-side: abort the in-flight GET (HTTP has no server push)",
        "HEARTBEAT":       "GET /api/health",
    },
    "binary_chunk_format": "magic 'BRN1' | u32 vertex_count | u32 index_count "
                           "| f32[V*3] positions | u32[I] indices",
}

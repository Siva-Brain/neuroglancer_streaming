"""Brain Streaming Server (DGX side).

Run:
    python app.py --host 0.0.0.0 --port 8000 --chunk-delay-ms 120

Endpoints:
    GET  /                        -> browser client (stands in for Unity)
    GET  /api/brain/info          -> brain metadata + chunk list
    GET  /api/brain/chunks        -> per-chunk metadata (all LODs)
    GET  /api/brain/chunk/{id}    -> binary mesh  (?lod=0|1|2)
    POST /api/brain/view          -> camera-driven priority list
    GET  /api/protocol            -> protocol summary
    GET  /api/health              -> heartbeat
"""
from __future__ import annotations

import argparse
import asyncio
import os

from fastapi import FastAPI, HTTPException, Query, Response
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse

from brain.chunk import catalog
from brain.lod import LOD_CONFIGS
from streaming.protocol import ViewRequest, ViewResponse, PROTOCOL_DOC

# artificial per-chunk latency (ms) to make incremental streaming visible.
# mutated by CLI arg in __main__ before uvicorn starts.
CHUNK_DELAY_MS = 0.0

HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT_HTML = os.path.normpath(os.path.join(HERE, "..", "client", "index.html"))

app = FastAPI(title="Brain Streaming Server", version="1.0")
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"], allow_methods=["*"], allow_headers=["*"],
)


@app.get("/")
def client():
    if os.path.exists(CLIENT_HTML):
        return FileResponse(CLIENT_HTML)
    return JSONResponse({"error": "client/index.html not found", "path": CLIENT_HTML},
                        status_code=404)


@app.get("/api/health")
def health():
    return {"status": "ok", "chunk_delay_ms": CHUNK_DELAY_MS}


@app.get("/api/protocol")
def protocol():
    return PROTOCOL_DOC


@app.get("/api/brain/info")
def brain_info():
    return catalog.info()


@app.get("/api/brain/chunks")
def brain_chunks():
    return {"chunks": catalog.chunks()}


@app.get("/api/brain/chunk/{chunk_id}")
async def brain_chunk(chunk_id: str, lod: int = Query(0, ge=0, le=max(LOD_CONFIGS))):
    if not catalog.has(chunk_id, lod):
        raise HTTPException(status_code=404, detail=f"no chunk {chunk_id}@lod{lod}")
    if CHUNK_DELAY_MS > 0:
        await asyncio.sleep(CHUNK_DELAY_MS / 1000.0)
    blob = catalog.blob(chunk_id, lod)
    return Response(
        content=blob,
        media_type="application/octet-stream",
        headers={
            "X-Chunk-Id": chunk_id,
            "X-Chunk-Lod": str(lod),
            "Cache-Control": "no-store",
        },
    )


@app.post("/api/brain/view", response_model=ViewResponse)
def brain_view(view: ViewRequest):
    ranked = catalog.prioritize(view.camera_position, view.camera_forward, view.fov)
    return {"chunks": ranked}


if __name__ == "__main__":
    import uvicorn

    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8000)
    ap.add_argument("--chunk-delay-ms", type=float, default=120.0,
                    help="artificial per-chunk latency to visualize streaming")
    args = ap.parse_args()

    CHUNK_DELAY_MS = args.chunk_delay_ms
    # push value into the module namespace uvicorn will import
    import app as _self  # type: ignore
    _self.CHUNK_DELAY_MS = args.chunk_delay_ms

    print(f"[brain-server] chunks={catalog.info()['chunk_count']} "
          f"lods={list(LOD_CONFIGS)} delay={args.chunk_delay_ms}ms")
    print(f"[brain-server] open http://<this-host>:{args.port}/  (client served here)")
    uvicorn.run(_self.app, host=args.host, port=args.port, log_level="info")

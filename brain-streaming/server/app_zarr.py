"""DGX Brain Streaming Server -- real Zarr edition, multi-block.

    python app_zarr.py \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/584_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/585_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/586_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/587_ALL_3d.zarr \
        --host 0.0.0.0 --port 8010 --min-level 3

The whole brain was block-cut into several physical blocks, each scanned to its
own Zarr pyramid. This server streams ALL of them from one process. Each block
is baked in its OWN local millimetre frame (centred at its own origin); the
client places each block in the world with an editable transform (translation /
rotation / scale) and can show/hide it. The server only ships a *default*
placement (auto side-by-side) so the first paint shows distinct blocks.

Architecture enforced here:  Unity -> DGX API -> ChunkManager -> HTTP Zarr.
Unity never sees the Zarr server. The full dataset is never downloaded.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import os
import re
import struct
import threading
import time
from collections import OrderedDict
from dataclasses import dataclass, field
from typing import Dict, List, Optional

import numpy as np

from fastapi import Body, FastAPI, HTTPException, Query, Response
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse

from datasource import HttpZarrBrainSource
from chunks import ChunkManager, LruChunkCache, CameraViewSelector
from gpu import GpuProcessor
from streaming.view import (ViewRequest, ViewResponse, ChunkRequestBody,
                            ChunkCancelBody, PROTOCOL_DOC)

# --- config (set in __main__) ---
DEFAULT_ZARR_ROOTS = [
    "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr",
    "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/584_ALL_3d.zarr",
    "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/585_ALL_3d.zarr",
    "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/586_ALL_3d.zarr",
    "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/587_ALL_3d.zarr",
]
ZARR_ROOTS = list(DEFAULT_ZARR_ROOTS)
# Fallback finest voxel size (z,y,x microns) for blocks NOT in the authoritative
# histology_blocks.json map. The correct per-block sizes (8 um x/y + per-block z)
# come from that map — the 8 um x/y OME scale is REAL (each block is a ~192 mm
# sagittal slab); the earlier 0.5 um override was wrong (it made 12 mm cubes that
# would not reassemble). See BLOCK_META / _block_voxel_um below. None -> metadata.
VOXEL_UM_FINEST = (20.0, 8.0, 8.0)
MIN_LEVEL = 3
DELAY_MS = 0.0
BANDWIDTH_LIMIT_MBPS = 0.0        # 0 = unlimited
CACHE_BYTES = 2 * 1024 * 1024 * 1024   # A100 box has 2 TB RAM; be generous
WORKERS = 32                     # parallel prefetch workers (256 cores available)
USE_GPU = True                   # A100 processing (falls back to CPU if absent)
GPU_MAX_XY = 512                 # server-side downsample cap so bricks stay uniform
GLASS = True                     # Nissl see-through preprocessing (normalize + gradient)
PREFETCH_TOP = 8                 # warm this many top-priority chunks on each view
ROI_WORKERS = 192                # parallel inner-chunk range GETs for ROI (DDN can take it)
_roi_gpu_rr = 0                  # round-robin GPU selector for ROI resample

# ROI payload cache: first L0 fetch is remote-bandwidth-bound (~1 GB / ~30 s),
# but re-selecting a size/mode (or reloading) should be instant. 2 TB RAM here.
_roi_cache: "OrderedDict[tuple, bytes]" = OrderedDict()
_roi_cache_bytes = 0
_roi_cache_lock = threading.Lock()
ROI_CACHE_MAX_BYTES = 16 * 1024 * 1024 * 1024   # 16 GB


def _roi_cache_get(key):
    with _roi_cache_lock:
        v = _roi_cache.get(key)
        if v is not None:
            _roi_cache.move_to_end(key)
        return v


def _roi_cache_put(key, payload: bytes):
    global _roi_cache_bytes
    with _roi_cache_lock:
        if key in _roi_cache:
            _roi_cache_bytes -= len(_roi_cache[key])
        _roi_cache[key] = payload
        _roi_cache.move_to_end(key)
        _roi_cache_bytes += len(payload)
        while _roi_cache_bytes > ROI_CACHE_MAX_BYTES and len(_roi_cache) > 1:
            _, old = _roi_cache.popitem(last=False)
            _roi_cache_bytes -= len(old)

HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT_HTML = os.path.normpath(os.path.join(HERE, "..", "client", "volume.html"))

# ---- authoritative per-block OME geometry + placement (from the Neuroglancer
# 3d-stroke-viewer app: apps/frontend/src/viewer/histology_blocks.ts). Gives the
# TRUE finest voxel size and the omeToRas affine that reassembles blocks into one
# brain in shared RAS mm. ----
HISTOLOGY_BLOCKS_JSON = os.path.join(HERE, "transforms", "histology_blocks.json")


def _load_block_meta():
    try:
        with open(HISTOLOGY_BLOCKS_JSON) as f:
            data = json.load(f)
        brain = data.get("brain")
        return {b["id"]: {**b, "brain": brain} for b in data.get("blocks", [])}
    except Exception as e:
        print(f"[dgx-zarr] histology_blocks.json not loaded ({e}); "
              f"falling back to global voxel size, no anatomical transforms")
        return {}


BLOCK_META = _load_block_meta()


def _block_voxel_um(bid: str):
    """Per-block finest voxel size (z,y,x microns): authoritative map, else fallback."""
    m = BLOCK_META.get(bid)
    if not m:
        return VOXEL_UM_FINEST
    ip = m.get("inPlaneScaleMeters", 8e-6) * 1e6
    return (m["zScaleMeters"] * 1e6, ip, ip)

app = FastAPI(title="DGX Brain Streaming (Zarr, multi-block)", version="3.0")
app.add_middleware(CORSMiddleware, allow_origins=["*"],
                   allow_methods=["*"], allow_headers=["*"])


# ---------- block registry ----------
@dataclass
class BlockCtx:
    block_id: str
    url: str
    source: HttpZarrBrainSource
    manager: ChunkManager
    selector: CameraViewSelector
    default_transform: dict = field(default_factory=dict)


_gpu: Optional[GpuProcessor] = None
_blocks: "Dict[str, BlockCtx]" = {}
_order: List[str] = []


def _block_id_from_url(url: str) -> str:
    """'.../zarr_files/584_ALL_3d.zarr' -> '584'. Falls back to a slug."""
    base = url.rstrip("/").split("/")[-1]
    m = re.match(r"(\d+)", base)
    if m:
        return m.group(1)
    return re.sub(r"[^A-Za-z0-9]+", "_", base).strip("_") or base


def _auto_layout(ctxs: List[BlockCtx]) -> None:
    """Spread blocks side by side along world X by their widths, centred at 0.

    Each block is baked centred at its own origin, so its local X span is
    [-w/2, +w/2]; we translate each so the row is contiguous and centred.
    """
    widths = []
    for c in ctxs:
        ez, ey, ex = (e * 1000.0 for e in c.source.get_metadata().extent_m)  # mm
        widths.append(ex)
    gap = 0.10 * (max(widths) if widths else 0.0)
    total = sum(widths) + gap * (len(widths) - 1 if widths else 0)
    cursor = -total / 2.0
    for c, w in zip(ctxs, widths):
        tx = cursor + w / 2.0
        c.default_transform = {
            "translation": [tx, 0.0, 0.0],
            "rotation_deg": [0.0, 0.0, 0.0],
            "scale": [1.0, 1.0, 1.0],
        }
        cursor += w + gap


def build_blocks() -> "Dict[str, BlockCtx]":
    global _gpu, _blocks, _order
    if _blocks:
        return _blocks
    _gpu = GpuProcessor(enabled=USE_GPU)
    _gpu.warmup()                        # pre-compile downsample kernels (off user path)
    ctxs: List[BlockCtx] = []
    seen: Dict[str, int] = {}
    for url in ZARR_ROOTS:
        bid = _block_id_from_url(url)
        if bid in seen:                     # de-dupe id collisions deterministically
            seen[bid] += 1
            bid = f"{bid}_{seen[bid]}"
        else:
            seen[bid] = 0
        src = HttpZarrBrainSource(url, voxel_um_finest=_block_voxel_um(bid))
        mgr = ChunkManager(src, LruChunkCache(CACHE_BYTES),
                           workers=WORKERS, processor=_gpu, max_xy=GPU_MAX_XY)
        sel = CameraViewSelector(src, min_level=MIN_LEVEL)
        ctx = BlockCtx(bid, url, src, mgr, sel)
        ctxs.append(ctx)
        _blocks[bid] = ctx
        _order.append(bid)
        print(f"[dgx-zarr] block {bid}: {url}")
    _auto_layout(ctxs)
    print(f"[dgx-zarr] gpu={_gpu.info()} workers={WORKERS} max_xy={GPU_MAX_XY} "
          f"blocks={_order}")
    return _blocks


def get_block(block_id: Optional[str]) -> BlockCtx:
    build_blocks()
    if block_id is None or block_id == "":
        return _blocks[_order[0]]
    ctx = _blocks.get(block_id)
    if ctx is None:
        raise HTTPException(404, f"unknown block {block_id!r}; have {_order}")
    return ctx


async def _throttle(nbytes: int):
    if DELAY_MS > 0:
        await asyncio.sleep(DELAY_MS / 1000.0)
    if BANDWIDTH_LIMIT_MBPS > 0:
        await asyncio.sleep(nbytes / (BANDWIDTH_LIMIT_MBPS * 1e6))


@app.on_event("startup")
def _startup():
    # build every block's manager + GPU context now so /health is accurate and
    # the first client request doesn't pay CUDA init / metadata latency.
    build_blocks()
    # compute each block's gray-intensity window (for glass normalization) and
    # prewarm the ROI viewer's default panels -- both in the background (idle
    # DGX work) so the first client request isn't blocked.
    threading.Thread(target=_compute_norm_windows, daemon=True).start()
    threading.Thread(target=_prewarm_roi, daemon=True).start()


@app.get("/")
def client():
    if os.path.exists(CLIENT_HTML):
        return FileResponse(CLIENT_HTML)
    return JSONResponse({"error": "client/volume.html not built yet",
                         "hint": "use the API directly; see docs/protocol.md"}, status_code=404)


DASHBOARD_HTML = os.path.normpath(os.path.join(HERE, "..", "client", "index.html"))


@app.get("/dashboard")
def dashboard():
    if os.path.exists(DASHBOARD_HTML):
        return FileResponse(DASHBOARD_HTML)
    return JSONResponse({"error": "client/index.html missing"}, status_code=404)


@app.get("/api/health")
def health():
    build_blocks()
    return {"status": "ok", "delay_ms": DELAY_MS,
            "bandwidth_limit_mbps": BANDWIDTH_LIMIT_MBPS,
            "gpu": _gpu.info() if _gpu is not None else {"available": False},
            "workers": WORKERS, "gpu_max_xy": GPU_MAX_XY,
            "blocks": _order}


@app.get("/api/protocol")
def protocol():
    return PROTOCOL_DOC


def _block_info_dict(ctx: BlockCtx) -> dict:
    s = ctx.selector
    info = s.info
    return {
        "block_id": ctx.block_id,
        "url": ctx.url,
        "default_transform": ctx.default_transform,
        "name": info.name,
        "dtype": info.dtype,
        "axes": info.axes,
        "channels": info.channels,
        "finest_level": info.finest_level,
        "coarsest_level": info.coarsest_level,
        "min_streamable_level": s.min_level,
        "extent_mm": [e * 1000.0 for e in info.extent_m],   # (z,y,x)
        "levels": [
            {"level": l.level, "shape": list(l.shape), "grid": list(l.grid),
             "chunk_shape": list(l.chunk_shape),
             "xy_voxel_um": l.voxel_size_m[2] * 1e6}
            for l in info.levels
        ],
        "baseline_chunks": s.baseline_chunks(),   # whole coarsest level (load first)
        "attrs": info.attrs,
    }


@app.get("/api/dataset/info")
def dataset_info():
    build_blocks()
    blocks = [_block_info_dict(_blocks[bid]) for bid in _order]
    # top-level convenience mirror of the first block keeps single-block clients
    # working; multi-block clients read `blocks`.
    first = dict(blocks[0]) if blocks else {}
    first.pop("block_id", None)
    return {"blocks": blocks, **first}


@app.post("/api/view", response_model=ViewResponse)
def view(v: ViewRequest):
    ctx = get_block(v.block)
    s = ctx.selector
    aspect = (v.viewportWidth / v.viewportHeight) if v.viewportHeight else 1.777
    sel = s.select(v.position, v.forward, v.fov, aspect)
    # A100 upgrade: warm the top-priority chunks in parallel so the client's
    # subsequent GETs hit a warm cache (hides the ~170 ms Zarr HTTP latency).
    if PREFETCH_TOP > 0 and sel:
        ctx.manager.prefetch([(c.level, tuple(c.coords)) for c in sel[:PREFETCH_TOP]])
    return {
        "target_level": sel[0].level if sel else s.min_level,
        "chunks": [
            {"chunk_id": c.chunk_id, "level": c.level, "coords": list(c.coords),
             "center_mm": list(c.center_mm), "distance": c.distance,
             "visible": c.visible, "priority": c.priority}
            for c in sel
        ],
    }


@app.post("/api/chunks/request")
def request_many(body: ChunkRequestBody, block: str = Query(None)):
    m = get_block(block).manager
    out = []
    for cid in body.chunk_ids:
        try:
            lvl, coords = m.source.parse_chunk_id(cid)
            cd = m.get(lvl, coords)
            out.append({"chunk_id": cid, "status": "empty" if cd.source_bytes == 0 else "ready",
                        "voxel_bytes": int(cd.array.nbytes), "source_bytes": cd.source_bytes})
        except Exception as e:  # noqa
            out.append({"chunk_id": cid, "status": "error", "error": str(e)})
    return {"chunks": out}


@app.post("/api/chunks/cancel")
def cancel(body: ChunkCancelBody):
    # HTTP prototype: chunks are pulled, so cancel is advisory/no-op server-side.
    # Recorded for symmetry with the future WebSocket transport.
    return {"cancelled": body.chunk_ids}


@app.post("/api/prefetch")
def prefetch(body: ChunkRequestBody, block: str = Query(None)):
    m = get_block(block).manager
    specs = []
    for cid in body.chunk_ids:
        try:
            specs.append(m.source.parse_chunk_id(cid))
        except Exception:  # noqa
            pass
    return {"submitted": m.prefetch(specs)}


@app.get("/api/chunk/{chunk_id}")
async def get_chunk(chunk_id: str, channels: str = Query("0,1,2,3"),
                    block: str = Query(None), glass: int = Query(0)):
    m = get_block(block).manager
    try:
        lvl, coords = m.source.parse_chunk_id(chunk_id)
    except Exception:
        raise HTTPException(400, f"bad chunk_id {chunk_id}")
    ch_list = [int(x) for x in channels.split(",") if x != ""]
    cd = m.get(lvl, coords)
    payload = m.serialize(cd, ch_list, glass=bool(glass) and GLASS)
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Chunk-Id": chunk_id, "X-Chunk-Status":
                             "empty" if cd.source_bytes == 0 else "ready",
                             "Cache-Control": "no-store"})


@app.get("/api/stats")
def stats():
    build_blocks()
    per_block = {}
    agg_tracked = 0
    for bid in _order:
        st = _blocks[bid].manager.stats()
        st["prefetch_submitted"] = _blocks[bid].manager.prefetch_submitted
        per_block[bid] = st
        agg_tracked += st.get("tracked", 0)
    return {"tracked": agg_tracked, "blocks": per_block,
            "gpu": _gpu.info() if _gpu is not None else {"available": False}}


# ---------- centered ROI cuboid (for the 8-level ROI viewer) ----------
ROI_HTML = os.path.normpath(os.path.join(HERE, "..", "client", "roi585.html"))


@app.get("/roi")
def roi_page():
    if os.path.exists(ROI_HTML):
        return FileResponse(ROI_HTML)
    return JSONResponse({"error": "client/roi585.html missing"}, status_code=404)


ROI_SETTINGS_JSON = os.path.join(HERE, "roi_settings.json")
_ROI_SETTINGS_FALLBACK = {
    "size": 2048, "mode": "region", "opacity": 1.1, "quality": 224,
    "render": {"mode": 0, "wl": 0.31, "ww": 0.46, "thresh": 0.04,
               "iso": 0.33, "light": 0.8, "cmap": 0, "jitter": True},
    "center": {"fx": 0.5, "fy": 0.5, "fz": 0.5},
    "roiView": {"yaw": 0.5, "pitch": 0.3, "zoom": 1.0},
    "pickerView": {"yaw": 0.6, "pitch": 0.35, "zoom": 1.0},
}
_roi_settings_lock = threading.Lock()


@app.get("/api/roi/settings")
def roi_settings_get():
    """Default ROI-viewer settings (the backend baseline the browser starts from
    and can reset to). The browser remembers per-session tweaks itself."""
    with _roi_settings_lock:
        try:
            with open(ROI_SETTINGS_JSON) as f:
                return json.load(f)
        except Exception:
            return dict(_ROI_SETTINGS_FALLBACK)


@app.put("/api/roi/settings")
def roi_settings_put(payload: dict = Body(...)):
    """Promote the posted settings to the backend default (writes roi_settings.json)."""
    if not isinstance(payload, dict):
        raise HTTPException(400, "settings must be a JSON object")
    with _roi_settings_lock:
        tmp = ROI_SETTINGS_JSON + ".tmp"
        with open(tmp, "w") as f:
            json.dump(payload, f, indent=2)
        os.replace(tmp, ROI_SETTINGS_JSON)      # atomic swap
    return {"status": "saved"}


# ---------- anatomical block transforms (OME-Zarr -> shared RAS mm) ----------
# Replicates the Neuroglancer app: each block's OME-Zarr is placed by its omeToRas
# affine (BLOCK_META). omeToRas maps OME physical mm -> RAS mm, with its 3 input
# columns acting on the OME axes in z,y,x order and the last column a translation
# in mm; it is fed physical mm = voxel * (zScaleMeters, inPlaneScale, inPlaneScale)
# — the same per-block scales this server now bakes with (_block_voxel_um).
#
# The viewer's brick coords are centred physical mm in LOCAL (x,y,z) (see
# chunks/manager.world_bbox_mm). So for a local point p=(x,y,z):
#   ome(z,y,x) = (p + halfExtent)  reordered to z,y,x
#   RAS(x,y,z) = A @ ome + t
# giving a 4x4 W (col-major) with columns from A reversed (local x<-col z-index):
#   W_lin[:,x]=A[:,2]  W_lin[:,y]=A[:,1]  W_lin[:,z]=A[:,0]
#   W_trans   = A @ (halfEz,halfEy,halfEx) + t
# W is non-rigid on purpose (omeToRas carries the anisotropic z scaling).


@app.get("/api/transforms")
def transforms_get(brain: str = Query(None)):
    """Per-block world matrices placing each block's OME-Zarr into shared RAS mm,
    matching the Neuroglancer reassembly. Col-major 4x4 mapping the viewer's LOCAL
    centred-mm (x,y,z) frame -> RAS (x,y,z). Keyed by block id."""
    out, brains = {}, {}
    for bid, ctx in _blocks.items():
        m = BLOCK_META.get(bid)
        if not m or "omeToRas" not in m:
            continue
        if brain and m.get("brain") != brain:
            continue
        A = [row[:3] for row in m["omeToRas"]]              # rows x,y,z; cols z,y,x
        t = [row[3] for row in m["omeToRas"]]
        Ez, Ey, Ex = (e * 1000.0 for e in ctx.source.get_metadata().extent_m)  # mm (z,y,x)
        half = [Ez / 2.0, Ey / 2.0, Ex / 2.0]              # z,y,x — matches A's columns
        tr = [A[i][0] * half[0] + A[i][1] * half[1] + A[i][2] * half[2] + t[i]
              for i in range(3)]
        W = [A[0][2], A[1][2], A[2][2], 0.0,               # local x  <- ome col x (idx 2)
             A[0][1], A[1][1], A[2][1], 0.0,               # local y  <- ome col y (idx 1)
             A[0][0], A[1][0], A[2][0], 0.0,               # local z  <- ome col z (idx 0)
             tr[0],   tr[1],   tr[2],   1.0]
        out[bid] = {"brain": m.get("brain"), "block": bid, "matrix": W,
                    "matrix_origin": W, "matrix_center": W,   # single correct matrix
                    "centerX": m.get("centerX"),
                    "extent_mm": [Ex, Ey, Ez]}
        brains.setdefault(m.get("brain"), []).append(bid)
    # `transforms` is keyed by block id (browser); `list` is the same data as an
    # array (JsonUtility-friendly for the Unity client, which can't parse dict keys).
    lst = [{"block": bid, "matrix": out[bid]["matrix"],
            "centerX": out[bid]["centerX"], "extent_mm": out[bid]["extent_mm"]}
           for bid in out]
    return {"space": "RAS-mm", "brains": brains, "transforms": out, "list": lst}


def _roi_side(mode: str, size: int, level: int) -> int:
    """Box side in THIS level's voxels.
    mode 'region' -> `size` is in L0 voxels => same physical box at every level.
    mode 'voxel'  -> `size` is in this level's voxels => same voxel count per level.
    """
    if mode == "voxel":
        return max(1, size)
    return max(1, round(size / (2 ** level)))


@app.get("/api/roi/info")
def roi_info(block: str = Query("585"), size: int = Query(2048),
             mode: str = Query("region")):
    """Per-level metadata for a centered cuboid ROI (full-z)."""
    ctx = get_block(block)
    info = ctx.selector.info
    levels = []
    for l in info.levels:
        sz, sy, sx, sc = l.shape
        s = _roi_side(mode, size, l.level)
        vz, vy, vx, _ = l.voxel_size_m
        levels.append({
            "level": l.level,
            "roi_vox": [int(min(s, sy)), int(min(s, sx)), int(sz)],  # y,x,z
            "xy_voxel_um": vx * 1e6,
            "roi_mm": [round(min(s, sx) * vx * 1000, 4),
                       round(min(s, sy) * vy * 1000, 4),
                       round(sz * vz * 1000, 4)],  # x,y,z
        })
    return {"block": ctx.block_id, "size": size, "mode": mode,
            "finest_level": info.finest_level, "coarsest_level": info.coarsest_level,
            "levels": levels}


@app.get("/api/roi")
async def roi(block: str = Query("585"), level: int = Query(...),
              size: int = Query(2048), cap: int = Query(256),
              mode: str = Query("region"),
              fx: float = Query(0.5), fy: float = Query(0.5),
              fz: float = Query(0.5),
              rgb: int = Query(0), flat: int = Query(0)):
    """Cuboid (full-z) of `block` at `level`, GPU-downsampled so max axis <= cap.
    Returns BVX2.
    mode 'region' -> `size` is L0 voxels (same physical box across levels);
    mode 'voxel'  -> `size` is this level's voxels (same voxel count per level).
    fx/fy in [0,1] place the ROI center within the slab plane (0.5,0.5 = middle);
    the box is clamped so it stays fully inside the slab.
    rgb=1  -> colour channels [0,1,2] (nch=3) instead of gray+mask [0,3] (nch=2).
    flat=1 -> a SINGLE z-slice (dz=1) at fz in [0,1] instead of the full-z cuboid
              (used for the finest 'single tile' panel)."""
    ctx = get_block(block)
    fx = min(1.0, max(0.0, fx)); fy = min(1.0, max(0.0, fy)); fz = min(1.0, max(0.0, fz))
    ckey = (ctx.block_id, level, size, mode, cap, round(fx, 4), round(fy, 4),
            round(fz, 4) if flat else None, bool(rgb), bool(flat))
    cached = _roi_cache_get(ckey)
    if cached is not None:
        await _throttle(len(cached))
        return Response(content=cached, media_type="application/octet-stream",
                        headers={"X-Roi-Level": str(level), "X-Roi-Cache": "hit",
                                 "Cache-Control": "no-store"})
    payload = _build_roi_payload(ctx, level, size, mode, cap, fx, fy,
                                 rgb=bool(rgb), flat=bool(flat), fz=fz)
    _roi_cache_put(ckey, payload)
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Roi-Level": str(level), "X-Roi-Cache": "miss",
                             "Cache-Control": "no-store"})


def _build_roi_payload(ctx: BlockCtx, level: int, size: int, mode: str, cap: int,
                       fx: float = 0.5, fy: float = 0.5,
                       rgb: bool = False, flat: bool = False, fz: float = 0.5) -> bytes:
    """Fetch + downsample an ROI and pack it as BVX2 (sync).
    fx/fy in [0,1] locate the box center in the slab plane; clamped so the
    box stays inside the slab (fx=fy=0.5 -> geometric center, the default).
    rgb  -> colour channels [0,1,2] (nch=3) instead of gray+mask [0,3] (nch=2).
    flat -> a single z-slice (dz=1) at fz instead of the full-z cuboid."""
    global _roi_gpu_rr
    src = ctx.source
    li = src.level_info(level)
    sz, sy, sx, sc = li.shape
    s = _roi_side(mode, size, level)
    half = s // 2
    cy = int(round(fy * sy)); cx = int(round(fx * sx))
    cy = min(max(cy, half), max(half, sy - half))   # keep box inside the slab
    cx = min(max(cx, half), max(half, sx - half))
    y0, y1 = max(0, cy - half), min(sy, cy + half)
    x0, x1 = max(0, cx - half), min(sx, cx + half)

    # rgb -> all channels (R,G,B + tissue mask) so the client can use the mask as
    # alpha; gray+mask (the cuboids) stays the compact 2-channel [0,3].
    channels = [0, 1, 2, 3] if rgb else [0, 3]
    if flat:                                          # a single z-slice at fz
        zc = min(max(int(round(fz * sz)), 0), sz - 1)
        z0, z1 = zc, zc + 1
    else:
        z0, z1 = 0, sz

    arr = src.get_roi(level, z0, z1, y0, y1, x0, x1, channels=channels, workers=ROI_WORKERS)
    # GPU downsample x/y to cap, round-robined across the 8 A100s; z strided.
    if _gpu is not None and max(arr.shape[1], arr.shape[2]) > cap:
        dev = _roi_gpu_rr; _roi_gpu_rr += 1
        arr = np.ascontiguousarray(_gpu.resample_xy_max(arr, cap, device=dev), dtype=np.uint8)
    if not flat and arr.shape[0] > cap:
        arr = np.ascontiguousarray(arr[:: math.ceil(arr.shape[0] / cap)])

    dz, dy, dx, nch = arr.shape
    vz, vy, vx, _ = li.voxel_size_m
    dxmm = (x1 - x0) * vx * 1000.0; dymm = (y1 - y0) * vy * 1000.0
    dzmm = (z1 - z0) * vz * 1000.0
    bbox = (-dxmm / 2, -dymm / 2, -dzmm / 2, dxmm / 2, dymm / 2, dzmm / 2)
    header = (b"BVX2" + struct.pack("<BBH", level, nch, 0)
              + struct.pack("<iii", z0, y0, x0) + struct.pack("<HHH", dz, dy, dx)
              + struct.pack("<6f", *bbox))
    return header + np.ascontiguousarray(arr, np.uint8).tobytes()


def _compute_norm_windows():
    """Per-block gray-intensity normalization window for the glass look.

    Each of the 5 blocks was stained/scanned separately, so their gray-level
    distributions differ -- that mismatch is a big part of the inter-block seams
    in the reassembled brain. We sample each block's coarsest level once, take a
    robust (p2,p98) window of channel 0, and hand it to the manager; serialize()
    then maps that window to 0..255 so every block shares one brightness range."""
    if not GLASS:
        return
    build_blocks()
    for bid in _order:
        ctx = _blocks[bid]
        try:
            info = ctx.source.get_metadata()
            lvl = info.coarsest_level
            full = (slice(None), slice(None), slice(None))
            vol = ctx.source.get_region(lvl, 0, full)        # (z,y,x,1) ch0
            lo, hi = _gpu.sample_window(vol, channel=0, lo=0.02, hi=0.98)
            ctx.manager.norm_lohi = (lo, hi)
            print(f"[glass-norm] block {bid}: gray window=({lo:.1f},{hi:.1f}) "
                  f"from L{lvl} {tuple(vol.shape[:3])}")
        except Exception as e:  # noqa
            print(f"[glass-norm] block {bid} failed: {e}")


def _prewarm_roi():
    """Background: fill the ROI cache for the viewer's default panels so the
    first page load is instant. Uses the otherwise-idle DGX + parallel access."""
    build_blocks()
    ctx = _blocks.get("585") or _blocks[_order[0]]
    for size in (2048,):
        for mode in ("region",):
            for level in (7, 4, 2, 0):        # coarse->fine
                key = (ctx.block_id, level, size, mode, 256, 0.5, 0.5)  # centered default
                if _roi_cache_get(key) is not None:
                    continue
                try:
                    _roi_cache_put(key, _build_roi_payload(ctx, level, size, mode, 256))
                    print(f"[roi-prewarm] cached L{level} size={size} {mode}")
                except Exception as e:  # noqa
                    print(f"[roi-prewarm] L{level} failed: {e}")


if __name__ == "__main__":
    import uvicorn
    ap = argparse.ArgumentParser()
    ap.add_argument("--zarr", action="append", default=None,
                    help="Zarr root URL for one block; repeat for multiple blocks. "
                         "A single value may also be comma-separated. "
                         "Omit to use the built-in 5-block set.")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8010)
    ap.add_argument("--min-level", type=int, default=3,
                    help="finest level the DGX will stream (0=full res, huge)")
    ap.add_argument("--delay-ms", type=float, default=0.0)
    ap.add_argument("--bandwidth-limit", type=float, default=0.0,
                    help="MB/s cap for chunk payloads (0=unlimited)")
    ap.add_argument("--cache-mb", type=int, default=2048)
    ap.add_argument("--workers", type=int, default=32, help="parallel prefetch workers")
    ap.add_argument("--gpu", dest="gpu", action="store_true", default=True,
                    help="use A100 GPU processing (default on, falls back to CPU)")
    ap.add_argument("--no-gpu", dest="gpu", action="store_false")
    ap.add_argument("--gpu-max-xy", type=int, default=512,
                    help="server-side resample cap so brick textures stay uniform (0=off)")
    ap.add_argument("--glass", dest="glass", action="store_true", default=True,
                    help="Nissl see-through preprocessing: per-block gray "
                         "normalization + precomputed gradient channel (default on)")
    ap.add_argument("--no-glass", dest="glass", action="store_false")
    ap.add_argument("--prefetch-top", type=int, default=8,
                    help="warm N top-priority chunks on each /view (0=off)")
    ap.add_argument("--voxel-um", default="20,0.5,0.5",
                    help="true finest-level voxel size 'z,y,x' in microns, "
                         "overriding the placeholder OME scale (default 20,0.5,0.5)")
    ap.add_argument("--voxel-um-raw", dest="voxel_raw", action="store_true",
                    help="trust the Zarr OME scale verbatim (no voxel override)")
    args = ap.parse_args()

    voxel_um = None
    if not args.voxel_raw:
        parts = [float(v) for v in args.voxel_um.split(",") if v.strip() != ""]
        if len(parts) != 3:
            ap.error("--voxel-um must be 'z,y,x' (three values in microns)")
        voxel_um = tuple(parts)

    # normalise --zarr: repeated flags and/or comma-separated -> flat list
    roots: List[str] = []
    for item in (args.zarr or []):
        roots.extend([u.strip() for u in item.split(",") if u.strip()])
    if not roots:
        roots = list(DEFAULT_ZARR_ROOTS)

    import app_zarr as _self  # type: ignore
    _self.ZARR_ROOTS = roots
    _self.MIN_LEVEL = args.min_level
    _self.DELAY_MS = args.delay_ms
    _self.BANDWIDTH_LIMIT_MBPS = args.bandwidth_limit
    _self.CACHE_BYTES = args.cache_mb * 1024 * 1024
    _self.WORKERS = args.workers
    _self.USE_GPU = args.gpu
    _self.GPU_MAX_XY = args.gpu_max_xy
    _self.GLASS = args.glass
    _self.PREFETCH_TOP = args.prefetch_top
    _self.VOXEL_UM_FINEST = voxel_um

    print(f"[dgx-zarr] blocks={len(roots)}:")
    for u in roots:
        print(f"[dgx-zarr]   {u}")
    print(f"[dgx-zarr] voxel_um(z,y,x)={voxel_um if voxel_um else 'raw-metadata'}")
    print(f"[dgx-zarr] min_level={args.min_level} workers={args.workers} gpu={args.gpu} "
          f"gpu_max_xy={args.gpu_max_xy} glass={args.glass} prefetch_top={args.prefetch_top} "
          f"delay={args.delay_ms}ms bw={args.bandwidth_limit}MB/s cache={args.cache_mb}MB")
    uvicorn.run(_self.app, host=args.host, port=args.port, log_level="info")

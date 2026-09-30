"""DGX Brain Streaming Server -- real Zarr edition, multi-block.

    python app_zarr.py \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/584_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/585_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/586_ALL_3d.zarr \
        --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/587_ALL_3d.zarr \
        --host 0.0.0.0 --port 8010 --min-level 4

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

from datasource import HttpZarrBrainSource, LocalZarrBrainSource
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

# ---- multi-brain registry ------------------------------------------------
# Each brain is an independent dataset made of one or more Zarr "blocks". The
# client shows a selector and asks for one brain at a time; every data endpoint
# resolves a block by its (globally unique) id, so the block param keeps working.
#   kind  "http"  -> HttpZarrBrainSource  (remote sharded Zarr v3)
#         "local" -> LocalZarrBrainSource (on-disk sharded Zarr v3)
#   render "gray_mask" -> ch0 grayscale + ch3 tissue mask (the 5-block stroke set)
#          "rgb"       -> ch0..2 fused colour (the hb02 whole-brain volume)
#   voxel_override True lets the histology map replace a placeholder OME scale;
#     hb02 ships a REAL scale so it stays False (trust the metadata verbatim).
HB02_ZARR = "/home/users/azhar/projectM/viewer_data/fused_8um/hb02_fused.zarr"
HB02_LABELS_ZARR = "/home/users/azhar/projectM/viewer_data/fused_8um/hb02_labels.zarr"
DEFAULT_BRAINS = [
    {"id": "Stroke_1", "label": "Brain 1 — Stroke_1 (5 blocks)", "kind": "http",
     "roots": list(DEFAULT_ZARR_ROOTS), "voxel_override": True, "render": "gray_mask"},
    {"id": "hb02", "label": "Brain 2 — hb02 fused (8µm)", "kind": "local",
     "roots": [HB02_ZARR], "voxel_override": False, "render": "rgb",
     "block_ids": ["hb02"], "labels": HB02_LABELS_ZARR},
]
BRAINS = [dict(b) for b in DEFAULT_BRAINS]
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
PREFETCH_TOP = 8                 # warm this many top-priority chunks on each view
ROI_WORKERS = 192                # parallel inner-chunk range GETs for ROI (DDN can take it)
_roi_gpu_rr = 0                  # round-robin GPU selector for ROI resample
BRAIN_INPLANE_CAP = 384          # whole-brain picker: finest per-block level with
                                 # in-plane max <= this is used (detail vs. fetch cost)

# Small side-cache of the whole-brain assembly metadata (world AABB / dims) that
# accompanies each /api/brain payload; keyed identically to the payload cache.
_brain_info_cache: "Dict[tuple, dict]" = {}

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
    brain: str = ""
    render: str = "gray_mask"          # "gray_mask" | "rgb"
    default_transform: dict = field(default_factory=dict)


_gpu: Optional[GpuProcessor] = None
_blocks: "Dict[str, BlockCtx]" = {}
_order: List[str] = []
_labels: "Dict[str, BlockCtx]" = {}   # brain_id -> label-map BlockCtx (render="labels")
# brain_id -> {label, kind, render, blocks:[block_id,...]} ; and ordered ids
_brains_meta: "Dict[str, dict]" = {}
_brain_order: List[str] = []


def _roi_channels(ctx: BlockCtx) -> List[int]:
    """Which source channels the ROI / block-volume pipeline reads for a block.
    rgb -> the three colour channels; gray_mask -> grayscale(0) + tissue mask(3)."""
    return [0, 1, 2] if ctx.render == "rgb" else [0, 3]


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
    seen: Dict[str, int] = {}
    for brain in BRAINS:
        bctxs: List[BlockCtx] = []
        block_ids = brain.get("block_ids") or []
        for i, root in enumerate(brain["roots"]):
            # id: explicit from the registry, else derived from the root name
            bid = block_ids[i] if i < len(block_ids) else _block_id_from_url(root)
            if bid in seen:                 # de-dupe id collisions deterministically
                seen[bid] += 1
                bid = f"{bid}_{seen[bid]}"
            else:
                seen[bid] = 0
            try:
                if brain["kind"] == "local":
                    src = LocalZarrBrainSource(
                        root, voxel_um_finest=(_block_voxel_um(bid)
                                               if brain.get("voxel_override") else None))
                else:
                    src = HttpZarrBrainSource(
                        root, voxel_um_finest=(_block_voxel_um(bid)
                                               if brain.get("voxel_override") else None))
                info = src.get_metadata()   # fail fast here if the root is unreachable
            except Exception as e:  # noqa -- a bad brain must not sink the others
                print(f"[dgx-zarr] SKIP brain {brain['id']} block {bid} "
                      f"({brain['kind']} {root}): {e}")
                continue
            # render mode: registry hint, else infer from channel count
            render = brain.get("render") or ("rgb" if info.channels == 3 else "gray_mask")
            mgr = ChunkManager(src, LruChunkCache(CACHE_BYTES),
                               workers=WORKERS, processor=_gpu, max_xy=GPU_MAX_XY)
            sel = CameraViewSelector(src, min_level=MIN_LEVEL)
            ctx = BlockCtx(bid, root, src, mgr, sel, brain=brain["id"], render=render)
            bctxs.append(ctx)
            _blocks[bid] = ctx
            _order.append(bid)
            print(f"[dgx-zarr] brain {brain['id']} block {bid} [{render}]: {root}")
        if not bctxs:
            continue
        _auto_layout(bctxs)                  # lay each brain out independently, centred
        # optional segmentation label map for this brain (same geometry as the
        # tissue; served coloured + toggleable as a separate overlay volume).
        lpath = brain.get("labels")
        if lpath and os.path.isdir(lpath):
            try:
                lsrc = LocalZarrBrainSource(lpath, voxel_um_finest=None)
                lsrc.get_metadata()
                lmgr = ChunkManager(lsrc, LruChunkCache(CACHE_BYTES),
                                    workers=WORKERS, processor=_gpu, max_xy=GPU_MAX_XY)
                lsel = CameraViewSelector(lsrc, min_level=MIN_LEVEL)
                _labels[brain["id"]] = BlockCtx(f"{brain['id']}_labels", lpath, lsrc,
                                                lmgr, lsel, brain=brain["id"], render="labels")
                print(f"[dgx-zarr] brain {brain['id']} labels: {lpath}")
            except Exception as e:  # noqa
                print(f"[dgx-zarr] SKIP labels for brain {brain['id']} ({lpath}): {e}")
        _brains_meta[brain["id"]] = {
            "id": brain["id"], "label": brain.get("label", brain["id"]),
            "kind": brain["kind"], "render": bctxs[0].render,
            "has_labels": brain["id"] in _labels,
            "blocks": [c.block_id for c in bctxs]}
        _brain_order.append(brain["id"])
    print(f"[dgx-zarr] gpu={_gpu.info()} workers={WORKERS} max_xy={GPU_MAX_XY} "
          f"brains={_brain_order} blocks={_order}")
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
    # prewarm the ROI viewer's default panels in the background (idle DGX work),
    # so the first /roi page load is served from cache.
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
            "brains": _brain_order, "blocks": _order}


@app.get("/api/protocol")
def protocol():
    return PROTOCOL_DOC


def _block_info_dict(ctx: BlockCtx) -> dict:
    s = ctx.selector
    info = s.info
    return {
        "block_id": ctx.block_id,
        "brain": ctx.brain,
        "render": ctx.render,
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


@app.get("/api/brains")
def brains():
    """The selectable brains (id + human label + block ids). The client renders a
    dropdown from this and then loads one brain via /api/dataset/info?brain=<id>."""
    build_blocks()
    out = []
    for i, bid in enumerate(_brain_order):
        m = dict(_brains_meta[bid])
        m["default"] = (i == 0)
        out.append(m)
    return {"brains": out, "default": _brain_order[0] if _brain_order else None}


def _brain_block_ids(brain: Optional[str]) -> List[str]:
    """Ordered block ids for a brain; default to the first brain, then to all."""
    if not brain:
        brain = _brain_order[0] if _brain_order else None
    meta = _brains_meta.get(brain)
    if meta:
        return [b for b in meta["blocks"] if b in _blocks]
    return list(_order)


@app.get("/api/dataset/info")
def dataset_info(brain: str = Query(None)):
    """Blocks for ONE brain (defaults to the first). Pass ?brain=<id> to switch."""
    build_blocks()
    ids = _brain_block_ids(brain)
    blocks = [_block_info_dict(_blocks[bid]) for bid in ids]
    # top-level convenience mirror of the first block keeps single-block clients
    # working; multi-block clients read `blocks`.
    first = dict(blocks[0]) if blocks else {}
    first.pop("block_id", None)
    br = brain or (_brain_order[0] if _brain_order else None)
    return {"brain": br,
            "brains": [_brains_meta[b] for b in _brain_order],
            "labels": _label_info(br),
            "blocks": blocks, **first}


def _label_info(brain: Optional[str]) -> Optional[dict]:
    """Label-map metadata for a brain (or None): the pyramid levels the overlay
    can be fetched at + a sensible default level. Same geometry as the tissue."""
    lctx = _labels.get(brain or "")
    if lctx is None:
        return None
    info = lctx.selector.info
    return {"available": True, "block": lctx.block_id,
            "finest_level": info.finest_level, "coarsest_level": info.coarsest_level,
            "min_streamable_level": lctx.selector.min_level,
            "default_level": max(info.finest_level,
                                 min(lctx.selector.min_level, info.coarsest_level)),
            "n_channels": 1, "extent_mm": [e * 1000.0 for e in info.extent_m]}


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
                    block: str = Query(None)):
    m = get_block(block).manager
    try:
        lvl, coords = m.source.parse_chunk_id(chunk_id)
    except Exception:
        raise HTTPException(400, f"bad chunk_id {chunk_id}")
    ch_list = [int(x) for x in channels.split(",") if x != ""]
    cd = m.get(lvl, coords)
    payload = m.serialize(cd, ch_list)
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


def _block_world_matrix(ctx: BlockCtx):
    """Placement of one block's OME-Zarr into shared RAS mm (the Neuroglancer
    reassembly). Returns (M, W, ext) or None if the block has no omeToRas.

      M   : numpy 4x4 (row-major) mapping LOCAL centred-mm (x,y,z,1) -> RAS mm.
      W   : the same matrix as a col-major length-16 list (gl-matrix / browser).
      ext : [Ex, Ey, Ez] block extent in mm (x,y,z).

    W is non-rigid on purpose (omeToRas carries the anisotropic z scaling); see
    the derivation note above. This is the single source of truth for both
    /api/transforms and the whole-brain assembly in /api/brain."""
    m = BLOCK_META.get(ctx.block_id)
    if not m or "omeToRas" not in m:
        return None
    A = np.array([row[:3] for row in m["omeToRas"]], dtype=float)   # rows x,y,z; cols z,y,x
    t = np.array([row[3] for row in m["omeToRas"]], dtype=float)
    Ez, Ey, Ex = (e * 1000.0 for e in ctx.source.get_metadata().extent_m)  # mm (z,y,x)
    half = np.array([Ez / 2.0, Ey / 2.0, Ex / 2.0])                # z,y,x — matches A's columns
    tr = A @ half + t
    M = np.eye(4)
    M[0, 0], M[0, 1], M[0, 2] = A[0][2], A[0][1], A[0][0]          # local x,y,z <- ome cols x,y,z
    M[1, 0], M[1, 1], M[1, 2] = A[1][2], A[1][1], A[1][0]
    M[2, 0], M[2, 1], M[2, 2] = A[2][2], A[2][1], A[2][0]
    M[:3, 3] = tr
    W = [M[0, 0], M[1, 0], M[2, 0], 0.0,                           # col-major: column = local axis
         M[0, 1], M[1, 1], M[2, 1], 0.0,
         M[0, 2], M[1, 2], M[2, 2], 0.0,
         M[0, 3], M[1, 3], M[2, 3], 1.0]
    return M, W, [Ex, Ey, Ez]


def _block_placement(ctx: BlockCtx):
    """(M numpy 4x4 local(x,y,z)->world mm, ext[x,y,z] mm) for the whole-brain
    picker. Uses the histology omeToRas when the block has one; otherwise an
    identity placement translated by the block's default (auto-layout) transform —
    which is what a single-block brain like hb02 needs (centred at the origin)."""
    mw = _block_world_matrix(ctx)
    if mw is not None:
        M, _W, ext = mw
        return M, np.array(ext, dtype=float)
    ez, ey, ex = (e * 1000.0 for e in ctx.source.get_metadata().extent_m)  # mm (z,y,x)
    M = np.eye(4)
    M[:3, 3] = np.array(ctx.default_transform.get("translation", [0.0, 0.0, 0.0]), float)
    return M, np.array([ex, ey, ez], dtype=float)


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
        _M, W, ext = _block_world_matrix(ctx)
        out[bid] = {"brain": m.get("brain"), "block": bid, "matrix": W,
                    "matrix_origin": W, "matrix_center": W,   # single correct matrix
                    "centerX": m.get("centerX"),
                    "extent_mm": ext}
        brains.setdefault(m.get("brain"), []).append(bid)
    # `transforms` is keyed by block id (browser); `list` is the same data as an
    # array (JsonUtility-friendly for the Unity client, which can't parse dict keys).
    lst = [{"block": bid, "matrix": out[bid]["matrix"],
            "centerX": out[bid]["centerX"], "extent_mm": out[bid]["extent_mm"]}
           for bid in out]
    return {"space": "RAS-mm", "brains": brains, "transforms": out, "list": lst}


# ---------- whole-brain assembly (reassembled slice picker) ------------------
# The ROI viewer's picker used to show a single block. It now shows the WHOLE
# brain: every block is placed by its RAS-mm matrix (above) and resampled into
# ONE axis-aligned RAS grid (a coarse RG8 volume, gray + tissue-mask, same
# 2-channel layout as /api/roi). The browser slices this world grid to pick a
# center anywhere in the brain, then maps that world point back into whichever
# block owns it to fetch the high-LOD ROI. Non-overlapping blocks; overlaps are
# resolved by keeping the strongest tissue signal (max of mask*(1-gray)).

def _default_brain() -> Optional[str]:
    return _brain_order[0] if _brain_order else None


def _brain_source_level(ctx: BlockCtx, level_q: int) -> int:
    """Per-block source level for the whole-brain picker. level_q>=0 forces a
    level (clamped); otherwise pick the finest level whose in-plane max axis is
    <= BRAIN_INPLANE_CAP (detail without an expensive full-fine fetch)."""
    info = ctx.selector.info
    if level_q is not None and level_q >= 0:
        return max(info.finest_level, min(level_q, info.coarsest_level))
    best = info.coarsest_level
    for l in sorted(info.levels, key=lambda l: l.level):   # fine -> coarse
        _sz, sy, sx, _sc = l.shape
        if max(sy, sx) <= BRAIN_INPLANE_CAP:
            best = l.level
            break
    return best


def _build_brain_payload(brain: Optional[str], level_q: int, cap: int):
    """Assemble every placed block of `brain` into one RAS-mm RG8 world grid.
    Returns (payload BVX2, info dict). The BVX2 bbox is centred on the origin so
    the picker renders the brain centred; the true world AABB rides in `info`
    (worldMin/worldMax/span) so the browser can map picks back to blocks."""
    build_blocks()
    placed = []                                            # (ctx, M numpy4x4, ext[x,y,z])
    for bid in _brain_block_ids(brain):
        ctx = _blocks[bid]
        M, ext = _block_placement(ctx)
        placed.append((ctx, M, ext))
    if not placed:
        raise HTTPException(404, f"no placed blocks for brain {brain!r}")
    rgb = placed[0][0].render == "rgb"                     # brains are homogeneous

    # world AABB over every block's 8 corners
    signs = np.array([[sx, sy, sz] for sx in (-1, 1) for sy in (-1, 1)
                      for sz in (-1, 1)], dtype=float)
    mn = np.full(3, np.inf); mx = np.full(3, -np.inf)
    for _ctx, M, ext in placed:
        w = (M[:3, :3] @ (signs * (ext / 2.0)).T).T + M[:3, 3]
        mn = np.minimum(mn, w.min(0)); mx = np.maximum(mx, w.max(0))
    span = mx - mn

    # output grid: isotropic voxel, longest axis == cap
    vsize = float(span.max()) / max(1, cap)
    nx = int(min(cap, max(1, round(span[0] / vsize))))
    ny = int(min(cap, max(1, round(span[1] / vsize))))
    nz = int(min(cap, max(1, round(span[2] / vsize))))

    # world-mm coordinate of every output voxel centre, flattened in (z,y,x) order
    xs = mn[0] + (np.arange(nx) + 0.5) * span[0] / nx
    ys = mn[1] + (np.arange(ny) + 0.5) * span[1] / ny
    zs = mn[2] + (np.arange(nz) + 0.5) * span[2] / nz
    WX = np.broadcast_to(xs.reshape(1, 1, nx), (nz, ny, nx))
    WY = np.broadcast_to(ys.reshape(1, ny, 1), (nz, ny, nx))
    WZ = np.broadcast_to(zs.reshape(nz, 1, 1), (nz, ny, nx))
    pts = np.stack([WX, WY, WZ], axis=-1).reshape(-1, 3).astype(np.float32)

    N = pts.shape[0]
    nch = 3 if rgb else 2
    chans = np.zeros((N, nch), np.uint8)   # rgb: (r,g,b) ; gray_mask: (gray, mask)
    score = np.full(N, -1.0, np.float32)   # keep strongest tissue signal on overlaps

    for ctx, M, ext in placed:
        lvl = _brain_source_level(ctx, level_q)
        li = ctx.source.level_info(lvl)
        bz, by, bx, _bc = li.shape
        arr = ctx.source.get_roi(lvl, 0, bz, 0, by, 0, bx,
                                 channels=_roi_channels(ctx), workers=ROI_WORKERS)
        az, ay, ax = arr.shape[:3]
        Minv = np.linalg.inv(M)
        loc = pts @ Minv[:3, :3].T + Minv[:3, 3]           # world -> local (x,y,z) mm
        hx, hy, hz = ext / 2.0
        inside = ((np.abs(loc[:, 0]) <= hx) & (np.abs(loc[:, 1]) <= hy)
                  & (np.abs(loc[:, 2]) <= hz))
        idx = np.nonzero(inside)[0]
        if idx.size == 0:
            continue
        u = (loc[idx, 0] + hx) / ext[0]; v = (loc[idx, 1] + hy) / ext[1]
        w = (loc[idx, 2] + hz) / ext[2]
        ix = np.clip((u * ax).astype(np.int32), 0, ax - 1)
        iy = np.clip((v * ay).astype(np.int32), 0, ay - 1)
        iz = np.clip((w * az).astype(np.int32), 0, az - 1)
        samp = arr[iz, iy, ix, :]                          # (k, nch)
        if rgb:                                            # tissue = departure from white
            s = 255.0 - samp.min(axis=1).astype(np.float32)
        else:                                              # tissue = mask*(1-gray)
            s = samp[:, 1].astype(np.float32) * (1.0 - samp[:, 0].astype(np.float32) / 255.0)
        better = s > score[idx]
        sel = idx[better]
        chans[sel] = samp[better]; score[sel] = s[better]

    vol = chans.reshape(nz, ny, nx, nch)

    hxm, hym, hzm = span / 2.0                             # centred bbox for the picker cube
    bbox = (-hxm, -hym, -hzm, hxm, hym, hzm)
    header = (b"BVX2" + struct.pack("<BBH", 0, nch, 0)
              + struct.pack("<iii", 0, 0, 0) + struct.pack("<HHH", nz, ny, nx)
              + struct.pack("<6f", *bbox))
    payload = header + np.ascontiguousarray(vol).tobytes()
    info = {"brain": brain, "render": "rgb" if rgb else "gray_mask",
            "dims": [nz, ny, nx],
            "worldMin": [float(mn[0]), float(mn[1]), float(mn[2])],
            "worldMax": [float(mx[0]), float(mx[1]), float(mx[2])],
            "span": [float(span[0]), float(span[1]), float(span[2])],
            "blocks": [c.block_id for c, _M, _e in placed]}
    return payload, info


@app.get("/api/brain")
async def brain(brain: str = Query(None), level: int = Query(-1),
                cap: int = Query(256)):
    """One reassembled RAS-mm RG8 world volume for the whole `brain` (BVX2). The
    ROI viewer's picker; cached like /api/roi (first build is bandwidth-bound)."""
    build_blocks()
    br = brain or _default_brain()
    ckey = ("brain", br, level, cap)
    cached = _roi_cache_get(ckey)
    if cached is not None:
        await _throttle(len(cached))
        return Response(content=cached, media_type="application/octet-stream",
                        headers={"X-Brain": str(br), "X-Cache": "hit",
                                 "Cache-Control": "no-store"})
    payload, info = _build_brain_payload(br, level, cap)
    _roi_cache_put(ckey, payload); _brain_info_cache[ckey] = info
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Brain": str(br), "X-Cache": "miss",
                             "Cache-Control": "no-store"})


@app.get("/api/brain/info")
def brain_info(brain: str = Query(None), level: int = Query(-1),
               cap: int = Query(256)):
    """World AABB (worldMin/worldMax/span mm) + dims of the /api/brain grid, so
    the browser can map a picked world point back into the owning block."""
    build_blocks()
    br = brain or _default_brain()
    ckey = ("brain", br, level, cap)
    info = _brain_info_cache.get(ckey)
    if info is None:                                       # build (and cache) on demand
        payload, info = _build_brain_payload(br, level, cap)
        _roi_cache_put(ckey, payload); _brain_info_cache[ckey] = info
    return info


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
              fx: float = Query(0.5), fy: float = Query(0.5)):
    """Cuboid (full-z) of `block` at `level`, channels [0,3],
    GPU-downsampled so max axis <= cap. Returns BVX2.
    mode 'region' -> `size` is L0 voxels (same physical box across levels);
    mode 'voxel'  -> `size` is this level's voxels (same voxel count per level).
    fx/fy in [0,1] place the ROI center within the slab plane (0.5,0.5 = middle);
    the box is clamped so it stays fully inside the slab."""
    ctx = get_block(block)
    fx = min(1.0, max(0.0, fx)); fy = min(1.0, max(0.0, fy))
    ckey = (ctx.block_id, level, size, mode, cap, round(fx, 4), round(fy, 4))
    cached = _roi_cache_get(ckey)
    if cached is not None:
        await _throttle(len(cached))
        return Response(content=cached, media_type="application/octet-stream",
                        headers={"X-Roi-Level": str(level), "X-Roi-Cache": "hit",
                                 "Cache-Control": "no-store"})
    payload = _build_roi_payload(ctx, level, size, mode, cap, fx, fy)
    _roi_cache_put(ckey, payload)
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Roi-Level": str(level), "X-Roi-Cache": "miss",
                             "Cache-Control": "no-store"})


def _build_roi_payload(ctx: BlockCtx, level: int, size: int, mode: str, cap: int,
                       fx: float = 0.5, fy: float = 0.5) -> bytes:
    """Fetch + downsample an ROI cuboid and pack it as BVX2 (sync).
    fx/fy in [0,1] locate the box center in the slab plane; clamped so the
    box stays inside the slab (fx=fy=0.5 -> geometric center, the default)."""
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

    arr = src.get_roi(level, 0, sz, y0, y1, x0, x1,
                      channels=_roi_channels(ctx), workers=ROI_WORKERS)
    # GPU downsample x/y to cap, round-robined across the 8 A100s; z strided.
    if _gpu is not None and max(arr.shape[1], arr.shape[2]) > cap:
        dev = _roi_gpu_rr; _roi_gpu_rr += 1
        arr = np.ascontiguousarray(_gpu.resample_xy_max(arr, cap, device=dev), dtype=np.uint8)
    if arr.shape[0] > cap:
        arr = np.ascontiguousarray(arr[:: math.ceil(arr.shape[0] / cap)])

    dz, dy, dx, nch = arr.shape
    vz, vy, vx, _ = li.voxel_size_m
    dxmm = (x1 - x0) * vx * 1000.0; dymm = (y1 - y0) * vy * 1000.0; dzmm = sz * vz * 1000.0
    bbox = (-dxmm / 2, -dymm / 2, -dzmm / 2, dxmm / 2, dymm / 2, dzmm / 2)
    header = (b"BVX2" + struct.pack("<BBH", level, nch, 0)
              + struct.pack("<iii", 0, y0, x0) + struct.pack("<HHH", dz, dy, dx)
              + struct.pack("<6f", *bbox))
    return header + np.ascontiguousarray(arr, np.uint8).tobytes()


# ---- merged per-block volume (SRD client) : levers #1/#2/#3/#5 --------------
# One tissue-cropped, colour+opacity-baked RGBA volume for the WHOLE block, plus
# a tiny occupancy volume for empty-space skipping. The client then draws ONE box
# per block (instead of ~58 z-slab bricks) with a single depth-spanning raymarch.
# The XY cap stays GPU_MAX_XY (512); lever #4 is intentionally excluded.

def _tissue_bbox(mask: np.ndarray, pad: int = 2):
    """(z0,z1,y0,y1,x0,x1) of mask>0 (padded); full box if empty. mask is (z,y,x)."""
    m = mask > 0
    dz, dy, dx = m.shape
    if not m.any():
        return (0, dz, 0, dy, 0, dx)
    za = np.where(m.any(axis=(1, 2)))[0]
    ya = np.where(m.any(axis=(0, 2)))[0]
    xa = np.where(m.any(axis=(0, 1)))[0]
    z0 = max(0, int(za[0]) - pad); z1 = min(dz, int(za[-1]) + 1 + pad)
    y0 = max(0, int(ya[0]) - pad); y1 = min(dy, int(ya[-1]) + 1 + pad)
    x0 = max(0, int(xa[0]) - pad); x1 = min(dx, int(xa[-1]) + 1 + pad)
    return (z0, z1, y0, y1, x0, x1)


def _build_block_volume_payload(ctx: BlockCtx, level: int, cap: int,
                                occ_block: int = 16) -> bytes:
    """Assemble the whole block at `level` -> BVX3 (RGBA volume + occupancy).

    Pipeline: full-z/full-xy fetch -> tissue-bbox crop (#3) -> x/y downsample to
    `cap` (z kept full) -> colour+opacity RGBA bake (#5) -> occupancy max-pool
    (#2). The world bbox is computed from the crop offsets so the cropped volume
    still places correctly under the block's omeToRas transform."""
    global _roi_gpu_rr
    proc = _gpu if _gpu is not None else GpuProcessor(enabled=False)
    src = ctx.source
    li = src.level_info(level)
    sz, sy, sx, sc = li.shape
    rgb = ctx.render == "rgb"

    # full x/y, full z -> rgb: (z,y,x,3) colour ; gray_mask: (z,y,x,2)=(gray, mask)
    arr = src.get_roi(level, 0, sz, 0, sy, 0, sx,
                      channels=_roi_channels(ctx), workers=ROI_WORKERS)

    # #3 crop to tissue bounding box (trim empty margins / end slabs). For rgb the
    # background is white, so "tissue" is any voxel that departs from white.
    if rgb:
        tissue_mask = arr.min(axis=-1) < 250
    else:
        tissue_mask = arr[..., 1] > 0
    z0, z1, y0, y1, x0, x1 = _tissue_bbox(tissue_mask)
    arr = np.ascontiguousarray(arr[z0:z1, y0:y1, x0:x1, :])
    odz, ody, odx = arr.shape[:3]                 # ORIGINAL cropped dims -> world bbox

    # x/y downsample to cap (cap stays 512; #4 excluded). z kept full for depth.
    if max(arr.shape[1], arr.shape[2]) > cap:
        dev = _roi_gpu_rr; _roi_gpu_rr += 1
        arr = np.ascontiguousarray(proc.resample_xy_max(arr, cap, device=dev), dtype=np.uint8)

    # #5 colour+opacity RGBA bake ; #2 occupancy from the baked alpha
    dev = _roi_gpu_rr; _roi_gpu_rr += 1
    rgba = np.ascontiguousarray(
        proc.bake_rgba_rgb(arr, device=dev) if rgb else proc.bake_rgba(arr, device=dev),
        dtype=np.uint8)
    occ = np.ascontiguousarray(proc.maxpool_occupancy(rgba, occ_block, device=dev), dtype=np.uint8)

    dz, dy, dx, nch = rgba.shape
    oz, oy, ox, _oc = occ.shape
    bbox = ctx.manager.world_bbox_mm(level, (z0, y0, x0, 0), (odz, ody, odx))
    header = (b"BVX3" + struct.pack("<BBH", level, nch, 0)
              + struct.pack("<iii", z0, y0, x0) + struct.pack("<HHH", dz, dy, dx)
              + struct.pack("<6f", *bbox))
    trailer = struct.pack("<HHHBB", oz, oy, ox, occ_block, 0)
    return header + rgba.tobytes() + trailer + occ.tobytes()


@app.get("/api/block_volume")
async def block_volume(block: str = Query(None), level: int = Query(...),
                       cap: int = Query(0)):
    """One merged RGBA volume + occupancy for the WHOLE block at `level` (BVX3).
    The SRD client's low-jitter path: one draw per block. Cached like /api/roi."""
    ctx = get_block(block)
    c = cap if cap else GPU_MAX_XY
    ckey = ("blockvol", ctx.block_id, level, c)
    cached = _roi_cache_get(ckey)
    if cached is not None:
        await _throttle(len(cached))
        return Response(content=cached, media_type="application/octet-stream",
                        headers={"X-Block": ctx.block_id, "X-Level": str(level),
                                 "X-Cache": "hit", "Cache-Control": "no-store"})
    payload = _build_block_volume_payload(ctx, level, c)
    _roi_cache_put(ckey, payload)
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Block": ctx.block_id, "X-Level": str(level),
                             "X-Cache": "miss", "Cache-Control": "no-store"})


def _build_label_volume_payload(lctx: BlockCtx, level: int, cap: int,
                                occ_block: int = 16) -> bytes:
    """Assemble the whole label map at `level` -> BVX3 (coloured RGBA + occupancy).

    Like _build_block_volume_payload but for a segmentation: labels are downsampled
    by NEAREST (stride) — never averaged (a mean of ids is meaningless) — then
    coloured through the label LUT (alpha 0 on background id 0). The pyramid already
    holds nearest-downsampled levels, so a stride here only trims to `cap`."""
    proc = _gpu if _gpu is not None else GpuProcessor(enabled=False)
    src = lctx.source
    li = src.level_info(level)
    sz, sy, sx, sc = li.shape

    arr = src.get_roi(level, 0, sz, 0, sy, 0, sx, channels=[0], workers=ROI_WORKERS)

    # crop to the labelled bounding box (id > 0)
    z0, z1, y0, y1, x0, x1 = _tissue_bbox(arr[..., 0])
    arr = np.ascontiguousarray(arr[z0:z1, y0:y1, x0:x1, :])
    odz, ody, odx = arr.shape[:3]                 # ORIGINAL cropped dims -> world bbox

    # NEAREST downsample to cap (stride) — preserves label ids exactly.
    fy = max(1, math.ceil(max(arr.shape[1], arr.shape[2]) / cap))
    if fy > 1:
        arr = np.ascontiguousarray(arr[:, ::fy, ::fy, :])
    if arr.shape[0] > cap:
        arr = np.ascontiguousarray(arr[:: math.ceil(arr.shape[0] / cap)])

    rgba = proc.bake_labels_rgba(arr)
    occ = np.ascontiguousarray(proc.maxpool_occupancy(rgba, occ_block), dtype=np.uint8)

    dz, dy, dx, nch = rgba.shape
    oz, oy, ox, _oc = occ.shape
    bbox = lctx.manager.world_bbox_mm(level, (z0, y0, x0, 0), (odz, ody, odx))
    header = (b"BVX3" + struct.pack("<BBH", level, nch, 0)
              + struct.pack("<iii", z0, y0, x0) + struct.pack("<HHH", dz, dy, dx)
              + struct.pack("<6f", *bbox))
    trailer = struct.pack("<HHHBB", oz, oy, ox, occ_block, 0)
    return header + rgba.tobytes() + trailer + occ.tobytes()


@app.get("/api/label_volume")
async def label_volume(brain: str = Query(None), level: int = Query(-1),
                       cap: int = Query(0)):
    """Coloured label-map overlay for `brain` as one merged RGBA volume (BVX3),
    same placement as the tissue. Toggled on/off by the client. Cached like
    /api/block_volume. level<0 -> the label's default (min-streamable) level."""
    build_blocks()
    br = brain or _default_brain()
    lctx = _labels.get(br)
    if lctx is None:
        raise HTTPException(404, f"no label map for brain {br!r}")
    info = lctx.selector.info
    lvl = level if level >= 0 else lctx.selector.min_level
    lvl = max(info.finest_level, min(lvl, info.coarsest_level))
    c = cap if cap else GPU_MAX_XY
    ckey = ("labelvol", br, lvl, c)
    cached = _roi_cache_get(ckey)
    if cached is not None:
        await _throttle(len(cached))
        return Response(content=cached, media_type="application/octet-stream",
                        headers={"X-Brain": str(br), "X-Level": str(lvl),
                                 "X-Cache": "hit", "Cache-Control": "no-store"})
    payload = _build_label_volume_payload(lctx, lvl, c)
    _roi_cache_put(ckey, payload)
    await _throttle(len(payload))
    return Response(content=payload, media_type="application/octet-stream",
                    headers={"X-Brain": str(br), "X-Level": str(lvl),
                             "X-Cache": "miss", "Cache-Control": "no-store"})


def _prewarm_roi():
    """Background: fill the ROI cache for the viewer's default panels so the
    first page load is instant. Uses the otherwise-idle DGX + parallel access."""
    build_blocks()

    # whole-brain picker volume (reassembled slice picker) -- the first thing the
    # ROI viewer now fetches, so build it before the per-block ROI panels.
    br = _default_brain()
    bkey = ("brain", br, -1, 256)
    if _roi_cache_get(bkey) is None:
        try:
            payload, info = _build_brain_payload(br, -1, 256)
            _roi_cache_put(bkey, payload); _brain_info_cache[bkey] = info
            print(f"[brain-prewarm] cached brain={br} dims={info['dims']}")
        except Exception as e:  # noqa
            print(f"[brain-prewarm] failed: {e}")

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

    # merged per-block volumes for the SRD client -- one per block at the default
    # (finest streamed) level, so the first Unity frame is a cache hit.
    ml = ctx.selector.min_level
    for bid in _order:
        bctx = _blocks[bid]
        key = ("blockvol", bctx.block_id, ml, GPU_MAX_XY)
        if _roi_cache_get(key) is not None:
            continue
        try:
            _roi_cache_put(key, _build_block_volume_payload(bctx, ml, GPU_MAX_XY))
            print(f"[blockvol-prewarm] cached {bctx.block_id} L{ml}")
        except Exception as e:  # noqa
            print(f"[blockvol-prewarm] {bctx.block_id} L{ml} failed: {e}")


if __name__ == "__main__":
    import uvicorn
    ap = argparse.ArgumentParser()
    ap.add_argument("--zarr", action="append", default=None,
                    help="Zarr root URL for one block; repeat for multiple blocks. "
                         "A single value may also be comma-separated. "
                         "Omit to use the built-in 5-block set.")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8010)
    ap.add_argument("--min-level", type=int, default=4,
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
    ap.add_argument("--prefetch-top", type=int, default=8,
                    help="warm N top-priority chunks on each /view (0=off)")
    ap.add_argument("--voxel-um", default="20,0.5,0.5",
                    help="true finest-level voxel size 'z,y,x' in microns, "
                         "overriding the placeholder OME scale (default 20,0.5,0.5)")
    ap.add_argument("--voxel-um-raw", dest="voxel_raw", action="store_true",
                    help="trust the Zarr OME scale verbatim (no voxel override)")
    ap.add_argument("--hb02-zarr", default=HB02_ZARR,
                    help="local path to the hb02 fused RGB brain (brain 2). "
                         "Added as a second selectable brain when present.")
    ap.add_argument("--hb02-labels-zarr", default=HB02_LABELS_ZARR,
                    help="local path to the hb02 segmentation label map "
                         "(toggleable coloured overlay on brain 2).")
    ap.add_argument("--no-hb02", dest="hb02", action="store_false", default=True,
                    help="do not register the local hb02 brain (brain 2)")
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

    # compose the brain registry: brain 1 = the (possibly overridden) HTTP block
    # set; brain 2 = the local hb02 fused RGB volume, if present.
    brains = [{"id": "Stroke_1", "label": "Brain 1 — Stroke_1 (5 blocks)",
               "kind": "http", "roots": roots, "voxel_override": True,
               "render": "gray_mask"}]
    if args.hb02 and os.path.isdir(args.hb02_zarr):
        hb = {"id": "hb02", "label": "Brain 2 — hb02 fused (8µm)",
              "kind": "local", "roots": [args.hb02_zarr],
              "voxel_override": False, "render": "rgb", "block_ids": ["hb02"]}
        if os.path.isdir(args.hb02_labels_zarr):
            hb["labels"] = args.hb02_labels_zarr
            print(f"[dgx-zarr] brain 2 labels: {args.hb02_labels_zarr}")
        brains.append(hb)
        print(f"[dgx-zarr] brain 2 (hb02): {args.hb02_zarr}")
    elif args.hb02:
        print(f"[dgx-zarr] brain 2 (hb02) NOT found at {args.hb02_zarr} — disabled")

    import app_zarr as _self  # type: ignore
    _self.ZARR_ROOTS = roots
    _self.BRAINS = brains
    _self.MIN_LEVEL = args.min_level
    _self.DELAY_MS = args.delay_ms
    _self.BANDWIDTH_LIMIT_MBPS = args.bandwidth_limit
    _self.CACHE_BYTES = args.cache_mb * 1024 * 1024
    _self.WORKERS = args.workers
    _self.USE_GPU = args.gpu
    _self.GPU_MAX_XY = args.gpu_max_xy
    _self.PREFETCH_TOP = args.prefetch_top
    _self.VOXEL_UM_FINEST = voxel_um

    print(f"[dgx-zarr] blocks={len(roots)}:")
    for u in roots:
        print(f"[dgx-zarr]   {u}")
    print(f"[dgx-zarr] voxel_um(z,y,x)={voxel_um if voxel_um else 'raw-metadata'}")
    print(f"[dgx-zarr] min_level={args.min_level} workers={args.workers} gpu={args.gpu} "
          f"gpu_max_xy={args.gpu_max_xy} prefetch_top={args.prefetch_top} "
          f"delay={args.delay_ms}ms bw={args.bandwidth_limit}MB/s cache={args.cache_mb}MB")
    uvicorn.run(_self.app, host=args.host, port=args.port, log_level="info")

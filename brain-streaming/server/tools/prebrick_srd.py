#!/usr/bin/env python3
"""Offline bricker for the Sony-SRD, all-local Brain-2 viewer (no server, no runtime
decode). Tiles one or more pyramid levels of an OME-Zarr volume into GPU-ready
bricks + an index.json that a Unity brick loader streams into Texture3D bricks.

Why offline: Unity never touches zarr/blosc at runtime. We decode once here (reusing
the project's tested LocalZarrBrainSource) and write bricks laid out exactly like a
Unity Texture3D (x fastest, then y, then z; channels interleaved per voxel).

Levels >= L3 exceed Unity's single-Texture3D dim cap (~2048) in XY, so each level is
split into XY tiles (full Z) with a 1-voxel APRON overlap per side for seamless
trilinear filtering across brick borders. The index records, per brick, the CORE
origin/size (what the shader shows), the apron (what to trim when sampling), and the
core's local-mm bbox (for placement).

Formats:
  raw  : RGB24 (or RGBA32 with --alpha) — works today, big on disk; use for L3/L4
         to bring the pipeline up before BC7 is wired.
  bc7  : GPU block-compressed, 1 byte/voxel — the target (zero runtime decode, 4x
         less VRAM, L0 fits on disk). Pending the P0 BC7-Texture3D check; the encode
         hook is marked NotImplemented until then (see _encode_bc7).

Run on the box where the zarr lives (fast local reads), then copy <out>/ to the SRD
machine's local disk.

    python3 prebrick_srd.py --levels 4,3 --format raw --out ../unity/BrainVolumeSRD/Assets/StreamingAssets/Bricks
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

import numpy as np

# the project's tested local reader
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from datasource.local_zarr import LocalZarrBrainSource   # noqa: E402
from datasource.http_zarr import HttpZarrBrainSource      # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ZARR = "/home/users/azhar/projectM/viewer_data/fused_8um/hb02_fused.zarr"
DEFAULT_OUT = os.path.normpath(os.path.join(
    HERE, "..", "..", "unity", "BrainVolumeSRD", "Assets", "StreamingAssets", "Bricks"))


def _white_alpha(rgb: np.ndarray) -> np.ndarray:
    """Tissue alpha from a WHITE background: 255 where the colour departs from white,
    0 on pure white. rgb is (...,3) uint8 -> (...,1) uint8."""
    mn = rgb.min(axis=-1, keepdims=True).astype(np.int16)
    return np.clip(255 - mn, 0, 255).astype(np.uint8)


def _pad4(a: np.ndarray) -> np.ndarray:
    """Pad a (z,y,x,c) array so x,y are multiples of 4 (BC block size), edge-replicated.
    z is per-slice so it is left as-is. The index records the padded 'stored' dims; the
    shader only samples the core sub-region, so the extra edge texels are never shown."""
    _z, y, x, _c = a.shape
    py, px = (-y) % 4, (-x) % 4
    if py or px:
        a = np.pad(a, ((0, 0), (0, py), (0, px), (0, 0)), mode="edge")
    return np.ascontiguousarray(a)


# --- block encoders: one 4x4-block stream per z-slice, slices concatenated in z ---
# Unity Texture3D(BCn) wants exactly this: for each depth slice, the standard 2D block
# layout (row-major blocks), slices in z order. Both take (z,y,x,4) uint8 RGBA.

BC_LEVEL = 10                                            # quicktex effort 0..18 (set in main)
_POOL = None                                             # process pool for slice encode
_WENC = None                                             # per-process BC3 encoder


def _bc3_init(level: int) -> None:
    global _WENC
    from quicktex.s3tc.bc3 import BC3Encoder
    _WENC = BC3Encoder(level=level)


def _bc3_slice(args) -> bytes:
    data, w, h = args
    from quicktex import RawTexture
    return _WENC.encode(RawTexture.frombytes(data, w, h)).tobytes()


def _encode_bc3(rgba: np.ndarray) -> bytes:
    """DXT5/BC3: 1 byte/voxel, colour(565)+8-interp alpha. Universally supported.
    Quality is 565-level (fine for Nissl overviews); BC7 is the later upgrade.
    CPU (quicktex) per z-slice, fanned out across a process pool -- with enough cores
    even L0 bakes in minutes. (A CuPy GPU encoder is the later perf upgrade.)"""
    z, y, x, _ = rgba.shape
    tasks = [(np.ascontiguousarray(rgba[zi]).tobytes(), x, y) for zi in range(z)]
    if _POOL is not None:
        parts = _POOL.map(_bc3_slice, tasks, chunksize=4)
    else:
        parts = [_bc3_slice(t) for t in tasks]           # single-process fallback
    return b"".join(parts)


def _encode_bc7(rgba: np.ndarray) -> bytes:
    """BC7: 1 byte/voxel, near-lossless colour. No Python/CLI encoder is available on
    this box yet (checked P1), so this stays a hard stop. Upgrade path: a CuPy mode-6
    BC7 encoder (GPU, validated via texture2ddecoder.decode_bc7) or a native encoder."""
    raise NotImplementedError(
        "BC7 encode not wired (no encoder available on this host). Use --format bc3 "
        "(same 1 B/voxel, drop-in) for now; see docs/brain2-srd-bricking-plan.md (P1).")


_TEX_FORMAT = {"bc3": "BC3", "bc7": "BC7", "raw": None}   # raw -> RGBA32/RGB24 by nch
_ENCODERS = {"bc3": _encode_bc3, "bc7": _encode_bc7}


def brick_level(src, level, out_dir, name, brick_xy, apron, fmt, alpha, channels):
    """Tile one level into bricks; return the level's index dict."""
    li = src.level_info(level)
    sz, sy, sx, _sc = li.shape
    vz, vy, vx, _ = li.voxel_size_m                       # metres/voxel
    vzmm, vymm, vxmm = vz * 1000, vy * 1000, vx * 1000
    Exmm, Eymm, Ezmm = sx * vxmm, sy * vymm, sz * vzmm    # full-level extent (mm)

    ny = -(-sy // brick_xy)                               # ceil: number of core tiles
    nx = -(-sx // brick_xy)
    lvl_dir = os.path.join(out_dir, f"L{level}")
    os.makedirs(lvl_dir, exist_ok=True)
    is_block = fmt in _ENCODERS                            # bc3/bc7 -> RGBA + 4-aligned
    nch = 4 if (alpha or is_block) else 3                  # block formats need RGBA
    ext = fmt if is_block else "raw"
    tex_format = _TEX_FORMAT[fmt] or ("RGBA32" if nch == 4 else "RGB24")

    bricks = []
    print(f"[prebrick] L{level}: {sz}x{sy}x{sx}  -> {ny}x{nx} XY tiles "
          f"(core {brick_xy}, apron {apron}), {tex_format}")
    for iy in range(ny):
        for ix in range(nx):
            cy0, cy1 = iy * brick_xy, min((iy + 1) * brick_xy, sy)   # CORE bounds
            cx0, cx1 = ix * brick_xy, min((ix + 1) * brick_xy, sx)
            ay0, ay1 = max(0, cy0 - apron), min(sy, cy1 + apron)     # +apron (stored)
            ax0, ax1 = max(0, cx0 - apron), min(sx, cx1 + apron)

            arr = src.get_roi(level, 0, sz, ay0, ay1, ax0, ax1,
                              channels=channels, workers=192)        # (z, by, bx, len(ch))
            if arr.shape[-1] == 1:
                arr = np.repeat(arr, 3, axis=-1)
            rgb = np.ascontiguousarray(arr[..., :3], np.uint8)
            if nch == 4:
                vox = np.concatenate([rgb, _white_alpha(rgb)], axis=-1)
            else:
                vox = rgb
            if is_block:
                vox = _pad4(vox)                             # BC needs XY multiple of 4
            dz, dy, dx, _ = vox.shape                        # STORED (padded, incl apron)

            fn = f"b_{iy}_{ix}.{ext}"
            payload = _ENCODERS[fmt](vox) if is_block else vox.tobytes()
            with open(os.path.join(lvl_dir, fn), "wb") as f:
                f.write(payload)

            # CORE local-mm bbox, centred on the level origin (matches the viewer's
            # centred-mm convention: x in [-Ex/2, +Ex/2], etc.)
            bbox = [cx0 * vxmm - Exmm / 2, cy0 * vymm - Eymm / 2, 0.0 * vzmm - Ezmm / 2,
                    cx1 * vxmm - Exmm / 2, cy1 * vymm - Eymm / 2, sz * vzmm - Ezmm / 2]
            bricks.append({
                "file": f"L{level}/{fn}", "tex_format": tex_format, "channels": nch,
                "stored": [int(dx), int(dy), int(dz)],       # texture dims (padded+apron)
                "core_origin": [int(cx0), int(cy0), 0],       # voxel origin of core
                "core_size": [int(cx1 - cx0), int(cy1 - cy0), int(sz)],
                "apron": [int(cx0 - ax0), int(cy0 - ay0), 0], # trim at sample time
                "bbox_mm": [round(v, 4) for v in bbox],       # CORE placement box
            })
    return {
        "level": int(level),
        "voxel_mm": [round(vxmm, 6), round(vymm, 6), round(vzmm, 6)],  # x,y,z
        "extent_mm": [round(Exmm, 4), round(Eymm, 4), round(Ezmm, 4)],
        "grid": [int(ny), int(nx)], "bricks": bricks,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zarr", default=DEFAULT_ZARR, help="local path or http:// URL")
    ap.add_argument("--out", default=DEFAULT_OUT, help="output brick directory")
    ap.add_argument("--name", default=None, help="dataset name (default: zarr stem)")
    ap.add_argument("--levels", default="4,3",
                    help="comma list, coarse->fine (default 4,3)")
    ap.add_argument("--brick-xy", type=int, default=1024,
                    help="core XY tile size; keep <= 1024 to stay under the ~2048 cap")
    ap.add_argument("--apron", type=int, default=1, help="voxel overlap per side")
    ap.add_argument("--format", choices=["bc3", "bc7", "raw"], default="bc3",
                    help="bc3 = DXT5 1 B/vox (works now); bc7 = pending encoder; "
                         "raw = RGB(A) uncompressed (big)")
    ap.add_argument("--alpha", action="store_true",
                    help="store RGBA with tissue alpha (white background -> 0); "
                         "default RGB (white-cutout done in the shader)")
    ap.add_argument("--channels", default="0,1,2", help="source RGB channels")
    ap.add_argument("--bc-level", type=int, default=10,
                    help="BC3 encode effort 0..18 (lower = faster; 18 for final bakes)")
    ap.add_argument("--jobs", type=int, default=min(32, os.cpu_count() or 8),
                    help="parallel slice-encode workers (this box has many cores)")
    args = ap.parse_args()
    global BC_LEVEL, _POOL
    BC_LEVEL = max(0, min(18, args.bc_level))
    _bc3_init(BC_LEVEL)                                  # for the single-process fallback
    if args.format == "bc3" and args.jobs > 1:
        import multiprocessing as mp
        _POOL = mp.Pool(args.jobs, initializer=_bc3_init, initargs=(BC_LEVEL,))

    is_http = args.zarr.lower().startswith(("http://", "https://"))
    src = HttpZarrBrainSource(args.zarr) if is_http else LocalZarrBrainSource(args.zarr)
    info = src.get_metadata()
    name = args.name or os.path.basename(args.zarr.rstrip("/")).replace(".zarr", "")
    channels = [int(c) for c in args.channels.split(",") if c != ""]
    levels = [int(l) for l in args.levels.split(",") if l != ""]
    out_dir = os.path.join(args.out, name)
    os.makedirs(out_dir, exist_ok=True)

    ez, ey, ex = (e * 1000.0 for e in info.extent_m)       # mm (z,y,x)
    index = {
        "name": name, "source": args.zarr, "colorMode": "rgb-white-background",
        "render": "rgb", "world_extent_mm": [round(ex, 4), round(ey, 4), round(ez, 4)],
        "finest_level": info.finest_level, "coarsest_level": info.coarsest_level,
        "levels": [],
    }
    t0 = time.time()
    for lvl in levels:
        index["levels"].append(
            brick_level(src, lvl, out_dir, name, args.brick_xy, args.apron,
                        args.format, args.alpha, channels))

    with open(os.path.join(out_dir, "index.json"), "w") as f:
        json.dump(index, f, indent=2)
    nbricks = sum(len(l["bricks"]) for l in index["levels"])
    print(f"[prebrick] wrote {nbricks} bricks + index.json to {out_dir} "
          f"in {time.time() - t0:.1f}s")
    print(f"[prebrick] copy {out_dir} to the SRD machine's StreamingAssets/Bricks/")


if __name__ == "__main__":
    main()

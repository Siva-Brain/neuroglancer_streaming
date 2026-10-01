#!/usr/bin/env python3
"""Export one pyramid level of an OME-Zarr volume to a flat RGB24 .raw + .json
that the Unity FusedVolumeLoader loads straight into a Texture3D (no server, no
streaming -- the "local L4/L5/L7 volume with DVR" path for the SRD).

The .raw layout matches Unity's Texture3D: x fastest, then y, then z, with RGB
interleaved per voxel -- which is exactly a C-order (z, y, x, c) uint8 array
flattened, so no reordering is needed.

Usage (run on the DGX, where the zarr lives; then copy the small .raw to the SRD):
    python3 zarr_level_to_raw.py \
        --zarr /home/users/azhar/projectM/viewer_data/fused_8um/hb02_fused.zarr \
        --level 7 \
        --out  ../unity/BrainVolumeSRD/Assets/StreamingAssets/Fused \
        --name hb02_L7

L7 is ~25 MB, L5 ~0.4 GB, L4 ~1.6 GB (uncompressed RGB). Start with L7.
"""
import argparse
import json
import os
import sys
import time

import numpy as np

# import the project's tested local reader
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from datasource.local_zarr import LocalZarrBrainSource  # noqa: E402
from datasource.http_zarr import HttpZarrBrainSource     # noqa: E402


HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ZARR = "/home/users/azhar/projectM/viewer_data/fused_8um/hb02_fused.zarr"
DEFAULT_OUT = os.path.normpath(os.path.join(
    HERE, "..", "..", "unity", "BrainVolumeSRD", "Assets", "StreamingAssets", "Fused"))


def main():
    import re
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", type=int, required=True, help="pyramid level to export (e.g. 7)")
    ap.add_argument("--zarr", default=DEFAULT_ZARR,
                    help=f"path (local) or http:// URL of the .zarr (default: {DEFAULT_ZARR})")
    ap.add_argument("--out", default=DEFAULT_OUT,
                    help=f"output directory (default: the Unity StreamingAssets/Fused)")
    ap.add_argument("--name", default=None,
                    help="base name -> <name>.raw/.json (default: <zarr-prefix>_L<level>, e.g. hb02_L6)")
    ap.add_argument("--channels", default="0,1,2",
                    help="source channels to pack as RGB (default 0,1,2)")
    ap.add_argument("--force", action="store_true",
                    help="write even if the level exceeds Unity's single-Texture3D limits")
    args = ap.parse_args()

    is_http = args.zarr.lower().startswith(("http://", "https://"))
    src = (HttpZarrBrainSource(args.zarr) if is_http
           else LocalZarrBrainSource(args.zarr))
    info = src.get_metadata()
    li = src.level_info(args.level)
    z, y, x, _ = li.shape
    is_label = info.channels == 1                        # 3D single-channel = segmentation
    nch = 1 if is_label else 3

    if not args.name:
        stem = os.path.basename(args.zarr.rstrip("/")).replace(".zarr", "")
        m = re.match(r"[A-Za-z0-9]+", stem)
        prefix = m.group(0) if m else "vol"
        args.name = f"{prefix}_L{args.level}" + ("_labels" if is_label else "")

    ez, ey, ex = (e * 1000.0 for e in info.extent_m)     # mm (z, y, x)
    vz, vy, vx = ez / z, ey / y, ex / x                  # mm per voxel

    print(f"[export] L{args.level}: {z}x{y}x{x} voxels, "
          f"{'LABELS (R8)' if is_label else 'RGB24'}, "
          f"voxel {vz:.3f}x{vy:.3f}x{vx:.3f} mm (z,y,x)")

    # Unity single-Texture3D limits: every dimension <= 2048, and File.ReadAllBytes
    # tops out at a ~2 GB byte[]. Finer levels need tiling (use the streaming client).
    raw_bytes = z * y * x * nch
    too_big_dim = max(z, y, x) > 2048
    too_big_file = raw_bytes > 2_000_000_000
    if too_big_dim or too_big_file:
        why = []
        if too_big_dim:
            why.append(f"max dim {max(z, y, x)} > 2048")
        if too_big_file:
            why.append(f"raw {raw_bytes/1e9:.1f} GB > 2 GB")
        print(f"[export] REFUSING L{args.level}: {', '.join(why)} -- too big for one "
              f"Unity Texture3D. L4 is the finest single-volume level; finer needs "
              f"tiling (the streaming client). Pass --force to write it anyway.",
              file=sys.stderr)
        if not args.force:
            sys.exit(2)

    t0 = time.time()
    if is_label:
        # labels are 3D (no channel axis) -> get_roi's shard path is 4D-only; read
        # via get_region (uses get_chunk, which handles 3D). NEAREST in Unity.
        vol = src.get_region(args.level, 0, (slice(0, z), slice(0, y), slice(0, x)))
        vol = np.ascontiguousarray(vol[..., :1], dtype=np.uint8)    # (z, y, x, 1)
    else:
        chans = [int(c) for c in args.channels.split(",") if c != ""]
        vol = src.get_roi(args.level, 0, z, 0, y, 0, x, channels=chans, workers=192)
        if vol.shape[-1] == 1:
            vol = np.repeat(vol, 3, axis=-1)
        elif vol.shape[-1] == 2:
            vol = np.concatenate([vol, vol[..., :1]], axis=-1)
        vol = np.ascontiguousarray(vol[..., :3], dtype=np.uint8)    # (z, y, x, 3)
    print(f"[export] read {vol.nbytes/1e6:.1f} MB in {time.time()-t0:.1f}s")

    os.makedirs(args.out, exist_ok=True)
    raw_path = os.path.join(args.out, args.name + ".raw")
    json_path = os.path.join(args.out, args.name + ".json")
    with open(raw_path, "wb") as f:
        f.write(vol.tobytes())                            # x fastest, y, z (RGB interleaved)

    meta = {
        "width": int(x), "height": int(y), "depth": int(z),   # Unity Texture3D dims
        "channels": nch, "format": "R8" if is_label else "RGB24",
        "colorMode": "label-ids" if is_label else "rgb-white-background",
        "level": int(args.level),
        "voxelSizeMM": [float(vx), float(vy), float(vz)],      # (x, y, z)
        "sizeMM": [float(ex), float(ey), float(ez)],          # (x, y, z) extent
        "source": args.zarr,
    }

    # For a label volume, also emit a 256-entry RGB LUT (<name>.lut, 768 bytes) from
    # the sibling manifest.json so Unity can colour regions. id 0 stays black.
    if is_label and not is_http:
        man_path = os.path.join(os.path.dirname(args.zarr.rstrip("/")), "manifest.json")
        if os.path.exists(man_path):
            lut = np.zeros((256, 3), np.uint8)
            with open(man_path) as mf:
                regions = json.load(mf).get("regions", [])
            for r in regions:
                rid = int(r["id"]) & 255
                c = r.get("color", [255, 255, 255])
                lut[rid] = c[:3]
            lut_path = os.path.join(args.out, args.name + ".lut")
            with open(lut_path, "wb") as lf:
                lf.write(lut.tobytes())
            meta["lut"] = args.name + ".lut"
            meta["regionCount"] = len(regions)
            print(f"[export] wrote {lut_path} ({len(regions)} regions)")

    with open(json_path, "w") as f:
        json.dump(meta, f, indent=2)

    print(f"[export] wrote {raw_path} ({os.path.getsize(raw_path)/1e6:.1f} MB)")
    print(f"[export] wrote {json_path}")
    if is_label:
        fused = args.name.replace("_labels", "")
        print(f"[export] copy to StreamingAssets/Fused/; set FusedVolumeLoader.labelName "
              f"= {args.name!r} (alongside baseName = {fused!r})")
    else:
        print(f"[export] copy to StreamingAssets/Fused/; set FusedVolumeLoader.baseName "
              f"= {args.name!r}")


if __name__ == "__main__":
    main()

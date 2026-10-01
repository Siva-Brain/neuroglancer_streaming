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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zarr", required=True, help="path (local) or http:// URL of the .zarr")
    ap.add_argument("--level", type=int, required=True, help="pyramid level to export (e.g. 7)")
    ap.add_argument("--out", required=True, help="output directory (Unity StreamingAssets/Fused)")
    ap.add_argument("--name", required=True, help="base name, e.g. hb02_L7 -> <name>.raw/.json")
    ap.add_argument("--channels", default="0,1,2",
                    help="source channels to pack as RGB (default 0,1,2)")
    args = ap.parse_args()

    is_http = args.zarr.lower().startswith(("http://", "https://"))
    src = (HttpZarrBrainSource(args.zarr) if is_http
           else LocalZarrBrainSource(args.zarr))
    info = src.get_metadata()
    li = src.level_info(args.level)
    z, y, x, _ = li.shape
    chans = [int(c) for c in args.channels.split(",") if c != ""]

    ez, ey, ex = (e * 1000.0 for e in info.extent_m)     # mm (z, y, x)
    vz, vy, vx = ez / z, ey / y, ex / x                  # mm per voxel

    print(f"[export] L{args.level}: {z}x{y}x{x} voxels, channels {chans}, "
          f"voxel {vz:.3f}x{vy:.3f}x{vx:.3f} mm (z,y,x)")
    t0 = time.time()
    vol = src.get_roi(args.level, 0, z, 0, y, 0, x, channels=chans, workers=192)
    # pad/truncate to exactly 3 channels (Unity RGB24)
    if vol.shape[-1] == 1:
        vol = np.repeat(vol, 3, axis=-1)
    elif vol.shape[-1] == 2:
        vol = np.concatenate([vol, vol[..., :1]], axis=-1)
    vol = np.ascontiguousarray(vol[..., :3], dtype=np.uint8)   # (z, y, x, 3) C-order
    print(f"[export] read {vol.nbytes/1e6:.1f} MB in {time.time()-t0:.1f}s")

    os.makedirs(args.out, exist_ok=True)
    raw_path = os.path.join(args.out, args.name + ".raw")
    json_path = os.path.join(args.out, args.name + ".json")
    with open(raw_path, "wb") as f:
        f.write(vol.tobytes())                            # x fastest, y, z; RGB interleaved

    meta = {
        "width": int(x), "height": int(y), "depth": int(z),   # Unity Texture3D dims
        "channels": 3, "format": "RGB24",
        "colorMode": "rgb-white-background",                  # shader: 1-max(rgb), black=empty
        "level": int(args.level),
        "voxelSizeMM": [float(vx), float(vy), float(vz)],      # (x, y, z)
        "sizeMM": [float(ex), float(ey), float(ez)],          # (x, y, z) extent
        "source": args.zarr,
    }
    with open(json_path, "w") as f:
        json.dump(meta, f, indent=2)

    print(f"[export] wrote {raw_path} ({os.path.getsize(raw_path)/1e6:.1f} MB)")
    print(f"[export] wrote {json_path}")
    print(f"[export] copy both to the SRD machine's StreamingAssets/Fused/ and set "
          f"FusedVolumeLoader.baseName = {args.name!r}")


if __name__ == "__main__":
    main()

"""Phase 2 verification: fetch + decode ONE real chunk, print measurements.

    python tools/verify_chunk.py                       # the known example 5/c/19/0/0/0
    python tools/verify_chunk.py --level 7 --coords 20,0,0,0 --save out.png

Downloads only the requested chunk -- never the whole dataset.
"""
from __future__ import annotations

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from datasource import HttpZarrBrainSource  # noqa: E402

DEF_ROOT = "http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--zarr", default=DEF_ROOT)
    ap.add_argument("--level", type=int, default=5)
    ap.add_argument("--coords", default="19,0,0,0", help="z,y,x,c shard grid index")
    ap.add_argument("--save", default=None, help="write a diagnostic PNG (needs pillow)")
    args = ap.parse_args()

    coords = tuple(int(x) for x in args.coords.split(","))
    src = HttpZarrBrainSource(args.zarr)

    info = src.get_metadata()
    print(f"dataset={info.name} dtype={info.dtype} axes={info.axes} "
          f"channels={info.channels} finest=L{info.finest_level} coarsest=L{info.coarsest_level}")

    cd = src.get_chunk(args.level, coords)
    a = cd.array
    print(f"chunk_id={cd.chunk_id}")
    print(f"  http_fetch   : {cd.fetch_s*1000:8.1f} ms")
    print(f"  decompress   : {cd.decode_s*1000:8.1f} ms")
    print(f"  download_size: {cd.source_bytes:10d} B (compressed)")
    print(f"  decoded_size : {a.nbytes:10d} B  shape={a.shape} {a.dtype}")
    if cd.source_bytes:
        print(f"  ratio        : {a.nbytes/cd.source_bytes:8.2f} : 1")
    print(f"  values       : min={a.min()} max={a.max()} mean={a.mean():.1f} "
          f"nonzero={100*(a>0).mean():.1f}%")
    print(f"  per-channel  : {[round(float(a[...,c].mean()),1) for c in range(a.shape[-1])]}")

    if args.save:
        from PIL import Image
        mid = a[a.shape[0] // 2]
        img = mid[:, :, :3] if a.shape[-1] >= 3 else mid[:, :, 0]
        Image.fromarray(img).save(args.save)
        print(f"  saved        : {args.save}")


if __name__ == "__main__":
    main()

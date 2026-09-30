# Deployment (DGX A100)

Two server editions share one client and protocol:
- `app.py` — synthetic brain (no data dependency), meshes.
- `app_zarr.py` — **real** HTTP Zarr source, volume bricks, optional A100 GPU.

This doc covers `app_zarr.py` on the A100 box.

## Hardware verified on this box
8× A100-SXM4-80GB (640 GB GPU), 256 cores, ~2 TB RAM, driver 535 (CUDA 12),
Python 3.10. `nvidia-smi` shows the 8 GPUs; there is no full CUDA toolkit
(`nvcc`) on PATH, and system `/usr/local/cuda-12.2` ships libs but not headers.

## CPU-only (always works)
```bash
cd brain-streaming/server
pip install --user fastapi "uvicorn[standard]" pydantic requests numcodecs
python3 app_zarr.py --host 0.0.0.0 --port 8090 --min-level 5 --no-gpu
```
`gpu.available=false`; everything streams, just without GPU resample/prefetch-processing.

## GPU-accelerated (A100)
CuPy needs three things this box doesn't provide together: CUDA **math libs**
(present in `/usr/local/cuda-12.2/lib64`), **nvrtc + cudart** and **headers**
(from pip wheels). Install:
```bash
pip install --user cupy-cuda12x==13.3.0 nvidia-cuda-nvrtc-cu12 nvidia-cuda-runtime-cu12
```
Then launch through the wrapper, which discovers those paths and exports
`CUDA_HOME` / `LD_LIBRARY_PATH` for you:
```bash
cd brain-streaming/server
./run_dgx.sh --host 0.0.0.0 --port 8090 --min-level 4 --workers 48
```
Verify: `curl http://localhost:8090/api/health` → `gpu.available: true`, 8 devices.

### Why these versions
- `cupy-cuda12x==13.3.0` (not 14.x): 14.x needs a `cuda.pathfinder` module not
  present here and fails to import. 13.3.0 loads cleanly.
- `libcurand` is **not** required — CuPy only pulls it for `cp.random`, which
  the pipeline never uses (all data comes from Zarr). Ignore its absence.

### What `run_dgx.sh` sets
```
CUDA_HOME = server/.cuda_home  (include -> pip cuda_runtime headers, lib64 -> cuda-12.2)
LD_LIBRARY_PATH = /usr/local/cuda-12.2/lib64 : <pip nvrtc>/lib : <pip cudart>/lib
```

## Server flags (`app_zarr.py`)
| Flag | Default | Meaning |
|------|---------|---------|
| `--zarr URL` | 580 dataset | Zarr root (read-only source of truth) |
| `--host/--port` | `0.0.0.0`/`8010` | bind |
| `--min-level N` | 3 | finest level streamed (0 = full ~1 TB, never for prototype) |
| `--workers N` | 32 | parallel prefetch workers (256 cores available) |
| `--gpu / --no-gpu` | on | A100 processing (auto-fallback to CPU) |
| `--gpu-max-xy N` | 512 | server-side resample cap so brick textures stay uniform (0=off) |
| `--prefetch-top N` | 8 | warm N top-priority chunks on each `/view` |
| `--delay-ms`, `--bandwidth-limit` | 0 | network simulation (Phase 12) |
| `--cache-mb N` | 2048 | DGX LRU cache budget |

## What the A100 actually buys (measured)
| Stage | Time |
|-------|------|
| Zarr HTTP fetch (dominates) | ~170 ms/shard |
| blosc decode (CPU) | ~13 ms |
| GPU upload 18 MB + downsample + gradient | **~4 ms** |
| Prefetch 12 shards serial → 48-way parallel | **4.2× faster** |
| `/view` prefetch → next client GET | **0.038 s (warm cache hit)** |

So GPU processing is ~free; the wins are **parallel prefetch** (hiding HTTP
latency) and **server-side resample** (fine levels like L4=1500² stream as
uniform ≤512² bricks: ~9× smaller, bounded client texture memory).

## Ports
One inbound TCP port (default 8090). `sudo ufw allow 8090/tcp`. Outbound HTTP to
the Zarr host (`3dstrokeviewer.humanbrain.in:8056`) must be reachable from the DGX.

## Multi-GPU (future, not yet)
`GpuProcessor(device=k)` selects a GPU; round-robining bricks across the 8 A100s
is a one-line change but unnecessary now (processing is 4 ms/brick).

## Optional: Docker
Not required. A CUDA base image (`nvidia/cuda:12.2.2-runtime-ubuntu22.04`) plus
`pip install cupy-cuda12x==13.3.0 ...` avoids the manual lib wiring; run with
`--gpus all`. The CPU image (`docs/dgx-setup.md`) also works.

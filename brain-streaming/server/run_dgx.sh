#!/usr/bin/env bash
# Launch the DGX Zarr streaming server with the A100/CuPy environment wired up.
# CuPy here needs: system CUDA math libs (/usr/local/cuda-12.2/lib64) + the pip
# nvrtc/cudart wheels + the pip CUDA headers. This script discovers them, builds
# a self-contained CUDA_HOME, and starts app_zarr.py. Falls back to CPU cleanly
# if CuPy/CUDA are missing (the server just runs with gpu.available=false).
#
#   ./run_dgx.sh --host 0.0.0.0 --port 8090 --min-level 4
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

SYS_CUDA=/usr/local/cuda-12.2/lib64
find_dir(){ python3 -c "import glob,os;f=glob.glob(os.path.expanduser('$1'),recursive=True);print(os.path.dirname(f[0]) if f else '')"; }
NVRTC_DIR=$(find_dir '~/.local/**/libnvrtc.so.12*')
CUDART_DIR=$(find_dir '~/.local/**/libcudart.so.12*')
INC_DIR=$(find_dir '~/.local/**/nvidia/cuda_runtime/include/vector_types.h')

CH="$HERE/.cuda_home"; mkdir -p "$CH"
[ -n "$INC_DIR" ] && ln -sfn "$INC_DIR" "$CH/include"
[ -d "$SYS_CUDA" ] && ln -sfn "$SYS_CUDA" "$CH/lib64"
export CUDA_HOME="$CH" CUDA_PATH="$CH"
export LD_LIBRARY_PATH="${SYS_CUDA}:${NVRTC_DIR}:${CUDART_DIR}:${LD_LIBRARY_PATH:-}"

echo "[run_dgx] CUDA_HOME=$CH"
echo "[run_dgx] LD_LIBRARY_PATH=$LD_LIBRARY_PATH"
exec python3 "$HERE/app_zarr.py" "$@"

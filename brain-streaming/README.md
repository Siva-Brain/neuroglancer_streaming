# Brain Streaming — Hello World Brain prototype

Proves the streaming architecture **DGX → network → client → synthetic brain**
with a procedurally generated brain, spatial chunks, 3 LODs, incremental
streaming, and camera-driven prioritization.

The client used for the first test is a **browser (Three.js) viewer** served by
the DGX server itself (Unity is the documented next step — see
[docs/unity-setup.md](docs/unity-setup.md)). Both clients speak the **same
HTTP/JSON + binary protocol**, so Unity drops in with zero server changes.

```
DGX A100                                    Windows PC / laptop
┌───────────────────────────┐               ┌───────────────────────────┐
│ synthetic_brain (8 regions)│              │ browser Three.js viewer    │
│   → chunk/LOD generator     │  HTTP/JSON  │   BrainStreamClient (net)  │
│   → FastAPI streaming server│◀───────────▶│   BrainChunkCache          │
│     GET  /info /chunks       │  binary     │   BrainRenderer            │
│     GET  /chunk/{id}?lod=N   │  chunks     │   BrainCameraController    │
│     POST /view (camera)      │             │   [later] Sony ELF-SR2     │
└───────────────────────────┘               └───────────────────────────┘
```

## Quick start (test in ~5 min)

On the **DGX** (Linux):

```bash
cd brain-streaming/server
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt

python app.py --host 0.0.0.0 --port 8000 --chunk-delay-ms 120
```

Then, from **your laptop's browser**, open:

```
http://<DGX_IP>:8000/          # e.g. http://172.20.23.120:8000/
```

You should see a coarse (LOD0, faceted) brain appear region-by-region, then
sharpen to LOD2 where you're looking. **Drag to orbit / scroll to zoom** — the
region you turn toward becomes detailed; turn away and distant regions stay
coarse. The HUD (top-left) shows chunks received, bytes, per-LOD state, active/
pending requests, cache hits, and FPS.

- `--chunk-delay-ms 120` makes the progressive arrival visually obvious. Set
  `--chunk-delay-ms 0` for full speed, or `500` for slow-motion streaming.

## Network / ports

| What | Port | Notes |
|------|------|-------|
| Streaming server (HTTP) | **8000** (TCP, configurable) | must be reachable from the client machine |

Open one inbound TCP port on the DGX firewall:

```bash
sudo ufw allow 8000/tcp        # or: firewall-cmd --add-port=8000/tcp
```

Same-origin: the browser client is served *from* the server, so no CORS setup is
needed for the browser. CORS is already wide-open (`*`) for a future Unity/WebGL
build on another origin.

## Example API calls

```bash
DGX=http://172.20.23.120:8000

curl $DGX/api/health
curl $DGX/api/protocol
curl $DGX/api/brain/info
curl $DGX/api/brain/chunks | head -c 400

# one binary chunk (LOD2) -> file
curl -s "$DGX/api/brain/chunk/left_hemisphere?lod=2" -o lh_lod2.bin
xxd lh_lod2.bin | head -1     # starts with 'BRN1'

# camera-driven priority list
curl -s -X POST $DGX/api/brain/view -H 'Content-Type: application/json' -d '{
  "camera_position":[200,60,320],
  "camera_forward":[-0.4,-0.1,-0.9],
  "camera_rotation":[0,0,0,1],
  "fov":60,
  "viewport":{"width":1280,"height":720}
}'
```

## Project layout

```
brain-streaming/
├── server/                 # DGX side (Python / FastAPI)
│   ├── app.py              # HTTP endpoints + CLI (--chunk-delay-ms)
│   ├── brain/
│   │   ├── synthetic_brain.py   # 8 procedural regions (swap point for real data)
│   │   ├── lod.py               # ellipsoid mesh + binary 'BRN1' serialization
│   │   └── chunk.py             # catalog: builds all LODs, camera prioritization
│   ├── streaming/protocol.py    # transport-agnostic message shapes
│   └── requirements.txt
├── client/index.html       # Three.js viewer (Unity stand-in), served at "/"
├── unity/BrainStreaming/…  # C# scripts mirroring the browser client (next step)
└── docs/                   # architecture, protocol, dgx-setup, unity-setup
```

## Replacing the synthetic brain with the real reconstruction

The **only** module that knows the geometry is fake is
[`server/brain/synthetic_brain.py`](server/brain/synthetic_brain.py). Everything
downstream (chunking, LOD, serialization, prioritization, HTTP, the client)
operates on generic meshes and is reuse-ready.

To go real, keep the server API identical and change how the catalog is built in
[`server/brain/chunk.py`](server/brain/chunk.py):

1. **Source**: replace `make_ellipsoid(...)` per region with meshes derived from
   your reconstruction. Read the Zarr/Neuroglancer volume **on the DGX**, run
   marching-cubes per label/region to get a triangle mesh (e.g. skimage
   `measure.marching_cubes` or VTK).
2. **LOD**: replace the per-LOD tessellation with real mesh decimation
   (LOD0/1/2 = quadric-decimated versions, e.g. `open3d` /
   `trimesh.simplify_quadratic_decimation` / `pymeshlab`).
3. **Chunks**: for a large brain, split each region into spatial tiles (octree /
   grid) so `chunk_id` becomes `region/tile`, and populate `dependencies`
   (parent tile / lower LOD). The `Catalog` interface (`info/chunks/blob/
   prioritize`) does not change.
4. Serialize the resulting `(positions, indices)` with the existing
   `serialize_mesh()` — the client needs no changes. (Add Draco later as an
   `Accept`-negotiated encoding; see docs/protocol.md.)

Unity, Windows, and Zarr never touch each other: the DGX stays the only machine
that reads Zarr, and only meshes cross the network.

## Next 3 technical steps (not yet implemented)

1. **WebSocket transport + server push.** Move `VIEW_UPDATE`/`CHUNK_AVAILABLE`
   onto a WebSocket so the DGX *pushes* prioritized chunks (and true
   `CANCEL_CHUNK`) instead of the client polling `/view`. The protocol layer is
   already written to make this a transport swap, not a rewrite.
2. **Real mesh pipeline on one region.** Replace `synthetic_brain` for a single
   region with marching-cubes + quadric decimation from an actual Zarr volume,
   keeping the same 3-LOD chunk output — validates the swap end-to-end before
   scaling to all regions.
3. **Spatial tiling + Draco compression.** Split regions into an octree of tiles
   and add Draco-encoded payloads (content-negotiated), so chunk sizes stay
   small as real resolution grows.

## Docker

Runs the multi-block real-Zarr server (`app_zarr.py`) — all 5 block slabs plus
the WebGL client at `/`. Build context is `brain-streaming/`.

```bash
# from brain-streaming/
docker compose up --build            # CPU, http://<host>:8010/
```

Or plain Docker:

```bash
docker build -t brain-streaming -f server/Dockerfile .
docker run --rm -p 8010:8010 brain-streaming
```

Append flags to override the defaults (they pass straight to `app_zarr.py`):

```bash
docker run --rm -p 8010:8010 brain-streaming --min-level 4 --voxel-um 20,0.5,0.5 \
    --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr \
    --zarr http://3dstrokeviewer.humanbrain.in:8056/zarr_files/584_ALL_3d.zarr
```

**A100 / GPU** (needs the NVIDIA Container Toolkit on the host):

```bash
docker build -t brain-streaming:gpu -f server/Dockerfile.gpu .
docker run --rm --gpus all -p 8010:8010 brain-streaming:gpu
# or: docker compose --profile gpu up --build brain-gpu
```

The CPU image falls back to NumPy; the GPU image falls back to CPU if no GPU is
present. `PORT` and `MIN_LEVEL` env vars are honored by compose.

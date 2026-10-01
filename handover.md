# neuroglancer_streaming — Project Handover

**Repo**: `D:\SGBC\Unity Projects\neuroglancer_streaming` (git, branch `main`, remote `https://github.com/Siva-Brain/neuroglancer_streaming.git`, in sync with `origin/main` at `7872d5b` apart from one modified scene file — see §9).
**What it is**: a streaming pipeline that takes a whole human brain's histology, stored as five very large Zarr volumes on a remote HTTP server, and shows it as one reassembled 3D volume in a browser and in Unity on a Sony Spatial Reality Display (SRD, model ELF-SR2). A Python server on the DGX A100 box is the only machine that reads the Zarr data; clients only ever receive small voxel bricks.
**Unity project**: `brain-streaming/unity/BrainVolumeSRD` — Unity **6000.3.10f1**, **Built-in Render Pipeline** (not URP), Sony `SRDisplayUnityPlugin` **2.6.0**, scene `Assets/Scenes/SampleScene.unity`.
**Status in one line**: the server, both browser viewers and the Unity scene all exist and are committed (5 commits, all on 2026-09-30). The repo holds no record of the Unity scene having been seen on a real SRD or built to a player, and no evidence that the streamed brain refines beyond its coarsest level in Unity (§10, item 1).

**How this document was produced** (2026-10-01): by reading every doc, the server code, the Unity scripts, the scene file and the git history. **Nothing was run** — no server was started, Unity was not entered into Play, no endpoint was called. Measurements quoted below come from the repo's own docs and are marked as such. Things I worked out by reading code rather than observing are marked *inferred*. §11 lists what is verified and what is not.

The format follows `D:\SGBC\Stroke_video\handover.md` (the StrokeVideo_v2 handover). That project's §2bu describes a sibling SRD project (`SRD_test`) and several of its SRD/Editor gotchas apply here; the relevant ones are repeated in §8.

---

## 0. Orientation

There are three parts, and they only meet at an HTTP API:

```
Remote Zarr host (read-only source of truth)
  http://3dstrokeviewer.humanbrain.in:8056/zarr_files/{580,584,585,586,587}_ALL_3d.zarr
        │  HTTP range GETs, one shard at a time
        ▼
DGX A100 — brain-streaming/server/app_zarr.py  (FastAPI)
  decode shard → LRU cache → optional GPU downsample → BVX2 voxel brick
        │  HTTP/JSON + binary bricks
        ├──▶ Browser: client/volume.html   (all 5 blocks reassembled)   served at /
        ├──▶ Browser: client/roi585.html   (one region, 8 LODs)         served at /roi
        └──▶ Unity:   unity/BrainVolumeSRD (Texture3D bricks, raymarch, Sony SRD)
```

Two generations of the code live side by side. Don't confuse them:

| | Generation 1 — synthetic prototype | Generation 2 — real data (current) |
|---|---|---|
| Server | `server/app.py` | `server/app_zarr.py` |
| Data | 8 procedural ellipsoid "regions" | 5 real histology blocks from HTTP Zarr |
| Payload | `BRN1` triangle meshes | `BVX2` voxel bricks |
| API prefix | `/api/brain/*` | `/api/dataset/*`, `/api/view`, `/api/chunk/*`, `/api/transforms`, `/api/roi*` |
| Browser client | `client/index.html` | `client/volume.html`, `client/roi585.html` |
| Unity | `unity/BrainStreaming/` (scripts only, no project) | `unity/BrainVolumeSRD/` (full project) |
| Docs | `README.md` (top half), `docs/architecture.md`, `docs/protocol.md`, `docs/dgx-setup.md`, `docs/unity-setup.md` | `docs/dataset.md`, `docs/deployment.md`, `docs/unity-srd-setup.md`, `README.md` "Docker" section |

Generation 1 proved the architecture and is kept as a no-data-dependency fallback. All current work is Generation 2.

---

## 1. Repository layout

```
neuroglancer_streaming/
├── handover.md                      this file
├── .gitignore                       Python, Unity generated folders, *.log, *.bin, the T1 .raw, _Recovery
├── sony_srd_streaming.code-workspace
└── brain-streaming/
    ├── README.md                    Gen-1 quick start + Docker section for Gen-2
    ├── docker-compose.yml           services `brain` (CPU) and `brain-gpu` (profile gpu)
    ├── docs/                        7 docs, see the table in §0
    ├── client/
    │   ├── index.html               Gen-1 Three.js mesh viewer
    │   ├── volume.html              Gen-2 multi-block volume viewer (WebGL raymarch)
    │   └── roi585.html              Gen-2 ROI viewer: pick a centre, compare LODs
    ├── server/
    │   ├── app.py                   Gen-1 server
    │   ├── app_zarr.py              Gen-2 server (583 lines; everything in §3)
    │   ├── run_dgx.sh               wires CuPy's CUDA paths, then execs app_zarr.py
    │   ├── Dockerfile, Dockerfile.gpu
    │   ├── requirements.txt
    │   ├── roi_settings.json        backend default settings for the ROI viewer
    │   ├── transforms/
    │   │   ├── histology_blocks.json   AUTHORITATIVE per-block voxel size + placement affine
    │   │   └── stroke_transforms.csv   same blocks at 480 µm; not referenced by any code
    │   ├── datasource/              base.py (interface), http_zarr.py (sharded Zarr v3 reader)
    │   ├── chunks/                  manager.py (cache/dedup/BVX2), cache.py (LRU), priority.py (camera selection)
    │   ├── gpu/processor.py         CuPy downsample, falls back to NumPy
    │   ├── streaming/               protocol.py (Gen-1 shapes), view.py (Gen-2 pydantic models)
    │   ├── brain/                   Gen-1 synthetic brain, LOD, catalog
    │   └── tools/verify_chunk.py    fetch + decode one real shard, optionally save a PNG
    └── unity/
        ├── BrainStreaming/          Gen-1 C# scripts only (7 files), never made into a project
        └── BrainVolumeSRD/          the Unity project (§6)
```

---

## 2. The data: five histology blocks

One brain ("Stroke_1") was cut into five physical blocks. Each block was sectioned, imaged and stored as its own OME-Zarr pyramid on the remote host. The server streams all five and the clients reassemble them.

### Zarr format (from `docs/dataset.md`, inspected 2026-09-29 for block 580)
- Zarr **v3**, OME-NGFF 0.5, `uint8`, 4D `[z, y, x, c]`, **4 channels**.
- 8 pyramid levels, `0` finest to `7` coarsest. Only x/y are downsampled; z keeps every section at every level.
- Level 0 is `[457, 24000, 24000, 4]` at 8 µm in-plane; level 7 is `[457, 187, 187, 4]` at 1024 µm.
- Chunk grid `[8, 4096, 4096, 4]` (each chunk is a *shard*), inner chunks `[8, 256, 256, 4]`, blosc-zstd, shard index at the end with crc32c.
- Chunk URL is `{root}/{level}/c/{z}/{y}/{x}/{c}`. The `c` after the level is the Zarr chunk-key prefix, not the channel.
- **Channels 0, 1, 2 are RGB brightfield** (bright background, darker tissue). **Channel 3 is a binary tissue mask** (0 or 255). Clients request channels `0,3` and render `density = mask × (1 − grey)`.
- The remote host supports HTTP range requests. `tensorstore` hung against it, so the server uses its own reader (`requests` + `numcodecs`), measured there at about 22 ms per shard decode.

Only block 580's shape was inspected directly. The other four blocks' shapes are taken from `histology_blocks.json`.

### Per-block geometry (`server/transforms/histology_blocks.json`)
This file is the authority for voxel size and placement. Its header says it was copied from the Neuroglancer "3d-stroke-viewer" app (`apps/frontend/src/viewer/histology_blocks.ts`).

| Block id | Name in `stroke_transforms.csv` | Sections (z) | z spacing | In-plane | `centerX` (mm) | Thickness = sections × spacing |
|---|---|---:|---:|---:|---:|---:|
| 580 | block3 | 457 | 20.00 µm | 8 µm | −22.27 | 9.14 mm |
| 584 | block5 | 340 | 79.41 µm | 8 µm | −59.87 | 27.00 mm |
| 585 | block4 | 197 | 89.69 µm | 8 µm | −43.74 | 17.67 mm |
| 586 | block2 | 411 | 84.73 µm | 8 µm | 18.07 | 34.82 mm |
| 587 | block1 | 556 | 61.95 µm | 8 µm | 52.85 | 34.44 mm |

Each block is a slab about 192 mm × 192 mm in-plane (24000 × 8 µm). The thickness column is my arithmetic from the JSON, not a measured value.

### Reassembly: `omeToRas`
Each block carries a 3×4 affine `omeToRas` that maps its own physical millimetres to a shared **RAS mm** space (the frame the whole brain lives in). Its three input columns act on the block's axes in **z, y, x** order; the last column is a translation in mm. The affine is deliberately non-rigid: block 580's z column has magnitude about 4.13, so that block is stretched through its thickness when placed.

The server converts this to one 4×4 matrix per block that maps the client's block-local frame (millimetres, x-y-z order, block centred on its own origin) into RAS mm, and serves it from `GET /api/transforms`. The derivation is in the comment above `transforms_get` in `app_zarr.py:450`.

### The 8 µm versus 0.5 µm question
The repo contradicts itself here, so this needs stating plainly:
- `histology_blocks.json` and the comment at `app_zarr.py:57` say **8 µm in-plane is real** and that an earlier 0.5 µm override was wrong (it produced 12 mm cubes that would not reassemble).
- The docstring in `datasource/http_zarr.py:29`, the `--voxel-um` default (`20,0.5,0.5`) and its help text in `app_zarr.py:640`, and the Docker example in `README.md` still say the opposite.

What the code actually does: any block listed in `histology_blocks.json` uses the JSON's values, whatever `--voxel-um` says. `--voxel-um` only applies to a block that is not in the JSON. All five default blocks are in the JSON, so the stale 0.5 µm default has no effect on them. The stale text should be corrected before it misleads someone adding a sixth block.

---

## 3. DGX server — `server/app_zarr.py`

FastAPI app, version string "3.0", CORS open to all origins, no authentication. All blocks are built at startup (`build_blocks`), each with its own `HttpZarrBrainSource`, `ChunkManager` (LRU cache + thread pool) and `CameraViewSelector`.

### Endpoints
| Route | Purpose |
|---|---|
| `GET /` | serves `client/volume.html` |
| `GET /roi` | serves `client/roi585.html` |
| `GET /dashboard` | serves `client/index.html` (the Gen-1 viewer — see §10 item 6) |
| `GET /api/health` | status, GPU info, worker count, block ids |
| `GET /api/protocol` | protocol description |
| `GET /api/dataset/info` | `{blocks:[…]}` plus a top-level mirror of the first block for single-block clients. Per block: id, URL, `default_transform` (side-by-side layout), levels, `extent_mm` in **(z, y, x)** order, `baseline_chunks` (the whole coarsest level) |
| `GET /api/transforms` | per-block column-major 4×4, block-local mm → RAS mm. Returned twice: `transforms` keyed by block id (browser) and `list` as an array (Unity's `JsonUtility` cannot parse dictionary keys) |
| `POST /api/view` | camera in **one block's local mm frame** → prioritized chunk list for that block, capped at 24 chunks. Also prefetches the top 8 in parallel |
| `GET /api/chunk/{id}?channels=0,3&block=585` | one brick as `BVX2` |
| `POST /api/chunks/request`, `/api/prefetch` | warm the cache |
| `POST /api/chunks/cancel` | no-op over HTTP, kept for a future WebSocket transport |
| `GET /api/stats` | per-block cache and prefetch statistics |
| `GET /api/roi/info`, `GET /api/roi` | a centred cuboid of one block at one level, full z, GPU-downsampled to a size cap, as `BVX2`. 16 GB in-memory payload cache. Levels 7, 4, 2, 0 of block 585 are prewarmed at startup in a background thread |
| `GET`/`PUT /api/roi/settings` | read / overwrite `roi_settings.json` |

### Identifiers and payload
- **chunk id**: `L{level}.{z}.{y}.{x}.{c}` — shard grid indices. Each z index covers 8 sections.
- **`BVX2`** (little-endian, 50-byte header): `'BVX2'`, `u8 level`, `u8 nch`, `u16 pad`, `i32 z0,y0,x0`, `u16 dz,dy,dx`, `f32[6] bbox_mm (xmin,ymin,zmin,xmax,ymax,zmax)`, then `dz·dy·dx·nch` bytes in (z, y, x, c) order. The bbox is the brick's box in block-local mm for the *unresampled* brick, so a downsampled brick still lands in the right place. The module docstring in `chunks/manager.py` still describes the older `BVX1` layout without the bbox.

### Level selection (`chunks/priority.py`)
- Target level depends only on the camera's distance from the block centre: `t = min(distance / (2 × largest extent), 1)`, interpolated between `--min-level` and the coarsest level. A camera further than twice the block's largest dimension gets the coarsest level.
- Chunks inside the view cone come first, nearest first; the rest follow. At most 24 are returned.
- `--min-level` (default 3) is the finest level the server will stream. Level 0 is about 1 TB per block; do not set 0 casually.

### GPU (`gpu/processor.py`)
CuPy if available, NumPy otherwise; results are identical either way. Its job is `resample_xy_max`: block-mean downsample so no brick exceeds `--gpu-max-xy` (default 512) in x or y. `docs/deployment.md` reports the fetch dominating (~170 ms/shard) and GPU processing at ~4 ms, so the real gains are parallel prefetch and smaller bricks.

### Flags
`--zarr URL` (repeatable; default is the five blocks), `--host`, `--port` (default **8010**), `--min-level` (3), `--workers` (32), `--gpu/--no-gpu`, `--gpu-max-xy` (512), `--prefetch-top` (8), `--cache-mb` (2048, per block), `--delay-ms` and `--bandwidth-limit` (network simulation), `--voxel-um` / `--voxel-um-raw` (see §2).

---

## 4. Synthetic prototype — `server/app.py`

Eight procedural ellipsoid regions, three mesh LODs, `BRN1` binary meshes, camera-driven prioritization. No data dependency and no GPU. Port 8000, `--chunk-delay-ms` to make progressive arrival visible. Fully documented in `README.md`, `docs/architecture.md`, `docs/protocol.md` and `docs/dgx-setup.md`. Its Unity counterpart is the script-only folder `unity/BrainStreaming/` with setup steps in `docs/unity-setup.md`. Nothing current depends on it.

---

## 5. Browser clients (Gen-2)

- **`client/volume.html`** ("Brain Volume Stream", served at `/`): loads `/api/dataset/info` and `/api/transforms`, streams every block's baseline then camera-driven refinements, and raymarches all bricks in one WebGL view. Left HUD shows connection, block count, bandwidth, chunk counters and FPS. Right panel lists the blocks with per-block show/hide and editable transforms, plus Reset all, Copy JSON and Fit view. A visualization panel offers colour mode (grayscale / per-block / colormap), window level and width, threshold, density, lighting, quality and ray jitter. Visualization settings and per-block adjustments are saved in the browser's `localStorage`, so a stale saved layout can survive a server change — clear it or use Reset all if blocks look misplaced.
- **`client/roi585.html`** ("585 ROI · pick center + LODs", served at `/roi`): pick a centre in a block, then view the same region at several pyramid levels through `/api/roi`. Defaults come from `/api/roi/settings`; the page can promote its current settings to the backend default with a `PUT`.

The browser viewer is the reference implementation. The Unity client was written to mirror it.

---

## 6. Unity project — `unity/BrainVolumeSRD`

### Project facts
- Unity 6000.3.10f1, Built-in RP, linear colour space, default resolution 3840×2160.
- `Assets/SRDisplayUnityPlugin/` is Sony's plugin 2.6.0, committed in full (253 files including samples). The Sony runtime is installed on this machine at `C:\Program Files\Sony\SpatialRealityDisplay`.
- `Assets/csc.rsp` contains `-define:SONY_SRD_SDK`. This is what compiles the real `SonySRDAdapter`; Player Settings' scripting define symbols are empty. `docs/unity-srd-setup.md` says to use Player Settings — the project does it through `csc.rsp` instead.
- Player Settings: `insecureHttpOption = 2` (plain HTTP always allowed — required, the server is HTTP), `activeInputHandler = 2` (Both — the plugin uses legacy Input, `ModelMoveController` uses the new Input System), `runInBackground = 1`.
- Packages: Input System 1.18.0, uGUI, Timeline, and **`com.coplaydev.unity-mcp`** (the Editor can be driven from Claude Code through the UnityMCP tools).
- Build Settings scene list is **empty**. No player build is recorded anywhere in the repo.
- `docs/unity-srd-setup.md` was written before the project existed (it says "Unity 2021.3 LTS+" and "I could not run the Unity editor here"). Its class map and data flow are still accurate; its setup steps are superseded by the committed project.

### Scene `Assets/Scenes/SampleScene.unity` — nine root objects
| Object | State | Components and key values |
|---|---|---|
| `Main Camera` | **inactive** | Camera at (0, 1, −10), `BrainCameraController` |
| `Directional Light` | active | |
| `BrainRoot` | active | `BrainVolumeRenderer` (steps 48, density 8, material created at runtime), `ModelMoveController` (**disabled**). Position (−0.121, 0.352, 0.232), euler (−90, 0, 30), scale 0.5 |
| `SonySRD` | active | `SonySRDManager`, mode = `SonyElfSr2`, pointing at the `SRDisplayManager` instance |
| `HUD` | active | `BrainTelemetry` (IMGUI overlay) |
| `BrainApp` | active | `BrainApp`: `serverUrl`, channels `0,3`, `unitsPerMm` 0.01, no flips, view interval 0.35 s, max concurrency 6, cache 512 MB. `targetCamera` and `cameraController` both reference the inactive `Main Camera` |
| `SRDisplayManager` | active | Sony prefab at the origin, scale 3, `_SRDViewSpaceScale` 3, spatial clipping off |
| `Floor` | active | Plane at (0, 0, 0.356), scale (0.179, 1, 0.0712), `Materials/FloorMat.mat` (dark grey) |
| `T1Volume` | active in the working tree, inactive in the last commit (§9) | `T1VolumeLoader` (folder `T1`, base name `t1_mri`, `unitsPerMm` 0.0025, steps 128, density 1, window 0.1–1), `ModelMoveController` (enabled). Position (0, 0.34, 0.36), rotated 180° about Y |

### Streaming path (namespace `BrainVolume`)
| Layer | Files | Role |
|---|---|---|
| Orchestrator | `Scripts/BrainApp.cs`, `Scripts/BrainBlock.cs` | boot, one cache + scheduler per block, periodic `/api/view` per block, HUD stats |
| Networking | `Networking/BrainStreamClient.cs`, `BrainChunk.cs`, `BrainStreamRequest.cs` (`RequestScheduler`) | `UnityWebRequest` on the main thread, `JsonUtility` DTOs, BVX2 parse, prioritized bounded-concurrency fetch with cancellation |
| Cache | `Cache/BrainChunkCache.cs` | per-block LRU of GPU bricks, keyed by **z-slab only**, keeps the finest level seen per slab |
| Rendering | `Rendering/BrainVolumeRenderer.cs`, `BrainChunkRenderer.cs` (`Brick`), `BrainCoordinateSystem.cs`, `BrainRaymarch.shader`, `BrainCameraController.cs` | one `Texture3D` (RG16) per brick, drawn with `Graphics.DrawMeshNow` in `OnRenderObject`, sorted back to front |
| Sony | `Sony/SonySRDManager.cs`, `SonySRDAdapter.cs` | `ISonySRDAdapter` isolates the SDK; `NullSRDAdapter` when absent |
| Debug | `Debug/BrainTelemetry.cs` | on-screen stats |

Boot sequence in `BrainApp.Start`: `GET /api/dataset/info` (3 tries) → `GET /api/transforms` → build one `BrainBlock` per block with its world matrix (identity plus a warning if the server sent none) → bind the renderer → `FitView` → enqueue every block's baseline chunks. After that, `Update` posts the camera to `/api/view` for each block every 0.35 s, but only while the camera has moved or fetches are outstanding.

**Coordinate chain** (documented in `BrainCoordinateSystem.cs`, and the same as the browser):

```
unit cube ─BrickMmMatrix→ block-local mm ─WorldMatrix (omeToRas)→ RAS mm ─×unitsPerMm (+ optional flips)→ Unity local ─BrainRoot→ world
```

The camera goes the other way for `/api/view`: world → `BrainRoot` local → RAS mm → that block's local mm. Flips mirror the whole brain and are only for fixing left/right against a landmark; orient the brain by rotating `BrainRoot`.

**Because rendering happens in `OnRenderObject`, it runs once per rendering camera**, so the volume appears in the Sony plugin's eye cameras with no Sony-specific drawing code.

`Brain/Raymarch`: object-space march through the unit cube, premultiplied OVER blending, `ZWrite Off`, `ZTest Always`, `Cull Front` by default (so it still draws with the camera inside a brick). R = grey, G = tissue mask.

### Local T1 MRI path (independent of the server)
`Rendering/T1VolumeLoader.cs` + `Rendering/T1Raymarch.shader` + `Materials/T1Volume.mat` (tint 0.85, 0.65, 0.55). Loads `Assets/StreamingAssets/T1/t1_mri.json` and `t1_mri.raw` — a 384×384×183 single-channel R8 volume (26,984,448 bytes) downsampled 0.75× from a 512×512×244 NIfTI (`t1_mri.nii.gz`, 0.488 × 0.488 × 0.700 mm). The loader remaps axes from the raw file's (superior, anterior, right) to Unity's (right, up, anterior) and centres the volume on its transform. It uses per-sample opacity that does not depend on step length, giving a solid surface rather than fog.

**`t1_mri.raw` is gitignored.** It is on this machine but a fresh clone will not have it, and the loader will log `[T1] Missing …`. The conversion script named in the loader's comment (`nifti_grayscale_to_raw.py`) is not in this repo.

### Controls — `Scripts/ModelMoveController.cs` (global namespace)
Moves the object it sits on, in the SRD display frame (X right, Y up, Z away from the viewer), falling back to world axes if no active `SRDManager` is found.
- **W/S** deeper / nearer, **A/D** left / right, **Q/E** down / up, **left drag** slide in the display plane.
- **Arrow keys** or **right drag** rotate (turntable about the bounds centre).
- **+ / −** or **scroll** zoom, clamped to 0.25×–4× the starting scale.

It finds its pivot from child `Renderer` bounds. Neither `BrainRoot` nor `T1Volume` has a `Renderer` (both draw procedurally), so the pivot is the object's own position. That is correct for `T1Volume`, which is centred on its transform.

---

## 7. How to run

### Server (DGX, Linux)
```bash
cd brain-streaming/server
pip install -r requirements.txt

python3 app_zarr.py --host 0.0.0.0 --port 8010 --min-level 3 --no-gpu     # CPU, always works

# A100 / CuPy: one-time install, then launch through the wrapper
pip install --user cupy-cuda12x==13.3.0 nvidia-cuda-nvrtc-cu12 nvidia-cuda-runtime-cu12
./run_dgx.sh --host 0.0.0.0 --port 8010 --min-level 3 --workers 48
```
Docker, from `brain-streaming/`: `docker compose up --build` (CPU, port 8010) or `docker compose --profile gpu up --build brain-gpu`. `PORT` and `MIN_LEVEL` environment variables are honoured.

The DGX needs outbound HTTP to `3dstrokeviewer.humanbrain.in:8056` and one inbound TCP port. Check with `curl http://<host>:<port>/api/health`.

### Browser
`http://<host>:<port>/` for the reassembled brain, `/roi` for the ROI viewer.

### Unity
1. Open `brain-streaming/unity/BrainVolumeSRD` in Unity 6000.3.10f1 and load `Assets/Scenes/SampleScene.unity`.
2. Select `BrainApp` and set **Server Url** to the running server.
3. Press Play. The HUD (top left) should show `CONNECTED`, the block count, and bricks loading.
4. Without an SRD attached, the plugin's "Run Without Spatial Reality Display" setting must be on — see §8 item 3.

---

## 8. Standing gotchas

1. **There is no single agreed server address or port.** `app_zarr.py` and Docker default to 8010; `docs/deployment.md` and `docs/unity-srd-setup.md` use 8090; Gen-1 uses 8000; the `BrainApp` script default is `http://192.168.1.50:8090`; the committed scene has `http://172.20.23.156:8095/`; the uncommitted scene has `http://172.20.23.230:10226/`. I do not know which of these, if any, is serving right now. Confirm with `/api/health` before debugging anything else.
2. **`extent_mm` is in (z, y, x) order** in `/api/dataset/info`, while the BVX2 bbox and the transform matrices are in (x, y, z). `BrainBlock.HalfExtentMm` does the swap; any new client code must too.
3. **Running without the display.** There is no `Assets/SRDisplayUnityPlugin/Resources/SRDProjectSettings.asset` in this project, so the plugin uses its default, `RunWithoutSpatialRealityDisplay = false`. The StrokeVideo handover (§2bu A) records what that does on a machine with no SRD: entering Play pops native "Failed to detect Spatial Reality Display" error boxes, blocks the Editor main thread, and the plugin exits Play when they are closed. Turn the setting on under Project Settings ▸ Spatial Reality Display to test on a normal monitor, and off again for the real display. In that mode the plugin's mouse head-simulator also uses right-drag and scroll, which overlaps with `ModelMoveController` (set `rightDragRotates = false` if it gets in the way).
4. **One Unity Editor at a time** if you drive it through UnityMCP — the StrokeVideo handover notes all project copies share the bridge port (8080).
5. **The Unity brick cache is keyed by z-slab only.** That is correct only while each level has a 1×1 shard grid in x/y, which holds for levels 3–7 (3000 px and smaller fit in one 4096 shard). Levels 2, 1 and 0 have 2×2, 3×3 and 6×6 grids, so running the server with `--min-level` below 3 would make bricks overwrite each other in Unity. The comment in `BrainChunkCache.cs` says to extend the key to (z, y, x) when that day comes.
6. **Both raymarch shaders use `ZTest Always` and `ZWrite Off`.** They ignore scene depth: the volume draws over the floor and anything else, and two volumes do not occlude each other correctly.
7. **If a volume is invisible or looks inside-out**, flip `Cull` on its material (Front ↔ Back). If it is too faint or too solid, change density (streamed brain: 2–15 is the suggested range).
8. **`Texture3D` in `RG16`** is fine on desktop DX11; `docs/unity-srd-setup.md` lists fallbacks if a platform rejects it.
9. **Server-side caches are large by default**: 2 GB LRU *per block* (five blocks → up to 10 GB) plus a 16 GB ROI payload cache. Fine on the DGX (~2 TB RAM per `docs/deployment.md`), not on a laptop — pass `--cache-mb`.
10. **The first GPU call can take ~20 s** (CuPy compiles kernels). `GpuProcessor.warmup` does this at startup so no user request pays for it. Use `cupy-cuda12x==13.3.0`, not 14.x (per `docs/deployment.md`, 14.x fails to import on the DGX).
11. **The server has no authentication and open CORS**, and `PUT /api/roi/settings` rewrites a file on the server. Keep it on a trusted network.
12. **`LruChunkCache` defines `__len__`**, so an empty cache is falsy. Test `cache is not None`, never `cache or default` (noted in `chunks/manager.py`).
13. **`Assets/_Recovery/`** holds two crash-recovery scene copies from 2026-09-30 (14:48 and 14:56). They are gitignored. If Unity offers to recover scene backups on launch and the scene was saved, answer No (StrokeVideo handover §2bu F).
14. **Docs lag the code.** Treat `histology_blocks.json`, `app_zarr.py` and the scene file as truth; see §2 (voxel size), §6 (setup doc) and item 1 above (ports).

---

## 9. Uncommitted state (working tree, 2026-10-01)

One file is modified, `brain-streaming/unity/BrainVolumeSRD/Assets/Scenes/SampleScene.unity`, with two changes:
- `BrainApp.serverUrl`: `http://172.20.23.156:8095/` → `http://172.20.23.230:10226/`
- `T1Volume` GameObject: inactive → **active**

So in the working tree the streamed brain (`BrainRoot`) and the local T1 head (`T1Volume`) are both active and sit in overlapping positions inside the display volume. Given gotcha 6, they will not composite correctly against each other. Whether showing both is intended (for example, to compare or co-register them) or the T1 was switched on for a separate test is not recorded anywhere; ask before committing.

A Unity process was running on this machine when this was written. If it has this scene open with unsaved edits, the file on disk may not be the latest state.

---

## 10. Open issues and suggested next steps

1. **Streamed brain is probably stuck at the coarsest level in Unity** (*inferred from code, not observed*). `BrainApp` sends `/api/view` using `targetCamera`, which is the **inactive** `Main Camera` fixed at (0, 1, −10). That is roughly 10 Unity units from `BrainRoot`, which at scale 0.5 and 0.01 units/mm is about 2,000 mm in brain space — far beyond twice the block size (384 mm) — so the server's distance rule returns level 7 for every block. The camera never moves (its controller is on the inactive object, and `ModelMoveController` moves the model, not the camera), so after the baseline loads no further views are sent. The viewer's real eye position on the SRD is never used. Fix options: drive `/api/view` from the SRD's tracked eye camera or a synthetic camera placed at the viewer's nominal position in the display frame, and re-post when `BrainRoot` moves; or bypass distance and request a fixed level for the SRD case. Verify first by watching the HUD's "Target LOD" and "Loaded LOD" lines in Play.
2. **Decide the `BrainRoot` / `T1Volume` relationship** (§9), and if both are to be shown together, register the T1 into RAS mm properly. `t1_mri.json` carries the NIfTI affine, which is what a registration would start from.
3. **`ModelMoveController` is disabled on `BrainRoot`**, so the streamed brain cannot currently be moved with the keyboard or mouse; only `T1Volume` can. Enable it (and decide whether both objects should move together under a common parent).
4. **No evidence of a run on the real ELF-SR2 or of a player build.** Build Settings has no scenes. Add `SampleScene`, build Windows x64, and test on the display.
5. **Correct the stale voxel-size text** (§2): the `http_zarr.py` docstring, the `--voxel-um` default and help, and the README Docker example.
6. **`/dashboard` under `app_zarr.py` serves the Gen-1 viewer**, which calls `/api/brain/*` routes that only `app.py` provides (*inferred*). Either remove the route or point it at something that works.
7. **Pick one port and one address convention** and make the docs, Docker and the scene agree (§8 item 1). Consider reading the server URL from a config file or command-line argument so the scene file stops changing every time the server moves.
8. **Check in or document the T1 data path**: where `t1_mri.raw` comes from and how to regenerate it (§6).
9. **Longer-term items already listed in the repo's docs**: WebSocket transport with server push and real cancellation (`README.md`, `docs/protocol.md`); multi-GPU round-robin for brick processing (`docs/deployment.md`; already done for the ROI path); a per-shard occupancy scan to find which sections hold real tissue (`docs/dataset.md`).
10. **This project has no Claude Code memory yet** (`C:\Users\HishamKadambot\.claude\projects\D--SGBC-Unity-Projects-neuroglancer-streaming\memory\` is empty), so this file is the only carried-over context.

---

## 11. What is verified and what is not

| Claim | Basis |
|---|---|
| File layout, scene contents, component values, flags, endpoints, payload format | Read directly from the files on 2026-10-01 |
| Zarr format, channel meaning, fetch/decode timings, DGX hardware, GPU timings, CuPy version advice | Quoted from `docs/dataset.md` and `docs/deployment.md` (measured by their author on 2026-09-29/30); not re-measured |
| Block thicknesses in §2 | My arithmetic from `histology_blocks.json` |
| Shapes of blocks 584–587 | From the JSON's `nSections`; only block 580's Zarr metadata was inspected in the docs |
| SRD behaviour with no display attached | Quoted from the StrokeVideo handover §2bu; not reproduced here |
| §10 items 1 and 6 | Inferred by reading the code paths; not observed running |
| Whether any server is currently up, and at which address | **Unknown** |
| Whether the Unity scene has ever been seen on the real display | **Unknown** — nothing in the repo records it |

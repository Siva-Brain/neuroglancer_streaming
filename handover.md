# neuroglancer_streaming — Project Handover

**Repo**: `D:\Unity\new\neuroglancer_streaming` (git, remote `https://github.com/Siva-Brain/neuroglancer_streaming.git`). **Current branch `brick_L0`**, pushed to `origin/brick_L0` at `9e23a41`, plus uncommitted work (§9). `brick_L0` contains everything: lordsiva's offline-brick work *and* the `neuro_version` work merged in (§6). `origin/neuro_version` is behind (`dfe60ff`; the local branch has `715500b`).
**What it is**: a streaming pipeline that takes a whole human brain's histology, stored as very large Zarr volumes, and shows it as a 3D volume in a browser and in Unity on a Sony Spatial Reality Display (SRD, model ELF-SR2). Originally (§0-§5) a Python server on the DGX A100 streams voxel bricks from Zarr. Since 2026-10-01 the Unity side also has **local, server-free** paths for the hb02 brain (one exported level, or offline bricks) and a scripted **neuronal-loss** presentation scene (§6).
**Unity project**: `brain-streaming/unity/BrainVolumeSRD` — Unity **6000.3.25f1** (upgraded from 6000.3.10f1), **Built-in Render Pipeline** (not URP), Sony `SRDisplayUnityPlugin` **2.6.0**. Scenes: `Assets/Scenes/SampleScene.unity` (brain only) and `Assets/Scenes/NeuronalLossScene.unity` (the timeline).
**Status in one line**: `NeuronalLossScene` plays a seekable timeline — hb02 brain turns to the left sagittal view, is sliced, and SRD_test's neuronal-loss block grows out of the cut face — currently rendered from the **bricked** brain (`Brain` GameObject). The user has seen the rotation, slicing and block on the Editor's Game view; nothing has been built to a player or recorded on the real SRD.

**How this document was produced**: first version 2026-10-01 by reading the repo only (nothing run). **Updated 2026-10-02** after a day of work in this project (sessions with Claude Code): the hb02 data were measured directly from the exported `.raw` files, the label volume was downloaded from the live DGX server, and every code change was compiled in the open Unity Editor (exceptions noted in §9). Visual results are from the user's screenshots/feedback, not from automated checks. Things worked out by reading code rather than observing are marked *inferred*. §11 lists what is verified and what is not.

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

**Since 2026-09-30 the default dataset changed.** `app_zarr.py` now has a named-brain registry (`server/brain_registry.py`, `server/brains/*.json`). With no `--zarr`, it activates `Brain_580_One_block` = **hb02**: one fused 8 µm RGB block read from a local DDN path on the DGX (`/home/users/azhar/projectM/viewer_data/fused_8um/hb02_fused.zarr`), with a region label map (`hb02_labels.zarr`) and a LUT (`manifest.json`). `--zarr` still bootstraps the five-block Stroke_1 brain described in §2. Most of §6 is about hb02.

---

## 1. Repository layout

```
neuroglancer_streaming/
├── handover.md                      this file
├── .gitignore                       Python, Unity generated folders, *.log, *.bin, all StreamingAssets .raw volumes,
│                                    Bricks/hb02_fused/, Recordings/, .vs, _Recovery
├── copy.sh                          tar one hb02 level (fused + labels) on the DGX for copying to the SRD PC
├── sony_srd_streaming.code-workspace
└── brain-streaming/
    ├── README.md                    Gen-1 quick start + Docker section for Gen-2
    ├── docker-compose.yml           services `brain` (CPU) and `brain-gpu` (profile gpu)
    ├── docs/                        the docs in §0, plus performance-plan.md (DDN + VRAM-resident levels)
    │                                and brain2-srd-bricking-plan.md (lordsiva's offline-brick plan, brick_L0)
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
    │   ├── brain_registry.py, brains/  named brains (Brain_580_One_block = hb02, Brain_580_whole, Brain_585_single)
    │   ├── deploy/                  Caddyfile, nginx-http2.conf
    │   ├── datasource/              base.py (interface), http_zarr.py (sharded Zarr v3 reader), local_zarr.py (DDN path)
    │   ├── chunks/                  manager.py (cache/dedup/BVX2), cache.py (LRU), priority.py (camera selection)
    │   ├── gpu/processor.py         CuPy downsample, falls back to NumPy
    │   ├── streaming/               protocol.py (Gen-1 shapes), view.py (Gen-2 pydantic models)
    │   ├── brain/                   Gen-1 synthetic brain, LOD, catalog
    │   └── tools/
    │       ├── verify_chunk.py      fetch + decode one real shard, optionally save a PNG
    │       ├── zarr_level_to_raw.py one pyramid level -> <name>.raw + .json for FusedVolumeLoader (RGB24, or R8 labels + .lut)
    │       └── prebrick_srd.py      (brick_L0) one level -> BC3/BC7 bricks + index.json for BrickVolumeLoader
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
- `--min-level` (default 4 since 2026-10-01; was 3) is the finest level the server will stream. Level 0 is about 1 TB per block; do not set 0 casually.

### GPU (`gpu/processor.py`)
CuPy if available, NumPy otherwise; results are identical either way. Its job is `resample_xy_max`: block-mean downsample so no brick exceeds `--gpu-max-xy` (default 512) in x or y. `docs/deployment.md` reports the fetch dominating (~170 ms/shard) and GPU processing at ~4 ms, so the real gains are parallel prefetch and smaller bricks.

### Flags
`--zarr URL` (repeatable; bootstraps the five Stroke_1 blocks), `--brain NAME` (a `server/brains/` entry; default hb02), `--host`, `--port` (default **8010**), `--min-level` (now **4**), `--workers` (32), `--gpu/--no-gpu`, `--gpu-max-xy` (now **1500**), `--resident-level` (4: preload that level into GPU memory), `--glass/--no-glass`, `--prefetch-top` (8), `--cache-mb` (2048, per block), `--delay-ms` and `--bandwidth-limit` (network simulation), `--voxel-um` / `--voxel-um-raw` (see §2).

### Added after 2026-09-30 (*read from code, not exercised*)
`/api/brains`, `/api/brains/{name}`, `/api/brains/save`, `/api/brains/{name}/activate` (brain registry); `/api/label_chunk/{id}` (single-channel BVX2 of region ids, never resampled) and `/api/labels/lut`. `/api/label_chunk` **was** used on 2026-10-01 to rebuild `hb02_L7_labels.raw` (§6). The server at `http://dgx3.humanbrain.in:8010` (= `172.20.23.156:8010`) answered `/api/health` that day with `blocks: ["hb02"]`, CuPy, 8 GPUs.

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
- Unity **6000.3.25f1**, Built-in RP, linear colour space, default resolution 3840×2160.
- `Assets/SRDisplayUnityPlugin/` is Sony's plugin 2.6.0, committed in full. `Assets/SRDisplayUnityPlugin/Resources/SRDProjectSettings.asset` now exists (committed 2026-10-01), so "Run Without Spatial Reality Display" is a saved project setting — check it before testing without/with the display.
- `Assets/csc.rsp` contains `-define:SONY_SRD_SDK` (this, not Player Settings, compiles the real `SonySRDAdapter`).
- Player Settings: `insecureHttpOption = 2` (plain HTTP always allowed), `activeInputHandler = 2` (Both), `runInBackground = 1`.
- Packages: Input System 1.20.0, uGUI 2.0.0, Timeline, `com.coplaydev.unity-mcp`.
- Build Settings scene list is **empty**. No player build exists.

### Three ways the brain gets into Unity
| Path | Component | Data | Server? |
|---|---|---|---|
| Streamed (original Gen-2) | `BrainApp` + `BrainVolumeRenderer` on `BrainRoot` | `/api/view` + `/api/chunk` BVX2 bricks | yes |
| One exported level | `FusedVolumeLoader` on `FusedVolume` | `StreamingAssets/Fused/hb02_L<n>.raw` (+ `_labels.raw` / `.lut`) from `tools/zarr_level_to_raw.py` | no |
| Offline bricks (brick_L0) | `BrickVolumeLoader` (whole level) / `BrickStreamer` (LOD, not in a scene) on `Brain` | `StreamingAssets/Bricks/hb02_fused/index.json` + `L<n>/b_*.bc3` from `tools/prebrick_srd.py` | no |

All draw procedurally in `OnRenderObject`, so they appear in the SRD eye cameras without SRD-specific code. All volume shaders use `ZTest Always` / `ZWrite Off` (they ignore scene depth; see §8).

### The hb02 data (measured 2026-10-01 from the exported .raw files)
- Volume 181.2 × 99.3 × 155.2 mm (x, y, z). L4 = 1416 × 776 × 485 voxels, 0.128 × 0.128 × 0.32 mm. L7 = 177 × 97 × 485. (L3 is 0.064 mm, L6 0.512 mm.) z = **485 sections = the left-right axis** (tissue nearly vanishes around z ≈ 230, the interhemispheric gap). Image row 0 (texture y = 0) is the top of the brain.
- Background is **pure white**; tissue is only slightly darker (max(rgb) 224-240) but clearly **coloured** (saturation max-min 24-56 vs < 8 for background). Darkness-based rendering (`1 - max(rgb)`) therefore gives a white fog; **opacity must come from saturation**.
- L4 has whole **black 512×512 blocks** (unwritten Zarr chunks). Trilinear filtering blends black into white -> grey sheets.
- Each section has **stitching seams**: coloured straight lines 10-22 voxels wide at L4 (1.3-2.8 mm) outside the brain, stacking into flat planes, plus some white seam gaps through tissue.
- `hb02_L3.raw` on this PC was a truncated copy (470 MB of 6.4 GB) and L3 is too big for one `Texture3D` anyway (2832 > 2048 per side on DX11; > 2 GB for `File.ReadAllBytes`). L4 (1.6 GB) is the finest level `FusedVolumeLoader` can load. The truncated L3/L2 .raw are no longer present; only their .json remain.
- Labels: `hb02_L7_labels.raw` was rebuilt on 2026-10-01 by downloading all 485 `/api/label_chunk/L7.<z>.0.0.0?block=hb02` bricks from the DGX and concatenating them (8,326,965 B = 177×97×485). 92 % of tissue voxels fall in a labelled region (73 % if mirrored), so it is aligned. 125 region ids, all present in the `.lut`. `hb02_L4_labels.raw` is now also present locally (copied by the user; not checked here).

### `FusedVolumeLoader` + `Brain/FusedRaymarch` (single exported level)
- On load: black no-data voxels -> white (`blackToWhite`), then a per-section **seam filter** (`seamFilterMm` 1.5: box opening cuts thin lines loose; then only pieces with a core ≥ `seamCoreMm` 3.2 survive, removed whole otherwise). Both in mm, so they behave the same at any level. The user confirmed "planes are gone".
- Shader: opacity from saturation window (`saturationLow` 0.06 / `saturationHigh` 0.2), colour `pow(rgb, colorGamma 2.5) * brightness 1.2`, gradient shading (`shading` 0.7), per-pixel ray jitter, `raySteps` 256, `opacity` 0.6.
- Region labels (merged from lordsiva's "lut apply"): `labelName`, `maskToLabels`, `colorRegions`, `labelOpacity`; label tint is applied before shading.
- Slicing: clip box along `sliceAxis` (default Z = sagittal). Keys **C** slice in / slice back (each press flips direction), **V** reset, **[ ]** step — switched off (`sliceKeysEnabled`) when a timeline owns the slice.
- Extras used by the timeline: `UnitCubeToWorld`, a `Drawn` event raised right after drawing (overlays composite on top), an optional carved `hole` box (currently unused).

### `BrickVolumeLoader` + `Brain/SRDBrickRaymarch` (offline bricks, brick_L0)
- lordsiva's P2 loader: reads `index.json`, uploads every brick of one level as BC3 `Texture3D`, places it by its core bbox (centred on the transform, same unit cube as `FusedVolumeLoader`, 0.0025 units/mm).
- 2026-10-02: shader switched to the `FusedRaymarch` look (saturation opacity, gamma, shading, jitter). Black-to-white and the seam filter can **not** be applied (data are BC3-compressed): grey blends are transparent anyway, but coloured seam planes would need fixing in `prebrick_srd.py`.
- Opacity is **step-length corrected** against the whole volume (`_BrickToVol`, `_RefSteps` 256): before, each brick took `steps` samples over its own small box and over-accumulated (smeared, streaky look).
- Slicing: `slicePosition` / `sliceFromHighZ` in whole-volume units, converted per brick to a `_ClipMin/_ClipMax` box; fully cut bricks are skipped. `Drawn` event as in `FusedVolumeLoader`.
- Data on this PC: L2 ≈ 8.1 GB (24 bricks), L3 ≈ 2.1 GB, L4 ≈ 0.5 GB, all BC3, gitignored. The scene's `Brain` loads **L2** — all bricks resident at once.
- `BrickStreamer.cs` (lordsiva, "all stages") uses the same shader; it passes the new per-brick values but is not in any scene.

### `ISliceableVolume`
Interface (`Rendering/ISliceableVolume.cs`) implemented by both loaders: `Loaded`, `UnitCubeToWorld`, `SlicePosition`, `SliceFromHighZ`, `SliceKeysEnabled`, `Drawn`. The timeline only talks to this, so it can drive either brain.

### `NeuronalLossScene` — the timeline (`Rendering/NeuronalLossSequence.cs`)
A copy of `SampleScene` (which stays brain-only) with a `NeuronalLossSequence` GameObject. Root objects: `Brain` (BrickVolumeLoader + ModelMoveController, **active**, pos (0.256, 0, −0.628), scale 1), `FusedVolume` (FusedVolumeLoader + ModelMoveController, **inactive but kept**, pos (0, 0.5, −0.4), scale 1.4, L4 + L4 labels), plus the original `BrainRoot`/`BrainApp`/`HUD`/`SonySRD`/`Floor`/`T1Volume`.

Which brain it drives: the `volume` field if set, else the active `BrickVolumeLoader`, else an active `FusedVolumeLoader` (logged as `[Loss] Timeline drives '<name>' (<type>)`).

Everything is a **pure function of timeline time** (seeking lands exactly):
| Step | Default length | What |
|---|---|---|
| Brain | `showBrain` 2.5 s | brain at Euler `initialEuler` (0, 270, 180) |
| Rotate | `rotate` (scene: 4 s) | **Euler Y only** 270 -> `endY` 360 (+ `extraTurns`×360), X and Z held = pure world-Y turn, ends on the left sagittal view |
| Slice | `slice` 4 s | cut from the side facing the viewer to `clippingDepth` 0.3 |
| Neuronal loss | `grow` 4 s + label + `hold` 3 s | block appears tiny (`startScale` 0.02) at `pointOnCut` on the cut face, heads straight out of the cut face (`arcTowardViewer` 0.3) and curves left past the brain's front (`sideDistance` 0.2, `comeOut` 0.3), slight size overshoot; then the label card fades in |

- **The brain's transform position and scale are never written** — only its rotation (and only while the timeline time moves) and its slice.
- `pointOnCut` (0.82, 0.28) = upper-front cortex marked by the user on a screenshot (located by calibrating against the ROI dot; just inside the tissue edge of section 145). Same coordinates for both loaders.
- The block is **SRD_test's neuronal-loss block**: `StreamingAssets/NeuronalLoss/NeuronalLoss_48x48x117_RG.bytes` (copied from `D:\Unity\SRD_test\Assets\Volume`, RG16: R = `neuronal_loss_roi_smooth` grey tissue, G = `neuronal_loss_inverse_roi` pink/purple signal), drawn by `Brain/NeuronalLossVolume` (Built-in port of SRD_test's URP shader, same numbers), true voxel proportions, yaw 45°, tilt 12°, `blockLength` 0.3, **not rotating** (`spinDegreesPerSecond` 0), white bold-italic "Neuronal loss / Stroke tissue · 3D histology stack" card. SRD_test's arrow is available (`showArrow`) but off.
- Source .npy (not used at runtime): `D:\Unity\new\npy_neuronal_loss\neuronal_loss_{roi_smooth,inverse_roi}_133-181_81-129.npy`, float32 (117, 48, 48). The ROI is x 133-181, y 81-129 of hb02 **L6** (354×194), all sections (checked: 77 % tissue vs 9-14 % for the swapped box). Axis 0 (117) spans all 485 sections (both maps ≈ 0 at slice 56 ≈ the midline gap) — *inferred*.
- **Final block pose you set yourself**: at runtime the block's final pose is the GameObject **`NeuronalLossBlock`** (top level in the Hierarchy, Play mode only). Once the block is in place the mouse/keyboard move **the block** (`controlsMoveBlock`; **M** switches to the brain and back; WASD/QE move, arrows/right-drag rotate, +/-/scroll scale); **K** copies `finalPosition / finalEuler / finalScale` to the clipboard and Console. Put those in the fields and tick `useCustomFinal` to make them permanent. *The user was about to choose these values; none are set yet.*
- **Seek bar** `UI/TimelineTransportUI.cs` (port of SRD_test's `StoryTransportUI`, added automatically): play/pause, scrubbable bar with a tick per step, time and step name, on the panel near the bottom edge. Keys: **Space** play/pause, **R** restart, **, .** ±1 s (hold to scrub), **[ ]** ±5 s, **Home/End**, **H/T** hide/show. While the bar is dragged `TimelineTransportUI.PointerCaptured` makes `ModelMoveController` ignore the mouse (so a drag never moves the brain).

### Other scripts added 2026-10-01
- `Rendering/BrainShowcase.cs`: 30 s scripted orbit/slice of the fused volume with MP4 recording (`UnityEditor.Media.MediaEncoder`, Editor only, to `BrainVolumeSRD/Recordings/`). **P** preview, **F10** record. Not in any scene (add it to a GameObject to use). Written for the earlier "video" request; never recorded.
- `UI/UiKit.cs`: tiny world-space uGUI builders.
- `ModelMoveController.cs`: one-line guard for the seek bar (above).

### Streaming path (unchanged, `BrainApp` on `BrainRoot`)
`BrainApp` gained `pinCoarsest` (default on: pin to the coarsest level), `wholeBrain`, `lodMin/lodMax` and an RGB path for hb02. Its default `serverUrl` is `http://dgx3.humanbrain.in:8010`; the scenes have `http://172.20.23.156:8010/` (same machine). `BrainRoot` is **inactive** in both scenes. The coordinate chain, cache and Sony adapter are as described in the 2026-10-01 version of this file (see git history of `handover.md`).

### Local T1 MRI path
`T1VolumeLoader` + `T1Raymarch` on `T1Volume` (inactive). `t1_mri.raw` is gitignored and was not on this PC's StreamingAssets listing on 2026-10-02.

---

## 7. How to run

### Server (DGX, Linux)
```bash
cd brain-streaming/server
python3 app_zarr.py --host 0.0.0.0 --port 8010 --no-gpu          # CPU; default brain = hb02
./run_dgx.sh --host 0.0.0.0 --port 8010 --workers 48             # CuPy (cupy-cuda12x==13.3.0)
```
Check with `curl http://dgx3.humanbrain.in:8010/api/health`.

### Exporting data for the local Unity paths (on the DGX)
```bash
cd brain-streaming/server/tools
python3 zarr_level_to_raw.py --level 4                                     # hb02_L4.raw/.json (RGB24)
python3 zarr_level_to_raw.py --level 4 --zarr .../hb02_labels.zarr         # hb02_L4_labels.raw/.json/.lut
python3 prebrick_srd.py ...                                                # bricks (see docs/brain2-srd-bricking-plan.md)
```
Copy the outputs into `Assets/StreamingAssets/Fused/` or `Assets/StreamingAssets/Bricks/hb02_fused/` — **they are gitignored, so a fresh clone has none of them.** Check sizes: an L4 RGB .raw is exactly 1416×776×485×3 bytes; the loaders refuse a truncated file.

### Unity
1. Open `brain-streaming/unity/BrainVolumeSRD` in **6000.3.25f1**.
2. `Assets/Scenes/NeuronalLossScene.unity` -> Play: the timeline runs (Space/R/seek bar). `SampleScene.unity` = brain only (C slices).
3. Without an SRD attached, the plugin's "Run Without Spatial Reality Display" setting must be on (Project Settings ▸ Spatial Reality Display, stored in `SRDProjectSettings.asset`).

---

## 8. Standing gotchas

1. **Address**: the live server is `http://dgx3.humanbrain.in:8010` = `http://172.20.23.156:8010` (2026-10-01). Older docs/scenes mention 8090, 8095, 10226.
2. **`extent_mm` is (z, y, x)** in `/api/dataset/info`; BVX2 bbox and transforms are (x, y, z).
3. **Running without the display**: see §7.3 and the StrokeVideo handover §2bu A.
4. **One Unity Editor at a time** if driven through UnityMCP (shared bridge port 8080).
5. **Streamed brick cache is keyed by z-slab only** (fine for levels ≥ 3).
6. **All volume shaders ignore depth** (`ZTest Always`, `ZWrite Off`). Volumes draw over everything; ordinary scene objects (floor, the uGUI label card and seek bar) can be **hidden behind the brain**. Overlays that must sit on top are drawn from the loader's `Drawn` event (the neuronal-loss block does this).
7. **Large StreamingAssets** (all gitignored): `Fused/hb02_L4.raw` 1.6 GB, `Bricks/hb02_fused/L2` 8.1 GB. The `Brain` object loads all of L2 at Play — slow start and heavy GPU memory; `level = 3` (2.1 GB) if it struggles.
8. **Namespace trap**: brick_L0's scripts live in `BrainVolume.SRD`, so inside `namespace BrainVolume` a bare `SRD.Core.X` resolves to `BrainVolume.SRD.Core` and fails (CS0234). Use `using SRD.Core;` at file top (current fix in the Sony scripts) or `global::SRD.Core.X`.
9. **Play-mode edits are lost** on Stop — e.g. the `NeuronalLossBlock` pose; copy with K first.
10. **Unity compiles only when the Editor has focus**; a stale `Library/ScriptAssemblies/Assembly-CSharp.dll` timestamp means "not compiled yet", not "compiled OK".
11. **Branches**: `brick_L0` is lordsiva's; today's work was merged into it and pushed. `neuro_version` (local) also has the NeuronalLossScene commit `715500b`, but `origin/neuro_version` does not.
12. Server-side gotchas from the first version still hold: big default caches (2 GB/block + 16 GB ROI), first CuPy call ~20 s, no auth / open CORS, `LruChunkCache` is falsy when empty, docs lag the code.

---

## 9. Working-tree state (2026-10-02, branch `brick_L0`)

**Uncommitted** (2026-10-01 late / 2026-10-02):
- Brick shader -> FusedRaymarch look + step-length opacity correction + clip box: `Rendering/SRDBrickRaymarch.shader`, `SRD/BrickVolumeLoader.cs`, `SRD/BrickStreamer.cs` (per-brick values only).
- Timeline drives the bricked `Brain`: new `Rendering/ISliceableVolume.cs` (+ .meta), `Rendering/FusedVolumeLoader.cs` (implements it), `Rendering/NeuronalLossSequence.cs` (`volume` field, `FindVolume`), `Scenes/NeuronalLossScene.unity` (the user's `Brain` GameObject, `FusedVolume` inactive, block-controls fields).
- `BrainVolumeSRD.slnx` — Unity/VS regenerated, whitespace only; leave out.

**Compile status**: all of the above compiled without errors (Editor.log, 2026-10-02 10:36). The one Play run since logged `[Loss] No active brain volume (BrickVolumeLoader or FusedVolumeLoader) in the scene.` with **no `[Bricks]` line at all** in that session, i.e. `BrickVolumeLoader` never started — most likely `Brain` was switched off in the Editor during that run (the saved scene has it active). Re-check: Brain active, press Play in `NeuronalLossScene`, expect `[Bricks] hb02_fused L2: loaded 24/24 bricks` and `[Loss] Timeline drives 'Brain' (BrickVolumeLoader)`.

**Stash**: `stash@{0}` "brick_L0 Unity-generated slnx + packages-lock before merging neuro_version" — regenerated files, normally safe to drop.
**Backup**: 16 `.meta` files Unity had regenerated on brick_L0 before the merge are in the Claude scratchpad (`brick_L0_untracked_backup`); the tracked versions from neuro_version replaced them. Not needed unless a GUID problem shows up.

---

## 10. Open issues and suggested next steps

1. **Run the uncommitted Brain/timeline work** with `Brain` active (§9), then commit it on brick_L0.
2. **Set the neuronal-loss block's final transform**: in Play press End (block in place), move `NeuronalLossBlock` or edit it in the Inspector, press K, put the values in `finalPosition/finalEuler/finalScale` with `useCustomFinal` on.
3. **Seam planes on the bricked brain**: if visible, add black-to-white + the seam filter (port `FusedVolumeLoader.RemoveSeams`) to `tools/prebrick_srd.py` and re-brick.
4. **Brain vs FusedVolume placement**: `Brain` sits at a different position/scale from `FusedVolume`; the block's computed end pose follows the brain, but `pointOnCut` / `sideDistance` / `comeOut` were tuned on FusedVolume's screenshots.
5. **Which side is the left hemisphere** is still *inferred* (section 0 faces the viewer at Y = 360). If it is the right one, set `endY` = 180.
6. **Video**: `BrainShowcase` records the old orbit, not this timeline. If a video of NeuronalLossScene is wanted, point F10 recording at `NeuronalLossSequence` (it is already time-driven, so `Time.captureFramerate` recording fits).
7. **Push `neuro_version`** if anyone still works from that branch.
8. Nothing has been **built to a player** or **seen on the real ELF-SR2**; Build Settings has no scenes.
9. From the first version, still open: stale 0.5 µm voxel text (§2), `/dashboard` serves the Gen-1 viewer, pick one port/address convention, document the T1 data path, WebSocket transport / multi-GPU / occupancy scan.

---

## 11. What is verified and what is not

| Claim | Basis |
|---|---|
| hb02 voxel statistics (white background, saturation separates tissue, black 512² chunks, seam widths, interhemispheric gap at z ≈ 230) | Measured 2026-10-01 from `hb02_L4.raw` / `hb02_L7.raw` with small C# scans |
| Seam filter removes the planes without eating the brain | Offline test on L4 sections 60/260/330/420 + the user's "planes are gone" |
| `hb02_L7_labels.raw` correct and aligned | Built from the live server's `/api/label_chunk`; header/size checks; 92 % vs 73 % alignment test |
| ROI of the .npy files = L6 x 133-181, y 81-129 | Tissue-overlap test (77 % vs 9-14 %); the in-plane transpose could not be decided from data |
| Timeline, block, seek bar, centre/side placements, Y-only rotation | Compiled without errors in the Editor; behaviour from the user's Play-mode screenshots/feedback |
| Brick shader look + timeline on `Brain` (2026-10-02) | Compiled without errors; **not yet seen running** (the only Play run had Brain inactive, see §9) |
| SRD_test reference look | From `D:\Unity\SRD_test\Captures\neuronal_block_final.png` / `neuronal_loss_pose.png` (the .mp4 was not viewed) |
| Server state, Gen-1, browser clients, streamed path | As in the first version: read from code/docs, not re-run (except `/api/health` and `/api/label_chunk` on 2026-10-01) |
| Real SRD display, player build | **Unknown / not done** |

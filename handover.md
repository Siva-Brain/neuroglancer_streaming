# neuroglancer_streaming — Project Handover

**Repo**: `D:\Unity\new\neuroglancer_streaming` (git, remote `https://github.com/Siva-Brain/neuroglancer_streaming.git`). **Current branch `brick_L0`**, pushed to `origin/brick_L0` at `589ac1d` (2026-10-02). The later BFI / V1 changes are **uncommitted** (§9). `brick_L0` contains everything: lordsiva's offline-brick work *and* the `neuro_version` work merged in (§6). `origin/neuro_version` is behind (`dfe60ff`; the local branch has `715500b`).
**What it is**: a streaming pipeline that takes a whole human brain's histology, stored as very large Zarr volumes, and shows it as a 3D volume in a browser and in Unity on a Sony Spatial Reality Display (SRD, model ELF-SR2). Originally (§0-§5) a Python server on the DGX A100 streams voxel bricks from Zarr. Since 2026-10-01 the Unity side also has **local, server-free** paths for the hb02 brain (one exported level, or offline bricks) and a scripted **neuronal-loss** presentation scene (§6).
**Unity project**: `brain-streaming/unity/BrainVolumeSRD` — Unity **6000.3.25f1** (upgraded from 6000.3.10f1), **Built-in Render Pipeline** (not URP), Sony `SRDisplayUnityPlugin` **2.6.0**. Scenes: `SampleScene` (brain only), `NeuronalLossScene` (the development timeline), `NeuronalLossScene_DualScreen` (copy + a second-monitor camera), and the three **presentation scenes** `Nissl and Labels V1`, `NeuronalLoss Display V2`, `NeuronalLoss Fib Astrocytes V3` (§6a), each built to its own Windows exe.
**Status in one line**: three 30-second, **looping** presentation timelines (V1: BFI / Nissl / labels three-way split + slice; V2: slice + neuronal-loss block; V3: slice + neuronal loss, Fib and astrocyte maps coming out of the brain) run in the Editor and are built to `BrainVolumeSRD/Builds/<scene>/<scene>.exe` (~46 GB each). Every step was checked in Editor Play mode with screenshots, but **the exes have not yet been seen working on the real SR display**. The first exe showed nothing; two causes were fixed (shaders stripped from the build, a second-display window covering the SRD), and the user's test with the Editor closed is still outstanding (§10).

**How this document was produced**: first version 2026-10-01 by reading the repo only (nothing run). **Updated 2026-10-02** after a day of work in this project (sessions with Claude Code): the hb02 data were measured directly from the exported `.raw` files, the label volume was downloaded from the live DGX server, and every code change was compiled in the open Unity Editor (exceptions noted in §9). **Updated again 2026-10-03** for the presentation scenes V1-V3, the exe builds, the `.npz`/`.nii` map loader and the player-build fixes (§6a, §7, §8, §9-§11). Those were compiled and run in Editor Play mode through UnityMCP and checked with screenshots from a fixed viewer position. Visual results are from those screenshots and the user's feedback, not from automated checks. Things worked out by reading code rather than observing are marked *inferred*. §11 lists what is verified and what is not.

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
        ├── npz_files/               (gitignored) astrocyte_density / fib_probability HB02 maps (.npz), §6a
        ├── bfi/                     (gitignored) BFI_in_MRI_2.nii (200 MB NIfTI), §6a
        └── BrainVolumeSRD/          the Unity project (§6); Builds/<scene>/ = the exes (gitignored, ~46 GB each)
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
- Build Settings scene list holds only `Nissl and Labels V1`. The exes are built per scene by `Assets/Editor/SceneBuilds.cs` or `manage_build` with the scene passed explicitly (§7), so the list does not matter.
- **Graphics ▸ Always Included Shaders** now lists every custom shader that scripts load with `Shader.Find`: `Brain/SRDBrickRaymarch`, `FusedRaymarch`, `NeuronalLossVolume`, `OverlayUnlit`, `DensityVolume`, `Raymarch`, `T1Raymarch`. Without that, a player build strips them and draws nothing (§8.13). Add any new `Shader.Find` shader there too.
- Graphics API: Direct3D 12 (automatic) in both the Editor and the exe.

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
- Data on this PC: `Bricks/hb02_fused` L1-L4 ≈ 43 GB in total (L1 ≈ 32 GB, L2 ≈ 8.1 GB / 24 bricks, L3 ≈ 2.1 GB, L4 ≈ 0.5 GB), all BC3, gitignored. All bricks of the loaded level stay resident at once.
- **GPU-memory fallback**: `Brain` asks for `level` 1. If the level is bigger than `vramBudgetMB` (0 = 70 % of the GPU's memory), the finest level that fits loads instead and a warning is logged. On this RTX 3090 (24 GB) L1 needs 32.8 GB against a 17 GB budget, so **L2 loads**, in the Editor and in the exe.
- **Tissue mask** (`useTissueMask`, on): the BC3 bricks cannot be seam-filtered, so at load a whole-volume R8 mask is built from `Fused/hb02_L5.raw`. It uses the same black-to-white + seam filter as `FusedVolumeLoader`, grown by `maskGrowMm` 0.3. Opacity outside the mask is zero, which removes the stitching-seam planes. About 1.3-1.9 s at start.
- `BrickStreamer.cs` (lordsiva, "all stages") uses the same shader; it passes the new per-brick values but is not in any scene.

### `ISliceableVolume`
Interface (`Rendering/ISliceableVolume.cs`) implemented by both loaders: `Loaded`, `UnitCubeToWorld`, `SlicePosition`, `SliceFromHighZ`, `SliceKeysEnabled`, `Drawn`. The timeline only talks to this, so it can drive either brain.

### `NeuronalLossScene` — the timeline (`Rendering/NeuronalLossSequence.cs`)
A copy of `SampleScene` (which stays brain-only) with a `NeuronalLossSequence` GameObject (components `NeuronalLossSequence` + `TimelineRecorder`). Root objects: `Brain` (BrickVolumeLoader + ModelMoveController, **active, the brain to develop on** — user's decision 2026-10-02), `FusedVolume` (FusedVolumeLoader + ModelMoveController, **inactive but kept**, pos (0, 0.5, −0.4), scale 1.4, L4 + L4 labels), plus the original `BrainRoot`/`BrainApp`/`HUD`/`SonySRD`/`Floor`/`T1Volume`.

Which brain it drives: the `volume` field — in the scene set to `Brain`'s BrickVolumeLoader — else the active `BrickVolumeLoader`, else an active `FusedVolumeLoader` (logged as `[Loss] Timeline drives '<name>' (<type>)`).

**Start pose** (`useStartPose`, on; 2026-10-02): when the timeline starts, the brain is put once at `startPosition` (−0.0225, 0.5353, **−0.57**), `startRotation` quaternion (0.52188, −0.04395, −0.84894, 0.07078) and `startBrainScale` 1.4. The transform came from the user (originally z −0.7203; moved 0.15 back so the block stays in frame). `Brain`'s Transform in the scene holds the same values. With `useStartPose` off, the old behaviour (start at `initialEuler`, Y-only turn) returns.

Everything is a **pure function of timeline time** (seeking lands exactly):
| Step | Default length | What |
|---|---|---|
| Brain | `showBrain` 2.5 s | brain at the start pose (or Euler `initialEuler` (0, 270, 180) if `useStartPose` off) |
| Rotate | `rotate` (scene: 4 s) | **Slerp** (shortest way, ≈ 64°) from `startRotation` to the left sagittal pose Euler (`initialEuler.x`, `endY` 360, `initialEuler.z`) = (0, 360, 180), plus `extraTurns`×360 about world Y. Without the start pose: Euler Y only 270 -> 360 |
| Slice | `slice` 4 s | cut from the side facing the viewer to `clippingDepth` 0.3 |
| Neuronal loss | `grow` 4 s + label + `hold` 3 s | block appears tiny (`startScale` 0.02) at `pointOnCut` on the cut face and travels in a **straight line** (`straightPath`, on; eased in/out) to its place past the brain's front (`sideDistance` 0.2) and out of the cut face (`comeOut` 0.3), slight size overshoot; then the label card fades in. `straightPath` off = the old curve (`arcTowardViewer` 0.3 out of the cut face, then left) |

- **The brain's transform position and scale are written only once** (the start pose) — after that only its rotation (and only while the timeline time moves) and its slice.
- `pointOnCut` (0.82, 0.28) = upper-front cortex marked by the user on a screenshot (located by calibrating against the ROI dot; just inside the tissue edge of section 145). Same coordinates for both loaders.
- The block is **SRD_test's neuronal-loss block**: `StreamingAssets/NeuronalLoss/NeuronalLoss_48x48x117_RG.bytes` (copied from `D:\Unity\SRD_test\Assets\Volume`, RG16: R = `neuronal_loss_roi_smooth` grey tissue, G = `neuronal_loss_inverse_roi` pink/purple signal), drawn by `Brain/NeuronalLossVolume` (Built-in port of SRD_test's URP shader, same numbers), true voxel proportions, yaw 45°, tilt 12°, `blockLength` **0.2** (was 0.3; reduced 2026-10-02 at the user's request; the label card scales with it), **not rotating** (`spinDegreesPerSecond` 0), white bold-italic "Neuronal loss / Stroke tissue · 3D histology stack" card. SRD_test's arrow is available (`showArrow`) but off.
- Source .npy (not used at runtime): `D:\Unity\new\npy_neuronal_loss\neuronal_loss_{roi_smooth,inverse_roi}_133-181_81-129.npy`, float32 (117, 48, 48). The ROI is x 133-181, y 81-129 of hb02 **L6** (354×194), all sections (checked: 77 % tissue vs 9-14 % for the swapped box). Axis 0 (117) spans all 485 sections (both maps ≈ 0 at slice 56 ≈ the midline gap) — *inferred*.
- **Final block pose you set yourself**: at runtime the block's final pose is the GameObject **`NeuronalLossBlock`** (top level in the Hierarchy, Play mode only). Once the block is in place the mouse/keyboard move **the block** (`controlsMoveBlock`; **M** switches to the brain and back; WASD/QE move, arrows/right-drag rotate, +/-/scroll scale); **K** copies `finalPosition / finalEuler / finalScale` to the clipboard and Console. Put those in the fields and tick `useCustomFinal` to make them permanent. *The user was about to choose these values; none are set yet.*
- **Seek bar** `UI/TimelineTransportUI.cs` (port of SRD_test's `StoryTransportUI`, added automatically): play/pause, scrubbable bar with a tick per step, time and step name, on the panel near the bottom edge. Keys: **Space** play/pause, **R** restart, **, .** ±1 s (hold to scrub), **[ ]** ±5 s, **Home/End**, **H/T** hide/show. While the bar is dragged `TimelineTransportUI.PointerCaptured` makes `ModelMoveController` ignore the mouse (so a drag never moves the brain).

### `TimelineRecorder` — the video (`Rendering/TimelineRecorder.cs`, 2026-10-02)
On the `NeuronalLossSequence` GameObject. Records the whole timeline to `BrainVolumeSRD/Recordings/neuronal_loss_<yyyyMMdd_HHmmss>.mp4` (gitignored) with `UnityEditor.Media.MediaEncoder` — **Editor only**.
- `recordOnPlay` (on): records once automatically as soon as the brain is loaded and the timeline is ready; **F10** records again from the start.
- View: the SRD's `WatcherCamera` (head pose between the eyes, off-axis projection onto the panel), copied every frame into a hidden `RecordCamera` that renders into a RenderTexture; `width` 1920, height from the panel's aspect, `fps` 30, `tail` 1.5 s after the end (≈ 20 s total). Without an SRD manager: fallback camera looking along world +Z at the timeline's `volume`.
- `Time.captureFramerate` = fps while recording, so the timeline advances exactly 1/fps per frame regardless of render speed. The seek bar is hidden during recording and restored after.
- Console: `[Record] <s> s, <w>x<h> @ 30 fps, SRD watcher view -> <path>` at the start, `[Record] Saved N frames to <path>` at the end.
- *Not yet run.* Unknowns: whether the watcher camera's pose/projection are updated with "Run Without Spatial Reality Display" on; whether the frames come out flipped or with wrong gamma (the ReadPixels/MediaEncoder path is copied from `BrainShowcase`, which was never run either).

### Other scripts added 2026-10-01
- `Rendering/BrainShowcase.cs`: 30 s scripted orbit/slice of the fused volume with MP4 recording (`UnityEditor.Media.MediaEncoder`, Editor only, to `BrainVolumeSRD/Recordings/`). **P** preview, **F10** record. Not in any scene (add it to a GameObject to use). Written for the earlier "video" request; never recorded. Superseded by `TimelineRecorder` for the NeuronalLossScene video (both use F10 — don't put both in one scene).
- `UI/UiKit.cs`: tiny world-space uGUI builders.
- `ModelMoveController.cs`: one-line guard for the seek bar (above).

### Streaming path (unchanged, `BrainApp` on `BrainRoot`)
`BrainApp` gained `pinCoarsest` (default on: pin to the coarsest level), `wholeBrain`, `lodMin/lodMax` and an RGB path for hb02. Its default `serverUrl` is `http://dgx3.humanbrain.in:8010`; the scenes have `http://172.20.23.156:8010/` (same machine). `BrainRoot` is **inactive** in both scenes. The coordinate chain, cache and Sony adapter are as described in the 2026-10-01 version of this file (see git history of `handover.md`).

### Local T1 MRI path
`T1VolumeLoader` + `T1Raymarch` on `T1Volume` (inactive). `t1_mri.raw` is gitignored and was not on this PC's StreamingAssets listing on 2026-10-02.

### Other changes in `NeuronalLossScene` (user's work, committed in `589ac1d`)
- `AstrocyteDensity` (`NpzDensityVolume`, §6a) sits next to `Brain` as a split view: Brain at (−0.36, 0.49, −0.57), AstrocyteDensity at (0.36, 0.49, −0.57), both scale 1.35. AstrocyteDensity `follow`s the Brain's rotation and cut. `NeuronalLossSequence.startPosition` is (−0.36, 0.49, −0.57), scale 1.35.
- `ModelMoveController` gained an **R** reset: holding R puts the object back to its transform at Start. In scenes with the seek bar R also restarts the timeline, and both happen at once.
- `NeuronalLossScene_DualScreen`: copy of `NeuronalLossScene` with the old `Main Camera` renamed `Display2Camera` (untagged, so it is not `Camera.main`; no orbit control, no AudioListener; `targetDisplay` 1) and a `DualScreenView` component. In that scene `activateSecondDisplay` is still on; see §8.14 before building it.

---

## 6a. Presentation scenes V1-V3 (2026-10-02/03)

Three scenes, each a **30 s timeline that loops forever** (`NeuronalLossSequence.loop`), built to one exe each (§7). All share the same rig: the bricked `Brain` (L2 on this PC), the SRD, the seek bar, and `TimelineRecorder` with `recordOnPlay` **off** (F10 still records; `tail` 0, so a recording is exactly 30 s). `Display2Camera` is **inactive** and `DualScreenView.activateSecondDisplay` is **off** in all three (§8.14). Start rotation is always the user's quaternion (0.52188, −0.04395, −0.84894, 0.07078), scale 1.35.

Lineage: V1 was copied from `NeuronalLossScene_DualScreen`, V2 from V1, V3 from V2. V2 and V3 are (re)generated by menu commands in `Assets/Editor/SceneBuilds.cs`: **Brain ▸ Scenes ▸ Create NeuronalLoss Display V2 / Create NeuronalLoss Fib Astrocytes V3**. V1's BFI/ending setup is **Brain ▸ Scenes ▸ V1: add BFI (three-way split)**. They are safe to re-run and the layout constants are at the top of that file. *Caveat*: re-creating V2 copies the current V1, and re-creating V3 copies the current V2.

### V1 — `Nissl and Labels V1` (exe `Builds/Nissl and Labels V1/`)
Start position (0.082, 0.49, −0.57) (user's transform).
| Time | Step |
|---|---|
| 0-2.5 | Nissl brain (bricked `Brain`) at the start pose |
| 2.5-6.5 | Rotate to the left sagittal view |
| 6.5-9.5 | **Split**: `BFI` (left, (−0.47, 0.49, −0.35)), Nissl (middle, (0, 0.49, −0.35)), labels (`FusedVolume`, L4 + `colorRegions`, right, (0.47, 0.49, −0.35)). BFI and labels fade in from the Nissl brain's spot, all shrink to `splitScale` 0.65 |
| 9.5-15 | Slice to 95 % (`clippingDepth` 0.95, `slice` 5.5 s), all three |
| 15-15.3 | pause (`sliceBackDelay` 0.3) |
| 15.3-20.8 | **Slice back** (`sliceBackSeconds` 5.5) |
| 20.8-25.8 | **Final turn** 360° about world Y (`finalTurnSeconds` 5) |
| 25.8-27.8 | **Combine**: BFI and labels slide back into the Nissl brain and fade out; Nissl back to its start spot and size |
| 27.8-30 | **Return**: the brain turns back to the start rotation, so 0:30 = the start pose exactly (checked numerically) |
- At 95 % only the outermost 5 % of each volume is left, which is a small cap of cortex. That is expected.
- BFI and labels copy the Nissl brain's rotation, scale and cut every frame. The labels are `FusedVolumeLoader` with the same unit cube. BFI is an `NpzDensityVolume` with `follow` = Brain and the npz `relativeRotation`. All three line up (screenshots at 0:09 and 0:13).

### V2 — `NeuronalLoss Display V2` (exe `Builds/NeuronalLoss Display V2/`)
Start (0.082, 0.49, −0.57). Brain 3 s → rotate 6 s → slice 6 s to 30 % → the neuronal-loss block (the two `npy_neuronal_loss` stacks, already packed in `StreamingAssets/NeuronalLoss/*_RG.bytes`) grows out of the cut face 6 s → label 0.8 s → hold 3.2 s → **Combine** 2.5 s (25-27.5: label fades, the block flies back into the cut face) → **Return** 2.5 s (27.5-30: the brain turns back to the start pose and the cut closes) → loop. Added 2026-10-03 at the user's request ("like in other versions"); the settings were read back, but this ending was not watched in Play mode, because UnityMCP dropped. It uses the same code as V3, which was checked. `blockLength` 0.2, `comeOut` 0.3, no split. FusedVolume, AstrocyteDensity and BFI are off. Menu **Brain ▸ Scenes ▸ V2: return to start at the end** applies just this ending.

### V3 — `NeuronalLoss Fib Astrocytes V3` (exe `Builds/NeuronalLoss Fib Astrocytes V3/`)
Layout from the user's sketch (`D:\Unity\new\scrnshot\Screenshot 2026-10-02 172157.png`): brain in the centre, neuronal loss upper-left, Fib upper-right, astrocytes lower-right.
| Time | Step |
|---|---|
| 0-3 | brain, **centred** (start (0, 0.49, −0.57)) |
| 3-9 | rotate to sagittal |
| 9-15 | slice to 30 % |
| 15-21.8 | **together**: the brain moves back (+0.35 z) and shrinks to 70 % (`recede` 2 s, `recedeDuringGrow`) while the neuronal-loss block (`blockLength` 0.27, `comeOut` 0.1), **Fib** (`fib_probability_HB02_0.24mm_0.9999.npz`, orange, at (0.53, 0.70, −0.30)) and **Astrocytes** (`astrocyte_density_HB02_0.24mm.npz`, at (0.53, 0.32, −0.30)), both maps at scale 0.85, all come out of the same cut-face point (tiny → full size, straight line, slight overshoot). Then each gets a white label card |
| 21.8-25 | hold |
| 25-27.5 | **Combine**: cards fade, all three fly back into the cut face |
| 27.5-30 | **Return**: the brain turns back, comes forward, and the cut closes. 0:30 = the start pose exactly |
- The maps follow the brain's rotation but **not** its cut (whole maps).
- Map cards have only a title ("Fib", "Astrocytes"). The user has not supplied subtitles or said what "Fib" stands for.

### V4 — `Axon Damage Repair V4` (exe `Builds/Axon Damage Repair V4/`, 2026-10-03)
Recreates the reference video `npz_files/V4/axon_damage_repair_APP_GAP43_540p15.mp4` (26 s, 960×540, 15 fps): Healthy axons → Axonal damage (APP) → Axonal repair (GAP43) → Damage and repair. Created by **Brain ▸ Scenes ▸ Create Axon Damage Repair V4** (copies V3, switches off its maps, no slice, no block).
- Data `npz_files/V4/` (copied to `StreamingAssets/Npz/` for builds), all (501, 783, 712) (k, j, i), 0.24 mm, no affine: `healthy` uint8 0/1 (522,599 fibre voxels of the stroke hemisphere); `app` float32 on exactly the same voxels, levels 0.25-0.75 (183k), 1.0 (268k), 1.25-2.0 (72k); `gap43` float32 ~0.95-1 (35,554 voxels, only 368 inside the healthy fibres = new growth). All APP levels are spread evenly over the hemisphere, so the data has no time channel. The "spreading from the stroke" is a radial reveal around the GAP43 centroid (i 236, j 381, k 312).
- **Colour mapping (my reading of the video legend, not confirmed by the data's author)**: APP < 0.95 stays healthy blue; ≥ 0.95 = APP+ light (orange), shading to APP+ dense (red) from 1.2 to 2.
- `AxonDamageRepairVolume` (+ shader `Brain/AxonDamageRepair`, in Always Included Shaders) packs the three maps into one RGBA32 3-D texture (R healthy, G APP×presence, B GAP43; `downsample` 2 = block max, 356×392×251, ~140 MB) and ray-marches them together. It follows the Brain's pose (same `relativeRotation` as `NpzDensityVolume`) and draws on its `Drawn` event. Load ~10-15 s in the Editor; the timeline waits for it (`NeuronalLossSequence.waitFor`).
- `AxonRepairTimeline` runs the stages in the timeline's hold and adds their marks to the transport bar (`holdMarks`). It fades the brain to a shell (`shellOpacity` 0.2), sets the APP / GAP43 radii (65 / 60 mm), and shows a white card above the brain with the video's title, subtitle and colour legend.

| Time | Step |
|---|---|
| 0-3 | brain, centred (V3's start) |
| 3-8 | rotate to sagittal |
| 8-12 | **Healthy axons**: the brain fades to a shell and the blue fibres fade in |
| 12-17.5 | **APP damage**: orange/red spreads from the stroke centre |
| 17.5-22.5 | **GAP43 repair**: green grows from the core |
| 22.5-25.5 | **Damage + repair** |
| 25.5-27.5 | Combine: everything fades and the brain is solid again |
| 27.5-30 | Return to the start pose, then loop |

Checked in Editor Play mode with screenshots at each stage, including the end pose. Not yet seen on the SR display.

### Visible area (why the layouts are where they are)
The SRD panel is x ±0.895, y 0..1.007 at z = 0 in world space (SRDisplayManager at the origin, `SRDViewSpaceScale` 3). The nominal viewer (WatcherCamera) is at about (0, 0.64, −1.62). Content in front of the panel (negative z) is seen through a narrower window: about **x ±0.58 at z −0.57**, ±0.70 at −0.35, ±0.73 at −0.30, ±0.52 at −0.69. That is why three brains needed to shrink and move back in V1, and why V3's maps sit at z −0.30. For layout checks, screenshot from the fixed viewer position `view_position [0, 0.64, -1.62]`, `view_target [0, 0.5, 0]` (`manage_camera`). The live WatcherCamera follows the head tracker and changes framing between shots.

### Timeline options added to `NeuronalLossSequence` (all off by default; `NeuronalLossScene` behaves as before)
| Option | What |
|---|---|
| `loop` | at the end, seek to 0 and keep playing |
| `splitVolume`, `splitLeftPosition` (where the **driven** brain goes), `splitRightPosition`, `split` s | "Split" step after Rotate: a second `ISliceableVolume` (labels) fades in and moves apart. It copies the brain's rotation, scale and cut every frame; its `ModelMoveController` is disabled |
| `splitThird` (an `NpzDensityVolume`), `splitThirdPosition`, `splitScale` | third split brain (BFI) and the brains' size after the split |
| `sliceBack`, `sliceBackDelay`, `sliceBackSeconds` | undo the cut |
| `finalTurnSeconds`, `finalTurnDegrees` | turn about world Y at the end |
| `recede`, `recedeOffset`, `recedeScale`, `recedeDuringGrow` | "Brain back" (V3) |
| `returnToStart`, `combineSeconds`, `returnSeconds` | Combine + Return ending: block, maps and split brains go back into the brain, which then turns back to `startRotation`, start position/scale and no cut. With this on, the hand controls (`ModelMoveController`) are never enabled |
| `slicing` (default on) | off = no Slice step / mark, the brain stays whole (V4) |
Public helpers for other components: `CutAnchorWorld` (the cut-face point the block comes out of), `GrowSeconds`, `GrowStartScale`, `GrowOvershoot`, `CombineAmount`, `HoldStart`, `CombineStart`; `holdMarks` (extra transport marks inside the hold) and `waitFor` (extra "loaded?" checks before the start), both filled in `Awake` (V4's `AxonRepairTimeline`).

### `NpzDensityVolume` — `.npz` and `.nii` maps (`Rendering/NpzDensityVolume.cs` + `Brain/DensityVolume`)
- Reads a float32 3-D array from a NumPy **`.npz`** (array `arrayName`, voxel size from the 4×4 `affineName` if present, else `voxelMmIfNoAffine` 0.24) or a single-file **NIfTI-1 `.nii`** (float32 only; voxel size from `pixdim`, `scl_slope/inter` applied). It runs on a background thread and maps values to 8 bits (0 = transparent, then linear to the `percentileHigh` percentile of the non-zero values). Optional gap filling between sampled sections (`fillGapMm`, `inPlaneFillMm`). Drawn composite or max-intensity.
- Axes: index i → Right, j → Anterior, k → Superior for both the HB02 `.npz` maps and the BFI `.nii` (RAS identity sform). Unity local x = i, y = k, z = j. Against the hb02 brain this is `relativeRotation` (0.7071, 0, 0.7071, 0).
- `follow` (a brain loader): copy its rotation (`followRotation`) and, optionally, its cut (`followSlice`). `visibility` 0..1 scales opacity (used for fading in).
- File lookup: absolute path, else `StreamingAssets/<path>`, `StreamingAssets/Npz/<file>`, `StreamingAssets/Nifti/<file>`, else `<unity folder>/<path>`. **Builds can only reach StreamingAssets**, so the maps are copied there: `Npz/astrocyte_density_HB02_0.24mm.npz` (22 MB), `Npz/fib_probability_HB02_0.24mm_0.9999.npz` (2 MB), `Nifti/BFI_in_MRI_2.nii` (200 MB). All gitignored; originals in `brain-streaming/unity/npz_files/` and `brain-streaming/unity/bfi/`.
- The data (measured): astrocyte density 712×783×501 at 0.24 mm, 2.3 % non-zero. Fib probability: the same grid, **no affine** in the file, 0.1 % non-zero, values 0..1. BFI: 411×472×259 at 0.4 mm (164 × 189 × 104 mm), values 0..1, 63 % zero background, tissue mostly 0.5-1. BFI is rendered with `threshold` 0.35, `density` 25, `valueGamma` 1.5 and a warm beige ramp.
- Load time in the Editor: ~25-45 s for astrocytes (1.1 GB float stream, two passes). V3's maps are needed at 0:15, so on the very first loop after launch they may appear late. The timeline waits for the BFI before starting (`splitThird` must be loaded before `Prepare`).

### `TimelineDensityOverlay` (`Rendering/TimelineDensityOverlay.cs`)
On a map GameObject (V3's `FibProbability`, `AstrocyteDensity`). Hidden until the timeline mark `appearAtMark` ("Neuronal loss") + `delay`. With `comeOutOfBrain` it then makes the neuronal-loss block's motion from `CutAnchorWorld` to the GameObject's scene position and size (`GrowSeconds`, start size, overshoot). Then a white card (`title`, optional `tagLine`, `cardScale` 0.0006) fades in above it. It follows `CombineAmount` back into the brain. It runs after `NpzDensityVolume` (execution order 110).

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
2. `Assets/Scenes/NeuronalLossScene.unity` -> Play: the timeline runs (Space/R/seek bar) and is recorded to `Recordings/neuronal_loss_*.mp4` (untick `recordOnPlay` on `TimelineRecorder` to just watch; F10 records again). `SampleScene.unity` = brain only (C slices).
3. Without an SRD attached, the plugin's "Run Without Spatial Reality Display" setting must be on (Project Settings ▸ Spatial Reality Display, stored in `SRDProjectSettings.asset`).
4. Presentation scenes (§6a): open `Assets/Scenes/<V1|V2|V3>.unity` and Play. They loop; Space pauses, the seek bar seeks.

### Building the exes (one per presentation scene)
- Menu **Brain ▸ Build ▸ Nissl and Labels V1 / NeuronalLoss Display V2 / NeuronalLoss Fib Astrocytes V3** (`Assets/Editor/SceneBuilds.cs`). Output: `BrainVolumeSRD/Builds/<scene>/<scene>.exe` with `<scene>_Data/` next to it. Windows x64.
- Without the Editor window (batch mode; the project must not be open in another Editor):
  ```
  "C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe" -batchmode -quit -projectPath <BrainVolumeSRD> ^
      -executeMethod BrainVolume.EditorTools.SceneBuilds.BuildNisslAndLabelsV1 -logFile build.log
  ```
  (`BuildNeuronalLossDisplayV2`, `BuildNeuronalLossFibAstrocytesV3` for the others).
- **Size**: ~46 GB each, because the whole `StreamingAssets` is copied (43 GB bricks incl. L1, which only loads on a GPU with ≥ ~47 GB). The first build of a scene copies for ~3 min; rebuilds take seconds. Excluding `Bricks/hb02_fused/L1` would cut a build to ~14 GB without changing what loads on a 24 GB GPU (not done).
- A build log shows a few "Destroy may not be called from edit mode" errors (`OnDestroy` cleanup running at build time). They are harmless; the build succeeds.
- **Running an exe**: close the Unity Editor first, otherwise the SRD runtime refuses with "Another Spatial Reality Display application is already running" (§8.15). Copy the whole `Builds/<scene>/` folder; the exe needs `<scene>_Data`. The player log is `%USERPROFILE%\AppData\LocalLow\DefaultCompany\BrainVolumeSRD\Player.log`. Look for `[Bricks] ... loaded 24/24`, `[Fused] Labels loaded`, `[Density] ...`, `[Loss] Timeline drives 'Brain'`.
- A fresh clone needs the gitignored data copied in before building: `StreamingAssets/Bricks/hb02_fused`, `Fused/hb02_L4*.raw` + `hb02_L5.raw`, `Npz/*.npz`, `Nifti/BFI_in_MRI_2.nii`.

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
13. **`Shader.Find` shaders are stripped from player builds** unless something references them. The first exe loaded all data but drew nothing; its Player.log showed `[Bricks] Shader 'Brain/SRDBrickRaymarch' not found` etc. Fixed by Always Included Shaders (§6). The Sony plugin's own `Shader.Find` shaders are in its `Resources/` folder and do ship.
14. **Don't activate a second display in an SRD exe the plain-Unity way.** On this PC Unity's display order is [0] the 1920×1080 monitor, [1] the SR display. The Sony plugin moves Unity's main window onto the SR display itself (`SRDApplicationWindow` activates every display and hides the extra windows). So `Display.displays[1].Activate()` (`DualScreenView.activateSecondDisplay`) opened a full-screen window **on the SR display**, covering the 3-D image. Only the IMGUI HUD showed, and the screen flickered. That is why `activateSecondDisplay` and `Display2Camera` are off in V1-V3. A real second-monitor view should go through the Sony plugin's 2-D view support (`SRD2DView`); not done.
15. **Only one SRD application at a time, and the Unity Editor counts** once it has used the display (e.g. after Play mode). An exe started while the Editor holds it pops up "Another Spatial Reality Display application is already running" and quits. It may also be why earlier exe runs stayed black (*not confirmed*).
16. **Editor crashes on D3D12**: three Editor crashes on 2026-10-02 (`Temp/Unity/Editor/Crashes`), all in the NVIDIA driver during a D3D12 draw (`nvwgf2umx` → `D3D12DeviceState::Transition`), during Play mode with several large volumes. If they continue, or if the exe stays black on the SR display, try Player Settings ▸ Graphics APIs = **Direct3D 11** (the Sony plugin supports it). Not tried yet.
17. **UnityMCP drops** after domain reloads, Play mode starts and long loads (`no_unity_session`). The Editor is usually fine; retry, or reconnect from the MCP for Unity window. After editing scripts, `refresh_unity` + checking that the new field/method exists (via reflection) is the reliable way to know the new code is loaded. `EditorUtility.RequestScriptReload()` forces it.
18. **Scene copies through UnityMCP**: `EditorSceneManager.SaveScene(scene, path, saveAsCopy: true)` copies the in-memory state including unsaved edits. Never `OpenScene(..., Single)` from code while the open scene is dirty (it discards the edits). Open the copy additively, edit, save, close.

---

## 9. Working-tree state (2026-10-03, branch `brick_L0`)

**Pushed** to `origin/brick_L0` (2026-10-02):
- `25eb342`: bricked-Brain timeline, start pose, straight block path, `TimelineRecorder`.
- `2d55ea2`: `NeuronalLossScene_DualScreen` + `DualScreenView` (with an R reset that was then removed).
- `a10a04a` "Reset button": removes that R reset again; `DualScreenView` now only routes the second camera.
- `589ac1d`: V1-V3 scenes, the timeline options, `TimelineDensityOverlay`, `NpzDensityVolume` (visibility, StreamingAssets lookup, no-affine voxel size), `SceneBuilds.cs`, Always Included Shaders, and the user's pending work (AstrocyteDensity split view, brick tissue mask + VRAM fallback, `ModelMoveController` R reset, earlier handover edits). `.gitignore` now excludes `Builds/`, `Build_V1/`, `npz_files/` and `StreamingAssets/Npz/*.npz`.

**Uncommitted** (everything after `589ac1d`, all compiled and run in the Editor):
- V1: 0.3 s pause before slice back, slice and slice back 5.5 s each, BFI three-way split, Combine/Return ending.
- `NpzDensityVolume`: NIfTI reader, `StreamingAssets/Nifti` lookup.
- `NeuronalLossSequence`: `splitThird`, `splitScale`, split brains merging on Combine.
- `SceneBuilds.cs`: `AddBfiToV1`.
- `.gitignore`: `bfi/` and `StreamingAssets/Nifti/*.nii`.
- `StreamingAssets/Nifti.meta`.
- This file.

**Builds on disk** (gitignored, all succeeded): `Builds/Nissl and Labels V1` (latest V1 with BFI + ending), `Builds/NeuronalLoss Display V2`, `Builds/NeuronalLoss Fib Astrocytes V3` (latest V3), ~46 GB each. `Build_V1/` is an older, user-made build folder.

**Compile / run status**: all scripts compile without errors. Every V1-V3 step was played in the Editor and checked with screenshots. Start/end poses were checked numerically (end pose = start pose for V1 and V3). The 2026-10-02 unknowns about `NeuronalLossScene` (straight path, start pose, recorder) have been superseded: the timeline has run in the Editor many times since. The MP4 recorder (F10) has still not been tried by the user in these scenes.

**Stash**: `stash@{0}` "brick_L0 Unity-generated slnx + packages-lock before merging neuro_version" — regenerated files, normally safe to drop.
**Backup**: 16 `.meta` files Unity had regenerated on brick_L0 before the merge are in the Claude scratchpad (`brick_L0_untracked_backup`); the tracked versions from neuro_version replaced them. Not needed unless a GUID problem shows up.

---

## 10. Open issues and suggested next steps

1. **See an exe on the real SR display.** Close the Unity Editor, run `Builds/Nissl and Labels V1/Nissl and Labels V1.exe` and check that the brain appears. If it stays black: read `Player.log` (§7), then switch the graphics API to Direct3D 11 (§8.16) and rebuild all three. Then confirm V2 and V3 the same way.
2. **Second monitor** (user asked for "two screens" on 2026-10-02): turned off in V1-V3 because it covered the SRD (§8.14). Redo it with the Sony plugin's 2-D view, then re-enable `Display2Camera` or replace it.
3. **Commit the uncommitted BFI/V1 work** (§9) when the user asks.
4. Open user choices: subtitles for the V3 map cards and what "Fib" stands for; whether V1/V2 should also start centred (x 0, like V3); exact timings (V1's final turn was cut to 5 s to fit the ending in 30 s).
5. Load time of the maps (25-45 s) vs. their first use at 0:15 in V3. If it shows on the presentation PC, cache the quantised 8-bit texture to disk (e.g. a `.raw` next to the `.npz`) instead of re-reading the float array every launch.
6. Smaller builds: exclude `StreamingAssets/Bricks/hb02_fused/L1` (32 GB, never loaded on a 24 GB GPU).
7. **Play `NeuronalLossScene` once** and check the MP4 in `Recordings/` (framing, orientation, colours). If the block leaves the frame: lower `sideDistance`/`comeOut`, or move `startPosition.z` further back (e.g. −0.45).
8. **Set the neuronal-loss block's final transform**: in Play press End (block in place), move `NeuronalLossBlock` or edit it in the Inspector, press K, put the values in `finalPosition/finalEuler/finalScale` with `useCustomFinal` on.
9. **Seam planes on the bricked brain** (now mostly handled by the tissue mask, §6): if visible, add black-to-white + the seam filter (port `FusedVolumeLoader.RemoveSeams`) to `tools/prebrick_srd.py` and re-brick.
10. **Block placement on the bricked Brain**: `pointOnCut` / `sideDistance` / `comeOut` were tuned on FusedVolume's screenshots. In the Editor screenshots of V2/V3 the block comes out of the bricked brain's cut face as intended, but nobody has checked that `pointOnCut` is on the cortex.
11. **Which side is the left hemisphere** is still *inferred* (section 0 faces the viewer at Y = 360). If it is the right one, set `endY` = 180.
12. **Video**: done in code (`TimelineRecorder`, §6) but no video has been produced yet. If the watcher view is wrong without the display, switch to the fallback camera (or add a fixed camera pose to `TimelineRecorder`).
13. **Push `neuro_version`** if anyone still works from that branch.
14. From the first version, still open: stale 0.5 µm voxel text (§2), `/dashboard` serves the Gen-1 viewer, pick one port/address convention, document the T1 data path, WebSocket transport / multi-GPU / occupancy scan.

---

## 11. What is verified and what is not

| Claim | Basis |
|---|---|
| hb02 voxel statistics (white background, saturation separates tissue, black 512² chunks, seam widths, interhemispheric gap at z ≈ 230) | Measured 2026-10-01 from `hb02_L4.raw` / `hb02_L7.raw` with small C# scans |
| Seam filter removes the planes without eating the brain | Offline test on L4 sections 60/260/330/420 + the user's "planes are gone" |
| `hb02_L7_labels.raw` correct and aligned | Built from the live server's `/api/label_chunk`; header/size checks; 92 % vs 73 % alignment test |
| ROI of the .npy files = L6 x 133-181, y 81-129 | Tissue-overlap test (77 % vs 9-14 %); the in-plane transpose could not be decided from data |
| Timeline, block, seek bar, centre/side placements, Y-only rotation | Compiled without errors in the Editor; behaviour from the user's Play-mode screenshots/feedback |
| Brick shader look + timeline on `Brain`, straight block path, start pose | Played many times in the Editor during the V1-V3 work (2026-10-02/03), screenshots |
| Block coming out of the cut face | User saw the curved version in the Editor ("coming out is nice") and asked for a straight track; straight version seen in V2/V3 screenshots |
| V1-V3 timelines: phase times, split layout, slice/slice back, recede + coming out together, Combine/Return, loop wrap | Editor Play mode via UnityMCP: marks and `TotalSeconds` read back (30 s each), screenshots at chosen times from the fixed viewer (0, 0.64, −1.62), end pose = start pose read back numerically (V1, V3), loop wrap seen (V2: 29.5 s → 3.7 s) |
| BFI / labels / Nissl line up in V1 | Screenshots at 0:09 and 0:13 (same orientation and size, all cut). Exact anatomical registration between BFI and hb02 **not** checked |
| `.nii` reader | Header parsed by hand (`od`) and by the loader (411×472×259, 0.4 mm), value statistics scanned (0..1, 63 % zero) |
| Map files reach the builds | `StreamingAssets/Npz` and `Nifti` present in the `_Data` folders after building |
| Shaders in builds | First exe's Player.log: shaders "not found". After Always Included Shaders: shader names found in the built data files; data/timeline load per Player.log |
| Second-display window covering the SRD | Desktop capture of the SR display while the exe ran (grey screen + HUD only); code reading of `SRDApplicationWindow` |
| `TimelineRecorder` MP4 | **Not run** |
| SRD_test reference look | From `D:\Unity\SRD_test\Captures\neuronal_block_final.png` / `neuronal_loss_pose.png` (the .mp4 was not viewed) |
| Server state, Gen-1, browser clients, streamed path | As in the first version: read from code/docs, not re-run (except `/api/health` and `/api/label_chunk` on 2026-10-01) |
| **Exes on the real SR display** | **Not confirmed.** The first runs were blank (two causes fixed); later desktop captures were black with only the HUD, possibly because the Editor held the display. The clean test (Editor closed) is outstanding |

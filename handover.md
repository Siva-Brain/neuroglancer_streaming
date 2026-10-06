# neuroglancer_streaming — Project Handover

**Repo**: `D:\Unity\new\neuroglancer_streaming` (git, remote `https://github.com/Siva-Brain/neuroglancer_streaming.git`). **Current branch `brick_L0`**, **pushed to `origin/brick_L0` at `4802b78`** (2026-10-05 evening). That push holds the all-in-one `Presentation All` / `Presentation All Flat` scenes, the menu bar, the Ultraleap hand control (`121130c`), the glass-slide slicer (`7ba097b`, `3296754`, `4802b78`), the V2 card / block poses plus a not-yet-working card overlay (`129237b`) and the first magnifying lens (`c07f99f`). **Uncommitted** after that (2026-10-05 night, §6a "Lens mode, IIP slide and V5"): the lens reworked into a crosshair + box with coordinates, the IIP slide fitted into the brain, a vessel overlay (off), and **V5** in both Presentation All scenes (§9). `brick_L0` contains everything: lordsiva's offline-brick work *and* the `neuro_version` work merged in (§6). `origin/neuro_version` is behind (`dfe60ff`; the local branch has `715500b`).
**What it is**: a streaming pipeline that takes a whole human brain's histology, stored as very large Zarr volumes, and shows it as a 3D volume in a browser and in Unity on a Sony Spatial Reality Display (SRD, model ELF-SR2). Originally (§0-§5) a Python server on the DGX A100 streams voxel bricks from Zarr. Since 2026-10-01 the Unity side also has **local, server-free** paths for the hb02 brain (one exported level, or offline bricks) and a scripted **neuronal-loss** presentation scene (§6).
**Unity project**: `brain-streaming/unity/BrainVolumeSRD` — Unity **6000.3.25f1** (upgraded from 6000.3.10f1), **Built-in Render Pipeline** (not URP), Sony `SRDisplayUnityPlugin` **2.6.0**. Scenes: `SampleScene` (brain only), `NeuronalLossScene` (the development timeline), `NeuronalLossScene_DualScreen` (copy + a second-monitor camera), and the four **presentation scenes** `Nissl and Labels V1`, `NeuronalLoss Display V2`, `NeuronalLoss Fib Astrocytes V3`, `Axon Damage Repair V4` (§6a), each built to its own Windows exe. **`Presentation All`** (§6a) combines V1-V4 in one scene with a menu bar at the bottom of the display and **Ultraleap hand control** (grab to turn the brain about Y, two-hand pinch to scale, fingertip touch for the menu; manual translation is off since 2026-10-05). It is also built to an exe.
**Status in one line**: four 30-second, **looping** presentation timelines (V1: BFI / Nissl / labels three-way split + slice; V2: slice + neuronal-loss block; V3: slice + neuronal loss, Fib and astrocyte maps coming out of the brain; V4: healthy axons → APP damage → GAP43 repair).
- They run in the Editor, separately and together in `Presentation All`.
- They are built to `BrainVolumeSRD/Builds/<scene>/<scene>.exe` (~46 GB each).
- Every step was checked in Editor Play mode with screenshots, and the hand gestures with simulated Ultraleap hands.
- **Not yet confirmed: the exes on the real SR display, and the hand control with real hands** (§10).
- The first exe showed nothing; two causes were fixed (shaders stripped from the build, a second-display window covering the SRD).

**How this document was produced**: first version 2026-10-01 by reading the repo only (nothing run). **Updated 2026-10-02** after a day of work in this project (sessions with Claude Code): the hb02 data were measured directly from the exported `.raw` files, the label volume was downloaded from the live DGX server, and every code change was compiled in the open Unity Editor (exceptions noted in §9). **Updated again 2026-10-03** for the presentation scenes V1-V3, the exe builds, the `.npz`/`.nii` map loader and the player-build fixes (§6a, §7, §8, §9-§11). Those were compiled and run in Editor Play mode through UnityMCP and checked with screenshots from a fixed viewer position. **Updated 2026-10-03/04** for the all-in-one `Presentation All` scene (menu bar, standby timelines), the Ultraleap hand control, the `Presentation All` exe and the hand-over copy on the external drive `E:\unity` (§6a, §7-§11). **Updated 2026-10-05** for the manual-control limits (no translation, Y-only rotation, turntable hand grab), V2's bigger block, V3's "Fibrinogen" label and spinning maps/block, and deselect-on-reselect in the menu (§6a "2026-10-05 changes"). UnityMCP was down that day, so those were edited on disk only. The Editor compiled them (12:55, no `error CS` in Editor.log); **not played by Claude**, the user ran Play mode in between. The hand gestures were tested by feeding Ultraleap's test hands into the provider's frame, not with real hands. **Later on 2026-10-05** (UnityMCP still down): the glass-slide slicer, the card overlay and V2's fixed card / block poses (§6a "2026-10-05, later"). These were compile-checked outside Unity (`dotnet build` of scratch copies of the generated `.csproj` files against `Library/ScriptAssemblies`), not by Claude in Play mode; the user ran Play mode (Slice on/off seen in Editor.log, a screenshot of V2). Visual results are from those screenshots and the user's feedback, not from automated checks. **Updated again late on 2026-10-05** (UnityMCP still down) for the lens, the IIP slide, the vessels and V5 (§6a "Lens mode, IIP slide and V5"): compile-checked outside Unity only (scratch `dotnet build` of the game and editor `.csproj` files), **none of it run by Claude**; the user ran Play mode in between and sent screenshots of V5's cut face. The IIP slide's place in the volume was measured offline (tissue-outline fit, below). Things worked out by reading code rather than observing are marked *inferred*. §11 lists what is verified and what is not.

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
- Packages: Input System 1.20.0, uGUI 2.0.0, Timeline, `com.coplaydev.unity-mcp`. Since 2026-10-03 also Ultraleap Tracking 7.3.0 (`com.ultraleap.tracking` + `.preview`, OpenUPM; uncommitted, §9), used by `Assets/Scripts/Interaction/` (§6a).
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

## 6a. Presentation scenes V1-V4 and Presentation All (2026-10-02/04)

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
*2026-10-05: block made 1.5× bigger at the user's request: `blockLength` 0.2 → **0.3** and `sideDistance` 0.2 → **0.25** (the block is centred on its final point, so the gap to the brain stays the same); in V2, Presentation All and Presentation All Flat. The values below are the older ones.*
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
- Map cards have only a title: **"Fibrinogen"** (was "Fib" until 2026-10-05; the user said Fib = Fibrinogen) and "Astrocytes". No subtitles supplied.
- **Spin** (2026-10-05, user: "make the three rotate, not the brain"): the Fibrinogen map, the Astrocytes map and the neuronal-loss block turn about world Y at **20°/s** from the moment each appears. Maps: new `TimelineDensityOverlay.spinDegreesPerSecond`, applied on top of the brain-following rotation (`NpzDensityVolume.follow`), as a function of time since appearing (seek/loop exact). Block: the existing `NeuronalLossSequence.spinDegreesPerSecond` (was 0). The brain and the cards don't spin. Set in V3, Presentation All and Presentation All Flat.

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

### Presentation All — V1-V4 in one scene with a menu bar and hand control (2026-10-03/04)
Status:
- Checked in Editor Play mode: V1 → V3 → V4 → V2 switches with screenshots, no errors.
- Built to an exe (below), but the exe has not been run.
- Hand gestures were tested with simulated hands only; not yet on the SR display or with real hands.
`Assets/Scenes/Presentation All.unity`, made by **Brain ▸ Scenes ▸ Create Presentation All (V1-V4 + menu bar)** (rebuilt from the four scenes each time; they are only read). It is a copy of V4 (the shared rig and the one bricked `Brain`). Each scene's timeline and its own objects are moved in under groups `V1`..`V4`: V1 gets `FusedVolume` + `BFI`, V3 gets `FibProbability` + `AstrocyteDensity`, V4 gets `AxonDamageRepair`. References to the source scene's Brain are re-pointed to this scene's Brain. The `TimelineRecorder`s are removed.
- `UI/PresentationMenu.cs`: a pill-shaped bar on the panel's bottom edge, `( ▷ | V1 V2 V3 V4 )`, after the user's sketch `scrnshot/Screenshot 2026-10-03 160451.png`. It has rounded corners (`UiKit.Rounded`, a 9-sliced rounded-rect sprite made at runtime), a soft shadow, a hairline edge and a round teal-ringed play button. Each version has a caption: Nissl & Labels / Neuronal Loss / Fib & Astrocytes / Axon Repair. A teal highlight slides to the selected version, and a thin line inside it shows the version's progress.
- **At the start only the brain is shown**, still, at V4's centred start pose (`startIndex` −1, `idlePoseFrom` 3). Nothing plays until a version is chosen: click it, press **1-4** (main row or numpad), or press ▷ (= V1 when none is selected). Selecting starts the version from 0:00 at its start pose; selecting the current version again **deselects** it (2026-10-05, was: restarts it): `Select(i)` with `i == Selected` calls `ShowBrainOnly()` (all standby, idle pose). Applies to clicks, 1-4 and fingertip touches. **Seek bar hidden by default** (2026-10-05, user): `PresentationMenu.showSeekBar` (new, default off) sets the seek bar's `visibleOnStart`; H or T shows / hides it and the choice is kept across versions. Keys (Space, Home, [ ], , .) still work while it is hidden; the menu still reserves the bar's height below it. **0** returns to the still brain. The seek bar appears just above (`seekBarPosition` y 0.165) only while a version is selected, and follows it (`TimelineTransportUI.SetTimeline`). The seek bar now has rounded corners too.
- Everything loads at start, and the timelines that are not selected are in **standby** (`NeuronalLossSequence.SetStandby`). In standby a timeline does not prepare, play or draw, and it hides its split brains, block and card. `Ready` is false in standby, so the V3 maps and V4 fibres/card hide themselves, and `AxonRepairTimeline` restores the brain's opacity.
- **Exe** (2026-10-03): **Brain ▸ Build ▸ Presentation All** -> `Builds/Presentation All/Presentation All.exe`. Succeeded with 0 errors in 222 s, 46,454 MB; the Ultraleap `LeapC.dll` is included under `_Data/Plugins/x86_64`. Not yet run.
- **Hand control (Ultraleap)**, `Interaction/LeapBrainManipulator.cs` on `HandControl`:
  - *Since 2026-10-05 grabbing no longer moves the brain and a one-hand grab turns it about Y only (turntable); see "2026-10-05 changes" below. The description here is the 10-03/04 behaviour, still available with `allowMove` on / `yawOnly` off.*
  - Gestures (changed 2026-10-03 at the user's request, "grab the brain, not pinching"): a one-hand **grab** (closing the hand, `GrabStrength` on 0.7 / off 0.45) holds the brain, which then moves and turns with the palm about its own centre. A two-hand grab moves the brain with the pair's midpoint and twisting the pair turns it about the vertical; since 2026-10-04 it no longer scales (pinch is the only way to scale). Open the hand to let go; grab again to go further. Pinch is no longer used for the brain, only for the menu. `requireNearBrain` (off) would only take hold when the palm is within `grabReachMetres` 0.05 of the brain's box.
  - Gains: `moveGain` 1.5, `rotateGain` 1.6; scale is clamped to 0.3-4 × the start scale. Gestures use hysteresis and smoothed hand data.
  - **Two-hand pinch scale** (added 2026-10-04 at the user's request): both hands pinching (thumb + index, rest of the hand open: `pinchMaxGrab` 0.5; `pinchOn` 0.85 / `pinchOff` 0.6), then drag them apart / together scales the brain about its box centre (no move or turn). Scale factor = (hand distance ratio)^`pinchScaleGain` (1), same 0.3-4 clamp; the pinch points get yellow discs and the hands tint yellow. A pinch that clicked the menu never scales, and a second hand pinching while the first already pinches starts scaling instead of clicking (`LeapMenuInteractor.IsMenuPinch`, `otherPinching`). Two closed fists no longer scale (changed the same day at the user's request). Compiled (0 errors) but **not yet tried in Play mode or with real hands**.
  - Grabbing while a timeline plays pauses it.
  - **The hands are drawn** by `Interaction/LeapHandRenderer.cs` (also on `HandControl`) as Ultraleap's rigged 3-D hand model: the `GhostHands` prefab (`GenericHand` mesh), set in `handsPrefab` by the scene builder. The user rejected the earlier capsule/skeleton hands ("don't want the lines").
  - How it draws: Ultraleap's `HandBinder` poses the skinned mesh from the provider. Unity's own drawing is switched off (`forceRenderingOff`), because a normally rendered mesh would vanish under the volume. Each frame the mesh is baked with `BakeMesh(mesh, false)` and drawn from the brain's `Drawn` event with `Brain/HandOverlay`.
    - Gotcha: the baked vertices already include the bones' scale and the right hand's mirroring, so draw with the renderer's position/rotation only. Using `localToWorldMatrix` scales the hands twice; `BakeMesh(…, true)` shrinks them.
    - The mirrored right hand's normals point inward; the shader flips any normal that faces away from the viewer.
  - `Brain/HandOverlay` (in Always Included Shaders): depth pre-pass + alpha pass, so the hand is depth-correct against itself and drawn over the volume. It has soft light from the viewer plus a rim, and the forearm fades out past the wrist (`armFade` 0.6 palm widths; `_FadeOrigin/_FadeDir/_FadeLength`).
  - Colours: hand colour (0.93, 0.91, 0.89, 0.88); a gesture tints it 55 % toward teal (holding, `grabColor`) or yellow (two-hand scale). A small disc marks the palm while it holds the brain.
  - `previewTestHands` (debug) poses the hands with Ultraleap's test hands when none are tracked; the look was checked that way in screenshots (2026-10-03).
  - Only drawn after the bricked Brain: a volume that draws later (V1's FusedVolume/BFI) can cover the hands.
  - At runtime it creates a desktop `LeapServiceProvider` at `deviceMetres` (0, −0.03, −0.20) in the SRDisplayManager frame, scale = `SRDViewSpaceScale` (3). The manager is rotated −45° about X; its local frame is the real room, so hands map 1:1.
  - Checked 2026-10-03: compiles, the device connects (`IsConnected` true), and the provider is placed at (0, −0.09, −0.60).
  - The grab version was checked in Play mode with **simulated hands**: Ultraleap test hands with `GrabStrength` set were written into the provider's `_transformedUpdateFrame`, then `Update` was invoked by reflection.
    - Open hand: nothing happens. Closed hand: holding.
    - Hand moved +0.1 and turned 20°: the brain moved 0.15 and turned 31.9° (the gains).
    - Opened and moved on: the brain stays.
    - Two closed hands spread ×1.42: the brain scaled ×1.42.
  - **Not tried with real hands yet.**
- **Menu by hand**, `Interaction/LeapMenuInteractor.cs` on `HandControl`: **touch** a button with the index fingertip (changed 2026-10-04 at the user's request: reaching for the pinch menu was moving the brain).
  - The bar (still a world-space canvas, tilted like the panel) now floats `touchForwardMetres` 0.04 m (× `SRDViewSpaceScale`) further toward the viewer than before (`PresentationMenu.Place`), so the fingertip can meet it clear of the brain. The SRD's box-front clipping starts about 10 cm in front, so keep this small.
  - The fingertip is projected straight onto the bar's plane (no eye ray any more). Within `hoverMetres` 0.06 a ring shows that spot, shrinking as the finger nears, and the button under it lights up. When the tip reaches `touchMetres` 0.006 from the front, that button is pressed (`Press`, white flash) and drawn pushed in (`PresentationMenu.HandPressed`, scale 0.92). Pull back past `releaseMetres` 0.018 to touch again; a tip up to `behindMetres` 0.04 through the bar still counts. Pinch no longer clicks; it is only for two-hand scaling.
  - **Menu zone**: a hand whose fingertip or palm is within `zoneMetres` 0.10 in front of the bar (within `zoneMargin` 90 canvas units of a button) can't start a grab or a pinch (`InMenuZone` → `LeapBrainManipulator.Read(…, atMenu)`). Also, a pointing hand (index extended: three curled fingers read as GrabStrength ≈ 0.7) never takes hold; it is held `pending` until it becomes a real fist away from the menu. A hand already holding or pinch-scaling the brain doesn't touch the menu.
  - Compiled (0 errors) 2026-10-04; **not yet tried in Play mode or with real hands**. The 2026-10-03 checks (`ButtonAt`, `Press(2)`) still apply.
  - **Menu safety** (2026-10-04, user: buttons work, "but model is also getting affected if hands collide after touching the buttons"). Cause: the pointing hand at the menu reads as closed (GrabStrength ≈ 0.7); the old `pending` grab then fired as the hand pulled back and the index curled. Now in `LeapBrainManipulator.Read`: (1) **latch**: a hand that was in the menu zone may not start a grab or pinch until it has been fully open (GrabStrength < `openBelow` 0.3, no pinch) and out of the zone for `menuReleaseSeconds` 0.4; the latch survives tracking dropouts; (2) for `pressLockSeconds` 0.6 after a hand press (`LeapMenuInteractor.LastPressTime`) no hand starts a grab or pinch; (3) a hand must be **seen open** after it appears before it can grab (a hand that appears closed doesn't grab); (4) a close whose index still reads extended may become a hold only within `fistWaitSeconds` 0.25, after that the hand must open again (replaces the open-ended `pending`). A hand already holding the brain keeps it. Compiled (0 errors); not yet tried with real hands.
- **Seek bar under the menu** (2026-10-04, user: "the video slider is big, put it small below the menu and keep it there when the menu moves"): `PresentationMenu.seekBarBelow` (on) sets `TimelineTransportUI.below` = the menu's bar. The seek bar then lies in the menu's plane, centred under it, `seekBarWidth` 0.6 × the menu's width (about half the old size), `seekBarGap` 10 menu canvas units below, re-placed in `LateUpdate` so it follows any move of the menu (`panelPosition`, `towardViewer`, `touchForwardMetres`, `widthOfPanel`). The menu lifts itself by the seek bar's height + gap (always, so it doesn't jump when a version starts) so the pair stays above the panel's bottom edge; `seekBarPosition` is only used with `seekBarBelow` off. Compiled (0 errors); not yet seen in Play mode.
- **R = reset the brain's pose only** (2026-10-04, user: "R should only reset the transform, not the video"). Before, R both restarted the video (`TimelineTransportUI`) and, while held, snapped the Brain to its scene-load transform (`ModelMoveController.ResetModelTransform`). Now R is handled by the active timeline: `NeuronalLossSequence.ResetPose()` puts the brain back to the start position/size and re-applies rotation, cut and split at the **current** time (`_seeked`); time and play/pause are untouched. Each timeline turns its brain's `ModelMoveController.resetKey` off in `Start` (new field, default on, so other scenes are unchanged where no timeline drives the object). With no version selected, `PresentationMenu` re-places the idle pose on R. Restart from 0:00 is still Home (selecting the version again now deselects it). Compiled (0 errors); not yet tried in Play mode.
- **`Presentation All Flat`** (2026-10-04, for the 85-inch flat display): a copy of Presentation All (written by hand as YAML, since UnityMCP was down; **Brain ▸ Scenes ▸ Create Presentation All Flat (85-inch screen)** regenerates it from Presentation All, **Brain ▸ Build ▸ Presentation All Flat (85-inch screen)** builds `Builds/Presentation All Flat/`). Changes vs Presentation All: SRDisplayManager saved **inactive** (prefab override `m_IsActive` 0; the Sony plugin never starts; `SRDProjectSettings.RunWithoutSpatialRealityDisplay` is already on, so the exe doesn't quit without an SRD); the old `Display2Camera` is now **`FlatCamera`** (MainCamera tag, Display 1, black background, AudioListener) with **`FlatDisplayRig`** (`Scripts/Rendering/FlatDisplayRig.cs`); `DualScreenView` disabled; `HandControl` (Ultraleap) on, see below. `FlatDisplayRig` stands in for the SRD panel: the ELF-SR2 panel (`panelSizeMetres` 0.5977 × 0.3362, tilted 45°) in the inactive SRDisplayManager's frame (−45° X, scale 3), i.e. an upright 1.79 × 1.01 rectangle with its bottom edge at the origin. `NeuronalLossSequence.HasPanel/PanelPoint/PanelRotation/PanelWidth` use it when the SRD isn't running, and `DisplayFrame.Get/ViewSpaceScale` replaces the `srd.isActiveAndEnabled ? srd.transform : null` lookups (NeuronalLossSequence.GetFrame, AxonRepairTimeline, TimelineDensityOverlay, ModelMoveController, PresentationMenu), so the menu, seek bar, cards and split layout land where they do on the SRD. The camera sits on the panel's normal through its centre, `viewerDistanceMetres` 0.7 (× 3) away, field of view set so the panel exactly fills a 16:9 screen (narrower screens: `fitWholePanel`). F10 recording uses the FlatCamera. Compiled (runtime + editor, 0 errors); **not yet opened or run in Unity**.
  - **Hands on the flat screen** (2026-10-04, user asked to set up Ultraleap there too): `LeapBrainManipulator.PlaceDevice` calls `FlatDisplayRig.PlaceHandDevice` when the SRD isn't running. The provider gets the panel's axes (x right, y up, z into the screen) and scale `HandScale` = panel width / `handReachMetres` (0.45 m of side-to-side reach spans the panel, ≈ 4 world units per real metre). `handCentreMetres` (0, 0.25, 0), a hand 25 cm over the device, lands on the panel centre, `handInFront` 0.05 panel widths in front of it. Assumes the device lies flat facing up in front of the screen, user's side toward the user. All real-metre settings (grab reach, discs, menu touch distances and zone) now use `LeapBrainManipulator.HandScale` = the provider's scale (SRDViewSpaceScale on the SRD, unchanged there). The menu keeps `touchForwardMetres` 0.04 (× the frame scale 3) so the bar is touched before the brain. Compiled (0 errors); not tried with real hands on the 85-inch screen.
  - **Brain start position on the flat screen** (2026-10-05, user: keep (0, 0.5, 0.4) as the initial position for every version): `FlatDisplayRig.overrideBrainStart` (on) / `brainStartPosition` (0, 0.5, 0.4) set every `NeuronalLossSequence.startPosition` in `Awake` (order −1000, before the timelines' Start), so all four versions, the brain-only view and R use it. Rotation and size stay each version's own; the scene file itself still holds the old values (−0.57 in z), only Play mode / the exe use the override. **Kept for the whole video** (same day, user: "throughout the complete video keep that position"): `keepBrainPosition` (on). Only V1 (split, `splitLeftPosition` (0, 0.49, −0.35)) and V3 (recede 2 s, `recedeOffset` (0, 0, 0.35)) moved the brain after the start. Now V1's `splitLeftPosition` = the start position and its split-off brains (right, third) move by the same offset (0, 0.01, 0.75), keeping the ±0.47 spacing; V3's `recedeOffset` is 0 (it still shrinks to `recedeScale` 0.7) and its two density maps (`TimelineDensityOverlay`, FibProbability / AstrocyteDensity) move by (0, 0.01, 0.62), so the layout around the brain is unchanged. V2 and V4 never moved it; the computed block pose and V4's axons follow the brain. Compiled (0 errors); not yet run.
- **2026-10-05 changes: limits on manual control** (user: "the brain should not translate with mouse, keyboard or hands — in the video it is fine — and only rotate about Y"). Scripts only, no scene values, so they apply in every scene:
  - `ModelMoveController.allowTranslation` (new, **default off**): WASD/QE and left drag do nothing. `yawOnly` (new, **default on**): Up/Down arrows and vertical right drag do nothing; Left/Right and horizontal right drag still turn about the display frame's up. Zoom unchanged. The neuronal-loss block's dev mover (M, created in `NeuronalLossSequence`) sets `allowTranslation` on / `yawOnly` off, so block placement still works.
  - `LeapBrainManipulator.allowMove` (new, **default off**): grabbing no longer moves the brain (one or two hands). `yawOnly` (new, **default on**): a one-hand grab turns the brain like a turntable, from the hand's left/right travel along the display frame's right (`yawDegreesPerMetre` 600 per real metre, about 180° for 30 cm): hand left → the brain's front turns left, same sense as right-drag with the mouse. The old palm-rotation mapping (any axis, `rotateGain`) is used only with `yawOnly` off; it felt reversed to the user ("hand goes left, brain goes right") because sweeping the hand swings the palm the other way. Two-hand twist (about Y) and two-hand pinch scale are unchanged; the two-hand twist direction (a flat steering wheel) has not been confirmed with the user.
  - **Tried and reverted the same day**: a `BrainPoseLock` component that pinned the brain to one world position for the whole video and filtered the timelines. The user wanted only the *manual* translation gone, not a fixed position, so it was removed completely (script, scene components, the Flat rig values). If a fixed pose is ever wanted again: the timelines write the brain's position/rotation/scale every frame, so a lock has to own the pose *and* the split-off brains copy it (V1), otherwise they drift apart.
- **2026-10-05, later: glass-slide slicer** (commit `7ba097b`). The user rejected a "knife hand" gesture and asked for "a rectangle slide (transparent slide)" that slices the brain as it is moved, turned on from the menu, taken by pinching a tab.
  - *Later the same day: the slide follows one hand (no tab pinch), a pinch stops / restarts it and Slice off keeps the cut; see below.*
  - **Use**: the menu bar has a new **Slice** toggle at its right end, behind a divider (`PresentationMenu.sliceButton`, default on; shown only when the scene has a `LeapSliceSlide`; the bar's size is still set from the version buttons, so it gets wider rather than shrinking them). On: a transparent glass slide appears, parked just outside the brain (`park` 0.06 of the volume) on the end facing the viewer, with a frosted tab on its upper edge. Pinch the tab with one hand (thumb + index; the tab lights magenta when an open hand is within `tabReachMetres` 0.045) and move the hand: the slide moves along its rail and everything on the viewer's side is cut. Open the fingers: slide and cut stay. **R** or turning Slice off removes the cut (off removes only the slide's own cut, not a timeline's).
  - **How**: `Interaction/LeapSliceSlide.cs`, added to `HandControl` by `LeapBrainManipulator.Awake` if missing (so no scene change; add it in the scene to keep tuned values). It drives `ISliceableVolume.SlicePosition` / `SliceFromHighZ`, the same cut the timelines use, so it works on the bricked Brain without shader changes ("Option 1"). The rail is the volume's **z axis** (sections = the brain's left-right axis): the slide is a sagittal plane fixed to the brain and turns with it. Seen with the brain facing the viewer it is edge-on; turn the brain ~90° (grab) to face the cut. Not held, the slide shows the volume's current cut, so a timeline's cut or R moves it. Hand travel along the rail × `gain` 1.5 moves it.
  - `LeapBrainManipulator`: new `Mode.Slide`. A pinch that *starts* within reach of the tab takes the slide until the pinch ends (`HandState.pinchStarted/sliding`, `TakeSlide`); one hand at a time; two-hand pinch scale ignores a sliding hand; taking the slide pauses a playing timeline. `IsSliding(left)`; `LeapMenuInteractor` treats a sliding hand as busy (no menu presses). `LeapHandRenderer.slideColor` tints that hand magenta; the pinch point gets a magenta disc.
  - **Look** (user: "make the slice plane look glassy"): `Rendering/GlassSlide.shader` (`Brain/GlassSlide`, ZTest Always like the other overlays). The slide is a thin box (`thicknessMetres` 0.003, × the hand scale): faint cyan body (`glassTint` alpha 0.06), Fresnel opacity at grazing angles, bevelled bright border (`bevelMetres` 0.004), two diagonal sheen streaks that shift with the view direction (parallax with head tracking), bright cyan side faces (a visible edge when edge-on), frosted tab (`tabFrost`). Edges / tab fade to magenta while held / at hand. Falls back to flat `Brain/OverlayUnlit` with a warning if the shader is missing.
  - Build: `SceneBuilds.cs` now has an `IPreprocessBuildWithReport` (`IncludeRuntimeShaders`) that adds `Brain/GlassSlide` and `Brain/OverlayCard` to Always Included Shaders before every build (they are only found by `Shader.Find` at runtime).
  - Status: compiles (scratch `dotnet build`, 0 errors). The user toggled it in Play mode (Editor.log `[Slice] Slide on/off`); **pinching, cutting and the glass look not yet confirmed** (no screenshot, no real-hands test). The shader was not compiled outside Unity; check the Console.
  - **Changed later on 2026-10-05: slide attached to one hand, no pinch** (user: "when I select slice, the slicing plane should attach to the hand, right or left, but only one hand; moving the hand slices; the other hand rotates"). While Slice is on, the slide belongs to one hand (`LeapBrainManipulator._slideHand`): the hand that pressed Slice (`LeapMenuInteractor.LastPressLeft`, new), else the tracked hand, else the one nearer the brain. Its palm movement along the rail moves the slide from the current cut (relative, × `gain`, no jump). That hand never grabs or pinches; the other hand's one-hand grab turns the brain at the same time (two-hand twist / pinch scale are off while sliding). The slide rests while its hand is in the menu zone or within `pressLockSeconds` of a press, so that hand can still press buttons, including Slice off (the menu no longer treats it as busy). A hand lost by tracking keeps the slide for `slideReassignSeconds` 1.5, then the other free hand takes it. A playing timeline is paused once the slide hand has moved `slidePauseMetres` 0.02 (real) since it took the slide. Removed: the tab pinch (`NearTab`, `TabHover`, `tabReachMetres`, `pinchStarted`); the tab is still drawn. Compiled (scratch `dotnet build` of `Assembly-CSharp.csproj`, 0 errors); **not tried in Play mode or with real hands**.
- **2026-10-05, later: cards covered by the brain** (user, Presentation All Flat: the cards are partly or fully covered by the brain). Cause (gotcha 6): the volumes draw in `OnRenderObject` with ZTest Always, after the uGUI label cards.
  - Attempted fix (commit `129237b`), **not working yet**: `Rendering/CardOverlay.cs` + `Brain/OverlayCard`. `CardOverlay.Attach(canvas, card)` (called in `NeuronalLossSequence`, `TimelineDensityOverlay`, `AxonRepairTimeline` `BuildCard`) disables the card's Canvas, renders it each frame it is visible into a RenderTexture with a hidden orthographic camera (layer 31), and draws that texture as a quad in `Camera.onPostRender` (meant to run after every `OnRenderObject`, and before the SRD's `AfterImageEffects` homography). Every volume's `OnRenderObject` skips the snapshot camera (`CardOverlay.IsSnapshot`; `BrickVolumeLoader`, `FusedVolumeLoader`, `NpzDensityVolume`, `BrickStreamer`, `T1VolumeLoader`, `BrainVolumeRenderer`). The user's screenshot afterwards (`D:\Unity\new\scrnshot\Screenshot 2026-10-05 133811.png`, V2) still shows the pink neuronal-loss block (drawn from the brain's `Drawn` event) over the "Neuronal loss" card. Not investigated: whether the overlay ran at all (no `[Card]` error in Editor.log; whether the Editor had recompiled before that Play run is unknown) or whether `onPostRender` really comes after `OnRenderObject` for the FlatCamera. Keep or revert it.
- **2026-10-05, later: V2 card and block poses in Presentation All Flat only** (commit `129237b`, values from the user, copied from the Inspector):
  - `NeuronalLossSequence.useCustomCardPose` / `cardPosition` / `cardRotation` (new, default off): the card stays at that world pose (its bottom-centre) instead of riding above the block. V2 in the Flat scene: (−0.4621, 0.7903, −0.0233), rotation quaternion (0.38197, 0, 0, 0.92418) = 45° about X.
  - V2's block: `useCustomFinal` on, `finalPosition` (−0.472, 0.621, −0.00476), `finalEuler` (0, 135, 12) (converted from the quaternion (0.0966, 0.9188, 0.0400, 0.3806)), `finalScale` 1. V2's `spinDegreesPerSecond` is 0, so this is the exact final pose.
  - Written into the scene YAML (Editor not playing); the user must reload the scene. Not yet seen in Play mode.
- **2026-10-05, evening: slice pinch and kept cut** (commit `4802b78`, pushed). A pinch with the slide's hand stops the slide where it is (edges and tab amber, `LeapSliceSlide.Paused` / `pausedColor`); the next pinch lets the hand move it again from there. **Turning Slice off now keeps the cut** (was: removed the slide's own cut); R resets it. Pinch edges are detected once per pinch: `LeapBrainManipulator.HandState.pinchHeld` keeps the pinch hysteresis because a tool hand's `pinching` is cleared every frame. Compiled; not tried with real hands.
- **2026-10-05, evening: first magnifying lens** (commit `c07f99f`, pushed; **replaced** the same night, see below). A circle on the hand showed the brain behind it, stepping L4 → L1 with hand depth (x2 per level) while `BrickVolumeLoader` streamed the finer bricks for the lens camera. The user asked to revert it. What is left of it: the menu's **Lens** toggle, the one-hand tool plumbing in `LeapBrainManipulator` (Slice and Lens exclusive, `Mode.Lens`, light-blue hand tint), and the lens-level streaming in `BrickVolumeLoader` (`LensLevel`, `LensBricks`, LRU within `lensBudgetMB`), which nothing sets any more, so it loads nothing.
- **Lens mode, IIP slide and V5 (2026-10-05 night, uncommitted)**. All compiled outside Unity (0 errors), not run by Claude.
  - **Lens = crosshair + box** (`Interaction/LeapLens.cs`, `Rendering/Lens.shader`). With **Lens** on, a teal crosshair follows the lens hand (just past the fingertips, `reachMetres` 0.10) or the mouse, whichever moved last. It sits **on the brain**: on the first tissue along the line of sight through the cursor / hand point, i.e. the outer surface or the cut face (`BrickVolumeLoader.RaycastTissue`, which marches the tissue mask in half-voxel steps inside the part not cut away; the mask is kept on the CPU for this, ~130 MB). Left click or the lens hand's pinch **stops** it (amber) on that spot of the brain (it turns with the brain); again = moves again. A rounded **box** at the lower left of the display panel (`boxPosition` (−0.70, 0.32), `boxSize` 0.20 × 0.18 panel widths, in front of the brain, after the user's sketch `scrnshot/Screenshot 2026-10-05 174422.png`) shows the brain around the crosshair from the viewer's head, 1x by default. **Zoom**: mouse wheel or + / −, x2 a step (up to 512); while the lens is on, `ModelMoveController` leaves the wheel and + / − to it (`LeapLens.TakesZoom`). Under the box: the crosshair's **coordinates** (mm from the volume's corner: x = columns, y = rows from the top, z = sections; and the L4 voxel), drawn with the built-in font from the `Drawn` event (`Brain/OverlayText`; not the unfinished `CardOverlay`). Stopping copies them to the clipboard and logs `[Lens] Crosshair stopped: …`. The box camera uses the SRD **WatcherCamera's pose even though the SRD keeps that camera disabled** (the first lens drew nothing in Presentation All because of that check).
  - **Vessels** (`SRD/BrickMaskOverlay.cs`, `Rendering/BrickMask.shader`): the user added `StreamingAssets/Bricks/hb02_vessels` (MRI TOF vessel mask, levels L0-L3 = 0.2-1.6 mm, one BC3 brick per level; rgb = a constant red tint, **alpha = the mask**; its bbox is in the brain's centred-mm frame, 224 × 134 × 181 mm). `BrickVolumeLoader.overlayDatasets` loads such mask datasets into the brain's space and draws them over the brain, cut with it. **Switched off** at the user's request: `overlayDatasets` defaults to empty; add `hb02_vessels` to bring them back (`overlayLevel` 1 = 0.4 mm). Alignment never seen.
  - **IIP slide** (`Interaction/IipSectionOverlay.cs`, `Rendering/SlideSection.shader`; added at runtime by `LeapLens`). Hardcoded for one slide: `http://deviipsrv.humanbrain.in:9081/iipsrv/fcgi-bin/iipsrv.fcgi?FIF=/ddn/storageIIT/humanbrain/analytics/580/NISL/B_580_HB2CV[LM]-SL_354-ST_NISL-SE_1060_lossless.tif` (brackets sent as `%5B`/`%5D`; the project already allows http). The server reports 192000 × 192000 px, 2048 px tiles, 8 levels (`JTL` level r = 1500·2^r px; the user says full resolution is 1 µm/px, so level 0 = 128 µm = L4, level 4 = 8 µm = L0, level 7 = 1 µm). **Fit to the volume** (measured offline: tissue-outline IoU against every section of `Fused/hb02_L4.raw`, with scale, rotation and offset searched): **section z = 150** of 485, IoU 0.95 (z 140: 0.86, z 160: 0.90), not mirrored, `thumb_px = T + s·R(a)·(v − C)` with v = L4 voxel (x, y), C = (708, 388), s = 1.04, a = −9.63°, T = (723.0, 782.4); full-resolution px = thumb px × 128. When the cut face is within `sectionTolerance` 4 sections of section 150, the slide's thumbnail is drawn on the cut face (tissue only, the white background transparent). Key **I** (lens on) cuts to section 150. With the crosshair on that cut face, the lens box shows the **IIP tiles** around it instead of the 3-D view: the level that fits the box's width at the current zoom, over the thumbnail, 4 downloads at a time, 32 tiles cached (`ComposeAround`); the readout adds e.g. `SL_354 Nissl   JTL 5   x8   px …`. No slide list or slide-to-volume transform exists in the repo: other slides need their own fit (or the pipeline's transform).
  - **V5 · "IIP Slide"** (user: "rotate the brain, stop at the left sagittal, slice till the hardcoded section", then a square like V2's block that zooms into the IIP slide down to cells). A `NeuronalLossSequence` copied from V2's settings (start pose, timings 3 + 6 + 6 s) with: no neuronal-loss block, no return, no loop (`PresentationMenu.Version.holdAtEnd`, new), and **`cutToSection` 150** (new, `sectionsInVolume` 485: the cut face lands on that section whichever end the cut starts from; −1 = `clippingDepth` as before). Then **`IipSlidePanel`** (`Interaction/IipSlidePanel.cs`, on `Timeline V5`) runs in the hold: "Slide out" (3 s) — a 10 mm patch of the cross-section (`startRegionMm`) lifts off the cut face, its slide picture lying over the tissue in the section's orientation, and rises **straight out along the cut face's normal** toward the viewer, growing to `panelSize` 0.22; "Zoom to cells" (8 s) — it zooms into the IIP slide down to 1 µm/px; then a 4 s hold. Start spot (user's marks, measured from their screenshots `scrnshot/Screenshot 2026-10-05 192737.png` and `195327.png` against section 150): `startOnCut` **(0.742, 0.235)** = L4 voxel (1050, 182), upper frontal cortex. Placement objects, made at play time unless the scene has them: **`V5 Panel Start`** (child of Brain: the spot) and **`V5 Panel End`** (child of `Timeline V5`: end position, rotation, `localScale.x` × size). **`Timeline V5` is in both scene files** (written as YAML next to `Timeline V2`, fileIDs 976250309-312, plus a V5 entry in the menu's `versions`); `PresentationMenu.addV5` would otherwise build it at play time, and **Tools ▸ Presentation ▸ Create V5 Timeline** (`Editor/CreateV5Timeline.cs`) creates it in an open scene. The user saw V5's cut face (screenshots) and approved the square coming out of the cross-section ("nice"); the moved start spot, the zoom and the IIP tiles in the square were not seen yet.
- **Ultraleap package**: `com.ultraleap.tracking` + `.preview` 7.3.0 from the OpenUPM scoped registry. In `Packages/manifest.json`; committed in `121130c` together with the samples. The user also imported the package samples (`Assets/Samples/Ultraleap Tracking/7.3.0`) and `Assets/Resources/Ultraleap Settings.asset`. Nothing in this project uses the samples; they only add a harmless CS0618 warning (`MovePoseExample.cs`).
- **Ideas offered to the user for further hand interaction** (none chosen yet):
  - hand-as-slicer: a flat hand sets the cut plane;
  - point at the brain to show the region label (hb02 label volume);
  - pinch-drag to scrub the timeline;
  - a two-hand pull-apart into V1's split;
  - a swipe for the next / previous version;
  - an idle demo mode when no hands are seen.

  The recommendation was the slicer and the region labels. **The slicer was built** (2026-10-05) as a glass slide moved by a pinch, not a flat-hand plane; see below.
- **Hand-over copy on the external drive `E:\unity`** (2026-10-03, for another developer; the user wanted **no Ultraleap** in it):
  - `E:\unity\BrainVolumeSRD` is `git archive 8bfdf0d` of the Unity project, plus `Presentation All` without `HandControl`, plus the menu scripts (`PresentationMenu`, `NeuronalLossSequence` standby, `TimelineTransportUI`, `UiKit`, `AxonRepairTimeline`) and a `SceneBuilds.cs` with the Ultraleap block removed.
  - `com.coplaydev.unity-mcp` is removed from its `manifest.json` and `packages-lock.json`.
  - All data except bricks L1 (13.2 GB, verified file-by-file).
  - `E:\unity\README.txt` and `E:\unity\handover.md` (the 8bfdf0d version).
  - It was opened once in batch-mode Unity 6000.3.25f1: all scripts compiled, all assets imported, exit 0. So it also has a ready `Library/`.
  - It has not been opened in Play mode.

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
4. Presentation scenes (§6a): open `Assets/Scenes/<V1|V2|V3|V4>.unity` and Play. They loop; Space pauses, the seek bar seeks.
5. `Assets/Scenes/Presentation All.unity` (§6a):
   - Starts with only the brain. **1-4** (or click / point + pinch on the bar) start a version; **0** goes back to the brain.
   - Hands: grab to move and turn, grab with both hands to scale. The Ultraleap tracking service must be running (Windows service `UltraleapTracking`, running on this PC 2026-10-03), with the device flat on the desk ~20 cm in front of the display.
   - Rebuild the scene from the four scenes with **Brain ▸ Scenes ▸ Create Presentation All (V1-V4 + menu bar)**. Edits made by hand in `Presentation All` are lost on a rebuild; change the source scenes or `SceneBuilds.cs` instead.

### Building the exes (one per presentation scene)
- Menu **Brain ▸ Build ▸ Nissl and Labels V1 / NeuronalLoss Display V2 / NeuronalLoss Fib Astrocytes V3 / Axon Damage Repair V4 / Presentation All** (`Assets/Editor/SceneBuilds.cs`). Output: `BrainVolumeSRD/Builds/<scene>/<scene>.exe` with `<scene>_Data/` next to it. Windows x64.
- Without the Editor window (batch mode; the project must not be open in another Editor):
  ```
  "C:\Program Files\Unity\Hub\Editor\6000.3.25f1\Editor\Unity.exe" -batchmode -quit -projectPath <BrainVolumeSRD> ^
      -executeMethod BrainVolume.EditorTools.SceneBuilds.BuildNisslAndLabelsV1 -logFile build.log
  ```
  (`BuildNeuronalLossDisplayV2`, `BuildNeuronalLossFibAstrocytesV3`, `BuildAxonDamageRepairV4`, `BuildPresentationAll` for the others).
- Through UnityMCP a build blocks the Editor for minutes, so `execute_menu_item` times out. That is expected; wait for `[SceneBuilds] <scene>: Succeeded ...` in `%LOCALAPPDATA%\Unity\Editor\Editor.log`.
- **Size**: ~46 GB each, because the whole `StreamingAssets` is copied (43 GB bricks incl. L1, which only loads on a GPU with ≥ ~47 GB). The first build of a scene copies for ~3 min; rebuilds take seconds. Excluding `Bricks/hb02_fused/L1` would cut a build to ~14 GB without changing what loads on a 24 GB GPU (not done).
- A build log shows a few "Destroy may not be called from edit mode" errors (`OnDestroy` cleanup running at build time). They are harmless; the build succeeds.
- **Running an exe**: close the Unity Editor first, otherwise the SRD runtime refuses with "Another Spatial Reality Display application is already running" (§8.15). Copy the whole `Builds/<scene>/` folder; the exe needs `<scene>_Data`. The player log is `%USERPROFILE%\AppData\LocalLow\DefaultCompany\BrainVolumeSRD\Player.log`. Look for `[Bricks] ... loaded 24/24`, `[Fused] Labels loaded`, `[Density] ...`, `[Loss] Timeline drives 'Brain'`, and in `Presentation All` also `[Menu] ...`, `[Leap] Created a desktop LeapServiceProvider ...` and `[Leap] Hand models: 2 (GhostHands) ...`.
- A fresh clone needs the gitignored data copied in before building: `StreamingAssets/Bricks/hb02_fused`, `Fused/hb02_L4*.raw` + `hb02_L5.raw`, `Npz/*.npz`, `Nifti/BFI_in_MRI_2.nii`.
- Building or opening the project on another PC: `com.coplaydev.unity-mcp` comes from GitHub (needs git + internet). Remove it from `Packages/manifest.json` if it doesn't resolve; nothing at runtime uses it (that was done for the `E:\unity` copy, §6a). The Ultraleap packages come from OpenUPM (`package.openupm.com`), so they need internet on the first open.

---

## 8. Standing gotchas

1. **Address**: the live server is `http://dgx3.humanbrain.in:8010` = `http://172.20.23.156:8010` (2026-10-01). Older docs/scenes mention 8090, 8095, 10226.
2. **`extent_mm` is (z, y, x)** in `/api/dataset/info`; BVX2 bbox and transforms are (x, y, z).
3. **Running without the display**: see §7.3 and the StrokeVideo handover §2bu A.
4. **One Unity Editor at a time** if driven through UnityMCP (shared bridge port 8080).
5. **Streamed brick cache is keyed by z-slab only** (fine for levels ≥ 3).
6. **All volume shaders ignore depth** (`ZTest Always`, `ZWrite Off`). Volumes draw over everything; ordinary scene objects (floor, the uGUI label card and seek bar) can be **hidden behind the brain**. Overlays that must sit on top are drawn from the loader's `Drawn` event (the neuronal-loss block does this). The label cards still lose to it (2026-10-05, Presentation All Flat V2); `CardOverlay` is an unfinished attempt (§6a), and fixed card poses that keep the card off the brain are the current workaround.
7. **Large StreamingAssets** (all gitignored): `Fused/hb02_L4.raw` 1.6 GB, `Bricks/hb02_fused/L2` 8.1 GB. The `Brain` object loads all of L2 at Play — slow start and heavy GPU memory; `level = 3` (2.1 GB) if it struggles.
8. **Namespace trap**: brick_L0's scripts live in `BrainVolume.SRD`, so inside `namespace BrainVolume` a bare `SRD.Core.X` resolves to `BrainVolume.SRD.Core` and fails (CS0234). Use `using SRD.Core;` at file top (current fix in the Sony scripts) or `global::SRD.Core.X`.
9. **Play-mode edits are lost** on Stop — e.g. the `NeuronalLossBlock` pose; copy with K first.
10. **Unity compiles only when the Editor has focus**; a stale `Library/ScriptAssemblies/Assembly-CSharp.dll` timestamp means "not compiled yet", not "compiled OK".
11. **Branches**: `brick_L0` is lordsiva's; today's work was merged into it and pushed. `neuro_version` (local) also has the NeuronalLossScene commit `715500b`, but `origin/neuro_version` does not.
12. Server-side gotchas from the first version still hold: big default caches (2 GB/block + 16 GB ROI), first CuPy call ~20 s, no auth / open CORS, `LruChunkCache` is falsy when empty, docs lag the code.
13. **`Shader.Find` shaders are stripped from player builds** unless something references them. The first exe loaded all data but drew nothing; its Player.log showed `[Bricks] Shader 'Brain/SRDBrickRaymarch' not found` etc. Fixed by Always Included Shaders (§6). The Sony plugin's own `Shader.Find` shaders are in its `Resources/` folder and do ship.
14. **Don't activate a second display in an SRD exe the plain-Unity way.** On this PC Unity's display order is [0] the 1920×1080 monitor, [1] the SR display. The Sony plugin moves Unity's main window onto the SR display itself (`SRDApplicationWindow` activates every display and hides the extra windows). So `Display.displays[1].Activate()` (`DualScreenView.activateSecondDisplay`) opened a full-screen window **on the SR display**, covering the 3-D image. Only the IMGUI HUD showed, and the screen flickered. That is why `activateSecondDisplay` and `Display2Camera` are off in V1-V4 and Presentation All. A real second-monitor view should go through the Sony plugin's 2-D view support (`SRD2DView`); not done.
15. **Only one SRD application at a time, and the Unity Editor counts** once it has used the display (e.g. after Play mode). An exe started while the Editor holds it pops up "Another Spatial Reality Display application is already running" and quits. It may also be why earlier exe runs stayed black (*not confirmed*).
16. **Editor crashes on D3D12**: three Editor crashes on 2026-10-02 (`Temp/Unity/Editor/Crashes`), all in the NVIDIA driver during a D3D12 draw (`nvwgf2umx` → `D3D12DeviceState::Transition`), during Play mode with several large volumes. If they continue, or if the exe stays black on the SR display, try Player Settings ▸ Graphics APIs = **Direct3D 11** (the Sony plugin supports it). Not tried yet.
17. **UnityMCP drops** after domain reloads, Play mode starts and long loads (`no_unity_session`). The Editor is usually fine; retry, or reconnect from the MCP for Unity window. After editing scripts, `refresh_unity` + checking that the new field/method exists (via reflection) is the reliable way to know the new code is loaded. `EditorUtility.RequestScriptReload()` forces it.
18. **Scene copies through UnityMCP**: `EditorSceneManager.SaveScene(scene, path, saveAsCopy: true)` copies the in-memory state including unsaved edits. Never `OpenScene(..., Single)` from code while the open scene is dirty (it discards the edits). Open the copy additively, edit, save, close.
19. **The SRDisplayManager is rotated −45° about X** (`SRDViewSpaceScale` 3). Its *local* frame is the real room (Y = real up, the panel tilted 45° inside it); world space is that frame tilted so the panel looks upright. Anything tied to the real world, such as the Ultraleap device, must be placed in the manager's frame, not in world axes (`LeapBrainManipulator.PlaceDevice`).
20. **Hands, menus and other 3-D overlays vanish under the volumes** (gotcha 6). Ultraleap's hand models are therefore not rendered by Unity (`forceRenderingOff`); they are baked and drawn from the brain's `Drawn` event (§6a). `SkinnedMeshRenderer.BakeMesh(m, false)` on the GenericHand already gives world-placed vertices with the bones' scale and mirroring, so draw with position/rotation only (§6a).
21. **Two Unity Editors on one project**: the batch-mode open of the `E:\unity` copy ran while the main Editor was open (a different project, so allowed). Around then the main Editor was restarted and UnityMCP dropped (`no_unity_session`) until `/mcp` reconnected. After every Editor restart, click into the Editor (it compiles only with focus, gotcha 10), then reconnect.
22. **`AssetDatabase.DeleteAsset` is blocked** by UnityMCP's `execute_code` safety checks; delete temporary assets on disk (with their `.meta`) and refresh.
23. **Editing a `.unity` file on disk while the Editor has that scene open** (2026-10-05, UnityMCP down, scenes edited as YAML): the Editor keeps playing its in-memory copy, so the user saw "nothing changed"; saving the scene in Unity would also overwrite the edit. After a YAML edit: stop Play mode, reopen the scene and answer **Don't Save** (or Reload). **The user's rule: make no file changes while Unity is in Play mode.** Check the end of `%LOCALAPPDATA%\Unity\Editor\Editor.log` first: the last "Reloading assemblies for play mode" with no later "Unloading … unused Assets", plus ongoing `[client] send/recv` SRD traffic, means it is playing. Prefer script defaults over scene values when a change can be made either way (new serialized fields take their code default in every scene, no reload needed).
24. **The SRD's WatcherCamera is disabled** (`Camera.enabled` false; the eye cameras render) but it is moved with the tracked head and holds the head's projection. Use its pose (find it among the SRDManager's children, `GetComponentsInChildren<Camera>(true)`, name starts with `WatcherCamera`); don't skip it for `!isActiveAndEnabled`.
25. **Compile-checking without UnityMCP**: copy the generated `Assembly-CSharp.csproj` to a scratch folder, make its paths absolute and turn the `ProjectReference`s into `Library/ScriptAssemblies/<name>.dll` references, add any new `.cs` Unity hasn't listed yet, and `dotnet build`. For `Assembly-CSharp-Editor.csproj`, reference the scratch build's `Assembly-CSharp.dll` instead of the stale one. Shaders can't be checked this way: look at the Console after Unity imports them.
26. **New per-session data folders are not gitignored**: `StreamingAssets/Bricks/hb02_vessels/` (≈ 750 MB) shows as untracked. Don't commit it; add it to `.gitignore` with the other StreamingAssets data.

---

## 9. Working-tree state (2026-10-05, branch `brick_L0`)

**Pushed** to `origin/brick_L0` (2026-10-03): `8bfdf0d` "Axon Damage Repair V4 (healthy / APP / GAP43) + V1 BFI three-way split". It contains everything listed under "Uncommitted (2026-10-03)" below, plus V4.

**Committed locally, not pushed** (2026-10-05): `121130c` "Presentation All scenes: Leap hand control, flat-screen rig, version menu" (616 files, most of them the Ultraleap samples). It holds the whole list below *and* the 2026-10-05 changes (§6a): Presentation All + Flat, V2/V3 scene values, `SceneBuilds.cs`, `PresentationMenu`, `FlatDisplayRig`, `Interaction/`, `HandOverlay.shader`, the changed timeline/UI scripts, `Assets/Resources/`, `Assets/Samples/`, `Packages/`, and the ProjectSettings files (Dynamics, Graphics, PackageManager, TagManager).

**Also committed locally, not pushed** (2026-10-05, later):
- `7ba097b` "Glass slide slicing: pinch the tab and move it through the brain": `LeapSliceSlide.cs`, `GlassSlide.shader`, `LeapBrainManipulator`/`LeapHandRenderer`/`LeapMenuInteractor`, `PresentationMenu` (Slice toggle; also the user's `showSeekBar`), `SceneBuilds.cs` (pre-build shader include).
- `129237b` "V2 card and block poses (Presentation All Flat), card overlay attempt": `NeuronalLossSequence` card pose, `Presentation All Flat.unity` (V2 values only), `CardOverlay.cs`, `OverlayCard.shader`, one-line snapshot guards in six volume scripts, `SceneBuilds.cs`.

**Pushed 2026-10-05 evening**: `origin/brick_L0` = `4802b78`; that push also carried `121130c`, `7ba097b`, `129237b`, `3296754` (glass slide follows one hand), `c07f99f` (first magnifying lens) and `4802b78` (slice pinch stop, Slice off keeps the cut).

**Uncommitted (2026-10-05 night)**, the "Lens mode, IIP slide and V5" work (§6a):
- New: `Interaction/IipSectionOverlay.cs`, `Interaction/IipSlidePanel.cs`, `SRD/BrickMaskOverlay.cs`, `Rendering/BrickMask.shader`, `Rendering/OverlayText.shader`, `Rendering/SlideSection.shader`, `Editor/CreateV5Timeline.cs` (+ `.meta` files).
- Changed: `LeapLens.cs` + `Lens.shader` (crosshair + box), `LeapBrainManipulator.cs`, `BrickVolumeLoader.cs` (`RaycastTissue`, CPU mask copy, `VolumeMm`/`VoxelMm`, overlays), `NeuronalLossSequence.cs` (`cutToSection`), `PresentationMenu.cs` (V5, `holdAtEnd`, `AddVersion`, `ConfigureV5`), `ModelMoveController.cs` (gives the wheel / + / − to the lens), `SceneBuilds.cs` (always-include `Brain/Lens`, `Brain/OverlayText`, `Brain/BrickMask`, `Brain/SlideSection`), `Presentation All.unity` and `Presentation All Flat.unity` (`Timeline V5`; the Flat scene also has older uncommitted changes of the user's).
- Don't commit: `StreamingAssets/Bricks/hb02_vessels/` (data, gotcha 26), `Assets/Screenshots/`.

**Still uncommitted** (left out on purpose): `Assets/Screenshots/` (28 MB, don't commit), `Assets/Scenes/Testing.unity`, `SRDProjectSettings.asset`, `BrainVolumeSRD.slnx`, this file.

**Was uncommitted before `121130c`** (after `8bfdf0d`; all compiled and run in the Editor, see §6a):
- New:
  - `Assets/Scenes/Presentation All.unity`
  - `Assets/Scripts/UI/PresentationMenu.cs`
  - `Assets/Scripts/Interaction/LeapBrainManipulator.cs`, `LeapHandRenderer.cs`, `LeapMenuInteractor.cs`
  - `Assets/Scripts/Rendering/HandOverlay.shader` (+ `.meta` files)
- Changed:
  - `NeuronalLossSequence.cs`: standby, `PlaceAtStart`, `ResetToStart`/`HideAll`, hides split brains before Prepare.
  - `TimelineTransportUI.cs`: `SetTimeline`, rounded corners, `MakeIcon` internal, `PointerCaptured` settable.
  - `UiKit.cs`: `Rounded`/`RoundedSprite`.
  - `AxonRepairTimeline.cs`: restores the brain's opacity when not Ready.
  - `SceneBuilds.cs`: Create/Build Presentation All.
  - `GraphicsSettings.asset`: `Brain/HandOverlay` always included.
- Ultraleap packages: `Packages/manifest.json`, `packages-lock.json`, `ProjectSettings/PackageManagerSettings.asset` (OpenUPM scoped registry). Changed by the user when importing the package.
- `Assets/Samples/` (Ultraleap samples) and `Assets/Resources/` (Ultraleap Settings). Imported by the user; decide whether to commit them, since nothing uses the samples.
- `Assets/Screenshots/`: the UnityMCP screenshots. **Don't commit**; consider adding it to `.gitignore`.
- `BrainVolumeSRD.slnx` (regenerated) and this file.

**Builds on disk**:
- `Builds/Axon Damage Repair V4`
- `Builds/Presentation All` (2026-10-03: 0 errors, 46,454 MB, 222 s; contains `LeapC.dll`; **not run yet**)
- plus V1-V3 as below.

**External copy**: `E:\unity` (§6a), menu but no Ultraleap.

**History.** Earlier pushes (2026-10-02):
- `25eb342`: bricked-Brain timeline, start pose, straight block path, `TimelineRecorder`.
- `2d55ea2`: `NeuronalLossScene_DualScreen` + `DualScreenView` (with an R reset that was then removed).
- `a10a04a` "Reset button": removes that R reset again; `DualScreenView` now only routes the second camera.
- `589ac1d`: V1-V3 scenes, the timeline options, `TimelineDensityOverlay`, `NpzDensityVolume` (visibility, StreamingAssets lookup, no-affine voxel size), `SceneBuilds.cs`, Always Included Shaders, and the user's pending work (AstrocyteDensity split view, brick tissue mask + VRAM fallback, `ModelMoveController` R reset, earlier handover edits). `.gitignore` now excludes `Builds/`, `Build_V1/`, `npz_files/` and `StreamingAssets/Npz/*.npz`.

Then, after `589ac1d` (committed in `8bfdf0d`):
- V1: 0.3 s pause before slice back, slice and slice back 5.5 s each, BFI three-way split, Combine/Return ending.
- `NpzDensityVolume`: NIfTI reader, `StreamingAssets/Nifti` lookup.
- `NeuronalLossSequence`: `splitThird`, `splitScale`, split brains merging on Combine.
- `SceneBuilds.cs`: `AddBfiToV1`.
- `.gitignore`: `bfi/` and `StreamingAssets/Nifti/*.nii`.
- `StreamingAssets/Nifti.meta`.
- V4 (`Axon Damage Repair V4`, `AxonDamageRepairVolume`, `AxonRepairTimeline`, `Brain/AxonDamageRepair`).

**Older builds** (gitignored, all succeeded): `Builds/Nissl and Labels V1` (latest V1 with BFI + ending), `Builds/NeuronalLoss Display V2`, `Builds/NeuronalLoss Fib Astrocytes V3` (latest V3), ~46 GB each. `Build_V1/` is an older, user-made build folder.

**Compile / run status**: all scripts compile without errors (2026-10-04; the only warning is CS0618 from the Ultraleap samples). Every V1-V4 step was played in the Editor and checked with screenshots. So were the `Presentation All` switching and the menu bar; the hand gestures were checked with simulated hands. Start/end poses were checked numerically (end pose = start pose for V1 and V3). The 2026-10-02 unknowns about `NeuronalLossScene` (straight path, start pose, recorder) have been superseded: the timeline has run in the Editor many times since. The MP4 recorder (F10) has still not been tried by the user in these scenes.

**Stash**: `stash@{0}` "brick_L0 Unity-generated slnx + packages-lock before merging neuro_version" — regenerated files, normally safe to drop.
**Backup**: 16 `.meta` files Unity had regenerated on brick_L0 before the merge are in the Claude scratchpad (`brick_L0_untracked_backup`); the tracked versions from neuro_version replaced them. Not needed unless a GUID problem shows up.

---

## 10. Open issues and suggested next steps

1. **See an exe on the real SR display.** Close the Unity Editor and run `Builds/Presentation All/Presentation All.exe` (it contains V1-V4), or one of the single-scene exes. Check that the brain appears, the menu works (1-4 / click) and the hands are tracked. If it stays black: read `Player.log` (§7), then switch the graphics API to Direct3D 11 (§8.16) and rebuild.
1a. **Try the hand control with real hands** (§6a).
   - Does a grab feel right (`grabOn` 0.7 / `grabOff` 0.45)? Are the gains comfortable (`moveGain` 1.5, `rotateGain` 1.6)?
   - Do the drawn hands line up with the real ones? If not, adjust `deviceMetres` / `deviceEuler` on `HandControl` to the device's real position (assumed: flat on the desk, 20 cm in front of the display's bottom edge, 3 cm below it).
   - Does point + pinch hit the menu buttons?
   - Open choice: `requireNearBrain` (grab only at the brain) vs. grab anywhere (current).
1b. **Hands under V1's split brains**: the hands are drawn after the bricked Brain, so V1's FusedVolume / BFI (drawn later) can cover them. If it shows, draw the hands from the last volume's `Drawn` event, or from a camera event after all volumes.
1c. **Next hand features**: the slicer is done as the glass slide (§6a); region labels on pointing are still open.
1d. **Try the glass slide** in Presentation All / Flat: Slice on (the slide follows the hand that pressed it), move the hand to cut, pinch to stop / pinch again to move on, Slice off (the cut stays), R (removes it). Check the glass look and the Console for `Brain/GlassSlide` errors. Tune `gain`, `glassTint`/`sheen`. Open choice for the user: a front-to-back rail instead of left-right, or "Option 2" (the slide tilts with the hand: a plane clip in `SRDBrickRaymarch`/`FusedRaymarch`/`DensityVolume`, `ClipPlane` on `ISliceableVolume`).
1e. **Cards covered by the brain** (§6a): first check whether `CardOverlay` ran in the user's Play run (log something in `LateUpdate`/`DrawAll`, check `Canvas.enabled` is false on `NeuronalLoss_Label`). If `onPostRender` is not after the brain for the FlatCamera, draw the cards from a `CommandBuffer` at `CameraEvent.AfterEverything`, or from the last volume's `Drawn`. If the fixed poses are enough for the user, consider reverting `CardOverlay`.
1f. **See V2's new card / block poses** in Presentation All Flat (reload the scene first). The SRD scene `Presentation All` still uses the old computed poses.
1g. **Play V5** in both Presentation All scenes (reload the scenes first: `Timeline V5` was written to the files on disk). Check: the turn ends on the left sagittal view; the cut stops on section 150 and the IIP slide shows on the cut face, lined up with the tissue (else adjust the fit on `IipSectionOverlay`: `scale`, `angleDeg`, `offset`); the square lifts out at the user's mark (upper frontal cortex; move `V5 Panel Start` if not) and its picture is **not upside down** (the tiles are copied into the texture with `GL.LoadPixelMatrix(0, w, h, 0)` + `Graphics.DrawTexture`, orientation not confirmed); the zoom reaches sharp 1 µm tiles. Keep hand-placed `V5 Panel Start` / `End` by creating them in the scene. Then the user will describe the rest of V5 and the magnification they want.
1h. **Lens mode**: try the crosshair on the brain (mouse and hand), stop / restart, the box view, the coordinates text and the IIP tiles in the box on section 150 (Lens on, press I). The box shows the slide as scanned, about 10° tilted against the brain.
1i. **IIP for more than one slide**: needs the section list (volume z → slide file; `SL_354` / `SE_1060` naming unknown) and the slide → volume transform from the alignment pipeline, or one fit per slide like §6a. Hand zoom (a gesture) for the lens is not done; mouse / keys only.
1j. **Vessels**: if wanted again, add `hb02_vessels` to the Brain's `overlayDatasets` and check the alignment ("same space", assumed from the bboxes) and the GPU cost.
2. **Second monitor** (user asked for "two screens" on 2026-10-02): turned off in V1-V3 because it covered the SRD (§8.14). Redo it with the Sony plugin's 2-D view, then re-enable `Display2Camera` or replace it.
3. **Commit the 2026-10-05 night work** when the user asks (§9; everything up to `4802b78` is pushed). Add `Assets/Screenshots/` and `StreamingAssets/Bricks/hb02_vessels/` to `.gitignore`.
3a. **Check the 2026-10-05 changes in Play mode / with real hands**: the one-hand turntable grab direction and speed (`yawDegreesPerMetre` 600), whether the two-hand twist feels the right way round, V2's bigger block (no overlap with the brain at `sideDistance` 0.25?), V3's spin speed (20°/s) and the "Fibrinogen" card width, menu deselect by touch (a double touch within `pressLockSeconds` could select + deselect).
4. Open user choices: subtitles for the V3 map cards; whether V1/V2 should also start centred (x 0, like V3); exact timings (V1's final turn was cut to 5 s to fit the ending in 30 s).
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
| `Presentation All`: switching V1 → V3 → V4 → V2, standby hides the other versions' objects, the brain's opacity is restored after V4, brain-only start, menu look | Editor Play mode via UnityMCP, screenshots from the fixed viewer and close-ups of the bar (2026-10-03) |
| Menu by hand: `ButtonAt`, `Press`, hover + pointer look | Play mode, called directly (no hand); screenshot (2026-10-03, before touch replaced pinch). Real touching **not** tried |
| Grab move / turn / release and two-hand scale | Simulated hands (Ultraleap test hands with `GrabStrength`, written into the provider frame), numbers read back (§6a). Real hands **not** tried |
| Drawn 3-D hands (GhostHands, on top of the brain, both hands shaded, forearm fade) | Screenshots with `previewTestHands` (test hands in the desktop pose) |
| Ultraleap device connects in Unity | `LeapServiceProvider.IsConnected()` true in Play mode (2026-10-03); service `UltraleapTracking` running |
| `Presentation All` exe | Built (0 errors, 46,454 MB, `LeapC.dll` present); **not run** |
| `E:\unity` copy (no Ultraleap) | 126 data files identical in size; batch-mode Unity compiled it and imported all assets, exit 0. Not played |
| 2026-10-05: no manual translation, Y-only manual rotation, turntable hand grab, V2 block ×1.5, V3 "Fibrinogen" + spins, menu deselect | Edited on disk (UnityMCP down); the turntable sign worked out from the code (`AngleAxis(+θ, up)` turns the near face toward −x). Compiled by the Editor without errors; **not played by Claude**. The user's Play runs in between showed earlier steps working; these last ones are unconfirmed |
| Glass-slide slicer (Slice toggle, pinch tab, cut, glass shader) | Compiled outside Unity (scratch `dotnet build`, 0 errors); the user toggled Slice in Play mode (Editor.log). Pinch / cut / look **not confirmed**; shader not compiled by Claude |
| Cards kept in front of the brain (`CardOverlay`) | **Failed** in the user's V2 screenshot (block still over the card); cause not found |
| V2 card / block poses in Presentation All Flat | Values written to the scene YAML and compiled; **not seen in Play mode** |
| Slice pinch stop / Slice off keeps the cut (`4802b78`) | Compiled; not tried with real hands |
| IIP slide SL_354 = volume section 150 (scale 1.04, −9.63°, offset) | Offline fit of the server's 1500 px thumbnail against every L4 section (tissue-outline IoU 0.95, neighbours lower), overlay image checked by eye. Mirroring settled by anatomy (cerebellum left in both) |
| IIP server reachable, tile URLs | `curl`: thumbnail and a full-resolution tile (3.3 MB JPEG) with `%5B`/`%5D`; `Max-size` / `Tile-size` / `Resolutions` read from the server |
| Vessel bricks: alpha = mask, rgb = tint | BC3 blocks decoded offline (L1): colour constant (230, 40, 49), alpha 0 or ~255, 0.3 % of blocks non-empty |
| Lens crosshair / box / coordinates / surface picking, IIP tiles in the box, vessels, V5 square | Compiled (game + editor, scratch `dotnet build`); **not run by Claude**. The user saw V5's cut face and the square lifting out of it (screenshots 19:27 / 19:53, "nice"); start spots measured from those screenshots against section 150, a few mm accuracy |
| `Timeline V5` in both scenes | YAML written on disk (Editor not playing) and read back; not yet loaded by Unity |
| **Exes on the real SR display** | **Not confirmed.** The first runs were blank (two causes fixed); later desktop captures were black with only the HUD, possibly because the Editor held the display. The clean test (Editor closed) is outstanding |

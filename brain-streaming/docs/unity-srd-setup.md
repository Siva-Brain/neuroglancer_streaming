# Unity + Sony ELF-SR2 client

A second, high-performance client for the **unchanged** DGX streaming API. It
consumes exactly what the browser client consumes; the DGX/Zarr backend is not
modified. GPU volume rendering on the Windows RTX box; Sony SRD is an isolated,
optional layer.

---

## 1. Verified data flow (browser → API → DGX → render, mapped to Unity)

```
BROWSER (client/volume.html, reference)          UNITY (this project)
─────────────────────────────────────           ─────────────────────────────
GET /api/dataset/info                     ==>    BrainStreamClient.GetDatasetInfoAsync()
  extent_mm, levels[], baseline_chunks           -> DatasetInfo
enqueue baseline_chunks @ coarsest (L7)   ==>    RequestScheduler.EnqueueBaseline()
POST /api/view {position,forward,fov,...} ==>    BrainStreamClient.PostViewAsync(CameraState)
  -> {target_level, chunks[priority...]}         -> ViewResponse
GET /api/chunk/{id}?channels=0,3 (BVX2)   ==>    BrainStreamClient.GetChunkAsync() -> BrainChunk
parse BVX2, upload RG 3D texture          ==>    Brick.Create() -> Texture3D (RG16)
per-brick raymarch, back-to-front         ==>    BrainVolumeRenderer (OnRenderObject, DrawMeshNow)
Map cache (never re-download)             ==>    BrainChunkCache (LRU, 58 z-slabs)
```

**Contract (read from the working code, do not re-invent):**
- **Endpoints:** `/api/dataset/info`, `/api/view`, `/api/chunk/{id}?channels=`,
  `/api/chunks/request|cancel`, `/api/prefetch`, `/api/health`, `/api/stats`.
- **chunk_id:** `L{level}.{z}.{y}.{x}.{c}` (shard grid indices). The z index is
  the brick identity of the 58-slab volume.
- **BVX2 payload (50-byte header, little-endian):** `'BVX2' | u8 level | u8 nch |
  u16 pad | i32 z0,y0,x0 | u16 dz,dy,dx | f32[6] bbox_mm(xmin,ymin,zmin,xmax,
  ymax,zmax) | u8[dz*dy*dx*nch] voxels (z,y,x,c order)`.
- **Channels 0,3** = grayscale + tissue mask → `RG` texture. Shader:
  `tissue = 1 - R; density = G * tissue` (identical to the browser).
- **LOD:** L0 finest … L7 coarsest (from metadata). Stream coarse→fine.
- **Coordinate system:** world millimetres, brain at origin, data axes (z,y,x) →
  world (x,y,z) as delivered in the BVX2 bbox. Unity transform is explicit
  (`BrainCoordinateSystem`).

CPU does networking + scheduling + cache; **GPU does all the heavy rendering**
(3D textures + ray-march), per the brief.

---

## 2. Project setup

- Unity **2021.3 LTS+**, **Built-in Render Pipeline** (the shader targets built-in;
  a URP port is a small change — see notes). Platform: **Windows / x64**.
- Copy `unity/BrainVolumeSRD/Assets/` into a new project's `Assets/`.
- No external packages required (JSON via `JsonUtility`, networking via
  `UnityWebRequest`).

### Scene wiring (one scene, ~4 objects)
| GameObject | Components | Notes |
|------------|-----------|-------|
| `Main Camera` | `BrainCameraController` | orbit/pan/zoom/reset (R) |
| `BrainRoot` (empty at origin) | `BrainVolumeRenderer` | assign material auto-created by BrainApp; `brainRoot` = itself |
| `BrainApp` (empty) | `BrainApp` | set **serverUrl** = `http://<DGX_IP>:8090`; drag in Camera, BrainRoot, BrainVolumeRenderer, BrainCameraController, SonySRDManager |
| `SonySRD` (empty) | `SonySRDManager` | mode = NormalMonitor for milestone 1 |
| `HUD` (empty) | `BrainTelemetry` | drag in BrainApp + SonySRDManager |

Press **Play**. Expected: coarse whole brain appears (58 slabs), then sharpens
where you look; HUD shows connection, GPU, FPS, chunks, cache, LOD, bandwidth,
latency, Sony status.

> If the volume is **invisible or inside-out**: select the auto-created
> `Brain/Raymarch` material and flip **Cull** (Front↔Back). This is the one
> winding-dependent setting. If it's too faint/dense, tune **Density** (2–15).

---

## 3. Coordinate validation (don't guess)

`BrainCoordinateSystem` is the single DGX-mm → Unity transform: `unitsPerMm`
(default 0.01) and optional per-axis sign flips. Validate, don't assume:

1. The brain box should span ~`192 mm * unitsPerMm` in x & y and ~`9 mm` in z
   (a thin slab). At `unitsPerMm=0.01` → ~1.92 × 1.92 × 0.09 units.
2. Orbit: the coronal face (the 192×192 plane) should read as a brain section
   (cortex outline, midline). If **left/right looks mirrored vs. a known
   landmark**, set `flipX` — this is the only correct use of a flip (it mirrors).
3. Prefer rotating `BrainRoot` (never mirrors) to stand the brain upright rather
   than flipping axes.

---

## 4. Sony ELF-SR2 integration (after milestone 1 works on a monitor)

Sony code is isolated behind `ISonySRDAdapter`; the app runs without the SDK.

1. Install the official **Spatial Reality Display SDK for Unity** on the Windows
   machine (with the ELF-SR2 connected), import its package.
2. Player Settings → Scripting Define Symbols → add **`SONY_SRD_SDK`**. This
   compiles `SonySRDAdapter` (guarded) against the SDK (`SRD.Core.SRDManager` —
   adjust the type/property names to your SDK version if needed).
3. Drag the SDK's **SRDisplayManager** prefab into the scene and assign it to
   `SonySRDManager.srDisplayManagerObject`. Set `SonySRDManager.mode =
   SonyElfSr2`.
4. Point the SRDisplayManager's source camera at the same `Main Camera`. **Do not
   hand-roll stereo** — the SDK renders the lightfield views itself.
5. Because `BrainVolumeRenderer` draws per rendering camera (`OnRenderObject`),
   the brain volume automatically appears in the SDK's SRD cameras — no
   Sony-specific rendering code. HUD shows `Sony SRD: AVAILABLE`.

Two run modes, switchable on `SonySRDManager`: `NORMAL_MONITOR` / `SONY_ELF_SR2`
(auto-falls back to normal if the display/SDK is absent).

---

## 5. Class map (networking ⟂ rendering ⟂ Sony)

| Layer | Files | Role |
|-------|-------|------|
| Networking | `BrainStreamClient`, `BrainChunk`, `BrainStreamRequest`(scheduler) | HTTP, BVX2 parse, prioritized fetch, cancel |
| Cache | `BrainChunkCache` | bounded LRU of GPU bricks (58 z-slabs) |
| Rendering | `BrainVolumeRenderer`, `BrainChunkRenderer`(Brick), `BrainCoordinateSystem`, `BrainCameraController`, `BrainRaymarch.shader` | Texture3D + procedural ray-march, transform, camera |
| Sony | `SonySRDManager`, `SonySRDAdapter` | isolated display integration |
| Debug | `BrainTelemetry` | overlay |
| Orchestrator | `BrainApp` | wires it all; boot + per-frame /view |

---

## 6. First milestone (do this order)

1. DGX server running (`app_zarr.py`, port 8090). 2. Unity Play on a normal
monitor → brain visible + navigable + progressively refining + bounded cache +
camera-driven /view (watch DGX logs). **Then** enable Sony and see the same
brain as a spatial 3D object on the ELF-SR2.

## 6b. Merged block-volume path (default; low-jitter SRD)

`BrainApp.useBlockVolume` (default **on**) switches from ~58 per-slab bricks per block
to **one merged volume per block** — the fix for SRD eye-move jitter on the RTX 5060.
The DGX assembles each block once (`GET /api/block_volume`), tissue-cropped, x/y-capped
at `GPU_MAX_XY` (512), with colour+opacity baked into RGBA and a tiny occupancy volume
appended (payload = **BVX3**, see `docs/protocol` / `streaming/view.py`). The client draws
**one raymarched box per block** (`Brain/BlockRaymarch` shader) with occupancy-gated
empty-space skipping. Level is still camera-distance driven: the throttled `/view` loop
now only picks each block's level and refetches its whole volume (double-buffered) when the
level changes. Turn the toggle **off** to fall back to the original per-brick path for A/B.
Both shaders are auto-created by `BrainApp` (`Shader.Find`), so no manual material wiring.

## 7. Notes / risks (I could not run the Unity editor here)
- Merged path uses `TextureFormat.RGBA32` volumes (~2.4 GB for 5 uncropped blocks; tissue
  crop trims this). If VRAM is tight on the 8 GB 5060, lower `--gpu-max-xy` on the server
  or reduce `blockSteps` on `BrainVolumeRenderer`.
- `TextureFormat.RG16` (2×uint8) for `Texture3D` is supported on desktop DX11/RTX;
  if a platform rejects it, switch to `R8`+two textures or `RGBA32` (4× memory).
- Built-in RP shader. For **URP**, wrap the pass in URP tags and replace
  `UnityObjectToClipPos`/`tex3Dlod` with URP equivalents; the raymarch math is
  identical.
- `Cull` is a material toggle (see §2) — the one winding-dependent knob.
- Async uses `UnityWebRequest` on the main thread (`await Task.Yield()`), so no
  threading marshaling issues.

# Brain 2 on Sony SRD — offline bricking plan (no server, all local, Unity Built-in RP)

**Goal:** view Brain 2 (`hb02_fused.zarr`, RGB Nissl @ 8 µm) on the Sony Spatial
Reality Display from a **single Windows machine** — no FastAPI, no HTTP — reaching
**L0 where you zoom in** on a **24 GB** GPU.

## Decisions (locked)

- **Fully offline pre-brick. No runtime zarr/blosc in Unity.** A one-time Python
  tool decodes the zarr and writes GPU-ready bricks + an index. Unity only reads
  bricks — no `libblosc`, no C# sharding reader, no decode on the hot path.
- **Brick format = a GPU block-compressed format** (zero runtime decode, **1 byte/
  voxel** = 4× less VRAM than RGBA32, L0 fits on disk ~136 GB vs ~545 GB raw).
  - **P0 confirmed BC7 `Texture3D` works** on the SRD box (incl. 1024×1024×485).
  - **BUT no BC7 encoder exists on the build box** (no pip/CLI; BC7 in Python is
    scarce). So **P1 ships BC3/DXT5** (quicktex) — same 1 B/voxel, universally
    supported, real alpha, validated via `texture2ddecoder` round-trip. **BC7 is a
    drop-in re-encode later** (the Unity loader reads the format from the index).
  - Encode is CPU (quicktex) but **parallelized across cores** — ~27× on 32 jobs,
    scales to this box's 256, so even L0 bakes in minutes. A CuPy GPU BC-encoder
    (or a native BC7 encoder) is the later perf/quality upgrade.
- **Render pipeline = Built-in RP** (project already is; a volume raymarch wants
  direct control, URP adds plumbing with no benefit here).
- **Color space = Linear**, BC7 authored sRGB — this, not the RP, governs Nissl
  colour fidelity.

## What we can get on 24 GB (verdict)

z is never downsampled (485 slabs at every level), so finer levels stay heavy.
BC7 = 1 B/voxel.

| Level | dims (z,y,x) | VRAM (BC7) | disk (BC7) | role |
|------|---------------|-----------|-----------|------|
| L4 | 485×776×1416 | 0.5 GB | 0.3 GB | single `Texture3D` (fits ≤2048, no tiling) |
| **L3** | 485×1552×2832 | **2.1 GB** | ~2 GB | **pin as base overview** |
| L2 | 485×3104×5664 | 8.5 GB | 8.5 GB | optional pin, or stream |
| L1 | 485×6208×11328 | 34 GB | 34 GB | stream on zoom |
| **L0** | 485×12416×22656 | 136 GB | 136 GB | **stream on zoom → true 8 µm** |

**Plan:** pin **L3 (2 GB)** always-on, keep a **~14 GB LRU** for streamed L1/L0
bricks → full L0 at the focus, whole-brain context behind it. (Can pin L2 instead
for a sharper base if VRAM headroom allows.) Total disk to copy to the SRD box
(BC7): **~181 GB**. Without L0: ~45 GB (finest zoom = L1, 16 µm).

## Two hard limits, both handled by bricking

1. **Max single 3D-texture dimension (~2048).** L3 x=2832, L2 x=5664… exceed it,
   so every level ≥ L3 must be **tiled** regardless of VRAM.
2. **VRAM capacity.** Screen-space LOD means a view only needs screen-resolution
   bricks, so VRAM scales with the SRD's per-view resolution, not the dataset.

## Brick scheme

- XY tiles with core size ≤ **1024** (safe under 2048), **full Z**, **1-voxel
  apron** overlap per side for seamless trilinear filtering across brick borders.
- Each brick → one BC7 `Texture3D`. Index records core origin/size, apron, the
  local-mm bbox of the **core** (for placement), level, and voxel size.
- Alpha / empty space: Nissl background is **white** → alpha = departure-from-white
  (or white-cutout in the shader, matching the existing `FusedVolumeLoader`
  convention) + occupancy skip so empty bricks/voxels cost nothing.

## Components & phases

- **P0 — verify + skeleton** *(this commit)*
  - `Assets/Scripts/SRD/BC7Probe.cs` — logs device + whether BC7 (and RGBA32)
    `Texture3D` of 256/512/1024/2048 actually create on the SRD box.
  - `server/tools/prebrick_srd.py` — offline bricker skeleton: tiles a level into
    bricks (+apron) and writes raw RGB(A) bricks + `index.json` now; BC7 is a
    marked hook pending the P0 result.
  - L4 whole-brain already works today via `zarr_level_to_raw.py` +
    `FusedVolumeLoader` — use it to see the brain in the SRD immediately.
- **P1 — block encode** *(done)* BC3/DXT5 wired into the bricker (per z-slice,
  parallel pool), XY padded to mult-of-4, white-departure alpha, `tex_format` +
  `stored`/`core`/`apron` in the index; validated via BC3 decode round-trip.
  `--format bc3|raw` (bc7 stub), `--bc-level`, `--jobs`.
- **P2 — brick loader (Unity)** load the L3 brick set, place by index, raymarch
  the whole brain on the SRD (reuse `BrainBlockRaymarch.shader` as the model).
- **P3 — LOD streaming** screen-space LOD + frustum cull + background brick load +
  LRU VRAM cache (~14 GB, L3 pinned) → L0 on zoom.
- **P4 — polish** apron seams, empty-space skip, SRD per-view perf (steps, native
  per-view res), Linear colour check.

## Risks

- **BC7 `Texture3D` support in Unity** (P0 gate). If unsupported → coarse levels
  raw, and L0 would need a different compressed path (e.g. KTX2/ASTC) or stays out.
- **SRD multi-view cost** — raymarch runs once per light-field view; lean on
  empty-space skipping (lots of white) + modest step counts + native per-view res.
- **Disk** — ~181 GB on the Windows box for L0.
- **Depth tax** — full Z (485) at L0 means a zoomed column brick 512×512×485 ≈
  0.5 GB (BC7 ≈ 0.12 GB); budget the LRU accordingly.

## File manifest (this workstream)

- `server/tools/prebrick_srd.py` — offline bricker (build tool, not a server).
- `server/tools/zarr_level_to_raw.py` — existing single-volume exporter (L4/L5/L7).
- `unity/.../Assets/Scripts/SRD/BC7Probe.cs` — P0 capability probe.
- `docs/brain2-srd-bricking-plan.md` — this doc.


python3 server/tools/prebrick_srd.py --levels 4,3 --format bc3 --brick-xy 1024 --jobs 64
# → Assets/StreamingAssets/Bricks/hb02_fused/{index.json, L4/*.bc3, L3/*.bc3}   (~1-2 min)
# Dataset: `580_ALL_3d.zarr` — verified inspection

Source (read-only, source of truth — never modified, never copied whole):
```
http://3dstrokeviewer.humanbrain.in:8056/zarr_files/580_ALL_3d.zarr/
```
Inspected 2026-09-29 by fetching real metadata + decoding one real shard. This
file separates **what was verified**, **what was inferred**, **what is unknown**.

---

## ✅ VERIFIED (read directly from the server)

### Format & structure
| Property | Value |
|----------|-------|
| Zarr format | **v3** (`zarr_format: 3`) |
| Root node | `group`, OME metadata `ome.version: 0.5` (OME-Zarr / NGFF 0.5) |
| Multiscale levels | **8**, paths `0`–`7`, all `node_type: array` |
| Data type | **uint8** (all levels) |
| Dimensions | **4D**, `dimension_names: [z, y, x, c]` (this exact order) |
| Channels | **c = 4** |
| Axes (OME) | z=space/meter, y=space/meter, x=space/meter, c=channel |

### Per-level shape `[z, y, x, c]` and in-plane voxel size
z is **457 at every level** (the pyramid downsamples **x/y only**, never z).

| Level | shape `[z,y,x,c]` | xy voxel | z voxel | shards* |
|------:|-------------------|---------:|--------:|--------:|
| **0** | `[457, 24000, 24000, 4]` | **8 µm**  | 20 µm | 2088 |
| 1 | `[457, 12000, 12000, 4]` | 16 µm | 20 µm | 522 |
| 2 | `[457, 6000, 6000, 4]` | 32 µm | 20 µm | 232 |
| 3 | `[457, 3000, 3000, 4]` | 64 µm | 20 µm | 58 |
| 4 | `[457, 1500, 1500, 4]` | 128 µm | 20 µm | 58 |
| 5 | `[457, 750, 750, 4]` | 256 µm | 20 µm | 58 |
| 6 | `[457, 375, 375, 4]` | 512 µm | 20 µm | 58 |
| **7** | `[457, 187, 187, 4]` | **1024 µm** | 20 µm | 58 |

\* shards = `ceil(457/8) × ceil(y/4096) × ceil(x/4096) × 1` = `58 × … `.

**Resolution direction (determined, not assumed):** OME `scale` for x/y grows
with level (`8e-6 → 1.024e-3 m`), so **level 0 = FINEST, level 7 = COARSEST**.
Progressive loading therefore goes **7 → … → 0** (coarse first).

### Physical extent (from OME `coordinateTransformations.scale`, in metres)
- In-plane FOV ≈ **192 mm × 192 mm** (constant across levels: size halves as
  voxel doubles). Depth (z) = 457 × 20 µm = **9.14 mm**.
- z scale constant `2e-5 m` (20 µm) all levels; translation `[0,0,0,0]`.
- Root note: `580_ALL` = five section-series volumes forced into **one shared z
  space** at contiguous 20 µm; depth is *not* anatomically true (documented in
  the server's own `z_spacing_model` attribute). `biosample_id: 580`,
  `stain: unknown`.

### Chunking & codecs (identical on every level)
| Property | Value |
|----------|-------|
| Chunk grid | `regular`, **chunk_shape `[8, 4096, 4096, 4]`** (this is the *shard*) |
| Sharding | codec **`sharding_indexed`**, inner **chunk_shape `[8, 256, 256, 4]`** |
| Inner codecs | `bytes` → **`blosc`** (`cname=zstd, clevel=3, shuffle=bitshuffle, typesize=1`) |
| Shard index | `index_codecs: [bytes(LE), crc32c]`, **`index_location: end`** |
| Chunk key encoding | `default`, **separator `/`** → key = `c/{z}/{y}/{x}/{c}` |
| `fill_value` | 0 |

**The `c` in a chunk URL is the Zarr-v3 chunk-key prefix (literally "chunk"),
NOT the channel.** The 4 numbers after it are the chunk-grid indices in
`[z, y, x, c]` order. So the example:
```
.../5/c/19/0/0/0   →  level 5, z-shard 19 (z-planes 152–159), y-shard 0, x-shard 0, c-shard 0
```
covers z 152–159, the full 750×750 x/y plane, all 4 channels.

### Direct chunk access — measured (this box → server)
| Metric | Value |
|--------|-------|
| Root `zarr.json` fetch | 200, 4133 B, ~0.10 s |
| Level `zarr.json` fetch | 200, ~0.03 s |
| HTTP **range requests** | **supported** (`Accept-Ranges: bytes`, `206 Partial Content`) |
| One shard `5/c/19/0/0/0` | 200, **2,803,613 B (2.8 MB)**, ~0.10 s |
| Shard decoded (9 inner blosc chunks) | → `(8, 750, 750, 4)` uint8 = **18.87 MB** (~6.7:1) |
| Decompress + assemble | **0.022 s** (numcodecs blosc, CPU) |

Decoded content = **a real brain section** (cortical folding, white matter,
brainstem visible — see `scratchpad/diag_L5_z156_rgb.png`). Values:
- **ch0/1/2 = RGB brightfield** (background ~240–250, tissue darker; 100+ levels).
- **ch3 = binary tissue mask / alpha** (only values {0,255}; corr 0.936 with
  `255 − mean(RGB)`, i.e. 255 where tissue is). Use as opacity.

---

## 🔶 INFERRED (consistent with evidence, not stated by the server)
- **ch0/1/2 = R,G,B** in that order (assumed; OME gives no per-channel names).
- ch3 is an **alpha/foreground mask** rather than a labelled segmentation
  (binary, tissue-shaped). Not confirmed to be semantic.
- Downsampling method per OME `type: "nearest"` for the multiscale set.
- The full FOV includes large white margins (typical whole-slide background).

## ❓ UNKNOWN / to confirm later
- Exact per-channel semantics / stain identity (`stain: unknown`).
- Which z-planes contain real tissue vs blank (457 forced planes across 5
  volumes → some may be near-empty). Needs a cheap per-shard occupancy scan.
- Whether all x/y shards at fine levels (0–2) are populated or sparse.
- Real-world anatomical registration (z is explicitly *not* anatomically true).

---

## ⚠️ Tooling caveat (affects the DGX reader choice)
`tensorstore`'s `zarr3` driver over its `http` kvstore **hung on `.result()`**
here (even opening the 187² level 7; the same `zarr.json` returns to `curl` in
27 ms) — timed out at 35 s and 120 s. Root cause not yet isolated (Python 3.8
box, no proxy). Since the server supports range requests and the sharding format
is fully specified above, the DGX reader uses a **direct manual sharded reader**
(`requests` range GETs + `numcodecs` blosc + crc32c), which is proven working at
22 ms/shard. tensorstore remains a future option once the hang is understood.

## Reader recipe (what the DGX does per chunk) — validated
1. `GET .../{L}/c/{z}/{y}/{x}/{c}` (optionally ranged).
2. Read trailing `256*16 + 4` bytes = shard index (`u64 offset,u64 nbytes` ×256,
   C-order over inner grid `[1,16,16,1]`) + crc32c.
3. For each present inner entry (`offset != 2^64−1`): slice `shard[off:off+n]`,
   `blosc.decode` → `[8,256,256,4]` uint8, place into the level block, cropping
   at array edges.
4. Result is `[8, ≤4096, ≤4096, 4]` uint8 for that shard.

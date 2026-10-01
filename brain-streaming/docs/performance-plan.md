# Streaming performance plan — local DDN + VRAM-resident coarse levels

Status: **planned, not yet implemented** (as of 2026-10-01).
Owner: siva. Branch at time of writing: `FirstBaseVersion`.

This documents three throughput/latency improvements for the DGX Zarr streaming
server, discussed and sized against the live dataset. The **glass Nissl
preprocessing** referenced throughout is already implemented (see
[architecture.md](architecture.md) and the `--glass` path in
`server/app_zarr.py`); the two items below build on it.

---

## Where the time goes today (the bottleneck)

The server is **latency-bound on upstream chunk fetches**, not on compute:

- `~170 ms` Zarr HTTP latency per chunk (`app_zarr.py`, `/api/view` comment).
- Fine-level ROI = `~100 shards × ~350 ms` round-trip (`datasource/http_zarr.py`).
- First L0 ROI is `~1 GB / ~30 s` ≈ **33 MB/s**, which is the **HTTP server's
  egress**, not DDN bandwidth.
- GPU glass processing (normalize + denoise + gradient) is `~1–4 ms/brick` —
  negligible by comparison.

The data already physically lives on **DDN**; the port-8056 HTTP server reads
DDN and re-serves it. So today's path is:

```
DGX app  ->  network  ->  HTTP Zarr server (:8056)  ->  DDN
```

Both items below shorten or remove that path.

---

## Item 1 — Local DDN filesystem source (`LocalZarrBrainSource`)

**Idea:** mount DDN on the DGX and read the sharded zarr as local files
(`pread` at shard offsets) instead of HTTP GET/Range. Collapses the path to:

```
DGX app  ->  DDN
```

**Expected gain**

| | HTTP now | Local DDN |
|---|---|---|
| Per-chunk open/read latency | ~170–350 ms | sub-ms to a few ms |
| Big-ROI / first-L0 throughput | ~33 MB/s (HTTP egress) | DDN/NVMe-bound, GB/s class |
| Repeat/overlapping reads | app LRU only | + OS page cache in the DGX's 2 TB RAM |
| Range reads | HTTP Range per inner chunk | `pread` at offset (no TLS/conn/queue) |

Rough: first-ROI load ~30 s → low single-digit seconds; fine ROI ~**5–20×**
faster wall-clock. Decode path unchanged (same sharded bytes).

**Work**

- Add `datasource/local_zarr.py` : `LocalZarrBrainSource` mirroring
  `HttpZarrBrainSource` — same shard-index parsing and decode, but `pread`
  instead of `requests.get`/Range. (A `LocalZarrBrainSource` reportedly existed
  for the fused brain on another branch — reuse that pattern.)
- Add a `--zarr-local /ddn/path/...` option (or auto-select `file://` vs
  `http://` from the root string) in `app_zarr.py`.
- Keep `HttpZarrBrainSource` as the fallback so we can A/B the same blocks.

**Caveat:** the win assumes this DGX can mount the DDN export where the zarr
lives. If DDN is remote to *this* host, we only save the HTTP-server process
overhead (smaller gain).

---

## Item 2 — VRAM-resident coarse levels (preload L4 into GPU memory) — ✅ IMPLEMENTED

**Status: shipped.** `--resident-level 4` (default 4; 0=off). At brain activation,
`_build_resident` loads L4..coarsest fully into A100 VRAM per block as serve-ready
bricks (channel-select → resample → glass), round-robined across GPUs; `/api/chunk`
serves those levels by slicing VRAM (`_serve_resident`, `X-Chunk-Status: resident`).
`--min-level` defaults to 4 so nothing finer than the resident level is served.
Measured on hb02: L4 brick **2.1 ms from VRAM** vs ~10 ms warm / ~26 ms cold DDN;
L4–L7 = 2.12 GB resident. Build reuses `ChunkManager.process`/`pack_brick` so a
cached slab is byte-identical to a freshly served one. The notes below are the
original design.

### Original design

**Idea:** at startup, load every block's coarse level(s) **fully glass-processed
into persistent CuPy (GPU) arrays**, resident for the server's life. Serve any
L4-and-coarser request by **slicing GPU memory** — no fetch, no decode, and the
glass cost is paid once instead of per request.

### Does it fit? Yes — measured against the live dataset

Pyramid halves x/y per level; **z is constant (457 slices)**. Whole-brain
footprint (sum of all 5 blocks), uint8:

| Level | G-voxels | 1 ch | 2 ch | 3 ch (glass) | Residency |
|------:|---------:|-----:|-----:|-------------:|-----------|
| L4 | 4.41 | 4.4 GB | 8.8 GB | **13.2 GB** | fits in **one** A100 |
| L3 | 17.65 | 17.7 GB | 35.3 GB | 53.0 GB | fits in one A100 |
| L2 | 70.6 | 70.6 GB | 141 GB | 212 GB | needs ~3 GPUs |
| L0/L1 | 282 / 1130 | — | — | — | impossible (100s of TB) |

Hardware: **8× A100, ~80 GB each (~640 GB total VRAM)**. Full 3-channel glass
**L4 uses 13 GB of 80** on a single card. (L5/L6/L7 are tiny — keep them resident
too, essentially free.)

### Why it's a big win

1. **Kills per-chunk fetch latency for L4** — a request becomes a GPU slice
   (sub-ms to low-ms). Overview/navigation becomes instant.
2. **Glass preprocessing amortized** — `glass_pack` runs once at load for the
   whole volume, not per chunk per request.
3. **Cheap arbitrary access** — any ROI, re-center, or oblique slice at
   L4/coarser is just indexing GPU memory.

### Costs / what to watch

- **One-time load:** still read L4 (~4.4 G-voxels) from source once at startup.
  Over HTTP (~33 MB/s) that's minutes; over **local DDN (Item 1)** it's seconds.
  **These two items pair** — DDN makes warm-up cheap, VRAM makes steady-state
  instant. Hide it behind the existing warmup thread.
- **New bottleneck = GPU→client transfer.** Slicing is instant, but we still
  copy the slice to host and ship it over HTTP. A big L4 region is still hundreds
  of MB on the wire — unchanged from today; we've only removed the upstream fetch.
- **Multi-GPU residency.** Workers currently round-robin resample across all 8
  GPUs, but a resident volume lives on one card. Simplest fix: **replicate** the
  13 GB to each GPU (13×8 = 104 GB, trivial) so any worker slices its local copy —
  no cross-GPU traffic.

### Work

- Build resident arrays per block at startup (reuse `_compute_norm_windows`'s
  coarse-fetch + `glass_pack`), one CuPy array per (block, level) per GPU.
- Short-circuit `/api/chunk` and `/api/roi` for `level >= RESIDENT_LEVEL` to
  slice the resident array instead of going through `ChunkManager.get`.
- Gate behind `--resident-level 4` (0/off = current behaviour, full fallback).

### Recommended end state (hybrid)

- **L4 (+ L5/L6/L7) resident in VRAM, glass-processed** → instant overview/nav.
- **L0–L3 streamed on demand** (fine detail when zoomed into an ROI).
- **Zarr on local DDN** so both the one-time load and the fine-detail fetches
  are fast.

---

## Item 3 — Remaining glass-look polish (deferred, lower priority)

From the glass-pipeline work; not blocking the above.

- **Fused single overview volume** (`/api/block_volume` / BVX3): per-block
  normalization already removed most of the seam motivation, so lower value now.
- **Baked ambient occlusion:** the precomputed gradient "structure" shading
  already supplies most of the depth cue.
- **True empty-space skipping** (min/max octree): perf, not look; `min_level=4`
  + the tissue mask make it non-urgent.

---

## Suggested order

1. **Item 1 (local DDN)** — biggest, simplest win; unblocks Item 2's warm-up.
2. **Item 2 (resident L4)** — instant overview once DDN load is cheap.
3. **Item 3** — polish as needed.

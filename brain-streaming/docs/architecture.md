# Architecture

```
                         DGX A100
              ┌──────────────────────────┐
              │ Synthetic Brain Model     │  brain/synthetic_brain.py  (SWAP POINT)
              │          ↓                │
              │ Chunk / LOD Generator     │  brain/lod.py, brain/chunk.py
              │          ↓                │
              │ Brain Streaming Server    │  app.py  (FastAPI)
              └────────────┬─────────────┘
                           │  HTTP/JSON + binary 'BRN1'  (port 8000)
                           │  incremental, camera-driven
                           ▼
              ┌──────────────────────────┐
              │  Windows PC / laptop      │
              │  BrainStreamClient (net)  │  client/index.html  (Three.js today)
              │  BrainChunkCache          │  unity/… C# (next)
              │  BrainRenderer  ──────────┼──▶ normal display
              │  BrainCameraController    │  └▶ Sony SR Display SDK (optional)
              └────────────┬─────────────┘
                           ▼
                      Sony ELF-SR2
```

## Design principles

- **Networking is separate from rendering.** `BrainStreamClient` only produces
  `BrainChunk` objects and cache state; `BrainRenderer` only consumes them. The
  Sony ELF-SR2 path is a second renderer behind the same interface — the network
  layer never learns which display is used.
- **The DGX is the only machine that touches source data.** Zarr/Neuroglancer,
  reconstruction, chunking, and LOD all run on the DGX. Only meshes cross the
  network. Windows never mounts the DGX FS, never reads Zarr, never receives the
  full dataset.
- **Transport-agnostic protocol.** Message *shapes* (`VIEW_UPDATE`,
  `CHUNK_AVAILABLE`, `REQUEST_CHUNK`, `CANCEL_CHUNK`, `HEARTBEAT`) are defined
  independently of HTTP so WebSocket/QUIC/gRPC can be swapped later. See
  [protocol.md](protocol.md).
- **Synthetic today, real later.** Only `synthetic_brain.py` knows the geometry
  is fake. Chunking, LOD, serialization, prioritization, and both clients are
  reuse-ready for the real reconstruction. See the README "Replacing the
  synthetic brain" section.

## Streaming timeline (what you observe)

```
T0  request whole brain @ LOD0   → coarse faceted brain appears region-by-region
T1  POST /view (camera)          → visible regions upgrade to LOD1
T2  nearest visible regions      → upgrade to LOD2 (high detail)
    move camera                  → newly visible region prioritized & detailed
    revisit a region             → served from local cache (no re-download)
```

## Components

| Layer | File | Responsibility |
|-------|------|----------------|
| Synthetic model | `brain/synthetic_brain.py` | 8 procedural regions (swap point) |
| LOD + serialize | `brain/lod.py` | ellipsoid tessellation, `BRN1` binary |
| Catalog | `brain/chunk.py` | build all LODs; camera prioritization |
| Protocol | `streaming/protocol.py` | transport-agnostic message models |
| Server | `app.py` | HTTP endpoints, `--chunk-delay-ms` |
| Client (net) | `client/index.html` `BrainStreamClient` | fetch/parse, cache, cancel |
| Client (render) | `client/index.html` `BrainRenderer` | meshes, LOD replacement |
| Client (camera) | `client/index.html` `BrainCameraController` | `VIEW_UPDATE` loop |

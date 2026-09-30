# Streaming protocol (v1.0-http)

The protocol is defined by **message shapes**, not by the transport. Today they
ride on HTTP+JSON (metadata/camera) and raw binary (chunk payloads). The client
and server rendering/streaming logic depend only on these shapes, so HTTP can be
replaced by WebSocket / QUIC / gRPC by re-binding the same messages.

## Messages

| Logical message | Direction | HTTP binding (v1) |
|-----------------|-----------|-------------------|
| `VIEW_UPDATE`     | client → DGX | `POST /api/brain/view` (JSON `ViewRequest`) → `ViewResponse` |
| `REQUEST_CHUNK`   | client → DGX | `GET /api/brain/chunk/{chunk_id}?lod=N` → binary `BRN1` |
| `CHUNK_AVAILABLE` | DGX → client | HTTP response to `REQUEST_CHUNK`; also each entry of `ViewResponse.chunks` is an availability hint |
| `CANCEL_CHUNK`    | client-side  | abort the in-flight GET (HTTP has no server push; on WebSocket this becomes a real message) |
| `HEARTBEAT`       | client → DGX | `GET /api/health` |

Over HTTP the DGX cannot *push*, so the client polls `VIEW_UPDATE` (~3 Hz) and
pulls chunks. Under WebSocket/QUIC the same `CHUNK_AVAILABLE` becomes a true
server-push and `CANCEL_CHUNK` a real message — no change to `BrainChunk`,
`BrainChunkCache`, or `BrainRenderer`.

## Metadata

`GET /api/brain/info`
```json
{
  "name": "synthetic-hello-world-brain",
  "coordinate_frame": "+X right, +Y up, +Z forward (mm-ish)",
  "lods": [0, 1, 2], "max_lod": 2,
  "chunk_count": 8,
  "chunk_ids": ["left_hemisphere", "right_hemisphere", "..."],
  "bounds": {"min": [-88,-95,-88], "max": [88,83,32]}
}
```

`GET /api/brain/chunks` → `{"chunks": [ChunkMeta, ...]}`, each:
```json
{
  "chunk_id": "left_hemisphere",
  "center": [-42,25,0],
  "rotation": [0,0,0,1],          // quaternion xyzw (identity for now)
  "color": [0.90,0.72,0.75],      // flat region color, rgb 0..1
  "dependencies": [],             // e.g. brain_stem -> ["cerebellum"]
  "lods": {
    "0": {"bounds":{"min":[...],"max":[...]}, "vertex_count":63,  "triangle_count":96,  "bytes":1920},
    "1": {"...": "..."},
    "2": {"...": "..."}
  }
}
```

Per your spec, a chunk carries: `chunk_id`, LOD level(s), bounding box,
position (`center`), rotation, geometry (fetched separately as binary), and
dependencies.

## VIEW_UPDATE

`POST /api/brain/view`
```json
{
  "camera_position": [200,60,320],
  "camera_rotation": [0,0,0,1],     // quaternion xyzw
  "camera_forward":  [-0.4,-0.1,-0.9],
  "fov": 60,
  "viewport": {"width": 1280, "height": 720}
}
```
`camera_forward` is sent explicitly (derived from rotation client-side) so the
server stays math-light and transport-agnostic.

Response (`ViewResponse`):
```json
{ "chunks": [
  {"chunk_id":"right_hemisphere","target_lod":2,"visible":true,"distance":210.4,"priority":0},
  {"chunk_id":"...","target_lod":1,"visible":true,"distance":260.1,"priority":1},
  {"chunk_id":"...","target_lod":0,"visible":false,"distance":333.0,"priority":7}
]}
```
Ordering = importance. Prioritization rule (prototype): visible chunks first,
nearest-first; nearest few visible → top LOD; other visible → LOD1; hidden →
LOD0. See `server/brain/chunk.py::Catalog.prioritize`.

## Binary chunk payload — `BRN1`

Little-endian:

```
offset  type            field
0       char[4]         magic = 'BRN1'
4       uint32          vertex_count  V
8       uint32          index_count   I
12      float32[V*3]    positions (x,y,z)...
12+V*12 uint32[I]       triangle indices
```

Normals and color are **not** in the payload: the client computes vertex normals
locally and takes the flat color from `ChunkMeta.color`. This halves payload size
and keeps the parser trivial. Response headers `X-Chunk-Id`, `X-Chunk-Lod` echo
identity.

## Forward-compatibility (designed for, not implemented)

- **Compression / Draco**: negotiate via `Accept-Encoding` / `?encoding=draco`;
  add a codec byte after the magic. Client picks a decoder by header.
- **Binary packets / batching**: a WebSocket frame can carry `[header][BRN1...]`
  for many chunks; the `BRN1` body is unchanged.
- **Multiple clients**: server is stateless per request; `prioritize` takes the
  camera as input, so N clients = N independent view calls.
- **Predictive prefetch**: server may return chunks slightly outside the frustum
  in `ViewResponse` with lower priority — client already honors priority order.
- **GPU-friendly formats**: swap `BRN1` for a meshopt/quantized layout behind the
  same magic+version negotiation.

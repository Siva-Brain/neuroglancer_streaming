using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace BrainVolume
{
    // ---- JSON DTOs (match the DGX API exactly; parsed with JsonUtility) ------
    [Serializable] public class LevelDto {
        public int level; public int[] shape; public int[] grid;
        public int[] chunk_shape; public float xy_voxel_um;
    }
    [Serializable] public class DefaultTransformDto {
        public float[] translation; public float[] rotation_deg; public float[] scale;
    }
    // One block of the multi-block dataset (each is a physical brain slab).
    [Serializable] public class BlockInfo {
        public string block_id; public string brain; public string render;   // "gray_mask"|"rgb"
        public string name; public string dtype;
        public int channels; public int finest_level; public int coarsest_level;
        public int min_streamable_level; public float[] extent_mm;   // (z,y,x)
        public LevelDto[] levels; public string[] baseline_chunks;
        public DefaultTransformDto default_transform;
    }
    // /api/dataset/info -> { blocks:[...], <first-block mirror> }. We read `blocks`.
    [Serializable] public class DatasetInfo {
        public BlockInfo[] blocks;
        public string name; public float[] extent_mm;   // legacy top-level mirror (unused)
    }
    // /api/brains -> { brains:[{id,label,kind,render,blocks[]}], default }.
    [Serializable] public class BrainDto {
        public string id; public string label; public string kind; public string render;
        public string[] blocks;
    }
    [Serializable] public class BrainsResponse {
        public BrainDto[] brains; public string @default;   // @default: "default" is a C# keyword
    }
    // /api/transforms -> { space, transforms:{id:{...}}, list:[{block,matrix,...}] }.
    // JsonUtility can't parse the id-keyed dict, so we read the `list` array.
    [Serializable] public class BlockTransformDto {
        public string block; public float[] matrix;   // col-major 4x4, local mm -> RAS mm
        public float centerX; public float[] extent_mm;
    }
    [Serializable] public class TransformsResponse {
        public string space; public BlockTransformDto[] list;
    }
    [Serializable] public class SelectedChunkDto {
        public string chunk_id; public int level; public int[] coords;
        public float[] center_mm; public float distance; public bool visible; public int priority;
    }
    [Serializable] public class ViewResponse { public int target_level; public SelectedChunkDto[] chunks; }
    [Serializable] class ViewRequestDto {
        public float[] position; public float[] rotation; public float[] forward;
        public float fov; public int viewportWidth; public int viewportHeight;
        public string block;                 // which block this camera query is for
    }
    [Serializable] class ChunkIdsDto { public string[] chunk_ids; }

    /// <summary>
    /// Networking ONLY. Talks to the unchanged DGX streaming API and returns
    /// plain data. Knows nothing about rendering, caching, or Sony. Every per-block
    /// call carries the block id (?block= / "block" field) so one client serves all
    /// blocks. HTTP today; same shapes map to WebSocket later.
    /// </summary>
    public sealed class BrainStreamClient
    {
        public string BaseUrl { get; }
        public string Channels = "0,3";        // grayscale + tissue mask (matches browser)
        public bool Connected { get; private set; }
        public float LastLatencyMs { get; private set; }

        public BrainStreamClient(string baseUrl) { BaseUrl = baseUrl.TrimEnd('/'); }

        static string BrainQ(string brain) =>
            string.IsNullOrEmpty(brain) ? "" : "?brain=" + UnityWebRequest.EscapeURL(brain);

        /// <summary>GET /api/brains -> the selectable brains (id + label + blocks).</summary>
        public async Task<BrainsResponse> GetBrainsAsync()
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/brains");
            return txt != null ? JsonUtility.FromJson<BrainsResponse>(txt) : null;
        }

        public async Task<DatasetInfo> GetDatasetInfoAsync(string brain = null)
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/dataset/info{BrainQ(brain)}");
            Connected = txt != null;
            return txt != null ? JsonUtility.FromJson<DatasetInfo>(txt) : null;
        }

        public async Task<TransformsResponse> GetTransformsAsync(string brain = null)
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/transforms{BrainQ(brain)}");
            return txt != null ? JsonUtility.FromJson<TransformsResponse>(txt) : null;
        }

        public async Task<bool> HealthAsync()
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/health");
            Connected = txt != null;
            return Connected;
        }

        public async Task<ViewResponse> PostViewAsync(CameraState cam, string block)
        {
            var dto = new ViewRequestDto {
                position = new[] { cam.PositionMm.x, cam.PositionMm.y, cam.PositionMm.z },
                rotation = new[] { cam.Rotation.x, cam.Rotation.y, cam.Rotation.z, cam.Rotation.w },
                forward  = new[] { cam.ForwardMm.x, cam.ForwardMm.y, cam.ForwardMm.z },
                fov = cam.Fov, viewportWidth = cam.ViewportW, viewportHeight = cam.ViewportH,
                block = block,
            };
            var body = JsonUtility.ToJson(dto);
            var txt = await PostTextAsync($"{BaseUrl}/api/view", body);
            return txt != null ? JsonUtility.FromJson<ViewResponse>(txt) : null;
        }

        /// <summary>GET /api/block_volume -> one merged RGBA volume + occupancy (BVX3).</summary>
        public async Task<BlockVolumeData> GetBlockVolumeAsync(string block, int level, CancellationToken ct)
        {
            var url = $"{BaseUrl}/api/block_volume?block={block}&level={level}";
            float t0 = Time.realtimeSinceStartup;
            using var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) { if (ct.IsCancellationRequested) { req.Abort(); return null; } await Task.Yield(); }
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[BrainStreamClient] GET {url} FAILED: result={req.result} " +
                                 $"code={req.responseCode} error='{req.error}'");
                return null;
            }
            LastLatencyMs = (Time.realtimeSinceStartup - t0) * 1000f;
            return BlockVolumeData.TryParse(req.downloadHandler.data, out var v) ? v : null;
        }

        /// <summary>REQUEST_CHUNK for one block -> decoded BrainChunk. Cancellable.</summary>
        public async Task<BrainChunk> GetChunkAsync(string chunkId, string block, CancellationToken ct)
        {
            var url = $"{BaseUrl}/api/chunk/{chunkId}?channels={Channels}&block={block}";
            float t0 = Time.realtimeSinceStartup;
            using var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) { if (ct.IsCancellationRequested) { req.Abort(); return null; } await Task.Yield(); }
            if (req.result != UnityWebRequest.Result.Success) return null;
            LastLatencyMs = (Time.realtimeSinceStartup - t0) * 1000f;
            return BrainChunk.TryParse(chunkId, req.downloadHandler.data, out var c) ? c : null;
        }

        // ---- low-level helpers (main-thread async over UnityWebRequest) ------
        static async Task<string> GetTextAsync(string url)
        {
            using var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[BrainStreamClient] GET {url} FAILED: result={req.result} " +
                                 $"code={req.responseCode} error='{req.error}'");
                return null;
            }
            return req.downloadHandler.text;
        }

        static async Task<string> PostTextAsync(string url, string json)
        {
            using var req = new UnityWebRequest(url, "POST") {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[BrainStreamClient] POST {url} FAILED: result={req.result} " +
                                 $"code={req.responseCode} error='{req.error}'");
                return null;
            }
            return req.downloadHandler.text;
        }
    }
}

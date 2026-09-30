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
    [Serializable] public class DatasetInfo {
        public string name; public string dtype; public string[] axes;
        public int channels; public int finest_level; public int coarsest_level;
        public int min_streamable_level; public float[] extent_mm;   // (z,y,x)
        public LevelDto[] levels; public string[] baseline_chunks;
    }
    [Serializable] public class SelectedChunkDto {
        public string chunk_id; public int level; public int[] coords;
        public float[] center_mm; public float distance; public bool visible; public int priority;
    }
    [Serializable] public class ViewResponse { public int target_level; public SelectedChunkDto[] chunks; }
    [Serializable] class ViewRequestDto {
        public float[] position; public float[] rotation; public float[] forward;
        public float fov; public int viewportWidth; public int viewportHeight;
    }
    [Serializable] class ChunkIdsDto { public string[] chunk_ids; }

    /// <summary>
    /// Networking ONLY. Talks to the unchanged DGX streaming API and returns
    /// plain data (DatasetInfo / ViewResponse / BrainChunk). Knows nothing about
    /// rendering, caching, or Sony. HTTP today; same shapes map to WebSocket later.
    /// </summary>
    public sealed class BrainStreamClient
    {
        public string BaseUrl { get; }
        public string Channels = "0,3";        // grayscale + tissue mask (matches browser)
        public bool Connected { get; private set; }
        public float LastLatencyMs { get; private set; }

        public BrainStreamClient(string baseUrl) { BaseUrl = baseUrl.TrimEnd('/'); }

        public async Task<DatasetInfo> GetDatasetInfoAsync()
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/dataset/info");
            Connected = txt != null;
            return txt != null ? JsonUtility.FromJson<DatasetInfo>(txt) : null;
        }

        public async Task<bool> HealthAsync()
        {
            var txt = await GetTextAsync($"{BaseUrl}/api/health");
            Connected = txt != null;
            return Connected;
        }

        public async Task<ViewResponse> PostViewAsync(CameraState cam)
        {
            var dto = new ViewRequestDto {
                position = new[] { cam.PositionMm.x, cam.PositionMm.y, cam.PositionMm.z },
                rotation = new[] { cam.Rotation.x, cam.Rotation.y, cam.Rotation.z, cam.Rotation.w },
                forward  = new[] { cam.ForwardMm.x, cam.ForwardMm.y, cam.ForwardMm.z },
                fov = cam.Fov, viewportWidth = cam.ViewportW, viewportHeight = cam.ViewportH,
            };
            var body = JsonUtility.ToJson(dto);
            var txt = await PostTextAsync($"{BaseUrl}/api/view", body);
            return txt != null ? JsonUtility.FromJson<ViewResponse>(txt) : null;
        }

        /// <summary>Warm the DGX cache for these chunks (server prefetches in parallel).</summary>
        public async Task PrefetchAsync(IEnumerable<string> chunkIds)
        {
            var dto = new ChunkIdsDto { chunk_ids = new List<string>(chunkIds).ToArray() };
            await PostTextAsync($"{BaseUrl}/api/prefetch", JsonUtility.ToJson(dto));
        }

        /// <summary>REQUEST_CHUNK -> decoded BrainChunk. Cancellable (CANCEL_CHUNK).</summary>
        public async Task<BrainChunk> GetChunkAsync(string chunkId, CancellationToken ct)
        {
            var url = $"{BaseUrl}/api/chunk/{chunkId}?channels={Channels}";
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

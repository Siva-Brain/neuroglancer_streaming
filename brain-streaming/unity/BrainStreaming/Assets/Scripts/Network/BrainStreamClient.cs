using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace BrainStreaming
{
    // ---- protocol DTOs (transport-agnostic shapes; see docs/protocol.md) -------
    [Serializable] public struct ViewportDto { public int width; public int height; }

    [Serializable]
    public struct ViewRequestDto
    {
        public float[] camera_position;
        public float[] camera_rotation;   // xyzw
        public float[] camera_forward;
        public float fov;
        public ViewportDto viewport;
    }

    public struct PrioritizedChunk
    {
        public string chunk_id;
        public int target_lod;
        public bool visible;
        public float distance;
        public int priority;
    }

    /// <summary>
    /// Networking ONLY. Produces BrainChunk / metadata / priority lists.
    /// Knows nothing about rendering. HTTP today; the same methods map onto
    /// WebSocket/QUIC later (see docs/protocol.md).
    /// </summary>
    public sealed class BrainStreamClient
    {
        public string BaseUrl { get; }
        public bool Connected { get; private set; }

        public BrainStreamClient(string baseUrl) { BaseUrl = baseUrl.TrimEnd('/'); }

        public async Task<JObject> GetInfoAsync()
        {
            var text = await GetTextAsync($"{BaseUrl}/api/brain/info");
            Connected = text != null;
            return text != null ? JObject.Parse(text) : null;
        }

        public async Task<List<BrainChunkMeta>> GetChunksAsync()
        {
            var text = await GetTextAsync($"{BaseUrl}/api/brain/chunks");
            var list = new List<BrainChunkMeta>();
            if (text == null) return list;
            foreach (var c in JObject.Parse(text)["chunks"])
            {
                var col = c["color"];
                var ctr = c["center"];
                var rot = c["rotation"];
                var deps = new List<string>();
                foreach (var d in c["dependencies"]) deps.Add(d.ToString());
                list.Add(new BrainChunkMeta
                {
                    ChunkId = c["chunk_id"].ToString(),
                    Center = new Vector3((float)ctr[0], (float)ctr[1], (float)ctr[2]),
                    Rotation = new Quaternion((float)rot[0], (float)rot[1], (float)rot[2], (float)rot[3]),
                    Color = new Color((float)col[0], (float)col[1], (float)col[2]),
                    Dependencies = deps.ToArray(),
                });
            }
            return list;
        }

        /// <summary>REQUEST_CHUNK. Supports cancellation (CANCEL_CHUNK).</summary>
        public async Task<BrainChunk> GetChunkAsync(string id, int lod, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/api/brain/chunk/{id}?lod={lod}");
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { req.Abort(); return null; }
                await Task.Yield();
            }
            if (req.result != UnityWebRequest.Result.Success) return null;
            return ParseChunk(id, lod, req.downloadHandler.data);
        }

        /// <summary>VIEW_UPDATE -> prioritized chunk list.</summary>
        public async Task<List<PrioritizedChunk>> PostViewAsync(Camera cam)
        {
            var fwd = cam.transform.forward;
            var q = cam.transform.rotation;
            var dto = new ViewRequestDto
            {
                camera_position = new[] { cam.transform.position.x, cam.transform.position.y, cam.transform.position.z },
                camera_rotation = new[] { q.x, q.y, q.z, q.w },
                camera_forward = new[] { fwd.x, fwd.y, fwd.z },
                fov = cam.fieldOfView,
                viewport = new ViewportDto { width = Screen.width, height = Screen.height },
            };
            var body = JsonUtility.ToJson(dto);
            using var req = new UnityWebRequest($"{BaseUrl}/api/brain/view", "POST")
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            req.SetRequestHeader("Content-Type", "application/json");
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            var outList = new List<PrioritizedChunk>();
            if (req.result != UnityWebRequest.Result.Success) return outList;
            foreach (var c in JObject.Parse(req.downloadHandler.text)["chunks"])
                outList.Add(new PrioritizedChunk
                {
                    chunk_id = c["chunk_id"].ToString(),
                    target_lod = (int)c["target_lod"],
                    visible = (bool)c["visible"],
                    distance = (float)c["distance"],
                    priority = (int)c["priority"],
                });
            return outList;
        }

        // ---- binary 'BRN1' parse -------------------------------------------------
        private static BrainChunk ParseChunk(string id, int lod, byte[] buf)
        {
            if (buf == null || buf.Length < 12 ||
                buf[0] != (byte)'B' || buf[1] != (byte)'R' || buf[2] != (byte)'N' || buf[3] != (byte)'1')
                return null;
            int v = BitConverter.ToInt32(buf, 4);
            int i = BitConverter.ToInt32(buf, 8);
            var pos = new Vector3[v];
            int off = 12;
            for (int k = 0; k < v; k++, off += 12)
                pos[k] = new Vector3(
                    BitConverter.ToSingle(buf, off),
                    BitConverter.ToSingle(buf, off + 4),
                    BitConverter.ToSingle(buf, off + 8));
            var idx = new int[i];
            for (int k = 0; k < i; k++, off += 4)
                idx[k] = BitConverter.ToInt32(buf, off);
            return new BrainChunk { ChunkId = id, Lod = lod, Positions = pos, Indices = idx, ByteSize = buf.Length };
        }

        private static async Task<string> GetTextAsync(string url)
        {
            using var req = UnityWebRequest.Get(url);
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();
            return req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : null;
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace BrainStreaming
{
    /// <summary>
    /// Orchestrator: wires the network client, cache, renderer and camera together
    /// and runs the streaming loop (initial LOD0 → camera-driven upgrades). This is
    /// the only class that talks to both networking and rendering; each half stays
    /// unaware of the other.
    /// </summary>
    public sealed class BrainStreamingApp : MonoBehaviour
    {
        [Header("Server")]
        public string ServerUrl = "http://172.20.23.120:8000";
        public int MaxConcurrency = 3;
        public float ViewIntervalSec = 0.35f;

        [Header("Refs")]
        public BrainRenderer Renderer;
        public Camera Cam;
        public BrainHud Hud;

        public BrainChunkCache Cache { get; } = new BrainChunkCache();
        public int ActiveRequests => _active;
        public int PendingRequests => _queue.Count;
        public int CacheHits { get; private set; }
        public bool Connected { get; private set; }

        private BrainStreamClient _client;
        private readonly List<(string id, int lod, int prio)> _queue = new List<(string, int, int)>();
        private readonly HashSet<string> _inflight = new HashSet<string>();
        private readonly Dictionary<string, int> _desired = new Dictionary<string, int>();
        private readonly HashSet<string> _visible = new HashSet<string>();
        private readonly Dictionary<string, CancellationTokenSource> _cts = new Dictionary<string, CancellationTokenSource>();
        private int _active;
        private float _viewTimer;

        async void Start()
        {
            _client = new BrainStreamClient(ServerUrl);
            var info = await _client.GetInfoAsync();
            if (info == null) { Connected = false; return; }
            Connected = true;

            foreach (var m in await _client.GetChunksAsync())
                Renderer.SetRegionColor(m.ChunkId, m.Color);

            // Time 0: request the entire brain at LOD0 first.
            int i = 0;
            foreach (var id in info["chunk_ids"]) Enqueue(id.ToString(), 0, 1000 + i++);
            Pump();
        }

        void Update()
        {
            _viewTimer += Time.deltaTime;
            if (_viewTimer >= ViewIntervalSec) { _viewTimer = 0; _ = SendViewAsync(); }
            if (Hud != null) Hud.Refresh(this, Renderer, _visible);
        }

        private async Task SendViewAsync()
        {
            var ranked = await _client.PostViewAsync(Cam);
            if (ranked.Count == 0) return;

            _visible.Clear(); _desired.Clear();
            foreach (var c in ranked)
            {
                _desired[c.chunk_id] = c.target_lod;
                if (c.visible) _visible.Add(c.chunk_id);
                int start = Mathf.Max(Renderer.CurrentLod(c.chunk_id) + 1, 0);
                for (int l = start; l <= c.target_lod; l++)
                {
                    if (Cache.Has(c.chunk_id, l)) { if (l == c.target_lod) CacheHits++; continue; }
                    Enqueue(c.chunk_id, l, c.priority);
                }
            }
            CancelIrrelevant();
            Pump();
        }

        private void Enqueue(string id, int lod, int prio)
        {
            var key = BrainChunk.Key(id, lod);
            if (Cache.Has(id, lod) || _inflight.Contains(key)) return;
            int at = _queue.FindIndex(q => q.id == id && q.lod == lod);
            if (at >= 0) _queue[at] = (id, lod, prio);
            else _queue.Add((id, lod, prio));
        }

        private void CancelIrrelevant()
        {
            foreach (var kv in new List<KeyValuePair<string, CancellationTokenSource>>(_cts))
            {
                var parts = kv.Key.Split('@');
                string id = parts[0]; int lod = int.Parse(parts[1]);
                if (!_desired.TryGetValue(id, out var want)) continue;
                if (lod != want && lod != 0 && !(lod < want)) { kv.Value.Cancel(); }
            }
            _queue.RemoveAll(q => _desired.TryGetValue(q.id, out var w) && q.lod > w);
        }

        private void Pump()
        {
            _queue.Sort((a, b) => a.prio.CompareTo(b.prio));
            while (_active < MaxConcurrency && _queue.Count > 0)
            {
                var (id, lod, _) = _queue[0]; _queue.RemoveAt(0);
                var key = BrainChunk.Key(id, lod);
                if (Cache.Has(id, lod) || _inflight.Contains(key)) continue;
                _ = FetchAsync(id, lod, key);
            }
        }

        private async Task FetchAsync(string id, int lod, string key)
        {
            _inflight.Add(key); _active++;
            var cts = new CancellationTokenSource(); _cts[key] = cts;
            try
            {
                var chunk = await _client.GetChunkAsync(id, lod, cts.Token);
                if (chunk != null) { Cache.Add(chunk); Renderer.ApplyChunk(chunk); }
            }
            finally
            {
                _inflight.Remove(key); _cts.Remove(key); _active--;
                Pump();
            }
        }
    }
}

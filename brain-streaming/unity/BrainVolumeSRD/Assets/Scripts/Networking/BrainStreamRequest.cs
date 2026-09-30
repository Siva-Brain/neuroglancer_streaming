using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace BrainVolume
{
    public struct BrainStreamRequest
    {
        public string ChunkId; public int ZSlab; public int Level; public int Priority;
    }

    /// <summary>
    /// CPU-side request scheduler: turns DGX /view responses into a prioritized,
    /// de-duplicated, bounded-concurrency fetch stream, and cancels in-flight
    /// requests that a newer view made obsolete. Fetched bricks are inserted into
    /// the GPU cache. Rendering is entirely decoupled from this.
    /// </summary>
    public sealed class RequestScheduler
    {
        readonly BrainStreamClient _client;
        readonly BrainChunkCache _cache;
        readonly string _block;
        readonly int _maxConcurrency;

        readonly List<BrainStreamRequest> _queue = new List<BrainStreamRequest>();
        readonly Dictionary<string, CancellationTokenSource> _inflight = new Dictionary<string, CancellationTokenSource>();
        readonly Dictionary<int, int> _desired = new Dictionary<int, int>();   // zSlab -> target level

        public int Requested { get; private set; }
        public int Received { get; private set; }
        public long BytesReceived { get; private set; }
        public int Cancelled { get; private set; }
        public int InFlight => _inflight.Count;
        public int Pending => _queue.Count;
        public int TargetLevel { get; private set; } = -1;

        public RequestScheduler(BrainStreamClient client, BrainChunkCache cache, string block, int maxConcurrency = 4)
        {
            _client = client; _cache = cache; _block = block; _maxConcurrency = maxConcurrency;
        }

        public void EnqueueBaseline(string[] baselineChunkIds)
        {
            for (int i = 0; i < baselineChunkIds.Length; i++)
            {
                if (TryParseId(baselineChunkIds[i], out int lvl, out int zi))
                    Enqueue(new BrainStreamRequest { ChunkId = baselineChunkIds[i], ZSlab = zi, Level = lvl, Priority = 1000 + i });
            }
            Pump();
        }

        public void OnView(ViewResponse view)
        {
            if (view == null) return;
            TargetLevel = view.target_level;
            _desired.Clear();
            foreach (var c in view.chunks)
            {
                int zi = c.coords[0];
                if (!_desired.TryGetValue(zi, out int cur) || c.level < cur) _desired[zi] = c.level;
            }
            foreach (var c in view.chunks)
            {
                int zi = c.coords[0];
                if (_cache.HasAtLeast(zi, c.level)) { _cache.Touch(zi); continue; }
                Enqueue(new BrainStreamRequest { ChunkId = c.chunk_id, ZSlab = zi, Level = c.level, Priority = c.priority });
            }
            CancelObsolete();
            Pump();
        }

        void Enqueue(BrainStreamRequest r)
        {
            if (_cache.HasAtLeast(r.ZSlab, r.Level) || _inflight.ContainsKey(r.ChunkId)) return;
            int idx = _queue.FindIndex(q => q.ZSlab == r.ZSlab);
            if (idx >= 0) { if (r.Level < _queue[idx].Level || r.Priority < _queue[idx].Priority) _queue[idx] = r; return; }
            _queue.Add(r);
        }

        void CancelObsolete()
        {
            // abort in-flight fetches whose slab is no longer desired at that level
            var drop = new List<string>();
            foreach (var kv in _inflight)
            {
                TryParseId(kv.Key, out int lvl, out int zi);
                if (!_desired.TryGetValue(zi, out int want) || lvl < want) continue; // still wanted / finer-useful
                if (lvl != want) drop.Add(kv.Key);
            }
            foreach (var id in drop) { _inflight[id].Cancel(); _inflight.Remove(id); Cancelled++; }
            _queue.RemoveAll(q => _desired.Count > 0 && (!_desired.TryGetValue(q.ZSlab, out int w) || q.Level < w) && !IsBaseline(q));
        }

        static bool IsBaseline(BrainStreamRequest r) => r.Priority >= 1000;

        void Pump()
        {
            _queue.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            while (_inflight.Count < _maxConcurrency && _queue.Count > 0)
            {
                var r = _queue[0]; _queue.RemoveAt(0);
                if (_cache.HasAtLeast(r.ZSlab, r.Level) || _inflight.ContainsKey(r.ChunkId)) continue;
                _ = Fetch(r);
            }
        }

        async System.Threading.Tasks.Task Fetch(BrainStreamRequest r)
        {
            var cts = new CancellationTokenSource();
            _inflight[r.ChunkId] = cts; Requested++;
            try
            {
                var chunk = await _client.GetChunkAsync(r.ChunkId, _block, cts.Token);
                if (chunk != null) { _cache.Insert(chunk); Received++; BytesReceived += chunk.SizeBytes; }
            }
            catch { /* aborted / network */ }
            finally { _inflight.Remove(r.ChunkId); Pump(); }
        }

        public static bool TryParseId(string id, out int level, out int zSlab)
        {
            level = 0; zSlab = 0;
            var p = id.Replace("L", "").Split('.');
            return p.Length >= 2 && int.TryParse(p[0], out level) && int.TryParse(p[1], out zSlab);
        }
    }
}

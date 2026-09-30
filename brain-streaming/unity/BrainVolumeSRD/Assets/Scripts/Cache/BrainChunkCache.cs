using System.Collections.Generic;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Bounded, GPU-resident brick cache on the Windows client. Keyed by z-slab
    /// (the spatial region of the 58-slab volume); each slab holds its best (finest)
    /// loaded LOD. Never becomes a copy of the whole brain: the working set is at
    /// most 58 bricks, further bounded by a byte budget with LRU eviction.
    ///
    /// (When fine levels begin subdividing x/y into multiple shards, extend the key
    /// to (zSlab,ySlab,xSlab) -- BrainChunk already carries all four coords.)
    /// </summary>
    public sealed class BrainChunkCache
    {
        readonly Dictionary<int, Brick> _bricks = new Dictionary<int, Brick>();   // zSlab -> best brick
        readonly LinkedList<int> _lru = new LinkedList<int>();                    // MRU at front
        readonly BrainCoordinateSystem _coords;
        readonly long _maxBytes;
        readonly int _maxBricks;

        public long BytesUsed { get; private set; }
        public int Count => _bricks.Count;
        public int Evictions { get; private set; }
        public IEnumerable<Brick> Bricks => _bricks.Values;

        public BrainChunkCache(BrainCoordinateSystem coords, long maxBytes = 512L * 1024 * 1024,
                               int maxBricks = 58)
        {
            _coords = coords; _maxBytes = maxBytes; _maxBricks = maxBricks;
        }

        /// <summary>Do we already hold this slab at an equal-or-finer LOD? (skip re-download)</summary>
        public bool HasAtLeast(int zSlab, int level)
            => _bricks.TryGetValue(zSlab, out var b) && b.Level <= level;

        public int BestLevel(int zSlab)
            => _bricks.TryGetValue(zSlab, out var b) ? b.Level : int.MaxValue;

        public void Touch(int zSlab)
        {
            var n = _lru.Find(zSlab);
            if (n != null) { _lru.Remove(n); _lru.AddFirst(n); }
        }

        /// <summary>Insert/upgrade a slab. Returns true if it replaced/added a brick.</summary>
        public bool Insert(BrainChunk chunk)
        {
            int zi = chunk.ZSlab;
            if (_bricks.TryGetValue(zi, out var cur) && cur.Level <= chunk.Level)
                return false;                          // already equal/finer -> keep

            var brick = Brick.Create(chunk, _coords);
            if (cur != null) { BytesUsed -= cur.VoxelBytes; cur.Dispose(); _lru.Remove(zi); }
            _bricks[zi] = brick; BytesUsed += brick.VoxelBytes; _lru.AddFirst(zi);
            EvictIfNeeded();
            return true;
        }

        void EvictIfNeeded()
        {
            while ((_bricks.Count > _maxBricks || BytesUsed > _maxBytes) && _lru.Count > 1)
            {
                int victim = _lru.Last.Value; _lru.RemoveLast();
                if (_bricks.TryGetValue(victim, out var b))
                {
                    BytesUsed -= b.VoxelBytes; b.Dispose(); _bricks.Remove(victim); Evictions++;
                }
            }
        }

        public void Clear()
        {
            foreach (var b in _bricks.Values) b.Dispose();
            _bricks.Clear(); _lru.Clear(); BytesUsed = 0;
        }
    }
}

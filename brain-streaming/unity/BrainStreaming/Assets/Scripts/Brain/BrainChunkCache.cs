using System.Collections.Generic;

namespace BrainStreaming
{
    /// <summary>
    /// Local GPU-adjacent cache. Guarantees a chunk@lod is never downloaded twice
    /// and tracks the highest LOD currently held per region.
    /// </summary>
    public sealed class BrainChunkCache
    {
        private readonly HashSet<string> _have = new HashSet<string>();      // "id@lod"
        private readonly Dictionary<string, int> _bestLod = new Dictionary<string, int>();

        public int Count => _have.Count;
        public long BytesReceived { get; private set; }

        public bool Has(string id, int lod) => _have.Contains(BrainChunk.Key(id, lod));

        public void Add(BrainChunk c)
        {
            if (_have.Add(c.Key()))
                BytesReceived += c.ByteSize;
            if (!_bestLod.TryGetValue(c.ChunkId, out var cur) || c.Lod > cur)
                _bestLod[c.ChunkId] = c.Lod;
        }

        public int BestLod(string id) => _bestLod.TryGetValue(id, out var l) ? l : -1;
    }
}

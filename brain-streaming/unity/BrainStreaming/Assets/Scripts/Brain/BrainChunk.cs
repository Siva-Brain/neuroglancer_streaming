using UnityEngine;

namespace BrainStreaming
{
    /// <summary>Decoded geometry for one region at one LOD (parsed from a 'BRN1' blob).</summary>
    public sealed class BrainChunk
    {
        public string ChunkId;
        public int Lod;
        public Vector3[] Positions;
        public int[] Indices;
        public int ByteSize;

        public static string Key(string id, int lod) => id + "@" + lod;
        public string Key() => Key(ChunkId, Lod);
    }

    /// <summary>Static metadata for a region (from GET /api/brain/chunks).</summary>
    public sealed class BrainChunkMeta
    {
        public string ChunkId;
        public Vector3 Center;
        public Quaternion Rotation;
        public Color Color;
        public string[] Dependencies;
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace BrainStreaming
{
    /// <summary>
    /// Rendering ONLY. Turns BrainChunk geometry into Unity meshes and swaps a
    /// region's mesh when a higher LOD arrives. Networking never calls into here
    /// except via ApplyChunk(). The Sony ELF-SR2 path is a display option layered
    /// on top of this same object (see BrainDisplayMode / docs/unity-setup.md).
    /// </summary>
    public sealed class BrainRenderer : MonoBehaviour
    {
        public enum BrainDisplayMode { NormalUnity, SonySpatialReality }
        public BrainDisplayMode DisplayMode = BrainDisplayMode.NormalUnity;
        public Material BaseMaterial;   // assign an URP/Standard material in the scene

        private readonly Dictionary<string, GameObject> _objects = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, int> _lod = new Dictionary<string, int>();
        private readonly Dictionary<string, Color> _colors = new Dictionary<string, Color>();

        public void SetRegionColor(string id, Color c) => _colors[id] = c;
        public int CurrentLod(string id) => _lod.TryGetValue(id, out var l) ? l : -1;
        public IEnumerable<int> AllLods => _lod.Values;

        /// <summary>Replace the region's mesh if this chunk is a higher LOD than shown.</summary>
        public void ApplyChunk(BrainChunk c)
        {
            if (_lod.TryGetValue(c.ChunkId, out var cur) && cur >= c.Lod) return;

            var mesh = new Mesh { name = c.Key() };
            mesh.indexFormat = c.Positions.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            mesh.vertices = c.Positions;
            mesh.triangles = c.Indices;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (!_objects.TryGetValue(c.ChunkId, out var go))
            {
                go = new GameObject(c.ChunkId);
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mr.material = BaseMaterial != null ? new Material(BaseMaterial)
                                                   : new Material(Shader.Find("Standard"));
                if (_colors.TryGetValue(c.ChunkId, out var col)) mr.material.color = col;
                _objects[c.ChunkId] = go;
            }
            var oldFilter = go.GetComponent<MeshFilter>();
            if (oldFilter.sharedMesh != null) Destroy(oldFilter.sharedMesh);
            oldFilter.sharedMesh = mesh;
            _lod[c.ChunkId] = c.Lod;
        }

        // Sony branch: with the SR Display SDK present, the SR camera rig renders
        // this same object tree. See docs/unity-setup.md for wiring.
    }
}

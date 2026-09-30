using System.Collections.Generic;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// GPU volume renderer. Ray-marches every cached brick's 3D texture inside a
    /// proxy cube, compositing back-to-front. Draws PROCEDURALLY in OnRenderObject
    /// (one renderer, N DrawMeshNow calls) -- no GameObject per chunk. Because it
    /// runs per rendering camera, it composites correctly for a normal camera AND
    /// for the Sony SRD's own cameras with no Sony-specific code here.
    /// </summary>
    public sealed class BrainVolumeRenderer : MonoBehaviour
    {
        public Transform brainRoot;             // brain-local -> world placement
        public Material raymarchMaterial;       // Brain/Raymarch shader
        [Range(8, 128)] public int steps = 48;
        [Range(1f, 30f)] public float density = 8f;

        BrainChunkCache _cache;
        Mesh _cube;
        readonly List<Brick> _sorted = new List<Brick>(64);

        public void Bind(BrainChunkCache cache) => _cache = cache;

        void Awake()
        {
            if (brainRoot == null) brainRoot = transform;
            _cube = BuildUnitCube();
        }

        void OnRenderObject()
        {
            if (_cache == null || raymarchMaterial == null || _cube == null) return;
            var cam = Camera.current;
            if (cam == null) return;

            Matrix4x4 root = brainRoot.localToWorldMatrix;
            Vector3 camPos = cam.transform.position;

            _sorted.Clear();
            foreach (var b in _cache.Bricks) _sorted.Add(b);
            // back-to-front: farthest first (premultiplied OVER blending)
            _sorted.Sort((a, b) =>
            {
                float da = (root.MultiplyPoint3x4(a.CenterLocal) - camPos).sqrMagnitude;
                float db = (root.MultiplyPoint3x4(b.CenterLocal) - camPos).sqrMagnitude;
                return db.CompareTo(da);
            });

            raymarchMaterial.SetFloat("_Steps", steps);
            raymarchMaterial.SetFloat("_Density", density);
            foreach (var b in _sorted)
            {
                if (b.Texture == null) continue;
                raymarchMaterial.SetTexture("_VolumeTex", b.Texture);
                raymarchMaterial.SetPass(0);
                Graphics.DrawMeshNow(_cube, root * b.LocalMatrix);
            }
        }

        static Mesh BuildUnitCube()
        {
            // unit cube [0,1]^3, outward-facing (CCW) so the shader's Cull Front
            // keeps back faces -> a fragment even when the camera is inside a brick.
            var v = new Vector3[]{
                new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
                new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)};
            var t = new int[]{
                0,2,1, 0,3,2,   4,5,6, 4,6,7,     // -Z, +Z
                0,1,5, 0,5,4,   2,3,7, 2,7,6,     // -Y, +Y
                0,4,7, 0,7,3,   1,2,6, 1,6,5};    // -X, +X
            var m = new Mesh { name = "BrainUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrainVolume.SRD
{
    /// <summary>
    /// P2 of the offline-brick SRD viewer (docs/brain2-srd-bricking-plan.md). Loads a
    /// bricked level produced by tools/prebrick_srd.py from StreamingAssets, uploads
    /// each brick to a Texture3D (BC3/BC7/RGBA32/RGB24 per the index), places it by its
    /// CORE bbox, and ray-marches every brick with Brain/SRDBrickRaymarch — so the whole
    /// brain shows up in the SRD's cameras. No server, no runtime decode.
    ///
    /// Expects  <folder>/<name>/index.json  +  <name>/L&lt;lvl&gt;/b_*.{bc3|bc7|raw}.
    /// Start with the L3 base; P3 adds LOD streaming + an LRU for finer bricks.
    ///
    /// The brain is centred on this transform (index bboxes are centred on the level
    /// origin), so ModelMoveController here rotates/zooms it about its centre.
    /// </summary>
    public sealed class BrickVolumeLoader : MonoBehaviour
    {
        [Header("Files (under Assets/StreamingAssets)")]
        public string folder = "Bricks";
        public string datasetName = "hb02_fused";
        [Tooltip("Pyramid level to load as the base. Must be present in index.json.")]
        public int level = 3;

        [Header("Placement")]
        [Tooltip("Unity units per mm. 0.0025 -> a ~180 mm brain is ~0.45 units (fits the SRD box).")]
        public float unitsPerMm = 0.0025f;

        [Header("Rendering (RGB see-through glass)")]
        public Material material;                   // Brain/SRDBrickRaymarch
        [Range(16, 320)] public int steps = 160;
        [Range(0.02f, 3f)] public float density = 0.3f;
        [Range(0f, 0.5f)] public float windowLow = 0.05f;
        [Range(0.05f, 1f)] public float windowHigh = 0.5f;
        [Range(0f, 0.2f)] public float emptyCut = 0.04f;
        public bool flipX, flipY, flipZ;

        public int BrickCount => _bricks.Count;
        public bool Loaded => _bricks.Count > 0;

        // ---- index.json schema (JsonUtility) ----
        [System.Serializable] class BrickJson {
            public string file, tex_format; public int channels;
            public int[] stored, core_origin, core_size, apron; public float[] bbox_mm;
        }
        [System.Serializable] class LevelJson {
            public int level; public float[] voxel_mm, extent_mm; public int[] grid; public BrickJson[] bricks;
        }
        [System.Serializable] class IndexJson {
            public string name, render, colorMode; public float[] world_extent_mm;
            public int finest_level, coarsest_level; public LevelJson[] levels;
        }

        struct Brick { public Texture3D tex; public Matrix4x4 local; public Vector3 texScale, texOffset, centerLocal; }

        readonly List<Brick> _bricks = new List<Brick>();
        Mesh _cube;

        void Start()
        {
            _cube = BuildUnitCube();
            if (material == null)
            {
                var sh = Shader.Find("Brain/SRDBrickRaymarch");
                if (sh != null) material = new Material(sh);
                else { Debug.LogError("[Bricks] Shader 'Brain/SRDBrickRaymarch' not found."); return; }
            }
            Load();
        }

        void Load()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, folder, datasetName);
            string indexPath = Path.Combine(dir, "index.json");
            if (!File.Exists(indexPath)) { Debug.LogError("[Bricks] Missing " + indexPath); return; }

            var idx = JsonUtility.FromJson<IndexJson>(File.ReadAllText(indexPath));
            LevelJson lvl = null;
            if (idx.levels != null)
                foreach (var l in idx.levels) if (l.level == level) { lvl = l; break; }
            if (lvl == null) { Debug.LogError($"[Bricks] level {level} not in index (have " +
                $"{(idx.levels != null ? idx.levels.Length : 0)} levels)."); return; }

            int ok = 0;
            foreach (var b in lvl.bricks)
            {
                if (TryLoadBrick(dir, b, out var brick)) { _bricks.Add(brick); ok++; }
            }
            Debug.Log($"[Bricks] {idx.name} L{level}: loaded {ok}/{lvl.bricks.Length} bricks " +
                      $"(grid {(lvl.grid != null ? lvl.grid[0] + "x" + lvl.grid[1] : "?")}), " +
                      $"unitsPerMm={unitsPerMm}.");
        }

        bool TryLoadBrick(string dir, BrickJson b, out Brick brick)
        {
            brick = default;
            string path = Path.Combine(dir, b.file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { Debug.LogWarning("[Bricks] missing " + path); return false; }

            int dx = b.stored[0], dy = b.stored[1], dz = b.stored[2];
            if (!FormatOf(b.tex_format, out var fmt)) { Debug.LogWarning("[Bricks] bad fmt " + b.tex_format); return false; }
            long expect = ExpectedBytes(dx, dy, dz, fmt);
            var fi = new FileInfo(path);
            if (fi.Length != expect)
            {
                Debug.LogError($"[Bricks] {b.file} is {fi.Length} B, expected {expect} " +
                               $"({dx}x{dy}x{dz} {b.tex_format}) — truncated copy? Skipping.");
                return false;
            }
            var tex = new Texture3D(dx, dy, dz, fmt, false)
            {
                name = b.file, wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear, anisoLevel = 0,
            };
            tex.SetPixelData(File.ReadAllBytes(path), 0);
            tex.Apply(false, true);

            // CORE placement box (already in centred mm) -> Unity-local, centred on origin.
            Vector3 lo = new Vector3(b.bbox_mm[0], b.bbox_mm[1], b.bbox_mm[2]) * unitsPerMm;
            Vector3 hi = new Vector3(b.bbox_mm[3], b.bbox_mm[4], b.bbox_mm[5]) * unitsPerMm;
            brick.tex = tex;
            brick.local = Matrix4x4.TRS(lo, Quaternion.identity, hi - lo);
            brick.centerLocal = (lo + hi) * 0.5f;
            // apron/pad-aware sampling: core sub-region of the stored texture
            brick.texScale = new Vector3((float)b.core_size[0] / dx, (float)b.core_size[1] / dy, (float)b.core_size[2] / dz);
            brick.texOffset = new Vector3((float)b.apron[0] / dx, (float)b.apron[1] / dy, (float)b.apron[2] / dz);
            return true;
        }

        void OnRenderObject()
        {
            if (material == null || _cube == null || _bricks.Count == 0) return;
            var cam = Camera.current; if (cam == null) return;

            material.SetFloat("_Steps", steps);
            material.SetFloat("_Density", density);
            material.SetFloat("_Low", windowLow);
            material.SetFloat("_High", windowHigh);
            material.SetFloat("_EmptyCut", emptyCut);
            material.SetVector("_Flip", new Vector4(flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0, 0));

            // back-to-front (premultiplied OVER): sort by distance to camera, far first.
            Matrix4x4 l2w = transform.localToWorldMatrix;
            Vector3 camPos = cam.transform.position;
            _bricks.Sort((a, b2) =>
                (l2w.MultiplyPoint3x4(b2.centerLocal) - camPos).sqrMagnitude
                .CompareTo((l2w.MultiplyPoint3x4(a.centerLocal) - camPos).sqrMagnitude));

            foreach (var brk in _bricks)
            {
                material.SetTexture("_VolumeTex", brk.tex);
                material.SetVector("_TexScale", brk.texScale);
                material.SetVector("_TexOffset", brk.texOffset);
                material.SetPass(0);
                Graphics.DrawMeshNow(_cube, l2w * brk.local);
            }
        }

        void OnDestroy() { foreach (var b in _bricks) if (b.tex != null) Destroy(b.tex); _bricks.Clear(); }

        static bool FormatOf(string s, out TextureFormat fmt)
        {
            switch (s)
            {
                case "BC3": fmt = TextureFormat.DXT5; return true;
                case "BC7": fmt = TextureFormat.BC7; return true;
                case "RGBA32": fmt = TextureFormat.RGBA32; return true;
                case "RGB24": fmt = TextureFormat.RGB24; return true;
                default: fmt = TextureFormat.RGBA32; return false;
            }
        }

        static long ExpectedBytes(int dx, int dy, int dz, TextureFormat fmt)
        {
            if (fmt == TextureFormat.DXT5 || fmt == TextureFormat.BC7)   // 16 B / 4x4 block, per slice
                return (long)((dx + 3) / 4) * ((dy + 3) / 4) * 16 * dz;
            if (fmt == TextureFormat.RGBA32) return (long)dx * dy * dz * 4;
            return (long)dx * dy * dz * 3;                               // RGB24
        }

        static Mesh BuildUnitCube()
        {
            var v = new Vector3[]{
                new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
                new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)};
            var t = new int[]{
                0,2,1, 0,3,2,   4,5,6, 4,6,7,
                0,1,5, 0,5,4,   2,3,7, 2,7,6,
                0,4,7, 0,7,3,   1,2,6, 1,6,5};
            var m = new Mesh { name = "BrickUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace BrainVolume.SRD
{
    /// <summary>
    /// A bricked MASK volume (e.g. hb02_vessels: render "mask", colorMode "mask-tint") drawn inside the bricked brain
    /// that owns it (BrickVolumeLoader.overlayDatasets). Same brick layout as the brain (tools/prebrick_srd.py:
    /// &lt;folder&gt;/&lt;name&gt;/index.json + L&lt;n&gt;/b_*.bc3), and its bbox_mm are in the brain's centred-mm space, so
    /// the bricks are placed in the brain's local space with the brain's unitsPerMm and move / turn / scale with it.
    ///
    /// The bricks' alpha is the mask (0 = none, 255 = inside); their rgb the tint. Ray-marched with Brain/BrickMask:
    /// opaque-ish shaded surfaces, cut by the brain's slice (the same kept range along the brain's z).
    /// The brain draws it from its OnRenderObject, after its own bricks (over the brain) or before them.
    /// </summary>
    public sealed class BrickMaskOverlay
    {
        [System.Serializable] class BrickJson { public string file, tex_format; public int[] stored, core_size, apron; public float[] bbox_mm; }
        [System.Serializable] class LevelJson { public int level; public float[] voxel_mm; public BrickJson[] bricks; }
        [System.Serializable] class IndexJson { public string name, render; public int[] tint; public LevelJson[] levels; }

        struct Brick
        {
            public Texture3D tex; public Vector3 lo, hi, centre;   // core box, brain-local units
            public Vector3 texScale, texOffset, texSize;
        }

        public readonly string Name;
        public int Level { get; private set; } = -1;
        public bool Loaded => _bricks.Count > 0;
        public Color Tint { get; private set; } = new Color(0.86f, 0.16f, 0.16f, 1f);

        readonly List<Brick> _bricks = new List<Brick>();
        readonly Material _mat;

        BrickMaskOverlay(string name, Material mat) { Name = name; _mat = mat; }

        /// <summary>Load level `level` of the dataset (the nearest one present if not: finer first).
        /// null if the dataset or its shader is missing.</summary>
        public static BrickMaskOverlay Load(string folder, string name, int level, float unitsPerMm)
        {
            string dir = Path.Combine(Application.streamingAssetsPath, folder, name);
            string indexPath = Path.Combine(dir, "index.json");
            if (!File.Exists(indexPath)) { Debug.LogWarning("[Overlay] Missing " + indexPath); return null; }
            var sh = Shader.Find("Brain/BrickMask");
            if (sh == null) { Debug.LogError("[Overlay] Shader 'Brain/BrickMask' not found (Always Included Shaders?)."); return null; }
            var idx = JsonUtility.FromJson<IndexJson>(File.ReadAllText(indexPath));
            if (idx.levels == null || idx.levels.Length == 0) { Debug.LogWarning($"[Overlay] {name}: no levels."); return null; }

            // the wanted level if it is there, else the nearest (a finer one first)
            LevelJson lvl = null;
            foreach (var l in idx.levels)
                if (l.bricks != null && l.bricks.Length > 0 && File.Exists(Path.Combine(dir, l.bricks[0].file)))
                    if (lvl == null || Mathf.Abs(l.level - level) < Mathf.Abs(lvl.level - level)
                        || (Mathf.Abs(l.level - level) == Mathf.Abs(lvl.level - level) && l.level < lvl.level)) lvl = l;
            if (lvl == null) { Debug.LogWarning($"[Overlay] {name}: no level's bricks on disk."); return null; }

            var o = new BrickMaskOverlay(name, new Material(sh));
            if (idx.tint != null && idx.tint.Length >= 3) o.Tint = new Color(idx.tint[0] / 255f, idx.tint[1] / 255f, idx.tint[2] / 255f, 1f);
            o.Level = lvl.level;
            long bytes = 0;
            foreach (var b in lvl.bricks)
            {
                string path = Path.Combine(dir, b.file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path) || !FormatOf(b.tex_format, out var fmt)) { Debug.LogWarning($"[Overlay] {name}: skipping {b.file}"); continue; }
                var data = File.ReadAllBytes(path);
                int dx = b.stored[0], dy = b.stored[1], dz = b.stored[2];
                var tex = new Texture3D(dx, dy, dz, fmt, false)
                { name = name + "/" + b.file, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 0 };
                try { tex.SetPixelData(data, 0); tex.Apply(false, true); }
                catch (System.Exception e) { Debug.LogError($"[Overlay] {name}/{b.file}: {e.Message}"); Object.Destroy(tex); continue; }
                bytes += data.LongLength;
                Vector3 lo = new Vector3(b.bbox_mm[0], b.bbox_mm[1], b.bbox_mm[2]) * unitsPerMm;
                Vector3 hi = new Vector3(b.bbox_mm[3], b.bbox_mm[4], b.bbox_mm[5]) * unitsPerMm;
                o._bricks.Add(new Brick
                {
                    tex = tex, lo = lo, hi = hi, centre = (lo + hi) * 0.5f,
                    texScale = new Vector3((float)b.core_size[0] / dx, (float)b.core_size[1] / dy, (float)b.core_size[2] / dz),
                    texOffset = new Vector3((float)b.apron[0] / dx, (float)b.apron[1] / dy, (float)b.apron[2] / dz),
                    texSize = new Vector3(dx, dy, dz),
                });
            }
            string vox = lvl.voxel_mm != null && lvl.voxel_mm.Length == 3 ? $"{lvl.voxel_mm[0]} mm voxels, " : "";
            Debug.Log($"[Overlay] {idx.name ?? name} L{lvl.level}: {o._bricks.Count}/{lvl.bricks.Length} bricks, {vox}{bytes >> 20} MB.");
            return o.Loaded ? o : null;
        }

        /// <summary>Draw for the current camera. l2w = the brain's localToWorld; keepZ = the brain's kept range along
        /// its local z (brain-local units: everything outside is cut away).</summary>
        public void Draw(Camera cam, Matrix4x4 l2w, Vector2 keepZ, Color color, float density, float stepsPerVoxel, float shading)
        {
            if (_bricks.Count == 0 || _mat == null) return;
            Vector3 camPos = cam.transform.position;
            _bricks.Sort((a, b) => (l2w.MultiplyPoint3x4(b.centre) - camPos).sqrMagnitude
                                   .CompareTo((l2w.MultiplyPoint3x4(a.centre) - camPos).sqrMagnitude));
            _mat.SetColor("_Color", color);
            _mat.SetFloat("_Density", density);
            _mat.SetFloat("_Shade", shading);
            foreach (var b in _bricks)
            {
                float zLo = Mathf.Max(0f, (keepZ.x - b.lo.z) / Mathf.Max(1e-9f, b.hi.z - b.lo.z));
                float zHi = Mathf.Min(1f, (keepZ.y - b.lo.z) / Mathf.Max(1e-9f, b.hi.z - b.lo.z));
                if (zHi <= zLo) continue;
                _mat.SetVector("_ClipMin", new Vector4(0f, 0f, zLo, 0f));
                _mat.SetVector("_ClipMax", new Vector4(1f, 1f, zHi, 0f));
                _mat.SetTexture("_VolumeTex", b.tex);
                _mat.SetVector("_TexScale", b.texScale);
                _mat.SetVector("_TexOffset", b.texOffset);
                _mat.SetVector("_TexSize", b.texSize);
                _mat.SetFloat("_Steps", Mathf.Clamp(Mathf.Max(b.texSize.x, Mathf.Max(b.texSize.y, b.texSize.z)) * stepsPerVoxel, 16f, 2048f));
                _mat.SetPass(0);
                Graphics.DrawMeshNow(Cube, l2w * Matrix4x4.TRS(b.lo, Quaternion.identity, b.hi - b.lo));
            }
        }

        public void Dispose()
        {
            foreach (var b in _bricks) if (b.tex != null) Object.Destroy(b.tex);
            _bricks.Clear();
            if (_mat != null) Object.Destroy(_mat);
        }

        static bool FormatOf(string s, out TextureFormat fmt)
        {
            switch (s)
            {
                case "BC3": fmt = TextureFormat.DXT5; return true;
                case "BC7": fmt = TextureFormat.BC7; return true;
                case "RGBA32": fmt = TextureFormat.RGBA32; return true;
                default: fmt = TextureFormat.RGBA32; return false;
            }
        }

        static Mesh _cube;
        static Mesh Cube
        {
            get
            {
                if (_cube != null) return _cube;
                _cube = new Mesh { name = "MaskUnitCube" };
                _cube.vertices = new Vector3[] { new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0), new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1) };
                _cube.triangles = new[] { 0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,  2,3,7, 2,7,6,  0,4,7, 0,7,3,  1,2,6, 1,6,5 };
                _cube.RecalculateBounds();
                return _cube;
            }
        }
    }
}

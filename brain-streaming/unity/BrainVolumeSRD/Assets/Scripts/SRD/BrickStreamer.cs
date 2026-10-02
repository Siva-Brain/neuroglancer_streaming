using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace BrainVolume.SRD
{
    /// <summary>
    /// P3 of the offline-brick SRD viewer (docs/brain2-srd-bricking-plan.md): screen-space
    /// LOD + frustum cull + background loading + an LRU VRAM cache, so the pinned coarse
    /// base (e.g. L3) is always on and finer bricks (L2/L1/L0) stream in where you zoom —
    /// true 8 µm at the focus on a 24 GB GPU.
    ///
    /// No server, no runtime decode: reads the offline bricks (tools/prebrick_srd.py) from
    /// StreamingAssets and uploads each to a Texture3D (BC3/BC7/… per the index).
    ///
    /// Overlap/holes are avoided by drawing exactly ONE level per frame: the finest level
    /// whose currently-visible bricks are all resident (base is always resident → never a
    /// hole), while the LOD target's missing bricks load in the background (→ then it
    /// switches up). File reads run on a thread; Texture3D upload is throttled on the main
    /// thread. LRU evicts off-screen, non-base bricks past the VRAM budget.
    /// </summary>
    public sealed class BrickStreamer : MonoBehaviour
    {
        [Header("Files (under Assets/StreamingAssets)")]
        public string folder = "Bricks";
        public string datasetName = "hb02_fused";

        [Header("LOD + memory")]
        [Tooltip("Coarsest baked level = the pinned, always-resident base. -1 = auto (max level in index).")]
        public int baseLevel = -1;
        [Tooltip("Target on-screen voxel size (px). ~1-2 = crisp; raise to stream less.")]
        public float targetPixels = 1.5f;
        [Tooltip("Resident VRAM budget (MB). Keep ~20% of the card free for framebuffers.")]
        public int vramBudgetMB = 14000;
        [Tooltip("Max brick uploads per frame (throttle to avoid hitches).")]
        public int maxUploadsPerFrame = 2;
        [Tooltip("Camera used for the LOD decision. Empty = Camera.main.")]
        public Camera lodCamera;

        [Header("Placement")]
        public float unitsPerMm = 0.0025f;

        [Header("Rendering (RGB see-through glass)")]
        public Material material;                   // Brain/SRDBrickRaymarch
        [Range(16, 320)] public int steps = 160;
        [Range(0.02f, 3f)] public float density = 0.3f;
        [Range(0f, 0.5f)] public float windowLow = 0.05f;
        [Range(0.05f, 1f)] public float windowHigh = 0.5f;
        [Range(0f, 0.2f)] public float emptyCut = 0.04f;
        public bool flipX, flipY, flipZ;

        [Header("Debug (read-only)")]
        public int displayLevel = -1, targetLevel = -1, residentBricks, residentMB, drawnBricks;

        // ---- index.json schema ----
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

        sealed class Meta
        {
            public string path, key; public TextureFormat fmt; public long bytes;
            public int dx, dy, dz, level;
            public Vector3 lo, hi, center, texScale, texOffset;   // local units
        }
        sealed class Resident { public Texture3D tex; public long bytes; public int level; public float used; }

        readonly SortedDictionary<int, List<Meta>> _byLevel = new SortedDictionary<int, List<Meta>>();
        readonly Dictionary<string, Resident> _resident = new Dictionary<string, Resident>();
        readonly HashSet<string> _inflight = new HashSet<string>();
        readonly ConcurrentQueue<(string key, byte[] data, Meta meta)> _ready = new ConcurrentQueue<(string, byte[], Meta)>();
        long _residentBytes;
        float _voxelMmBase;                         // base-level voxel mm (for LOD scale)
        Vector3 _centerLocal = Vector3.zero;        // brain centre (local) ~ origin
        Mesh _cube;
        int _coarsest, _finest;

        void Start()
        {
            _cube = BuildUnitCube();
            if (material == null)
            {
                var sh = Shader.Find("Brain/SRDBrickRaymarch");
                if (sh != null) material = new Material(sh);
                else { Debug.LogError("[Stream] Shader 'Brain/SRDBrickRaymarch' not found."); return; }
            }
            if (lodCamera == null) lodCamera = Camera.main;
            if (!ParseIndex()) return;
            // pin the base level (load all its bricks now, synchronously — it's small)
            foreach (var m in _byLevel[baseLevel]) RequestSync(m);
            Debug.Log($"[Stream] {datasetName}: levels {_finest}..{_coarsest}, base L{baseLevel} " +
                      $"({_byLevel[baseLevel].Count} bricks pinned), budget {vramBudgetMB} MB.");
        }

        bool ParseIndex()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, folder, datasetName);
            string indexPath = Path.Combine(dir, "index.json");
            if (!File.Exists(indexPath)) { Debug.LogError("[Stream] Missing " + indexPath); return false; }
            var idx = JsonUtility.FromJson<IndexJson>(File.ReadAllText(indexPath));
            if (idx.world_extent_mm != null && idx.world_extent_mm.Length == 3)
                _volLocal = new Vector3(idx.world_extent_mm[0], idx.world_extent_mm[1], idx.world_extent_mm[2]) * unitsPerMm;
            if (idx.levels == null || idx.levels.Length == 0) { Debug.LogError("[Stream] no levels"); return false; }

            foreach (var L in idx.levels)
            {
                var list = new List<Meta>(L.bricks.Length);
                foreach (var b in L.bricks)
                {
                    if (!FormatOf(b.tex_format, out var fmt)) continue;
                    int dx = b.stored[0], dy = b.stored[1], dz = b.stored[2];
                    var m = new Meta {
                        path = Path.Combine(dir, b.file.Replace('/', Path.DirectorySeparatorChar)),
                        key = b.file, fmt = fmt, bytes = ExpectedBytes(dx, dy, dz, fmt),
                        dx = dx, dy = dy, dz = dz, level = L.level,
                        lo = new Vector3(b.bbox_mm[0], b.bbox_mm[1], b.bbox_mm[2]) * unitsPerMm,
                        hi = new Vector3(b.bbox_mm[3], b.bbox_mm[4], b.bbox_mm[5]) * unitsPerMm,
                        texScale = new Vector3((float)b.core_size[0] / dx, (float)b.core_size[1] / dy, (float)b.core_size[2] / dz),
                        texOffset = new Vector3((float)b.apron[0] / dx, (float)b.apron[1] / dy, (float)b.apron[2] / dz),
                    };
                    m.center = (m.lo + m.hi) * 0.5f;
                    list.Add(m);
                }
                _byLevel[L.level] = list;
                // level voxel mm (x) for LOD
                if (L.voxel_mm != null && L.voxel_mm.Length >= 1) _levelVoxelMm[L.level] = L.voxel_mm[0];
            }
            _finest = int.MaxValue; _coarsest = int.MinValue;
            foreach (var k in _byLevel.Keys) { _finest = Mathf.Min(_finest, k); _coarsest = Mathf.Max(_coarsest, k); }
            if (baseLevel < 0 || !_byLevel.ContainsKey(baseLevel)) baseLevel = _coarsest;
            _voxelMmBase = _levelVoxelMm.TryGetValue(baseLevel, out var vb) ? vb : 1f;
            return true;
        }
        readonly Dictionary<int, float> _levelVoxelMm = new Dictionary<int, float>();

        // ---- per-frame: choose LOD, request target bricks, evict ----
        void Update()
        {
            DrainReady();
            var cam = lodCamera != null ? lodCamera : Camera.main;
            if (cam == null) return;

            targetLevel = ChooseLevel(cam);
            // request the target level's visible bricks (base is already resident)
            if (_byLevel.TryGetValue(targetLevel, out var tgt) && targetLevel < baseLevel)
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                Matrix4x4 l2w = transform.localToWorldMatrix;
                foreach (var m in tgt)
                    if (GeometryUtility.TestPlanesAABB(planes, WorldBounds(m, l2w)))
                        Request(m);
            }
            Evict();
            residentBricks = _resident.Count; residentMB = (int)(_residentBytes >> 20);
        }

        int ChooseLevel(Camera cam)
        {
            float dist = Mathf.Max(0.001f, Vector3.Distance(cam.transform.position,
                transform.localToWorldMatrix.MultiplyPoint3x4(_centerLocal)));
            float ppu = cam.pixelHeight / (2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            // finest level whose voxel still projects to >= targetPixels (conservative)
            int best = baseLevel;
            for (int l = _finest; l <= _coarsest; l++)
            {
                if (!_levelVoxelMm.TryGetValue(l, out var vmm)) continue;
                float px = vmm * unitsPerMm * ppu;
                if (px >= targetPixels) { best = l; break; }
                best = l;   // nothing coarse enough yet; keep the finest available
            }
            return Mathf.Clamp(best, _finest, _coarsest);
        }

        // ---- draw: the finest level whose visible bricks are ALL resident (base = fallback) ----
        void OnRenderObject()
        {
            if (material == null || _cube == null || _byLevel.Count == 0) return;
            var cam = Camera.current; if (cam == null) return;
            Matrix4x4 l2w = transform.localToWorldMatrix;
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);

            int show = baseLevel;
            for (int l = Mathf.Max(_finest, targetLevel); l <= baseLevel; l++)
            {
                if (!_byLevel.TryGetValue(l, out var bricks)) continue;
                bool allResident = true;
                foreach (var m in bricks)
                    if (GeometryUtility.TestPlanesAABB(planes, WorldBounds(m, l2w)) && !_resident.ContainsKey(m.key))
                    { allResident = false; break; }
                if (allResident) { show = l; break; }
            }
            displayLevel = show;

            material.SetFloat("_Steps", steps); material.SetFloat("_Density", density);
            material.SetFloat("_RefSteps", 256f);   // SRDBrickRaymarch now uses saturation + defaults (see the shader)
            material.SetFloat("_EmptyCut", emptyCut);
            material.SetVector("_Flip", new Vector4(flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0, 0));

            // gather visible bricks of the shown level, sort far->near, draw
            _draw.Clear();
            foreach (var m in _byLevel[show])
                if (_resident.TryGetValue(m.key, out var r) && GeometryUtility.TestPlanesAABB(planes, WorldBounds(m, l2w)))
                { r.used = Time.unscaledTime; _draw.Add(m); }
            Vector3 camPos = cam.transform.position;
            _draw.Sort((a, b) =>
                (l2w.MultiplyPoint3x4(b.center) - camPos).sqrMagnitude.CompareTo(
                (l2w.MultiplyPoint3x4(a.center) - camPos).sqrMagnitude));
            foreach (var m in _draw)
            {
                material.SetTexture("_VolumeTex", _resident[m.key].tex);
                material.SetVector("_TexScale", m.texScale);
                material.SetVector("_TexOffset", m.texOffset);
                material.SetVector("_TexSize", new Vector3(m.dx, m.dy, m.dz));
                Vector3 core = m.hi - m.lo;
                material.SetVector("_BrickToVol", new Vector3(core.x / _volLocal.x, core.y / _volLocal.y, core.z / _volLocal.z));
                material.SetPass(0);
                Graphics.DrawMeshNow(_cube, l2w * Matrix4x4.TRS(m.lo, Quaternion.identity, m.hi - m.lo));
            }
            drawnBricks = _draw.Count;
        }
        readonly List<Meta> _draw = new List<Meta>(64);
        Vector3 _volLocal = Vector3.one;            // whole-volume size (local units): SRDBrickRaymarch opacity correction

        // ---- loading ----
        void Request(Meta m)
        {
            if (_resident.ContainsKey(m.key) || _inflight.Contains(m.key)) return;
            _inflight.Add(m.key);
            Task.Run(() =>
            {
                try { var data = File.ReadAllBytes(m.path); _ready.Enqueue((m.key, data, m)); }
                catch (System.Exception e) { Debug.LogWarning($"[Stream] read {m.key}: {e.Message}"); _ready.Enqueue((m.key, null, m)); }
            });
        }

        void RequestSync(Meta m)
        {
            if (_resident.ContainsKey(m.key)) return;
            try { Upload(m, File.ReadAllBytes(m.path)); }
            catch (System.Exception e) { Debug.LogError($"[Stream] base read {m.key}: {e.Message}"); }
        }

        void DrainReady()
        {
            int n = 0;
            while (n < maxUploadsPerFrame && _ready.TryDequeue(out var it))
            {
                _inflight.Remove(it.key);
                if (it.data != null && !_resident.ContainsKey(it.key)) { Upload(it.meta, it.data); n++; }
            }
        }

        void Upload(Meta m, byte[] data)
        {
            if (data.LongLength != m.bytes)
            { Debug.LogError($"[Stream] {m.key} {data.LongLength}B != {m.bytes} (truncated copy?)"); return; }
            var tex = new Texture3D(m.dx, m.dy, m.dz, m.fmt, false)
            { name = m.key, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 0 };
            tex.SetPixelData(data, 0); tex.Apply(false, true);
            _resident[m.key] = new Resident { tex = tex, bytes = m.bytes, level = m.level, used = Time.unscaledTime };
            _residentBytes += m.bytes;
        }

        void Evict()
        {
            long budget = (long)vramBudgetMB << 20;
            if (_residentBytes <= budget) return;
            // candidates: not base-level, least-recently-used first
            var cand = new List<KeyValuePair<string, Resident>>();
            foreach (var kv in _resident) if (kv.Value.level != baseLevel) cand.Add(kv);
            cand.Sort((a, b) => a.Value.used.CompareTo(b.Value.used));
            foreach (var kv in cand)
            {
                if (_residentBytes <= budget) break;
                Destroy(kv.Value.tex); _residentBytes -= kv.Value.bytes; _resident.Remove(kv.Key);
            }
        }

        Bounds WorldBounds(Meta m, Matrix4x4 l2w)
        {
            Vector3 lo = m.lo, hi = m.hi;
            var b = new Bounds(l2w.MultiplyPoint3x4(lo), Vector3.zero);
            for (int i = 1; i < 8; i++)
                b.Encapsulate(l2w.MultiplyPoint3x4(new Vector3(
                    (i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z)));
            return b;
        }

        void OnDestroy() { foreach (var r in _resident.Values) if (r.tex != null) Destroy(r.tex); _resident.Clear(); }

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
            if (fmt == TextureFormat.DXT5 || fmt == TextureFormat.BC7)
                return (long)((dx + 3) / 4) * ((dy + 3) / 4) * 16 * dz;
            if (fmt == TextureFormat.RGBA32) return (long)dx * dy * dz * 4;
            return (long)dx * dy * dz * 3;
        }
        static Mesh BuildUnitCube()
        {
            var v = new Vector3[]{ new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
                                   new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)};
            var t = new int[]{ 0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,
                               2,3,7, 2,7,6,  0,4,7, 0,7,3,  1,2,6, 1,6,5};
            var m = new Mesh { name = "BrickUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

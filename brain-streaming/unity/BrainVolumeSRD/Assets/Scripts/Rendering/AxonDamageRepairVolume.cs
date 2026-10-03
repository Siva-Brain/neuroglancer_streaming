using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// V4 "axon damage and repair": three HB02 maps (npz_files/V4, same 501x783x712 (k, j, i) grid and
    /// 0.24 mm voxels as the astrocyte / fib maps) drawn as ONE volume, so the colours mix per voxel:
    ///   healthy.npy  uint8 0/1   white-matter fibres of the stroke hemisphere (522,599 voxels)  -> blue
    ///   app.npy      float32 0..2 on exactly the healthy voxels; levels 0.25..0.75, 1.0, 1.25..2.0.
    ///                >= appLightFrom = APP+ light (orange), towards 2 = APP+ dense (red); below stays blue
    ///   gap43.npy    float32 ~0.95..1, 35,554 voxels, almost all OUTSIDE the healthy fibres (new growth) -> green
    /// The data has no time channel: "spreading from the stroke" is a radial reveal around strokeCentreVoxel
    /// (the GAP43 centroid) driven by appRadiusMm / gapRadiusMm (set by AxonRepairTimeline).
    /// Packed into one RGBA32 3D texture: R = healthy presence, G = APP value x presence (0..2 -> 0..255),
    /// B = GAP43 presence; downsample keeps the per-block maximum so the thin fibres survive.
    /// Follows the brain (position, scale, rotation x relativeRotation, like NpzDensityVolume) and draws
    /// right after it (its Drawn event), over it.
    /// </summary>
    [DefaultExecutionOrder(100)]   // LateUpdate after NeuronalLossSequence's (it applies the brain's pose there)
    public sealed class AxonDamageRepairVolume : MonoBehaviour
    {
        [Header("Files (npz_files/V4; builds: StreamingAssets/Npz/<file name>)")]
        public string healthyPath = "npz_files/V4/healthy_HB02_0.24mm.npz";
        public string appPath = "npz_files/V4/app_HB02_0.24mm_damage.npz";
        public string gapPath = "npz_files/V4/gap43_HB02_0.24mm_regeneration.npz";
        public float voxelMm = 0.24f;
        [Tooltip("Keep the maximum of every n x n x n block (1 = full 712x783x501, 2.2 GB RGBA; 2 = 280 MB).")]
        [Range(1, 4)] public int downsample = 2;

        [Header("Follow the brain")]
        [Tooltip("The brain (BrickVolumeLoader / FusedVolumeLoader): this volume copies its position, scale and " +
                 "rotation every frame and draws right after it.")]
        public MonoBehaviour follow;
        [Tooltip("Same as NpzDensityVolume: hb02 brain local axes x = anterior, y = inferior, z = right; " +
                 "this volume x = right, y = superior, z = anterior.")]
        public Quaternion relativeRotation = new Quaternion(0.70710677f, 0f, 0.70710677f, 0f);
        [Tooltip("Unity units per millimetre (0.0025 = same as the brain loaders).")]
        public float unitsPerMm = 0.0025f;

        [Header("Look")]
        [Range(64, 1024)] public int raySteps = 500;
        [Tooltip("Opacity per 10 cm of world length at full presence.")]
        [Range(0f, 400f)] public float density = 25f;
        [Range(0f, 4f)] public float brightness = 2.2f;
        [Range(0f, 1f)] public float jitter = 1f;
        public Color healthyColor = new Color(0.28f, 0.42f, 0.78f);
        public Color appLightColor = new Color(1.00f, 0.62f, 0.22f);
        public Color appDenseColor = new Color(0.95f, 0.16f, 0.10f);
        public Color gapColor = new Color(0.25f, 1.00f, 0.40f);
        [Tooltip("APP values from here are APP+ (orange); lower values stay healthy blue.")]
        public float appLightFrom = 0.95f;
        [Tooltip("APP values from here shade orange -> red, fully red at 2.")]
        public float appDenseFrom = 1.2f;
        [Tooltip("GAP43 opacity relative to the fibres (it is sparser, so it gets a boost).")]
        [Range(0f, 10f)] public float gapBoost = 5f;

        [Header("Spread from the stroke")]
        [Tooltip("Stroke centre in voxels (i, j, k) of the full grid. Default = the GAP43 centroid.")]
        public Vector3 strokeCentreVoxel = new Vector3(236f, 381f, 312f);
        [Tooltip("Width of the soft spreading front (mm).")]
        public float frontSoftMm = 14f;
        [Tooltip("Irregularity of the front (mm), so it does not look like a perfect sphere.")]
        public float frontNoiseMm = 9f;

        [Header("Driven by AxonRepairTimeline")]
        [Range(0f, 1f)] public float visibility = 1f;
        [Tooltip("0 = fibres hidden, 1 = shown.")]
        [Range(0f, 1f)] public float healthyAmount = 1f;
        [Tooltip("APP shows inside this radius (mm) around the stroke centre.")]
        public float appRadiusMm = 1000f;
        [Tooltip("GAP43 shows inside this radius (mm) around the stroke centre.")]
        public float gapRadiusMm = 1000f;

        public bool Loaded => _tex != null;
        public Vector3 SizeMm { get; private set; }

        Texture3D _tex;
        Material _mat;
        Mesh _cube;
        Task<Result> _load;
        ISliceableVolume _brain;

        sealed class Result { public byte[] bytes; public int nx, ny, nz; public string error; public string log; }

        void Start()
        {
            var sh = Shader.Find("Brain/AxonDamageRepair");
            if (sh == null) { Debug.LogError("[Axon] Shader Brain/AxonDamageRepair not found."); enabled = false; return; }
            _mat = new Material(sh);
            _cube = BuildUnitCube();
            string h = ResolvePath(healthyPath), a = ResolvePath(appPath), g = ResolvePath(gapPath);
            foreach (var (p, n) in new[] { (h, healthyPath), (a, appPath), (g, gapPath) })
                if (p == null) { Debug.LogError("[Axon] Missing " + n + " (also looked in StreamingAssets/Npz)"); enabled = false; return; }
            int ds = Mathf.Max(1, downsample);
            Debug.Log($"[Axon] Loading {Path.GetFileName(h)}, {Path.GetFileName(a)}, {Path.GetFileName(g)} (downsample {ds}) ...");
            _load = Task.Run(() => Load(h, a, g, ds));
            if (follow is ISliceableVolume v) { _brain = v; _brain.Drawn += Draw; }
            else Debug.LogWarning("[Axon] No brain to follow: not drawn.");
        }

        // absolute; StreamingAssets/<path>; StreamingAssets/Npz/<file name> (in builds); next to the Unity project
        static string ResolvePath(string p)
        {
            if (Path.IsPathRooted(p)) return File.Exists(p) ? p : null;
            string[] candidates =
            {
                Path.Combine(Application.streamingAssetsPath, p),
                Path.Combine(Application.streamingAssetsPath, "Npz", Path.GetFileName(p)),
                Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", p)),
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        void Update()
        {
            if (_load == null || !_load.IsCompleted) return;
            var r = _load.IsFaulted ? new Result { error = _load.Exception?.GetBaseException().Message } : _load.Result;
            _load = null;
            if (r.error != null) { Debug.LogError("[Axon] " + r.error); enabled = false; return; }   // (the timeline stops waiting)
            _tex = new Texture3D(r.nx, r.ny, r.nz, TextureFormat.RGBA32, false)
            {
                name = "AxonDamageRepair", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            _tex.SetPixelData(r.bytes, 0);
            _tex.Apply(false, true);
            float v = voxelMm * Mathf.Max(1, downsample);
            SizeMm = new Vector3(r.nx * v, r.nz * v, r.ny * v);   // local x = i, y = k, z = j
            Debug.Log($"[Axon] {r.nx}x{r.ny}x{r.nz} (i,j,k), {v:F2} mm. {r.log}");
        }

        void LateUpdate()
        {
            if (_brain == null || follow == null || !follow.isActiveAndEnabled) return;
            Transform b = _brain.transform;
            transform.SetPositionAndRotation(b.position, b.rotation * relativeRotation);
            transform.localScale = b.localScale;
        }

        void Draw(Camera cam)
        {
            if (_tex == null || _mat == null || cam == null || visibility <= 0.001f) return;
            float v = voxelMm * Mathf.Max(1, downsample);
            _mat.SetTexture("_Volume", _tex);
            _mat.SetFloat("_Steps", raySteps);
            _mat.SetFloat("_Density", density * visibility);
            _mat.SetFloat("_Brightness", brightness);
            _mat.SetFloat("_Jitter", jitter);
            _mat.SetColor("_HealthyColor", healthyColor);
            _mat.SetColor("_AppLightColor", appLightColor);
            _mat.SetColor("_AppDenseColor", appDenseColor);
            _mat.SetColor("_GapColor", gapColor);
            _mat.SetFloat("_AppLightFrom", appLightFrom);
            _mat.SetFloat("_AppDenseFrom", appDenseFrom);
            _mat.SetFloat("_GapBoost", gapBoost);
            _mat.SetFloat("_HealthyAmount", healthyAmount);
            _mat.SetFloat("_AppRadius", appRadiusMm);
            _mat.SetFloat("_GapRadius", gapRadiusMm);
            _mat.SetFloat("_FrontSoft", Mathf.Max(0.1f, frontSoftMm));
            _mat.SetFloat("_FrontNoise", frontNoiseMm);
            // object (unit cube) axes x = i, y = k, z = j; sizes and centre in mm in that order
            _mat.SetVector("_SizeMm", SizeMm);
            _mat.SetVector("_CentreMm", new Vector3(strokeCentreVoxel.x, strokeCentreVoxel.z, strokeCentreVoxel.y) * voxelMm);
            _mat.SetVector("_TexSize", new Vector4(_tex.width, _tex.height, _tex.depth, 0));
            _mat.SetPass(0);
            Graphics.DrawMeshNow(_cube, UnitCubeToWorld);
        }

        /// <summary>Unit cube (0..1) -> world, centred on this transform.</summary>
        public Matrix4x4 UnitCubeToWorld
        {
            get
            {
                Vector3 size = SizeMm * unitsPerMm;
                return transform.localToWorldMatrix * Matrix4x4.TRS(-0.5f * size, Quaternion.identity, size);
            }
        }

        void OnDestroy()
        {
            if (_brain != null) _brain.Drawn -= Draw;
            if (_tex != null) Destroy(_tex);
            if (_mat != null) Destroy(_mat);
            if (_cube != null) Destroy(_cube);
        }

        // ------------------------------------------------------------------ loading (background thread)

        static Result Load(string healthy, string app, string gap, int ds)
        {
            var r = new Result();
            int[] shape = null;
            int nx = 0, ny = 0, nz = 0;
            byte[] bytes = null;
            long nH = 0, nA = 0, nG = 0;
            // channel: 0 = healthy presence, 1 = APP value (0..2 -> 0..255), 2 = GAP43 presence
            void Read(string path, string array, int channel, ref long count)
            {
                using (var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry(array + ".npy") ?? throw new InvalidDataException($"{Path.GetFileName(path)} has no {array}.npy");
                    using (var s = new BufferedStream(entry.Open(), 1 << 20))
                    {
                        var h = ReadHeader(s);
                        if (h.fortran || h.shape.Length != 3) throw new InvalidDataException($"{array}: expected a C-order 3-D (k, j, i) array");
                        if (h.descr != "<f4" && h.descr != "|u1") throw new InvalidDataException($"{array}: dtype {h.descr}, expected <f4 or |u1");
                        if (shape == null)
                        {
                            shape = h.shape;
                            nx = (shape[2] + ds - 1) / ds; ny = (shape[1] + ds - 1) / ds; nz = (shape[0] + ds - 1) / ds;
                            bytes = new byte[(long)nx * ny * nz * 4];
                        }
                        else if (h.shape[0] != shape[0] || h.shape[1] != shape[1] || h.shape[2] != shape[2])
                            throw new InvalidDataException($"{array}: shape differs from the first map");
                        bool f4 = h.descr == "<f4";
                        int ni = shape[2], bpv = f4 ? 4 : 1;
                        var buf = new byte[ni * bpv];
                        var row = new float[ni];
                        for (int k = 0; k < shape[0]; k++)
                            for (int j = 0; j < shape[1]; j++)
                            {
                                ReadExact(s, buf, buf.Length);
                                if (f4) Buffer.BlockCopy(buf, 0, row, 0, buf.Length);
                                else for (int i = 0; i < ni; i++) row[i] = buf[i];
                                long o = ((long)(k / ds) * ny + j / ds) * nx;
                                for (int i = 0; i < ni; i++)
                                {
                                    float v = row[i];
                                    if (!(v > 0f)) continue;
                                    count++;
                                    byte b = channel == 1 ? (byte)Mathf.Clamp(Mathf.RoundToInt(v * 127.5f), 1, 255) : (byte)255;
                                    long at = (o + i / ds) * 4 + channel;
                                    if (b > bytes[at]) bytes[at] = b;
                                }
                            }
                    }
                }
            }
            try
            {
                Read(healthy, "healthy", 0, ref nH);
                Read(app, "app", 1, ref nA);
                Read(gap, "gap43", 2, ref nG);
            }
            catch (Exception e) { r.error = e.Message; return r; }
            // the shader reads the APP value as G / R: APP voxels without a healthy fibre count as present
            for (long p = 0; p < bytes.LongLength; p += 4)
                if (bytes[p + 1] > 0 && bytes[p] == 0) bytes[p] = 255;
            r.bytes = bytes; r.nx = nx; r.ny = ny; r.nz = nz;
            r.log = $"Healthy {nH:N0}, APP {nA:N0}, GAP43 {nG:N0} voxels.";
            return r;
        }

        struct NpyHeader { public string descr; public bool fortran; public int[] shape; }

        static NpyHeader ReadHeader(Stream s)
        {
            var pre = new byte[8];
            ReadExact(s, pre, 8);
            if (pre[0] != 0x93 || pre[1] != (byte)'N') throw new InvalidDataException("not a .npy stream");
            int len;
            if (pre[6] == 1) { var b = new byte[2]; ReadExact(s, b, 2); len = b[0] | (b[1] << 8); }
            else { var b = new byte[4]; ReadExact(s, b, 4); len = BitConverter.ToInt32(b, 0); }
            var hb = new byte[len];
            ReadExact(s, hb, len);
            string text = System.Text.Encoding.ASCII.GetString(hb);
            var h = new NpyHeader
            {
                descr = Regex.Match(text, @"'descr':\s*'([^']*)'").Groups[1].Value,
                fortran = Regex.IsMatch(text, @"'fortran_order':\s*True"),
            };
            var dims = new System.Collections.Generic.List<int>();
            foreach (var p in Regex.Match(text, @"'shape':\s*\(([^)]*)\)").Groups[1].Value.Split(','))
                if (p.Trim().Length > 0) dims.Add(int.Parse(p.Trim(), CultureInfo.InvariantCulture));
            h.shape = dims.ToArray();
            return h;
        }

        static void ReadExact(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
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
            var m = new Mesh { name = "AxonUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

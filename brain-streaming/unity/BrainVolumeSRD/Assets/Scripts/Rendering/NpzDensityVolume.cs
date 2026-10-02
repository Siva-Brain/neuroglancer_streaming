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
    /// Shows one density map from a NumPy .npz (e.g. npz_files/astrocyte_density_HB02_0.24mm.npz)
    /// as a coloured volume, centred on this transform. Read straight from the .npz on a background
    /// thread (no export step), drawn in OnRenderObject like the other volumes (ZTest Always).
    ///
    /// Expected .npz contents (as written for HB02, checked 2026-10-02):
    ///   density.npy                  float32, C order, shape (k, j, i) = (501, 783, 712)
    ///   index_to_physical_LPS_mm.npy float64 4x4: (i, j, k, 1) -> LPS mm. HB02: diag(-0.24, -0.24, 0.24),
    ///                                i.e. i -> Right, j -> Anterior, k -> Superior, 0.24 mm voxels
    ///   axes_order.npy "kji", units.npy "astrocytes per mm^3 (0 = no section sampled there)"
    /// Unity local axes are x = Right (i), y = Superior (k), z = Anterior (j) (RAS -> Unity, no mirror);
    /// the shader swizzles the texture lookup accordingly. Voxel sizes come from the affine.
    ///
    /// Values are mapped to 8 bits: 0 stays 0 (not sampled, transparent), the rest are scaled
    /// linearly up to the `percentileHigh` percentile of the non-zero values (two streaming passes over
    /// the 1.1 GB array: histogram, then quantise -- no full float copy in memory).
    /// </summary>
    [DefaultExecutionOrder(100)]   // LateUpdate after NeuronalLossSequence's (it applies the brain's pose there)
    public sealed class NpzDensityVolume : MonoBehaviour
    {
        [Header("File")]
        [Tooltip("Absolute path, or relative to StreamingAssets, or relative to the folder that contains the Unity " +
                 "project (brain-streaming/unity). StreamingAssets/Npz/<file name> is also tried (that copy ships in builds).")]
        public string npzPath = "npz_files/astrocyte_density_HB02_0.24mm.npz";
        public string arrayName = "density";
        public string affineName = "index_to_physical_LPS_mm";
        [Tooltip("Voxel size (mm) when the .npz has no affine (e.g. fib_probability_HB02_0.24mm: 0.24).")]
        public float voxelMmIfNoAffine = 0.24f;
        [Tooltip("Keep every n-th voxel per axis (1 = full 712x783x501; 2 = 1/8 of the memory).")]
        [Range(1, 4)] public int downsample = 1;
        [Tooltip("Values at or above this percentile of the non-zero voxels map to full colour.")]
        [Range(50f, 100f)] public float percentileHigh = 99.5f;
        [Tooltip("The HB02 astrocyte map is ~1-voxel-thick sagittal sections ~1.2 mm apart (along i = left-right). " +
                 "Gaps up to this many mm between sampled voxels are filled by linear interpolation along i, " +
                 "so the sections become a continuous volume. 0 = off (raw sections).")]
        [Range(0f, 5f)] public float fillGapMm = 2f;
        [Tooltip("Same, within each section (along j and k), for the small holes in the sampled sheets. 0 = off.")]
        [Range(0f, 3f)] public float inPlaneFillMm = 0.75f;

        [Header("Placement")]
        [Tooltip("Unity units per millimetre (0.0025 = same as the brain loaders).")]
        public float unitsPerMm = 0.0025f;

        public enum RenderMode { Composite = 0, MaxIntensity = 1 }

        [Header("Look")]
        [Tooltip("Composite = semi-transparent volume (front-to-back). MaxIntensity = each pixel shows the highest " +
                 "value along its ray (clearest for sparse maps; no depth order).")]
        public RenderMode mode = RenderMode.Composite;
        [Range(32, 1024)] public int raySteps = 600;
        [Tooltip("Normalised values below this are transparent (0..1 of the percentile window).")]
        [Range(0f, 1f)] public float threshold = 0.08f;
        [Tooltip("Composite: opacity per 10 cm of world length at full value.")]
        [Range(0f, 200f)] public float density = 40f;
        [Tooltip("Gamma on the normalised value before colour/opacity (< 1 lifts low densities).")]
        [Range(0.2f, 3f)] public float valueGamma = 0.7f;
        public Color colorLow = new Color(0.15f, 0.35f, 1.00f);
        public Color colorMid = new Color(0.20f, 0.95f, 0.70f);
        public Color colorHigh = new Color(1.00f, 0.85f, 0.20f);
        [Range(0f, 3f)] public float brightness = 1.3f;
        [Tooltip("Composite: gradient (surface) shading strength, gives the volume a 3D look.")]
        [Range(0f, 1f)] public float shading = 0.5f;
        [Range(0f, 1f)] public float jitter = 1f;
        [Tooltip("Draw a faint box around the volume (to find it while it loads / if it looks empty).")]
        public bool showBounds = false;
        [Tooltip("0 = hidden, 1 = fully shown (scales opacity and colour; driven by TimelineDensityOverlay to fade in).")]
        [Range(0f, 1f)] public float visibility = 1f;

        [Header("Follow the brain (split view)")]
        [Tooltip("A BrickVolumeLoader / FusedVolumeLoader whose rotation (and slice) this volume copies every frame, " +
                 "so both show the same anatomical view side by side. Empty = stand-alone.")]
        public MonoBehaviour follow;
        [Tooltip("This volume's rotation relative to the followed brain. hb02 brain local axes: x = anterior, " +
                 "y = inferior, z = right; this volume: x = right, y = superior, z = anterior -> 180 deg about (1,0,1).")]
        public Quaternion relativeRotation = new Quaternion(0.70710677f, 0f, 0.70710677f, 0f);
        public bool followRotation = true;
        [Tooltip("Cut this volume like the followed brain: same fraction, from the same anatomical side.")]
        public bool followSlice = true;

        Vector3 _clipMin = Vector3.zero, _clipMax = Vector3.one;

        public bool Loaded => _tex != null;
        public Vector3 SizeMm { get; private set; }

        Texture3D _tex;
        Material _mat;
        Mesh _cube;
        Task<Result> _load;

        sealed class Result
        {
            public byte[] bytes; public int nx, ny, nz;
            public Vector3 voxelMm; public float maxValue, highValue; public long nonZero, total;
            public string error;
        }

        void Start()
        {
            var sh = Shader.Find("Brain/DensityVolume");
            if (sh == null) { Debug.LogError("[Density] Shader Brain/DensityVolume not found."); enabled = false; return; }
            _mat = new Material(sh);
            _cube = BuildUnitCube();

            string path = ResolvePath(npzPath);
            if (path == null) { Debug.LogError("[Density] Missing " + npzPath + " (also looked in StreamingAssets)"); enabled = false; return; }
            Debug.Log($"[Density] Loading {path} ...");
            string arr = arrayName, aff = affineName;
            int ds = Mathf.Max(1, downsample);
            float pct = percentileHigh, gap = fillGapMm, inPlane = inPlaneFillMm, vox = voxelMmIfNoAffine;
            _load = Task.Run(() => Load(path, arr, aff, ds, pct, gap, inPlane, vox));
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
            if (r.error != null) { Debug.LogError("[Density] " + r.error); return; }

            _tex = new Texture3D(r.nx, r.ny, r.nz, TextureFormat.R8, false)
            {
                name = "NpzDensity", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            _tex.SetPixelData(r.bytes, 0);
            _tex.Apply(false, true);
            // texture axes (i, j, k) -> Unity local (x = i, y = k, z = j)
            SizeMm = new Vector3(r.nx * r.voxelMm.x, r.nz * r.voxelMm.z, r.ny * r.voxelMm.y);
            Debug.Log($"[Density] {name}: {r.nx}x{r.ny}x{r.nz} (i,j,k), voxel {r.voxelMm.x:F3}x{r.voxelMm.y:F3}x{r.voxelMm.z:F3} mm, " +
                      $"size {SizeMm.x:F1} x {SizeMm.y:F1} x {SizeMm.z:F1} mm (R-L x S-I x A-P). " +
                      $"Non-zero {r.nonZero:N0}/{r.total:N0} ({100.0 * r.nonZero / Math.Max(1, r.total):F1} %), " +
                      $"max {r.maxValue:G4}, p{percentileHigh:F1} = {r.highValue:G4} -> full colour.");
        }

        // After the timeline's LateUpdate (execution order), so the brain's rotation/slice of this frame is final.
        void LateUpdate()
        {
            if (follow == null || !follow.isActiveAndEnabled) return;
            if (followRotation) transform.rotation = follow.transform.rotation * relativeRotation;
            _clipMin = Vector3.zero; _clipMax = Vector3.one;
            if (followSlice && follow is ISliceableVolume s && s.SlicePosition > 0f)
            {
                // the brain cuts along its local z; that axis in this volume's local frame:
                Vector3 v = Quaternion.Inverse(Quaternion.Normalize(relativeRotation)) * Vector3.forward;
                int a = Mathf.Abs(v.x) >= Mathf.Abs(v.y) ? (Mathf.Abs(v.x) >= Mathf.Abs(v.z) ? 0 : 2)
                                                         : (Mathf.Abs(v.y) >= Mathf.Abs(v.z) ? 1 : 2);
                bool fromHigh = s.SliceFromHighZ ^ (v[a] < 0f);
                float cut = Mathf.Clamp(s.SlicePosition, 0f, 0.999f);
                if (fromHigh) _clipMax[a] = 1f - cut; else _clipMin[a] = cut;
            }
        }

        void OnRenderObject()
        {
            if (_tex == null || _mat == null || Camera.current == null || visibility <= 0.001f) return;
            _mat.SetTexture("_Volume", _tex);
            _mat.SetFloat("_Steps", raySteps);
            _mat.SetFloat("_Threshold", threshold);
            _mat.SetFloat("_Density", density * visibility);
            _mat.SetFloat("_Gamma", valueGamma);
            _mat.SetColor("_ColorLow", colorLow);
            _mat.SetColor("_ColorMid", colorMid);
            _mat.SetColor("_ColorHigh", colorHigh);
            _mat.SetFloat("_Brightness", brightness * (mode == RenderMode.MaxIntensity ? visibility : 1f));
            _mat.SetFloat("_Mode", (float)mode);
            _mat.SetFloat("_Shade", shading);
            _mat.SetVector("_TexSize", new Vector4(_tex.width, _tex.height, _tex.depth, 0));
            _mat.SetVector("_ClipMin", _clipMin);
            _mat.SetVector("_ClipMax", _clipMax);
            _mat.SetFloat("_Jitter", jitter);
            _mat.SetFloat("_Bounds", showBounds ? 1f : 0f);
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
            if (_tex != null) Destroy(_tex);
            if (_mat != null) Destroy(_mat);
            if (_cube != null) Destroy(_cube);
        }

        // ------------------------------------------------------------------ .npz reading (background thread)

        static Result Load(string path, string arrayName, string affineName, int ds, float pct,
                           float fillGapMm, float inPlaneFillMm, float voxelMmIfNoAffine)
        {
            var r = new Result { voxelMm = Vector3.one * voxelMmIfNoAffine };
            using (var zip = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
            {
                // voxel size from the affine's column lengths (i, j, k)
                var affEntry = zip.GetEntry(affineName + ".npy");
                if (affEntry != null)
                {
                    using (var s = affEntry.Open())
                    {
                        var h = ReadHeader(s);
                        if (h.descr == "<f8" && h.shape.Length == 2 && h.shape[0] == 4 && h.shape[1] == 4)
                        {
                            var br = new BinaryReader(s);
                            var m = new double[16];
                            for (int n = 0; n < 16; n++) m[n] = br.ReadDouble();
                            double Col(int c) => Math.Sqrt(m[c] * m[c] + m[4 + c] * m[4 + c] + m[8 + c] * m[8 + c]);
                            r.voxelMm = new Vector3((float)Col(0), (float)Col(1), (float)Col(2));
                        }
                    }
                }

                var entry = zip.GetEntry(arrayName + ".npy");
                if (entry == null) { r.error = $"{Path.GetFileName(path)} has no {arrayName}.npy"; return r; }

                int nk, nj, ni;
                using (var s = entry.Open())
                {
                    var h = ReadHeader(s);
                    if (h.descr != "<f4") { r.error = $"{arrayName}: dtype {h.descr}, expected <f4"; return r; }
                    if (h.fortran) { r.error = $"{arrayName}: Fortran order not supported"; return r; }
                    if (h.shape.Length != 3) { r.error = $"{arrayName}: {h.shape.Length}-D, expected 3-D (k, j, i)"; return r; }
                    nk = h.shape[0]; nj = h.shape[1]; ni = h.shape[2];
                }
                r.total = (long)nk * nj * ni;

                // pass 1: max + log2 histogram of the non-zero values
                const int Bins = 4096; const float LogMin = -20f, LogMax = 40f;
                var hist = new long[Bins];
                float max = 0f; long nonZero = 0;
                Stream(entry, nk, nj, ni, (k, j, row) =>
                {
                    for (int i = 0; i < row.Length; i++)
                    {
                        float v = row[i];
                        if (!(v > 0f)) continue;
                        nonZero++;
                        if (v > max) max = v;
                        int b = (int)((Mathf.Log(v, 2f) - LogMin) / (LogMax - LogMin) * Bins);
                        hist[Mathf.Clamp(b, 0, Bins - 1)]++;
                    }
                });
                r.maxValue = max; r.nonZero = nonZero;
                if (nonZero == 0) { r.error = $"{arrayName} is all zero"; return r; }
                long want = (long)Math.Ceiling(nonZero * pct / 100.0), acc = 0;
                float high = max;
                for (int b = 0; b < Bins; b++)
                {
                    acc += hist[b];
                    if (acc >= want) { high = Mathf.Pow(2f, LogMin + (b + 1f) / Bins * (LogMax - LogMin)); break; }
                }
                high = Mathf.Min(high, max);
                r.highValue = high;

                // pass 2: quantise (0 = not sampled; 1..255 = value up to `high`), keeping every ds-th voxel
                int nx = (ni + ds - 1) / ds, ny = (nj + ds - 1) / ds, nz = (nk + ds - 1) / ds;
                r.nx = nx; r.ny = ny; r.nz = nz;
                r.voxelMm *= ds;
                var bytes = new byte[(long)nx * ny * nz];
                float scale = 254f / high;
                Stream(entry, nk, nj, ni, (k, j, row) =>
                {
                    if (k % ds != 0 || j % ds != 0) return;
                    long o = ((long)(k / ds) * ny + j / ds) * nx;
                    for (int i = 0, x = 0; i < row.Length; i += ds, x++)
                    {
                        float v = row[i];
                        bytes[o + x] = v > 0f ? (byte)(1 + Mathf.Min(254f, v * scale)) : (byte)0;
                    }
                });
                // fill the gaps between the sampled sections (along i), then the holes inside them (j, k)
                int gx = Mathf.RoundToInt(fillGapMm / r.voxelMm.x);
                int gy = Mathf.RoundToInt(inPlaneFillMm / r.voxelMm.y), gz = Mathf.RoundToInt(inPlaneFillMm / r.voxelMm.z);
                if (gx > 0) FillGaps(bytes, nx, ny, nz, 0, gx);
                if (gy > 0) FillGaps(bytes, nx, ny, nz, 1, gy);
                if (gz > 0) FillGaps(bytes, nx, ny, nz, 2, gz);
                r.bytes = bytes;
            }
            return r;
        }

        // Along one axis (0 = x, 1 = y, 2 = z): every run of zeros of length <= maxGap that lies between
        // two non-zero voxels is filled by linear interpolation between them. Longer gaps (outside the
        // tissue, real holes) stay empty.
        static void FillGaps(byte[] v, int nx, int ny, int nz, int axis, int maxGap)
        {
            int n = axis == 0 ? nx : axis == 1 ? ny : nz;
            long stride = axis == 0 ? 1 : axis == 1 ? nx : (long)nx * ny;
            int na = axis == 0 ? ny : nx, nb = axis == 2 ? ny : nz;   // the two other axes
            Parallel.For(0, nb, b =>
            {
                for (int a = 0; a < na; a++)
                {
                    long o = axis == 0 ? ((long)b * ny + a) * nx
                           : axis == 1 ? (long)b * nx * ny + a
                           : (long)b * nx + a;
                    int last = -1;
                    for (int t = 0; t < n; t++)
                    {
                        byte c = v[o + t * stride];
                        if (c == 0) continue;
                        int gap = t - last - 1;
                        if (last >= 0 && gap > 0 && gap <= maxGap)
                        {
                            float c0 = v[o + last * stride], c1 = c;
                            for (int u = last + 1; u < t; u++)
                                v[o + u * stride] = (byte)Mathf.Max(1f, Mathf.Round(c0 + (c1 - c0) * (u - last) / (gap + 1f)));
                        }
                        last = t;
                    }
                }
            });
        }

        // Calls rowFn(k, j, row) for every (k, j) row of i values, in file order.
        static void Stream(ZipArchiveEntry entry, int nk, int nj, int ni, Action<int, int, float[]> rowFn)
        {
            using (var s = new BufferedStream(entry.Open(), 1 << 20))
            {
                ReadHeader(s);
                var buf = new byte[ni * 4];
                var row = new float[ni];
                for (int k = 0; k < nk; k++)
                    for (int j = 0; j < nj; j++)
                    {
                        int got = 0;
                        while (got < buf.Length)
                        {
                            int n = s.Read(buf, got, buf.Length - got);
                            if (n <= 0) throw new EndOfStreamException($"density.npy ended at row k={k} j={j}");
                            got += n;
                        }
                        Buffer.BlockCopy(buf, 0, row, 0, buf.Length);
                        rowFn(k, j, row);
                    }
            }
        }

        struct NpyHeader { public string descr; public bool fortran; public int[] shape; }

        static NpyHeader ReadHeader(Stream s)
        {
            var pre = new byte[8];
            ReadExact(s, pre, 8);
            if (pre[0] != 0x93 || pre[1] != (byte)'N') throw new InvalidDataException("not a .npy stream");
            int major = pre[6], len;
            if (major == 1) { var b = new byte[2]; ReadExact(s, b, 2); len = b[0] | (b[1] << 8); }
            else { var b = new byte[4]; ReadExact(s, b, 4); len = BitConverter.ToInt32(b, 0); }
            var hb = new byte[len];
            ReadExact(s, hb, len);
            string text = System.Text.Encoding.ASCII.GetString(hb);
            var h = new NpyHeader
            {
                descr = Regex.Match(text, @"'descr':\s*'([^']*)'").Groups[1].Value,
                fortran = Regex.IsMatch(text, @"'fortran_order':\s*True"),
            };
            string shape = Regex.Match(text, @"'shape':\s*\(([^)]*)\)").Groups[1].Value;
            var parts = shape.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var dims = new System.Collections.Generic.List<int>();
            foreach (var p in parts)
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
            var m = new Mesh { name = "DensityUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

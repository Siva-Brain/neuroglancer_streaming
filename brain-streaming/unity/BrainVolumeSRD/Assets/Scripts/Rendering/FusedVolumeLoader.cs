using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BrainVolume
{
    /// <summary>
    /// Loads a local RGB fused volume (one OME-Zarr level exported by
    /// tools/zarr_level_to_raw.py) from StreamingAssets and ray-marches it with
    /// Brain/FusedRaymarch. No server, no streaming -- one Texture3D drawn
    /// procedurally in OnRenderObject, so it shows up in the SRD's cameras like
    /// the streamed brain. Same approach as T1VolumeLoader, but 3-channel colour.
    ///
    /// Expects <folder>/<name>.json + <name>.raw. The .raw is width*height*depth*3
    /// bytes, x fastest, then y, then z, RGB interleaved -- i.e. a C-order
    /// (z, y, x, c) uint8 array straight from the zarr, so NO reordering here.
    ///
    /// The fused data is brightfield-style: WHITE background, pale stained tissue,
    /// BLACK = no data (unwritten Zarr chunks -- hb02 L4 has whole 512x512 blocks of
    /// them). Black is turned white on load: left black, trilinear filtering blends
    /// it with the white background into grey sheets along every chunk edge. The
    /// shader takes opacity from colour saturation (see FusedRaymarch.shader).
    ///
    /// The volume is centred on this transform, so ModelMoveController on the same
    /// GameObject rotates and zooms it about its centre.
    /// </summary>
    public sealed class FusedVolumeLoader : MonoBehaviour
    {
        [Header("Files (under Assets/StreamingAssets)")]
        public string folder = "Fused";
        public string baseName = "hb02_L7";

        [Header("Placement")]
        [Tooltip("Unity units per millimetre. 0.0025 -> a ~180 mm brain is ~0.45 units, fits the SRD box.")]
        public float unitsPerMm = 0.0025f;

        [Header("Rendering (RGB, opacity from stain saturation)")]
        public Material material;                   // Brain/FusedRaymarch
        [Tooltip("Ray-march samples per pixel. Higher = sharper, slower.")]
        [Range(16, 512)] public int raySteps = 256;
        [Tooltip("Per-sample opacity. Lower = more see-through, higher = solid surface.")]
        [Range(0.02f, 3f)] public float opacity = 0.6f;
        [Tooltip("Saturation (max-min of rgb) below this is background. hb02: background < 0.03, tissue 0.09-0.22.")]
        [Range(0f, 0.3f)] public float saturationLow = 0.06f;
        [Tooltip("Saturation at which a sample reaches full opacity.")]
        [Range(0.02f, 0.6f)] public float saturationHigh = 0.2f;
        [Tooltip("Colour gamma: >1 deepens the pale stain so tissue is not white fog.")]
        [Range(1f, 6f)] public float colorGamma = 2.5f;
        [Range(0.2f, 3f)] public float brightness = 1.2f;
        [Tooltip("Gradient (surface) shading. 0 = flat colour. Costs 6 extra texture reads per visible sample.")]
        [Range(0f, 1f)] public float shading = 0.7f;
        [Tooltip("Per-pixel random ray offset; removes moire rings.")]
        [Range(0f, 1f)] public float jitter = 1f;
        [Tooltip("Max(rgb) below this is treated as empty/no-data (black) and skipped.")]
        [Range(0f, 0.2f)] public float emptyCut = 0.04f;
        [Tooltip("On load, turn black no-data voxels into white background (removes grey sheets at missing-chunk edges).")]
        public bool blackToWhite = true;
        [Tooltip("On load, per section: open the tissue mask with a box this wide (mm) to cut thin " +
                 "stitching-seam lines loose from the brain. 0 = seam filter off.")]
        [Range(0f, 4f)] public float seamFilterMm = 1.5f;
        [Tooltip("Then keep only pieces that contain a core at least this wide (mm); thinner pieces " +
                 "(seam strips, up to ~2.8 mm wide in hb02) are removed whole. Kept pieces keep full detail.")]
        [Range(0f, 8f)] public float seamCoreMm = 3.2f;
        public bool flipX, flipY, flipZ;

        public enum Axis { X = 0, Y = 1, Z = 2 }

        [Header("Slicing (C = slice in / slice back, V = reset, [ ] = step)")]
        [Tooltip("Volume axis the sagittal sweep moves along (the left-right axis). " +
                 "For hb02 the 485-section z axis (155 mm) is the left-right one.")]
        public Axis sliceAxis = Axis.Z;
        [Tooltip("Sweep from the other end of the axis (use if it starts on the right).")]
        public bool sliceReverse = false;
        [Tooltip("Fraction of the volume cut away per second while sweeping.")]
        [Range(0.01f, 1f)] public float sliceSpeed = 0.1f;
        [Tooltip("Fraction moved per [ or ] press.")]
        [Range(0.001f, 0.2f)] public float sliceStep = 0.01f;
        [Tooltip("How much of the volume is cut away, 0 = none, 1 = all.")]
        [Range(0f, 1f)] public float slicePosition = 0f;
        public bool sliceSweeping = false;
        [Tooltip("+1 = slicing in (cutting away), -1 = slicing back (restoring). Flipped by each C press.")]
        public int sliceDirection = -1;
        [Tooltip("C / V / [ ] slicing keys. Turned off by a timeline (NeuronalLossSequence) that owns the slice.")]
        public bool sliceKeysEnabled = true;

        [Header("Labels (region segmentation, optional)")]
        [Tooltip("Base name of the aligned label export, e.g. hb02_L7_labels. Empty = no labels.")]
        public string labelName = "";
        [Tooltip("Render only where label > 0. Removes background/empty/fusion slabs, keeps tissue.")]
        public bool maskToLabels = true;
        [Tooltip("Tint tissue by region colour (from the LUT). Off = keep natural fused colour.")]
        public bool colorRegions = false;
        [Range(0f, 1f)] public float labelOpacity = 0.6f;

        public Texture3D Texture { get; private set; }
        public Texture3D LabelTex { get; private set; }
        public Texture2D LutTex { get; private set; }
        public Vector3 SizeMm { get; private set; }     // (x, y, z) mm
        public bool Loaded => Texture != null;

        /// <summary>Unit cube (drawn space, before _Flip) -> world. Valid once Loaded.</summary>
        public Matrix4x4 UnitCubeToWorld => transform.localToWorldMatrix * _local;

        /// <summary>Optional box (unit-cube coords) carved out of the volume, e.g. where a
        /// core of overlay data is shown. Off while holeMin >= holeMax on any axis.</summary>
        [HideInInspector] public Vector3 holeMin, holeMax;

        /// <summary>Raised right after the volume is drawn for a camera, so overlays drawn
        /// in the handler always composite on top of it (both shaders ignore depth).</summary>
        public event System.Action<Camera> Drawn;

        Mesh _cube;
        Matrix4x4 _local;                           // unit cube -> this transform's local space

        [System.Serializable]
        class Meta
        {
            public int width, height, depth, channels;
            public string format;
            public float[] voxelSizeMM;             // (x, y, z)
            public float[] sizeMM;                  // (x, y, z) extent
            public int level;
        }

        void Start()
        {
            _cube = BuildUnitCube();
            if (material == null)
            {
                var sh = Shader.Find("Brain/FusedRaymarch");
                if (sh != null) material = new Material(sh);
                else { Debug.LogError("[Fused] Shader 'Brain/FusedRaymarch' not found."); return; }
            }
            Load();
        }

        void Load()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, folder);
            string jsonPath = Path.Combine(dir, baseName + ".json");
            string rawPath = Path.Combine(dir, baseName + ".raw");
            if (!File.Exists(jsonPath) || !File.Exists(rawPath))
            {
                Debug.LogError("[Fused] Missing " + jsonPath + " or " + rawPath);
                return;
            }

            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(jsonPath));
            if (meta.channels != 3 || meta.format != "RGB24")
            {
                Debug.LogError($"[Fused] Expected 3-channel RGB24 (got {meta.channels} x {meta.format}).");
                return;
            }
            int w = meta.width, h = meta.height, d = meta.depth;
            const int MAXDIM = 2048;        // Unity Texture3D per-dimension limit
            if (w > MAXDIM || h > MAXDIM || d > MAXDIM)
            {
                Debug.LogError($"[Fused] L{meta.level} {w}x{h}x{d} exceeds Texture3D max " +
                               $"{MAXDIM}/dim. L4 is the finest single-volume level; finer " +
                               "needs tiling (use the streaming client).");
                return;
            }
            long expectBytes = (long)w * h * d * 3;
            var fi = new FileInfo(rawPath);
            if (fi.Length != expectBytes)
            {
                Debug.LogError($"[Fused] {baseName}.raw is {fi.Length} bytes, expected " +
                               $"{expectBytes} ({w}x{h}x{d}x3). Likely a truncated/incomplete " +
                               "copy -- re-copy the full file (compare sizes after transfer).");
                return;
            }
            if (expectBytes > int.MaxValue)
            {
                Debug.LogError($"[Fused] {expectBytes} bytes > 2 GB; File.ReadAllBytes can't " +
                               "load it. Use L4 or coarser.");
                return;
            }
            byte[] raw = File.ReadAllBytes(rawPath);

            if (meta.sizeMM != null && meta.sizeMM.Length == 3)
                SizeMm = new Vector3(meta.sizeMM[0], meta.sizeMM[1], meta.sizeMM[2]);
            else if (meta.voxelSizeMM != null && meta.voxelSizeMM.Length == 3)
                SizeMm = new Vector3(w * meta.voxelSizeMM[0], h * meta.voxelSizeMM[1], d * meta.voxelSizeMM[2]);
            else
                SizeMm = new Vector3(w, h, d);

            if (blackToWhite)
            {
                int cut = Mathf.RoundToInt(emptyCut * 255f);
                long fixedVoxels = BlackToWhite(raw, cut);
                if (fixedVoxels > 0)
                    Debug.Log($"[Fused] {fixedVoxels:N0} black no-data voxels set to white background.");
            }

            if (seamFilterMm > 0f && meta.voxelSizeMM != null && meta.voxelSizeMM.Length == 3)
            {
                // box widths in mm -> half-widths in voxels per axis
                int Rad(float mm, float vox) => Mathf.Max(0, Mathf.CeilToInt(0.5f * (mm / vox - 1f)));
                int rx = Rad(seamFilterMm, meta.voxelSizeMM[0]), ry = Rad(seamFilterMm, meta.voxelSizeMM[1]);
                int cx = Rad(seamCoreMm, meta.voxelSizeMM[0]), cy = Rad(seamCoreMm, meta.voxelSizeMM[1]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int satCut = Mathf.RoundToInt(saturationLow * 255f);
                long removed = RemoveSeams(raw, w, h, d, rx, ry, cx, cy, satCut);
                Debug.Log($"[Fused] Seam filter (open {2 * rx + 1}x{2 * ry + 1}, core {2 * cx + 1}x{2 * cy + 1} voxels): " +
                          $"{removed:N0} voxels cleared in {sw.ElapsedMilliseconds} ms.");
            }

            // Raw is already Unity-order (x fastest, y, z), RGB interleaved -> upload directly.
            Texture = new Texture3D(w, h, d, TextureFormat.RGB24, false)
            {
                name = "FusedVolume",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,   // trilinear for 3D
                anisoLevel = 0,
            };
            Texture.SetPixelData(raw, 0);
            Texture.Apply(false, true);

            Vector3 size = SizeMm * unitsPerMm;
            _local = Matrix4x4.TRS(-size * 0.5f, Quaternion.identity, size);  // centred on transform
            Debug.Log($"[Fused] Loaded L{meta.level} {w}x{h}x{d} RGB, " +
                      $"{SizeMm.x:F0}x{SizeMm.y:F0}x{SizeMm.z:F0} mm -> " +
                      $"{size.x:F3}x{size.y:F3}x{size.z:F3} units.");

            if (!string.IsNullOrEmpty(labelName))
                LoadLabels(dir, w, h, d);
        }

        // Aligned label volume (R8, NEAREST) + 256-colour region LUT. Same dims as fused.
        void LoadLabels(string dir, int fw, int fh, int fd)
        {
            string ljson = Path.Combine(dir, labelName + ".json");
            string lraw = Path.Combine(dir, labelName + ".raw");
            if (!File.Exists(ljson) || !File.Exists(lraw))
            {
                Debug.LogWarning("[Fused] Label files missing: " + lraw + " (labels disabled).");
                return;
            }
            var lm = JsonUtility.FromJson<Meta>(File.ReadAllText(ljson));
            if (lm.width != fw || lm.height != fh || lm.depth != fd)
            {
                Debug.LogError($"[Fused] Label dims {lm.width}x{lm.height}x{lm.depth} != fused " +
                               $"{fw}x{fh}x{fd}. Export labels at the SAME level.");
                return;
            }
            byte[] lraw8 = File.ReadAllBytes(lraw);
            if (lraw8.LongLength != (long)fw * fh * fd)
            {
                Debug.LogError($"[Fused] Label raw size {lraw8.LongLength} != {fw}x{fh}x{fd} " +
                               "(truncated copy?).");
                return;
            }
            LabelTex = new Texture3D(fw, fh, fd, TextureFormat.R8, false)
            {
                name = "FusedLabels",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,     // NEAREST: region ids must not interpolate
                anisoLevel = 0,
            };
            LabelTex.SetPixelData(lraw8, 0);
            LabelTex.Apply(false, true);

            // 256x1 RGB LUT from <labelName>.lut (768 bytes). id 0 = black.
            string lut = Path.Combine(dir, labelName + ".lut");
            if (File.Exists(lut))
            {
                byte[] lb = File.ReadAllBytes(lut);
                if (lb.Length >= 256 * 3)
                {
                    LutTex = new Texture2D(256, 1, TextureFormat.RGB24, false)
                    {
                        name = "FusedLut",
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Point,
                    };
                    LutTex.SetPixelData(lb, 0);
                    LutTex.Apply(false, true);
                }
            }
            Debug.Log($"[Fused] Labels loaded ({labelName}), LUT={(LutTex != null)}.");
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && sliceKeysEnabled)
            {
                // C alternates: slice in -> slice back -> slice in ... Pressing it mid-sweep
                // reverses from wherever the cut currently is.
                if (kb.cKey.wasPressedThisFrame)
                {
                    sliceDirection = sliceDirection > 0 ? -1 : 1;
                    sliceSweeping = true;
                }
                if (kb.vKey.wasPressedThisFrame) { sliceSweeping = false; sliceDirection = -1; slicePosition = 0f; }
                if (kb.leftBracketKey.wasPressedThisFrame)  { sliceSweeping = false; slicePosition -= sliceStep; }
                if (kb.rightBracketKey.wasPressedThisFrame) { sliceSweeping = false; slicePosition += sliceStep; }
            }

            if (sliceSweeping)
            {
                slicePosition += sliceDirection * sliceSpeed * Time.deltaTime;
                if (slicePosition >= 1f || slicePosition <= 0f) sliceSweeping = false;   // hold at either end
            }
            slicePosition = Mathf.Clamp01(slicePosition);
        }

        void OnRenderObject()
        {
            if (Texture == null || material == null || _cube == null) return;
            if (Camera.current == null) return;

            material.SetTexture("_VolumeTex", Texture);
            material.SetFloat("_Steps", raySteps);
            material.SetFloat("_Density", opacity);
            material.SetFloat("_SatLow", saturationLow);
            material.SetFloat("_SatHigh", saturationHigh);
            material.SetFloat("_Gamma", colorGamma);
            material.SetFloat("_Brightness", brightness);
            material.SetFloat("_Shade", shading);
            material.SetFloat("_Jitter", jitter);
            material.SetFloat("_EmptyCut", emptyCut);
            material.SetVector("_TexSize", new Vector4(Texture.width, Texture.height, Texture.depth, 0));
            material.SetVector("_Flip", new Vector4(flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0, 0));

            // Slicing: shrink the visible box along the sweep axis. Clamped just short
            // of a full cut so the box never becomes empty/degenerate.
            Vector4 cmin = Vector4.zero, cmax = Vector4.one;
            float cut = Mathf.Min(slicePosition, 0.999f);
            int a = (int)sliceAxis;
            if (sliceReverse) cmax[a] = 1f - cut; else cmin[a] = cut;
            material.SetVector("_ClipMin", cmin);
            material.SetVector("_ClipMax", cmax);

            bool hasLabels = LabelTex != null;
            material.SetTexture("_LabelTex", hasLabels ? LabelTex : Texture);
            material.SetTexture("_Lut", LutTex != null ? (Texture)LutTex : Texture2D.blackTexture);
            material.SetInt("_LabelMask", (hasLabels && maskToLabels) ? 1 : 0);
            material.SetInt("_LabelColor", (hasLabels && colorRegions && LutTex != null) ? 1 : 0);
            material.SetFloat("_LabelAlpha", labelOpacity);
            material.SetVector("_HoleMin", holeMin);
            material.SetVector("_HoleMax", holeMax);

            material.SetPass(0);
            Graphics.DrawMeshNow(_cube, UnitCubeToWorld);
            Drawn?.Invoke(Camera.current);
        }

        void OnDestroy()
        {
            if (Texture != null) Destroy(Texture);
            if (LabelTex != null) Destroy(LabelTex);
            if (LutTex != null) Destroy(LutTex);
        }

        // RGB voxels whose brightest channel is below `cut` -> 255,255,255. Parallel
        // over 1 MB-ish slabs; L4 (1.6 GB) takes about a second.
        static long BlackToWhite(byte[] raw, int cut)
        {
            long voxels = raw.LongLength / 3;
            const long Slab = 1 << 18;                          // voxels per task
            int slabs = (int)((voxels + Slab - 1) / Slab);
            long total = 0;
            System.Threading.Tasks.Parallel.For(0, slabs, () => 0L, (s, _, n) =>
            {
                long end = System.Math.Min(voxels, (s + 1) * Slab);
                for (long v = s * Slab; v < end; v++)
                {
                    long o = v * 3;
                    if (raw[o] < cut && raw[o + 1] < cut && raw[o + 2] < cut)
                    {
                        raw[o] = raw[o + 1] = raw[o + 2] = 255;
                        n++;
                    }
                }
                return n;
            }, n => System.Threading.Interlocked.Add(ref total, n));
            return total;
        }

        // Per-section seam removal on the tissue mask (saturation >= satCut):
        //  1. open with a (2rx+1)x(2ry+1) box -> thin seam lines are cut loose from the brain
        //  2. erode that with a (2cx+1)x(2cy+1) box -> seeds = cores of genuinely thick tissue
        //  3. flood-fill (4-connected) from the seeds inside the opened mask
        // Tissue voxels not reached are set to white background. So a seam strip with no
        // thick core disappears whole, while the brain keeps every fine edge the opening
        // left. Erode/dilate are separable running-window counts (O(N) at any radius).
        // Parallel over sections.
        static long RemoveSeams(byte[] raw, int w, int h, int d, int rx, int ry, int cx, int cy, int satCut)
        {
            long total = 0;
            System.Threading.Tasks.Parallel.For(0, d,
                () => new SliceBuffers(w, h),
                (z, _, buf) =>
                {
                    long baseIdx = (long)z * w * h * 3;
                    int n = w * h;
                    var m = buf.mask; var a = buf.a; var o = buf.open; var s = buf.seed; var q = buf.queue;
                    for (int i = 0; i < n; i++)
                    {
                        long p = baseIdx + (long)i * 3;
                        int r = raw[p], g = raw[p + 1], bl = raw[p + 2];
                        int mx = r > g ? (r > bl ? r : bl) : (g > bl ? g : bl);
                        int mn = r < g ? (r < bl ? r : bl) : (g < bl ? g : bl);
                        m[i] = (byte)(mx - mn >= satCut ? 1 : 0);
                    }
                    // 1. opening: erode (all ones in window), then dilate (any one in window)
                    RunX(m, a, w, h, rx, true);  RunY(a, o, w, h, ry, true);
                    RunX(o, a, w, h, rx, false); RunY(a, o, w, h, ry, false);
                    // 2. seeds
                    RunX(o, a, w, h, cx, true);  RunY(a, s, w, h, cy, true);
                    // 3. flood fill; s doubles as the "kept" flag
                    int head = 0, tail = 0;
                    for (int i = 0; i < n; i++) if (s[i] == 1) q[tail++] = i;
                    while (head < tail)
                    {
                        int i = q[head++], x = i % w;
                        if (x > 0     && s[i - 1] == 0 && o[i - 1] == 1) { s[i - 1] = 1; q[tail++] = i - 1; }
                        if (x < w - 1 && s[i + 1] == 0 && o[i + 1] == 1) { s[i + 1] = 1; q[tail++] = i + 1; }
                        if (i >= w    && s[i - w] == 0 && o[i - w] == 1) { s[i - w] = 1; q[tail++] = i - w; }
                        if (i < n - w && s[i + w] == 0 && o[i + w] == 1) { s[i + w] = 1; q[tail++] = i + w; }
                    }
                    for (int i = 0; i < n; i++)
                    {
                        if (m[i] == 1 && s[i] == 0)
                        {
                            long p = baseIdx + (long)i * 3;
                            raw[p] = raw[p + 1] = raw[p + 2] = 255;
                            buf.removed++;
                        }
                    }
                    return buf;
                },
                buf => System.Threading.Interlocked.Add(ref total, buf.removed));
            return total;
        }

        sealed class SliceBuffers
        {
            public readonly byte[] mask, a, open, seed;
            public readonly int[] queue;
            public long removed;
            public SliceBuffers(int w, int h)
            {
                int n = w * h;
                mask = new byte[n]; a = new byte[n]; open = new byte[n]; seed = new byte[n];
                queue = new int[n];
            }
        }

        // 1D window of half-width r along x. erode: out = 1 iff all in-window are 1
        // (window clipped at the edges); dilate: out = 1 iff any in-window is 1.
        static void RunX(byte[] src, byte[] dst, int w, int h, int r, bool erode)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w, sum = 0, lo = 0, hi = -1;
                for (int x = 0; x < w; x++)
                {
                    int nlo = System.Math.Max(0, x - r), nhi = System.Math.Min(w - 1, x + r);
                    while (hi < nhi) sum += src[row + ++hi];
                    while (lo < nlo) sum -= src[row + lo++];
                    int n = nhi - nlo + 1;
                    dst[row + x] = (byte)(erode ? (sum == n ? 1 : 0) : (sum > 0 ? 1 : 0));
                }
            }
        }

        static void RunY(byte[] src, byte[] dst, int w, int h, int r, bool erode)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0, lo = 0, hi = -1;
                for (int y = 0; y < h; y++)
                {
                    int nlo = System.Math.Max(0, y - r), nhi = System.Math.Min(h - 1, y + r);
                    while (hi < nhi) sum += src[++hi * w + x];
                    while (lo < nlo) sum -= src[lo++ * w + x];
                    int n = nhi - nlo + 1;
                    dst[y * w + x] = (byte)(erode ? (sum == n ? 1 : 0) : (sum > 0 ? 1 : 0));
                }
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
            var m = new Mesh { name = "FusedUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace BrainVolume
{
    /// <summary>
    /// One Nissl slide from the IIP image server, fitted into the brain (hardcoded for now: hb02 / brain 580, slide
    /// SL_354 = volume section z 150; fitted by tissue-outline overlap against the L4 export, IoU 0.95).
    ///
    /// The fit maps a volume section's L4 voxel v = (x, y) to a pixel of the slide's 1500 px thumbnail (JTL level 0,
    /// 128 um/px):   thumb = T + s * R(a) * (v - C);   full resolution (level 7, 1 um/px) = thumb * 128.
    ///
    /// * When the brain is cut down to that section (the cut face within sectionTolerance sections of it), the slide
    ///   is drawn on the cut face (its tissue only, the white background left out), over the brain. It builds up as the
    ///   cut face nears the section (revealSections: a circle growing from the middle, fading in) and goes the same way.
    /// * LeapLens asks for the slide around a point (ComposeAround): the IIP tiles of the level that fits the box
    ///   are fetched (a few at a time, cached) and drawn into a texture over the thumbnail, which is always there,
    ///   so it is never empty while tiles load. That texture is drawn on the cut face for the lens camera only
    ///   (SetLensDetail), like the slide itself: turning with the brain, its white background left out.
    /// Key I (with the Lens on, or always with keyJump): cut the brain to the slide's section.
    /// </summary>
    public sealed class IipSectionOverlay : MonoBehaviour
    {
        [Header("IIP slide (hardcoded for now)")]
        public string server = "http://deviipsrv.humanbrain.in:9081/iipsrv/fcgi-bin/iipsrv.fcgi";
        public string imagePath = "/ddn/storageIIT/humanbrain/analytics/580/NISL/B_580_HB2CV[LM]-SL_354-ST_NISL-SE_1060_lossless.tif";
        public string label = "SL_354 Nissl";
        [Tooltip("Pyramid: the coarsest level's size (px), the number of levels, the tile size (px). Level n is " +
                 "baseSize * 2^n px; the finest is 1 um/px.")]
        public int baseSize = 1500, levels = 8, tileSize = 2048;

        [Header("Fit to the volume (L4 voxels -> thumbnail px)")]
        [Tooltip("Volume section (L4 index along z) the slide matches.")]
        public int section = 150;
        public int sectionsInVolume = 485;
        public Vector2 l4Size = new Vector2(1416, 776);
        public Vector2 centre = new Vector2(708, 388);
        public float scale = 1.04f;
        public float angleDeg = -9.63f;
        public Vector2 offset = new Vector2(723.0f, 782.4f);

        [Header("On the cut face")]
        [Tooltip("The slide shows when the cut face is within this many sections of the slide's section.")]
        public float sectionTolerance = 4f;
        [Range(0f, 1f)] public float opacity = 1f;
        [Tooltip("Transition: the slide builds up over the last this-many sections before the cut face reaches its " +
                 "section (a soft circular reveal from the middle of the section, fading in), and goes away the same " +
                 "way when the cut moves off it. 0 = it just appears within sectionTolerance.")]
        public float revealSections = 14f;
        [Tooltip("Seconds the shown amount follows the cut (smooths a jump, e.g. key I or seeking).")]
        public float revealSmoothing = 0.15f;
        [Tooltip("Soft edge of the reveal circle (fraction of the section).")]
        [Range(0.01f, 0.5f)] public float revealSoftness = 0.18f;
        [Tooltip("Key I cuts the brain to the slide's section (only while the lens is on if off).")]
        public bool keyJump = false;

        [Header("Tiles")]
        public int maxConcurrent = 4;
        [Tooltip("Tiles kept in memory (~16 MB each); the least recently seen go first.")]
        public int cacheTiles = 128;
        [Tooltip("Local copy: tiles are read from StreamingAssets/<localFolder>/<slide>/<level>/<index>.jpg when there " +
                 "(Brain > IIP > Pre-download slide tiles, or saved on first view), else fetched and saved there " +
                 "(persistentDataPath if StreamingAssets is not writable).")]
        public bool diskCache = true;
        public string localFolder = "IIP";

        /// <summary>Is the cut face at the slide's section (the slide is shown there)?</summary>
        public bool OnCutFace { get; private set; }
        public float SectionUnitZ => (section + 0.5f) / Mathf.Max(1, sectionsInVolume);

        ISliceableVolume _vol;
        LeapLens _lens;
        Material _planeMat;
        Mesh _plane;
        readonly Dictionary<long, Texture2D> _tiles = new Dictionary<long, Texture2D>();
        readonly Dictionary<long, float> _used = new Dictionary<long, float>();
        readonly HashSet<long> _loading = new HashSet<long>();
        readonly HashSet<long> _failed = new HashSet<long>();
        readonly Queue<long> _want = new Queue<long>();
        int _active;
        float _faceUnitZ;
        float _shown, _shownVel;   // 0..1: how far the slide is revealed on the cut face
        Material _detailMat;       // the lens' finer tiles on the cut face (SetLensDetail)
        Mesh _detail;
        Texture _detailTex;
        Vector2 _detailCentre;
        float _detailSide;
        int _detailFrame = -1;

        Texture2D Thumb => _tiles.TryGetValue(Key(0, 0), out var t) ? t : null;

        public void Init(ISliceableVolume vol, LeapLens lens)
        {
            _vol = vol;
            _lens = lens;
            var sh = Shader.Find("Brain/SlideSection");
            if (sh == null) Debug.LogWarning("[IIP] Brain/SlideSection not found (Always Included Shaders?): no slide on the cut face.");
            else { _planeMat = new Material(sh); _detailMat = new Material(sh); }   // the detail: no reveal circle (default)
            _plane = new Mesh { name = "IipSlide" };
            _plane.MarkDynamic();
            _detail = new Mesh { name = "IipLensDetail" };
            _detail.MarkDynamic();
            Request(0, 0);   // the thumbnail: always kept
            _vol.Drawn += Draw;
        }

        // ------------------------------------------------------------------ the fit

        /// <summary>Volume unit (x, y) on the section -> the slide's full-resolution pixel (1 um/px).</summary>
        public Vector2 UnitToFullPx(Vector2 unit) => ThumbPx(new Vector2(unit.x * l4Size.x, unit.y * l4Size.y)) * FullPerThumb;
        float FullPerThumb => Mathf.Pow(2f, levels - 1);

        /// <summary>The slide's full-resolution pixel -> volume unit (x, y) on the section (the fit, inverted).</summary>
        public Vector2 FullPxToUnit(Vector2 full)
        {
            float a = -angleDeg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            Vector2 d = (full / FullPerThumb - offset) / Mathf.Max(1e-9f, scale);
            Vector2 v = centre + new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
            return new Vector2(v.x / l4Size.x, v.y / l4Size.y);
        }

        /// <summary>The cut face (volume unit z) the slide is drawn on.</summary>
        public float FaceUnitZ => _faceUnitZ;

        Vector2 ThumbPx(Vector2 v)
        {
            float a = angleDeg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            Vector2 d = v - centre;
            return offset + scale * new Vector2(c * d.x - s * d.y, s * d.x + c * d.y);
        }

        // ------------------------------------------------------------------ tiles

        static long Key(int level, int index) => ((long)level << 32) | (uint)index;
        int LevelSize(int level) => baseSize << level;
        int Cols(int level) => (LevelSize(level) + tileSize - 1) / tileSize;

        public string Url(int level, int index) =>
            $"{server}?FIF={imagePath.Replace("[", "%5B").Replace("]", "%5D")}&JTL={level},{index}";

        /// <summary>The slide's local tile folder under `root` (StreamingAssets or persistentDataPath).</summary>
        public string LocalDir(string root) =>
            System.IO.Path.Combine(root, localFolder, System.IO.Path.GetFileNameWithoutExtension(imagePath));
        public static string TileFile(string dir, int level, int index) =>
            System.IO.Path.Combine(dir, level.ToString(), index + ".jpg");

        // the tile's local copy (shipped in StreamingAssets, or saved earlier), null = none
        string LocalTile(int level, int index)
        {
            if (!diskCache) return null;
            string a = TileFile(LocalDir(Application.streamingAssetsPath), level, index);
            if (System.IO.File.Exists(a)) return a;
            string b = TileFile(LocalDir(Application.persistentDataPath), level, index);
            return System.IO.File.Exists(b) ? b : null;
        }

        // keep a fetched tile (off the main thread): StreamingAssets, else persistentDataPath
        void SaveTile(int level, int index, byte[] bytes)
        {
            if (!diskCache || bytes == null || bytes.Length == 0) return;
            string[] dirs = { LocalDir(Application.streamingAssetsPath), LocalDir(Application.persistentDataPath) };
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var dir in dirs)
                {
                    try
                    {
                        string path = TileFile(dir, level, index), tmp = path + ".part";
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                        System.IO.File.WriteAllBytes(tmp, bytes);
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                        System.IO.File.Move(tmp, path);
                        return;
                    }
                    catch (System.Exception) { }   // not writable: the next folder
                }
            });
        }

        void Request(int level, int index)
        {
            long k = Key(level, index);
            if (_tiles.ContainsKey(k) || _loading.Contains(k) || _failed.Contains(k)) return;
            if (!_want.Contains(k)) _want.Enqueue(k);
        }

        void Update()
        {
            while (_active < maxConcurrent && _want.Count > 0)
            {
                long k = _want.Dequeue();
                if (_tiles.ContainsKey(k) || _loading.Contains(k)) continue;
                StartCoroutine(Fetch(k));
            }
            Evict();
            if (keyJump || (_lens != null && _lens.Active))
            {
                var kb = Keyboard.current;
                if (kb != null && kb.iKey.wasPressedThisFrame) JumpToSection();
            }
        }

        System.Collections.IEnumerator Fetch(long k)
        {
            int level = (int)(k >> 32), index = (int)(k & 0xffffffff);
            _loading.Add(k); _active++;
            string local = LocalTile(level, index);
            string url = local != null ? new System.Uri(local).AbsoluteUri : Url(level, index);
            using (var req = UnityWebRequestTexture.GetTexture(url, true))
            {
                yield return req.SendWebRequest();
                _loading.Remove(k); _active--;
                if (req.result != UnityWebRequest.Result.Success)
                {
                    _failed.Add(k);
                    Debug.LogWarning($"[IIP] tile {level},{index}: {req.error}");
                    yield break;
                }
                if (local == null) SaveTile(level, index, req.downloadHandler.data);
                var tex = DownloadHandlerTexture.GetContent(req);
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.filterMode = FilterMode.Bilinear;
                tex.name = $"IIP {level},{index}";
                _tiles[k] = tex;
                _used[k] = Time.unscaledTime;
                if (k == Key(0, 0)) Debug.Log($"[IIP] {label}: thumbnail {tex.width}x{tex.height} loaded.");
            }
        }

        void Evict()
        {
            if (_tiles.Count <= cacheTiles) return;
            long thumb = Key(0, 0);
            var keys = new List<long>(_tiles.Keys);
            keys.Remove(thumb);
            keys.Sort((a, b) => _used[a].CompareTo(_used[b]));
            for (int i = 0; i < keys.Count && _tiles.Count > cacheTiles; i++)
            {
                if (Time.unscaledTime - _used[keys[i]] < 0.5f) break;   // still in view
                Destroy(_tiles[keys[i]]); _tiles.Remove(keys[i]); _used.Remove(keys[i]);
            }
        }

        /// <summary>Draw the slide around full-resolution pixel `centreFull`, `widthFull` px wide (1 um/px), into
        /// `rt` (its aspect sets the height): the thumbnail, then the level whose pixels best fit rt's width.
        /// Returns the level drawn on top (-1 = nothing loaded yet); `loading` = some of its tiles are still coming.</summary>
        public int ComposeAround(RenderTexture rt, Vector2 centreFull, float widthFull, out bool loading)
        {
            loading = false;
            float heightFull = widthFull * rt.height / Mathf.Max(1, rt.width);
            // the level with at least as many pixels across the region as the texture has
            int want = 0;
            for (int l = 0; l < levels; l++)
            {
                want = l;
                if (widthFull / Mathf.Pow(2f, levels - 1 - l) >= rt.width) break;
            }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.white);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, rt.width, rt.height, 0);   // y down, like the image
            int top = -1;
            foreach (int l in want > 0 ? new[] { 0, want } : new[] { 0 })
            {
                float f = Mathf.Pow(2f, levels - 1 - l);                // full px per level-l px
                float x0 = (centreFull.x - 0.5f * widthFull) / f, y0 = (centreFull.y - 0.5f * heightFull) / f;
                float x1 = (centreFull.x + 0.5f * widthFull) / f, y1 = (centreFull.y + 0.5f * heightFull) / f;
                float k = rt.width / Mathf.Max(1e-3f, x1 - x0);         // texture px per level-l px
                int size = LevelSize(l), cols = Cols(l);
                int c0 = Mathf.Max(0, Mathf.FloorToInt(x0 / tileSize)), c1 = Mathf.Min(cols - 1, Mathf.FloorToInt(x1 / tileSize));
                int r0 = Mathf.Max(0, Mathf.FloorToInt(y0 / tileSize)), r1 = Mathf.Min(cols - 1, Mathf.FloorToInt(y1 / tileSize));
                bool any = false;
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        if (c * tileSize >= size || r * tileSize >= size) continue;
                        long key = Key(l, r * cols + c);
                        if (!_tiles.TryGetValue(key, out var tex)) { Request(l, r * cols + c); loading = true; continue; }
                        _used[key] = Time.unscaledTime;
                        var dst = new Rect((c * tileSize - x0) * k, (r * tileSize - y0) * k, tex.width * k, tex.height * k);
                        Graphics.DrawTexture(dst, tex);
                        any = true;
                    }
                if (any) top = l;
            }
            GL.PopMatrix();
            RenderTexture.active = prev;
            return top;
        }

        /// <summary>The level ComposeAround would draw for a region this wide (full px) into this many texture px.</summary>
        public int LevelFor(float widthFull, int texWidth)
        {
            for (int l = 0; l < levels; l++) if (widthFull / Mathf.Pow(2f, levels - 1 - l) >= texWidth) return l;
            return levels - 1;
        }

        /// <summary>Cut the brain so its cut face is the slide's section.</summary>
        public void JumpToSection()
        {
            if (_vol == null) return;
            float z = SectionUnitZ;
            _vol.SlicePosition = _vol.SliceFromHighZ ? 1f - z : z;
            Debug.Log($"[IIP] Cut to section {section} ({label}).");
        }

        // ------------------------------------------------------------------ the slide on the cut face

        void LateUpdate()
        {
            OnCutFace = false;
            float target = 0f;
            if (_vol != null && _vol.Loaded && Thumb != null && _vol.SlicePosition > 0f)
            {
                float cut = _vol.SlicePosition;
                _faceUnitZ = _vol.SliceFromHighZ ? 1f - cut : cut;
                float d = Mathf.Abs(_faceUnitZ - SectionUnitZ) * sectionsInVolume;   // sections from the slide's
                OnCutFace = d <= sectionTolerance;
                target = revealSections > 0f ? 1f - Mathf.SmoothStep(0f, 1f, d / revealSections) : (OnCutFace ? 1f : 0f);
            }
            _shown = revealSmoothing > 0f
                ? Mathf.SmoothDamp(_shown, target, ref _shownVel, revealSmoothing, Mathf.Infinity, Time.unscaledDeltaTime)
                : target;
            if ((_shown < 0.001f && target == 0f) || _vol == null || _vol.SlicePosition <= 0f) { _shown = 0f; _shownVel = 0f; }   // uncut: gone at once
        }

        void Draw(Camera cam)
        {
            if (_shown <= 0f || _planeMat == null || cam == null || CardOverlay.IsSnapshot(cam) || opacity <= 0f) return;
            // the section's rectangle at the cut face; uvs from the fit (affine, so exact at the corners)
            Matrix4x4 m = _vol.UnitCubeToWorld;
            var corners = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var v = new Vector3[4];
            var uv = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                v[i] = m.MultiplyPoint(new Vector3(corners[i].x, corners[i].y, _faceUnitZ));
                Vector2 t = ThumbPx(new Vector2(corners[i].x * l4Size.x, corners[i].y * l4Size.y));
                uv[i] = new Vector2(t.x / baseSize, 1f - t.y / baseSize);   // texture rows run bottom-up
            }
            _plane.Clear();
            _plane.vertices = v; _plane.uv = uv; _plane.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            _planeMat.mainTexture = Thumb;
            // the reveal: a circle from the middle of the section (in the slide's uvs) growing past its corners, fading in
            Vector2 mid = ThumbPx(new Vector2(0.5f * l4Size.x, 0.5f * l4Size.y)) / baseSize;
            float reach = 0f;
            for (int i = 0; i < 4; i++) reach = Mathf.Max(reach, Vector2.Distance(uv[i], new Vector2(mid.x, 1f - mid.y)));
            float e = _shown * _shown * (3f - 2f * _shown);
            _planeMat.SetVector("_Reveal", new Vector4(mid.x, 1f - mid.y, (reach + revealSoftness * reach) * e, revealSoftness * reach));
            _planeMat.SetFloat("_Alpha", opacity * Mathf.Clamp01(_shown * 1.6f));
            _planeMat.SetPass(0);
            Graphics.DrawMeshNow(_plane, Matrix4x4.identity);

            // the lens' finer tiles over it, for the lens camera only (SetLensDetail this frame)
            if (_detailTex != null && _detailFrame == Time.frameCount && LeapLens.IsLensCamera(cam) && _detailMat != null)
            {
                Vector2 lo = _detailCentre - 0.5f * new Vector2(_detailSide, _detailSide);
                var full = new[] { lo, lo + new Vector2(_detailSide, 0f), lo + new Vector2(_detailSide, _detailSide), lo + new Vector2(0f, _detailSide) };
                for (int i = 0; i < 4; i++)
                {
                    Vector2 u = FullPxToUnit(full[i]);
                    v[i] = m.MultiplyPoint(new Vector3(u.x, u.y, _faceUnitZ));
                    uv[i] = new Vector2((full[i].x - lo.x) / _detailSide, 1f - (full[i].y - lo.y) / _detailSide);   // rows run bottom-up
                }
                _detail.Clear();
                _detail.vertices = v; _detail.uv = uv; _detail.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                _detailMat.mainTexture = _detailTex;
                _detailMat.SetFloat("_Alpha", opacity * Mathf.Clamp01(_shown * 1.6f));
                _detailMat.SetPass(0);
                Graphics.DrawMeshNow(_detail, Matrix4x4.identity);
            }
        }

        /// <summary>LeapLens, before rendering its camera: `tex` holds the slide's square `sideFull` px wide around
        /// full px `centreFull` (ComposeAround); this frame the lens camera sees it on the cut face over the thumbnail.</summary>
        public void SetLensDetail(Texture tex, Vector2 centreFull, float sideFull)
        {
            _detailTex = tex; _detailCentre = centreFull; _detailSide = sideFull;
            _detailFrame = Time.frameCount;
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            foreach (var t in _tiles.Values) if (t != null) Destroy(t);
            _tiles.Clear();
            if (_planeMat != null) Destroy(_planeMat);
            if (_plane != null) Destroy(_plane);
            if (_detailMat != null) Destroy(_detailMat);
            if (_detail != null) Destroy(_detail);
        }
    }
}

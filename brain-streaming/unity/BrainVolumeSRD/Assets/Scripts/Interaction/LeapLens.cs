using SRD.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BrainVolume
{
    /// <summary>
    /// Lens mode: a crosshair you move over the brain, and a box at the lower left of the display (in front of the
    /// brain) that shows the brain around the crosshair. Turned on / off by the menu bar's "Lens" button
    /// (PresentationMenu).
    ///
    /// The crosshair follows the lens hand (LeapBrainManipulator attaches one hand, as for the glass slide), just
    /// past its fingertips, or the mouse cursor: whichever moved last. It sits on the brain where the line of sight
    /// through that point first meets tissue (BrickVolumeLoader.RaycastTissue): the outer surface, or the cut face
    /// where the brain is sliced. A pinch with that hand or a left click
    /// stops it on that spot of the brain (amber; it turns with the brain); the next pinch / click lets it move
    /// again. Under the box, the crosshair's coordinates in the brain: mm from the volume's corner (x = image
    /// columns, y = image rows from the top, z = sections) and the voxel in the loaded level; stopping copies them
    /// to the clipboard and logs them. The other hand still grabs to turn
    /// the brain. Lens and Slice are one-hand tools: turning one on turns the other off.
    ///
    /// The box's view: a hidden camera at the viewer's head (the SRD WatcherCamera's pose, else the flat-screen
    /// camera) looking at the crosshair, with the box's own angular size as its field of view, so the brain shows
    /// at the same size as around it (no magnification yet). Rendered into a texture every frame in LateUpdate; the
    /// volumes draw into it like into any camera (OnRenderObject); the hands, slide, cursors, crosshair and box skip
    /// it (IsLensCamera). Crosshair and box are drawn from the volume's Drawn event, so the brain never hides them.
    /// </summary>
    [DefaultExecutionOrder(100)]   // after the menu (a click on a button is not a lens click) and the manipulator
    public sealed class LeapLens : MonoBehaviour
    {
        [Tooltip("Lens mode on from the start (else the menu's Lens button turns it on).")]
        public bool activeOnStart = false;

        [Header("Crosshair")]
        [Tooltip("Crosshair ahead of the palm, along the hand (real metres; palm to fingertips is about 0.09).")]
        public float reachMetres = 0.10f;
        [Tooltip("Crosshair size (real metres, × the hands' scale).")]
        public float crosshairMetres = 0.03f;
        public Color crosshairColor = new Color(0.18f, 0.83f, 0.75f, 0.95f);
        [Tooltip("Crosshair (and box border) while stopped (pinch / left click to stop and move again).")]
        public Color stoppedColor = new Color(1.00f, 0.80f, 0.30f, 0.95f);

        [Header("Box (on the display panel, in front of the brain)")]
        [Tooltip("Box centre on the panel: x -1..1 (left..right), y 0..1 (bottom..top).")]
        public Vector2 boxPosition = new Vector2(-0.70f, 0.32f);
        [Tooltip("Box width and height as fractions of the panel width.")]
        public Vector2 boxSize = new Vector2(0.20f, 0.18f);
        [Tooltip("Box offset toward the viewer, as a fraction of the panel width.")]
        public float boxTowardViewer = 0.02f;
        [Tooltip("Box view texture width (pixels; the height follows the box's shape).")]
        public int resolution = 768;
        public Color background = new Color(0.02f, 0.025f, 0.035f, 1f);
        public Color borderColor = new Color(0.78f, 0.97f, 1.00f, 0.9f);

        [Header("Zoom (mouse wheel or + / -, x2 a step)")]
        [Tooltip("Magnification of the box (1 = the size things have around the box). On the IIP slide the tile " +
                 "level follows it, down to the slide's full 1 um/px.")]
        public float zoom = 1f;
        public float maxZoom = 512f;

        [Header("Coordinates (under the box)")]
        [Tooltip("Show the crosshair's position in the brain under the box: mm from the volume's corner (x = image " +
                 "columns, y = image rows from the top, z = sections) and the voxel in the loaded level.")]
        public bool showCoordinates = true;
        [Tooltip("Line height as a fraction of the box height.")]
        public float lineHeight = 0.11f;
        public Color textColor = new Color(0.92f, 0.96f, 1.00f, 1f);
        public Color textBackground = new Color(0.055f, 0.071f, 0.102f, 0.85f);
        [Tooltip("Stopping the crosshair copies its coordinates to the clipboard (and the console).")]
        public bool copyOnStop = true;

        /// <summary>Is lens mode on?</summary>
        public bool Active { get; private set; }
        /// <summary>Is the crosshair stopped (not following the hand / mouse)?</summary>
        public bool Stopped { get; private set; }
        /// <summary>The crosshair, world.</summary>
        public Vector3 Point => _point;
        /// <summary>The crosshair in the volume's unit cube (0..1 across the brain: x columns, y rows from the top,
        /// z sections), its position in mm from the volume's corner (zero if the extent is unknown), and whether it
        /// sits on the brain (its surface or cut face; false = the line of sight misses it).</summary>
        public Vector3 Unit { get; private set; }
        public Vector3 Mm { get; private set; }
        public bool OnBrain { get; private set; }
        /// <summary>The coordinate readout, one line per entry.</summary>
        public string[] Lines => _lines;

        /// <summary>Is a lens on? Then the mouse wheel and + / - zoom its box, not the brain (ModelMoveController).</summary>
        public static bool TakesZoom => _instance != null && _instance.Active;
        static LeapLens _instance;

        static Camera _lensCam;
        /// <summary>Is this the hidden camera that renders the box's view? (Hands, slide, cursors skip it.)</summary>
        public static bool IsLensCamera(Camera cam) => cam != null && cam == _lensCam;

        ISliceableVolume _vol;
        LeapBrainManipulator _hands;
        NeuronalLossSequence[] _timelines;
        Camera _watcher;
        RenderTexture _rt;
        Material _boxMat, _flatMat;
        Mesh _quad, _bars;
        bool _placed;            // the crosshair has a position
        bool _byMouse;           // the mouse moved last (else the hand)
        bool _rendered;          // the box's view was rendered this frame
        Vector3 _point;          // crosshair, world (smoothed)
        Vector3 _handPoint, _handPrev;
        bool _handSeen;
        Vector3 _eye, _eyeUp;    // the head pose the view was rendered from
        Vector3 _boxCentre, _boxRight, _boxUp;   // the box on the panel (half-extents)
        BrainVolume.SRD.BrickVolumeLoader _loader;   // the bricked brain: its extent (mm) and voxel size
        string[] _lines = new string[0];
        Vector3 _stoppedUnit;    // where the crosshair was stopped, in the volume's unit cube
        IipSectionOverlay _iip;  // the IIP slide fitted into the brain
        RenderTexture _slideRt;  // the box's view of the slide
        bool _onSlide, _slideLoading;
        int _slideLevel = -1;
        Vector2 _slidePx;
        Font _font;
        Material _textMat;
        Mesh _text, _textBg;
        const int FontPixels = 48;

        /// <summary>Called by LeapBrainManipulator once it has the brain.</summary>
        public void Init(ISliceableVolume vol, LeapBrainManipulator hands)
        {
            _vol = vol;
            _hands = hands;
            _instance = this;
            _timelines = FindObjectsByType<NeuronalLossSequence>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var sh = Shader.Find("Brain/Lens");
            if (sh == null) Debug.LogWarning("[Lens] Brain/Lens not found (Always Included Shaders?): no box.");
            else _boxMat = new Material(sh);
            var flat = Shader.Find("Brain/OverlayUnlit");
            if (flat != null) _flatMat = new Material(flat);
            _quad = BuildQuad();
            _loader = vol as BrainVolume.SRD.BrickVolumeLoader;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var tsh = Shader.Find("Brain/OverlayText");
            if (tsh != null && _font != null) _textMat = new Material(tsh) { mainTexture = _font.material.mainTexture };
            _text = new Mesh { name = "LensText" };
            _text.MarkDynamic();
            _textBg = new Mesh { name = "LensTextBg" };
            _textBg.MarkDynamic();
            _bars = new Mesh { name = "Crosshair" };
            _bars.MarkDynamic();
            // the IIP slide (first, so it is drawn under the crosshair and box)
            _iip = GetComponent<IipSectionOverlay>();
            if (_iip == null) _iip = gameObject.AddComponent<IipSectionOverlay>();
            _iip.Init(vol, this);
            _vol.Drawn += Draw;
            if (activeOnStart) SetActive(true);
        }

        public void Toggle() => SetActive(!Active);

        public void SetActive(bool on)
        {
            if (on == Active || _vol == null) return;
            Active = on;
            Stopped = false;
            _placed = false;
            _handSeen = false;
            if (on && _hands != null && _hands.slide != null) _hands.slide.SetActive(false);   // one tool per hand
            Debug.Log(on ? "[Lens] Lens on." : "[Lens] Lens off.");
        }

        /// <summary>Stop the crosshair where it is / let it move again (the lens hand's pinch, a left click).</summary>
        public void ToggleStopped()
        {
            if (!Active || !_placed) return;
            Stopped = !Stopped;
            if (!Stopped) { Debug.Log("[Lens] Crosshair moving."); return; }
            _stoppedUnit = _vol.UnitCubeToWorld.inverse.MultiplyPoint(_point);
            UpdateCoordinates();
            string text = string.Join("  |  ", _lines);
            Debug.Log("[Lens] Crosshair stopped: " + text);
            if (copyOnStop) GUIUtility.systemCopyBuffer = text;
        }

        // The crosshair in the volume: unit cube, mm from the corner, the loaded level's voxel; the readout lines.
        void UpdateCoordinates()
        {
            Vector3 u = _vol.UnitCubeToWorld.inverse.MultiplyPoint(_point);
            Unit = u;
            Vector3 ext = _loader != null ? _loader.VolumeMm : Vector3.zero;
            Mm = Vector3.Scale(u, ext);
            var lines = new System.Collections.Generic.List<string>(3);
            if (ext != Vector3.zero)
            {
                lines.Add($"x {Mm.x:0.00}   y {Mm.y:0.00}   z {Mm.z:0.00} mm");
                Vector3 vox = _loader.VoxelMm;
                if (vox.x > 0f && vox.y > 0f && vox.z > 0f)
                    lines.Add($"L{_loader.level} voxel   {Mathf.FloorToInt(Mm.x / vox.x)}, {Mathf.FloorToInt(Mm.y / vox.y)}, {Mathf.FloorToInt(Mm.z / vox.z)}");
            }
            else lines.Add($"x {u.x:0.000}   y {u.y:0.000}   z {u.z:0.000} (of the volume)");
            if (!OnBrain) lines.Add("not on the brain");
            _lines = lines.ToArray();
        }

        /// <summary>Called every frame by LeapBrainManipulator with the lens hand: palm and hand direction (palm to
        /// fingers), world. tracked = false while the hand is at the menu or lost.</summary>
        public void Follow(bool tracked, Vector3 palm, Vector3 direction)
        {
            if (!Active || !tracked) { _handSeen = false; return; }
            _handPoint = palm + direction.normalized * (reachMetres * Scale);
            // the hand takes over from the mouse once it really moves (not on tracking jitter)
            if (!_handSeen) { _handPrev = _handPoint; _handSeen = true; if (!_placed) _byMouse = false; }
            else if (Vector3.Distance(_handPoint, _handPrev) > 0.004f * Scale) { _byMouse = false; _handPrev = _handPoint; }
        }

        float Scale => _hands != null ? _hands.HandScale : 1f;
        float Zoom => Mathf.Clamp(zoom, 1f, Mathf.Max(1f, maxZoom));

        // mouse wheel / + / -: x2 per step
        void HandleZoom()
        {
            int steps = 0;
            var mouse = Mouse.current;
            if (mouse != null)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel > 0.01f) steps++; else if (wheel < -0.01f) steps--;
            }
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) steps++;
                if (kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) steps--;
            }
            if (steps == 0) return;
            zoom = Mathf.Clamp(Zoom * Mathf.Pow(2f, steps), 1f, Mathf.Max(1f, maxZoom));
            Debug.Log($"[Lens] Zoom x{zoom:0.#}");
        }

        Camera Watcher => FindWatcher(ref _watcher);

        /// <summary>The camera the viewer looks through (the mouse ray is cast from it): the SRD WatcherCamera, else the
        /// flat-screen rig's, else Camera.main. `cache` keeps the last one found.</summary>
        public static Camera FindWatcher(ref Camera cache)
        {
            // only its pose (and its projection, for the mouse) is used, so a disabled camera is fine: the SRD
            // keeps its WatcherCamera disabled (the eye cameras render) but moves it with the tracked head
            if (cache != null && cache.gameObject.activeInHierarchy) return cache;
            cache = null;
            var srd = SRDSceneEnvironment.GetSRDManager();
            if (srd != null && srd.isActiveAndEnabled)
                foreach (var c in srd.GetComponentsInChildren<Camera>(true))
                    if (c.name.StartsWith("WatcherCamera") && c.gameObject.activeInHierarchy) { cache = c; break; }
            if (cache == null && FlatDisplayRig.Instance != null) cache = FlatDisplayRig.Instance.viewCamera;
            if (cache == null) cache = Camera.main;
            return cache;
        }

        // ------------------------------------------------------------------ crosshair (hand / mouse)

        void Update()
        {
            if (!Active || _vol == null || !_vol.Loaded) return;
            HandleZoom();
            var head = Watcher;
            var mouse = Mouse.current;

            // the mouse takes over when it moves; a left click (not on the menu / seek bar) stops / restarts
            if (mouse != null && head != null)
            {
                if (mouse.delta.ReadValue().sqrMagnitude > 0.25f) _byMouse = true;
                if (mouse.leftButton.wasPressedThisFrame && !TimelineTransportUI.PointerCaptured) ToggleStopped();
            }
            // stopped: the crosshair stays on that spot of the brain (it turns with it)
            if (Stopped) { _point = _vol.UnitCubeToWorld.MultiplyPoint(_stoppedUnit); return; }

            // the line of sight through the mouse cursor / the hand's point
            Ray ray;
            if (_byMouse && mouse != null && head != null) ray = head.ScreenPointToRay(mouse.position.ReadValue());
            else if (_handSeen && head != null) ray = new Ray(head.transform.position, _handPoint - head.transform.position);
            else return;

            // on the brain where that line first meets tissue: the surface, or the cut face where it is sliced;
            // off the brain, on the plane through its centre (mouse) or at the hand
            if (_loader != null && _loader.RaycastTissue(ray, out Vector3 hit)) { _point = hit; OnBrain = true; }
            else
            {
                OnBrain = false;
                if (_byMouse)
                {
                    Vector3 c = _vol.UnitCubeToWorld.MultiplyPoint(new Vector3(0.5f, 0.5f, 0.5f));
                    if (!new Plane(-ray.direction, c).Raycast(ray, out float t)) return;
                    _point = ray.GetPoint(t);
                }
                else _point = _handPoint;
            }
            _placed = true;
        }

        // ------------------------------------------------------------------ the box's view

        void LateUpdate()
        {
            _rendered = false;
            if (!Active || !_placed || _vol == null || !_vol.Loaded) return;
            UpdateCoordinates();   // after the brain and the crosshair have moved this frame
            if (_boxMat == null) return;
            var head = Watcher;
            if (head == null || !PlaceBox()) return;

            _eye = head.transform.position;
            Vector3 toPoint = _point - _eye;
            if (toPoint.sqrMagnitude < 1e-8f) return;
            _eyeUp = _boxUp.normalized;   // the view's up = the box's up, so it lands on the box unrotated

            EnsureCamera();
            float boxDist = Mathf.Max(1e-4f, Vector3.Distance(_eye, _boxCentre));

            // on the IIP slide's section (the brain cut there, the crosshair on the cut face): the slide's tiles
            _onSlide = false;
            if (_iip != null && _iip.OnCutFace && OnBrain && _loader != null
                && Mathf.Abs(Unit.z - _iip.SectionUnitZ) * _iip.sectionsInVolume <= _iip.sectionTolerance + 1f)
            {
                // the region the box covers at the crosshair (1x = the size it has around the box), / zoom, in
                // slide px (1 um/px, times the fit's scale: the slide is a little bigger than the volume)
                float regionWorld = 2f * _boxRight.magnitude * toPoint.magnitude / boxDist / Zoom;
                float mmPerWorld = _loader.VolumeMm.x / Mathf.Max(1e-9f, ((Vector3)_vol.UnitCubeToWorld.GetColumn(0)).magnitude);
                float widthFull = regionWorld * mmPerWorld * 1000f * _iip.scale;
                _slideLevel = _iip.ComposeAround(_slideRt, _iip.UnitToFullPx(new Vector2(Unit.x, Unit.y)), widthFull, out _slideLoading);
                _slidePx = _iip.UnitToFullPx(new Vector2(Unit.x, Unit.y));
                _onSlide = true;
                _rendered = true;
                AddSlideLine();
                BuildText();
                return;
            }

            // the box's own angular size: the brain shows at the size it has around the box (1x), / zoom
            _lensCam.transform.SetPositionAndRotation(_eye, Quaternion.LookRotation(toPoint, _eyeUp));
            _lensCam.fieldOfView = 2f * Mathf.Atan(_boxUp.magnitude / boxDist / Zoom) * Mathf.Rad2Deg;
            _lensCam.aspect = _boxRight.magnitude / Mathf.Max(1e-6f, _boxUp.magnitude);
            _lensCam.nearClipPlane = Mathf.Max(1e-4f, head.nearClipPlane);
            _lensCam.farClipPlane = Mathf.Max(head.farClipPlane, toPoint.magnitude * 4f);
            _lensCam.backgroundColor = background;
            _lensCam.Render();
            _rendered = true;
            BuildText();
        }

        // the slide line of the readout: which slide, its pyramid level, the zoom and the pixel at the crosshair
        void AddSlideLine()
        {
            var l = new System.Collections.Generic.List<string>(_lines);
            string lvl = _slideLevel >= 0 ? $"JTL {_slideLevel}{(_slideLoading ? " (loading)" : "")}" : "loading";
            l.Add($"{_iip.label}   {lvl}   x{Zoom:0.#}   px {_slidePx.x:0}, {_slidePx.y:0}");
            _lines = l.ToArray();
        }

        // The readout under the box, on the panel: a glyph quad per character (the built-in font's texture) and a
        // dark backing behind the lines.
        void BuildText()
        {
            _text.Clear(); _textBg.Clear();
            if (!showCoordinates || _textMat == null || _lines.Length == 0) return;
            Vector3 right = _boxRight.normalized, up = _boxUp.normalized;
            float h = 2f * _boxUp.magnitude * Mathf.Max(0.02f, lineHeight);    // line height (world)
            float s = h * 0.75f / FontPixels;                                   // world per font pixel
            float pad = h * 0.35f;
            Vector3 topLeft = _boxCentre - _boxRight - _boxUp - up * (h * 0.25f);   // just under the box's left edge

            var v = new System.Collections.Generic.List<Vector3>(256);
            var uv = new System.Collections.Generic.List<Vector2>(256);
            var t = new System.Collections.Generic.List<int>(384);
            float widest = 0f;
            foreach (var line in _lines) _font.RequestCharactersInTexture(line, FontPixels);
            for (int li = 0; li < _lines.Length; li++)
            {
                float pen = pad, baseline = -(pad + (li + 0.78f) * h);
                foreach (char ch in _lines[li])
                {
                    if (!_font.GetCharacterInfo(ch, out CharacterInfo ci, FontPixels)) continue;
                    int i = v.Count;
                    Vector3 o = topLeft + right * pen + up * baseline;
                    v.Add(o + right * (ci.minX * s) + up * (ci.minY * s));
                    v.Add(o + right * (ci.maxX * s) + up * (ci.minY * s));
                    v.Add(o + right * (ci.maxX * s) + up * (ci.maxY * s));
                    v.Add(o + right * (ci.minX * s) + up * (ci.maxY * s));
                    uv.Add(ci.uvBottomLeft); uv.Add(ci.uvBottomRight); uv.Add(ci.uvTopRight); uv.Add(ci.uvTopLeft);
                    t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
                    pen += ci.advance * s;
                }
                widest = Mathf.Max(widest, pen + pad);
            }
            _text.SetVertices(v); _text.SetUVs(0, uv); _text.SetTriangles(t, 0);

            float height = 2f * pad + _lines.Length * h;
            float width = Mathf.Max(widest, 2f * _boxRight.magnitude);
            _textBg.SetVertices(new System.Collections.Generic.List<Vector3> {
                topLeft, topLeft + right * width, topLeft + right * width - up * height, topLeft - up * height });
            _textBg.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        }

        // The box on the display panel (the panel the timelines use), a little in front of it.
        bool PlaceBox()
        {
            NeuronalLossSequence tl = null;
            if (_timelines != null) foreach (var t in _timelines) if (t != null && t.HasPanel) { tl = t; break; }
            if (tl == null) return false;
            float pw = tl.PanelWidth;
            Quaternion rot = tl.PanelRotation();   // forward = away from the viewer
            _boxCentre = tl.PanelPoint(boxPosition) - rot * Vector3.forward * (boxTowardViewer * pw);
            _boxRight = rot * Vector3.right * (0.5f * boxSize.x * pw);
            _boxUp = rot * Vector3.up * (0.5f * boxSize.y * pw);
            return true;
        }

        void EnsureCamera()
        {
            if (_lensCam == null)
            {
                var go = new GameObject("LensCamera");
                go.transform.SetParent(transform, false);
                _lensCam = go.AddComponent<Camera>();
                _lensCam.enabled = false;                    // rendered by hand, once a frame
                _lensCam.clearFlags = CameraClearFlags.SolidColor;
                _lensCam.cullingMask = 1 << 31;              // no scene geometry (31: the hidden card canvases); the volumes draw in OnRenderObject
                _lensCam.allowHDR = false;
                _lensCam.allowMSAA = false;
            }
            int w = Mathf.Clamp(resolution, 128, 2048);
            int h = Mathf.Clamp(Mathf.RoundToInt(w * boxSize.y / Mathf.Max(1e-3f, boxSize.x)), 64, 2048);
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LensView" };
                _rt.Create();
                if (_slideRt != null) { _slideRt.Release(); Destroy(_slideRt); }
                _slideRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LensSlide" };
                _slideRt.Create();
            }
            _lensCam.targetTexture = _rt;
        }

        // ------------------------------------------------------------------ drawing (both eyes)

        void Draw(Camera cam)
        {
            if (!Active || !_placed || cam == null || IsLensCamera(cam) || CardOverlay.IsSnapshot(cam)) return;
            Color mark = Stopped ? stoppedColor : crosshairColor;

            // the box: the view, a rounded border, a small mark at its centre (the crosshair's spot)
            if (_rendered)
            {
                _boxMat.SetTexture("_MainTex", _onSlide ? _slideRt : _rt);
                _boxMat.SetColor("_BorderColor", borderColor);
                _boxMat.SetColor("_MarkColor", mark);
                _boxMat.SetFloat("_Aspect", _boxRight.magnitude / Mathf.Max(1e-6f, _boxUp.magnitude));
                _boxMat.SetPass(0);
                Graphics.DrawMeshNow(_quad, Matrix4x4.TRS(_boxCentre, Quaternion.LookRotation(Vector3.Cross(_boxRight, _boxUp), _boxUp),
                                                         new Vector3(_boxRight.magnitude, _boxUp.magnitude, 1f)));

                // the coordinates under it
                if (_textMat != null && _text.vertexCount > 0 && _flatMat != null)
                {
                    _flatMat.SetColor("_Color", textBackground);
                    _flatMat.SetPass(0);
                    Graphics.DrawMeshNow(_textBg, Matrix4x4.identity);
                    _textMat.mainTexture = _font.material.mainTexture;   // the font texture may have been rebuilt
                    _textMat.SetColor("_Color", textColor);
                    _textMat.SetPass(0);
                    Graphics.DrawMeshNow(_text, Matrix4x4.identity);
                }
            }

            // the crosshair: four bars round a gap and a centre dot, facing this eye
            if (_flatMat == null) return;
            Vector3 toCam = cam.transform.position - _point;
            if (toCam.sqrMagnitude < 1e-10f) return;
            Vector3 n = toCam.normalized;
            Vector3 r = Vector3.Cross(cam.transform.up, n).normalized, u = Vector3.Cross(n, r);
            float s = 0.5f * crosshairMetres * Scale, gap = s * 0.3f, w = s * 0.07f;
            var v = new System.Collections.Generic.List<Vector3>(20);
            var t = new System.Collections.Generic.List<int>(30);
            void Bar(Vector3 a, Vector3 b, Vector3 side)
            {
                int i = v.Count;
                v.Add(a - side); v.Add(a + side); v.Add(b + side); v.Add(b - side);
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            Bar(_point + r * gap, _point + r * s, u * w);
            Bar(_point - r * gap, _point - r * s, u * w);
            Bar(_point + u * gap, _point + u * s, r * w);
            Bar(_point - u * gap, _point - u * s, r * w);
            Bar(_point - r * w * 1.6f, _point + r * w * 1.6f, u * w * 1.6f);   // centre dot
            _bars.Clear();
            _bars.SetVertices(v);
            _bars.SetTriangles(t, 0);
            _flatMat.SetColor("_Color", mark);
            _flatMat.SetPass(0);
            Graphics.DrawMeshNow(_bars, Matrix4x4.identity);
        }

        // unit quad -1..1 in the xy plane, uv 0..1
        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "LensBox" };
            m.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.RecalculateBounds();
            return m;
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            if (_lensCam != null) Destroy(_lensCam.gameObject);
            _lensCam = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_slideRt != null) { _slideRt.Release(); Destroy(_slideRt); }
            if (_boxMat != null) Destroy(_boxMat);
            if (_flatMat != null) Destroy(_flatMat);
            if (_quad != null) Destroy(_quad);
            if (_bars != null) Destroy(_bars);
            if (_textMat != null) Destroy(_textMat);
            if (_text != null) Destroy(_text);
            if (_textBg != null) Destroy(_textBg);
        }
    }
}

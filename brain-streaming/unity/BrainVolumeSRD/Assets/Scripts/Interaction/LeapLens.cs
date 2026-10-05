using SRD.Utils;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// A magnifying lens in one hand: a circle that shows the brain behind it at a finer pyramid level, magnified to
    /// match. Moving the hand toward the display steps L4 -> L3 -> L2 -> L1 -> L0 (whichever are on disk), x2 each;
    /// back = coarser. The finer bricks stream in for the lens only (BrickVolumeLoader.LensLevel); until they are
    /// loaded the lens shows the next coarser level, magnified. Dots along the bottom show the level (the last
    /// blinks while it loads). Turned on / off by the
    /// menu bar's "Lens" button (PresentationMenu); while on it is attached to ONE hand (the hand that pressed Lens,
    /// else the hand there; LeapBrainManipulator picks it, as for the glass slide) and sits just past its
    /// fingertips. Move the hand and the circle moves with it, magnifying the surface it is in front of. The other
    /// hand still grabs to turn the brain. Lens and Slice are one-hand tools: turning one on turns the other off.
    /// A pinch (thumb + index) with the lens hand pins the circle where it is (its outline turns amber); the next
    /// pinch takes it back into the hand.
    ///
    /// The zoomed view: a hidden camera at the viewer's head (the SRD WatcherCamera, else the flat-screen camera)
    /// looks through the circle's centre with a field of view of (the circle's angular size / zoom), rendered into
    /// a texture every frame in LateUpdate. The volumes draw into it like into any camera (OnRenderObject); the
    /// hands, slide, cursors and the lens itself skip it (IsLensCamera). The circle faces the head with that
    /// camera's axes, so the texture lands on it unchanged.
    ///
    /// Drawn from the volume's Drawn event (like the hands and the slide), so it is never hidden by the brain.
    /// Hidden while its hand is at the menu, so it never covers the buttons.
    /// </summary>
    [DefaultExecutionOrder(100)]   // LateUpdate after the timelines / manipulator have moved the brain and the hand
    public sealed class LeapLens : MonoBehaviour
    {
        [Tooltip("Show the lens from the start (else the menu's Lens button turns it on).")]
        public bool activeOnStart = false;

        [Header("Levels (hand forward = finer)")]
        [Tooltip("Hand travel toward the display (real metres) per level: L4 -> L3 -> L2 -> L1 -> L0. Back = coarser.")]
        public float stepMetres = 0.04f;
        [Tooltip("Extra travel (fraction of a step) past a boundary before the level changes back (no flicker).")]
        [Range(0f, 0.45f)] public float stepHysteresis = 0.2f;
        [Tooltip("Not a bricked brain (no levels): this many magnification steps (x2 each) instead.")]
        public int fallbackSteps = 3;
        [Tooltip("Zoomed view texture size (pixels, square).")]
        public int resolution = 768;
        [Tooltip("Inside the lens where there is no brain.")]
        public Color background = new Color(0.02f, 0.025f, 0.035f, 1f);

        [Header("Circle on the hand (real metres, × the hands' scale)")]
        [Tooltip("Circle radius.")]
        public float radiusMetres = 0.035f;
        [Tooltip("Circle centre ahead of the palm, along the hand (palm to fingertips is about 0.09).")]
        public float reachMetres = 0.12f;
        [Tooltip("Smoothing time constant for the circle's position (seconds).")]
        public float smoothing = 0.04f;

        [Header("Outline (Brain/Lens)")]
        public Color ringColor = new Color(0.78f, 0.97f, 1.00f, 0.95f);
        [Tooltip("Outline while the circle is pinned in place (pinch to pin / release).")]
        public Color pinnedColor = new Color(1.00f, 0.80f, 0.30f, 0.95f);
        [Tooltip("Outline width as a fraction of the radius.")]
        [Range(0.01f, 0.3f)] public float ringWidth = 0.05f;

        /// <summary>Is the lens on?</summary>
        public bool Active { get; private set; }
        /// <summary>Is the circle pinned in place (not following the hand)?</summary>
        public bool Pinned { get; private set; }
        /// <summary>The level the lens shows (bricked brain), -1 = none; and its magnification.</summary>
        public int Level => _loader != null && _levels.Count > 0 ? _levels[Mathf.Min(_step, _levels.Count - 1)] : -1;
        public float Zoom => Mathf.Pow(2f, _loader != null && _levels.Count > 0 ? _levels[0] - Level : _step);

        static Camera _lensCam;
        /// <summary>Is this the hidden camera that renders the zoomed view? (Hands, slide, cursors skip it.)</summary>
        public static bool IsLensCamera(Camera cam) => cam != null && cam == _lensCam;

        ISliceableVolume _vol;
        LeapBrainManipulator _hands;
        Camera _watcher;
        RenderTexture _rt;
        Material _mat;
        Mesh _disc;
        bool _placed;            // the circle has a position (a hand is attached)
        bool _visible;           // the hand is there and not at the menu
        bool _rendered;          // the view was rendered this frame
        Vector3 _centre;         // smoothed circle centre, world
        Vector3 _eye, _eyeUp;    // the head pose the view was rendered from
        BrainVolume.SRD.BrickVolumeLoader _loader;   // the bricked brain (its finer levels stream in for the lens)
        readonly System.Collections.Generic.List<int> _levels = new System.Collections.Generic.List<int>();   // base first, then finer
        int _step;               // 0 = the base level (L4), 1 = the next finer, ...
        bool _hasRef;            // _ref is set (the hand's depth at the start of the current step band)
        float _ref;              // hand depth (real metres along the display's forward) where step 0 begins
        int _loggedLevel = -2;

        /// <summary>Called by LeapBrainManipulator once it has the brain.</summary>
        public void Init(ISliceableVolume vol, LeapBrainManipulator hands)
        {
            _vol = vol;
            _hands = hands;
            _loader = vol as BrainVolume.SRD.BrickVolumeLoader;
            var sh = Shader.Find("Brain/Lens");
            if (sh == null) Debug.LogWarning("[Lens] Brain/Lens not found (Always Included Shaders?): no lens.");
            else _mat = new Material(sh);
            _disc = BuildDisc(64);
            _vol.Drawn += Draw;
            if (activeOnStart) SetActive(true);
        }

        public void Toggle() => SetActive(!Active);

        public void SetActive(bool on)
        {
            if (on == Active || _vol == null) return;
            Active = on;
            _placed = false;
            _visible = false;
            Pinned = false;
            _step = 0;
            _hasRef = false;
            if (!on && _loader != null) _loader.LensLevel = -1;   // its finer bricks are freed
            if (on && _hands != null && _hands.slide != null) _hands.slide.SetActive(false);   // one tool per hand
            Debug.Log(on ? "[Lens] Lens on." : "[Lens] Lens off.");
        }

        /// <summary>Pin the circle where it is / take it back into the hand (the lens hand's pinch).</summary>
        public void TogglePinned()
        {
            if (!Active || !_placed) return;
            Pinned = !Pinned;
            _hasRef = false;   // back in the hand: the level stays, its band starts where the hand is
            Debug.Log(Pinned ? "[Lens] Pinned." : "[Lens] Back in the hand.");
        }

        /// <summary>Called every frame by LeapBrainManipulator with the lens hand: palm and hand direction (palm to
        /// fingers), world. visible = false while the hand is at the menu or not tracked. A pinned circle stays.</summary>
        public void Follow(bool visible, Vector3 palm, Vector3 direction)
        {
            if (Pinned) { _visible = Active; return; }
            _visible = Active && visible;
            if (!_visible) return;
            Vector3 centre = palm + direction.normalized * (reachMetres * Scale);
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.001f, smoothing));
            _centre = _placed ? Vector3.Lerp(_centre, centre, k) : centre;
            _placed = true;
            StepByDepth(palm);
        }

        // Moving the hand toward the display steps to a finer level, back to a coarser one, one level per
        // stepMetres. The bands are relative to where the hand was when it took the lens (that is the current
        // level's band), and they slide along when the hand goes past the coarsest / finest end, so pulling back
        // always gets coarser at once.
        void StepByDepth(Vector3 palm)
        {
            UpdateLevels();
            int max = (_loader != null ? _levels.Count : fallbackSteps + 1) - 1;
            if (max <= 0) { _step = 0; return; }
            Transform f = DisplayFrame.Get();
            Vector3 forward = f != null ? f.forward : Vector3.forward;   // away from the viewer
            float depth = Vector3.Dot(palm, forward) / Mathf.Max(1e-6f, Scale);
            float len = Mathf.Max(0.005f, stepMetres);
            _step = Mathf.Clamp(_step, 0, max);
            if (!_hasRef) { _ref = depth - (_step + 0.5f) * len; _hasRef = true; }
            float x = (depth - _ref) / len;   // band s = [s, s + 1)
            if (x < 0f) { _ref = depth; x = 0f; }
            else if (x > max + 1f) { _ref = depth - (max + 1f) * len; x = max + 1f; }
            if (x >= _step + 1f + stepHysteresis || x < _step - stepHysteresis)
                _step = Mathf.Clamp(Mathf.FloorToInt(x), 0, max);
        }

        // The bricked brain's levels on disk, its base (what the brain shows) first, then each finer one.
        void UpdateLevels()
        {
            if (_loader == null || _levels.Count > 0 || _loader.Levels == null) return;
            for (int i = _loader.Levels.Count - 1; i >= 0; i--)
                if (_loader.Levels[i] <= _loader.level) _levels.Add(_loader.Levels[i]);
            if (_levels.Count == 0 || _levels[0] != _loader.level) _levels.Insert(0, _loader.level);
        }

        float Scale => _hands != null ? _hands.HandScale : 1f;
        float Radius => radiusMetres * Scale;

        Camera Watcher
        {
            get
            {
                // only its pose is used, so a disabled camera is fine: the SRD keeps its WatcherCamera disabled
                // (the eye cameras render) but moves it with the tracked head
                if (_watcher != null && _watcher.gameObject.activeInHierarchy) return _watcher;
                _watcher = null;
                var srd = SRDSceneEnvironment.GetSRDManager();
                if (srd != null && srd.isActiveAndEnabled)
                    foreach (var c in srd.GetComponentsInChildren<Camera>(true))
                        if (c.name.StartsWith("WatcherCamera") && c.gameObject.activeInHierarchy) { _watcher = c; break; }
                if (_watcher == null && FlatDisplayRig.Instance != null) _watcher = FlatDisplayRig.Instance.viewCamera;
                if (_watcher == null) _watcher = Camera.main;
                return _watcher;
            }
        }

        // ------------------------------------------------------------------ the zoomed view

        void LateUpdate()
        {
            _rendered = false;
            if (!Active || !_visible || !_placed || _mat == null || _vol == null || !_vol.Loaded) return;
            var head = Watcher;
            if (head == null) return;

            _eye = head.transform.position;
            _eyeUp = head.transform.up;
            Vector3 toLens = _centre - _eye;
            float dist = toLens.magnitude;
            if (dist < 1e-4f) return;

            EnsureCamera();
            // the level's own magnification (x2 per level finer than the brain's): the circle's angular size,
            // divided by it; the loader streams that level's bricks in for this camera
            UpdateLevels();
            int lvl = Level;
            if (_loader != null) _loader.LensLevel = lvl;
            if (lvl != _loggedLevel) { _loggedLevel = lvl; Debug.Log($"[Lens] {(lvl >= 0 ? "L" + lvl + ", " : "")}x{Zoom:0.#}"); }
            float half = Mathf.Atan(Radius / dist) / Mathf.Max(1f, Zoom);
            _lensCam.transform.SetPositionAndRotation(_eye, Quaternion.LookRotation(toLens, _eyeUp));
            _lensCam.fieldOfView = 2f * half * Mathf.Rad2Deg;
            _lensCam.nearClipPlane = Mathf.Max(1e-4f, head.nearClipPlane);
            _lensCam.farClipPlane = Mathf.Max(head.farClipPlane, dist * 4f);
            _lensCam.backgroundColor = background;
            _lensCam.Render();
            _rendered = true;
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
                _lensCam.aspect = 1f;
            }
            int res = Mathf.Clamp(resolution, 128, 2048);
            if (_rt == null || _rt.width != res)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "LensView" };
                _rt.Create();
            }
            _lensCam.targetTexture = _rt;
        }

        // ------------------------------------------------------------------ drawing (both eyes)

        void Draw(Camera cam)
        {
            if (!_rendered || cam == null || IsLensCamera(cam) || CardOverlay.IsSnapshot(cam)) return;
            // the circle faces the head the view was rendered from, with the lens camera's axes (so uv = the view)
            Quaternion face = Quaternion.LookRotation(_centre - _eye, _eyeUp);
            _mat.SetTexture("_MainTex", _rt);
            _mat.SetColor("_RingColor", Pinned ? pinnedColor : ringColor);
            _mat.SetFloat("_RingWidth", ringWidth);
            // level dots along the bottom: one per level, lit up to the current one; the last blinks while loading
            int count = _loader != null ? _levels.Count : fallbackSteps + 1;
            _mat.SetFloat("_Dots", count > 1 ? count : 0);
            _mat.SetFloat("_DotsOn", _step + 1);
            _mat.SetFloat("_Loading", _loader != null && Level >= 0 && _loader.LensShownLevel != Level ? 1f : 0f);
            _mat.SetPass(0);
            Graphics.DrawMeshNow(_disc, Matrix4x4.TRS(_centre, face, Vector3.one * Radius));
        }

        // unit disc (radius 1) in the xy plane, uv 0..1 across it
        static Mesh BuildDisc(int seg)
        {
            var v = new Vector3[seg + 1];
            var uv = new Vector2[seg + 1];
            var t = new int[seg * 3];
            v[0] = Vector3.zero; uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                uv[i + 1] = new Vector2(0.5f + 0.5f * v[i + 1].x, 0.5f + 0.5f * v[i + 1].y);
                t[i * 3] = 0; t[i * 3 + 1] = (i + 1) % seg + 1; t[i * 3 + 2] = i + 1;
            }
            var m = new Mesh { name = "LensDisc" };
            m.vertices = v; m.uv = uv; m.triangles = t; m.RecalculateBounds();
            return m;
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            if (_lensCam != null) Destroy(_lensCam.gameObject);
            _lensCam = null;
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_mat != null) Destroy(_mat);
            if (_disc != null) Destroy(_disc);
        }
    }
}

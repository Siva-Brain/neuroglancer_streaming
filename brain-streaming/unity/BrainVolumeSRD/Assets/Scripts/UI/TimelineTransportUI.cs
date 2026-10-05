using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BrainVolume
{
    /// <summary>
    /// Play/pause + seek bar for NeuronalLossSequence, ported from SRD_test's StoryTransportUI
    /// (2026-09-29): a play/pause button, a scrubbable progress bar with a tick per step, the time
    /// ("0:12 / 0:22") and the current step's name. World-space uGUI in the story-card navy, placed
    /// on the Spatial Reality Display's panel near its bottom edge (zero parallax); without a display
    /// it sits in front of the view camera.
    ///
    /// Mouse (new Input System): click the button = play/pause; click or drag on the bar = seek.
    /// While the pointer is held on the bar, PointerCaptured is true and ModelMoveController ignores
    /// the mouse, so the drag does not also move the brain. The ray is cast from the SRD
    /// WatcherCamera (else Camera.main).
    ///
    /// Keys:   Space          play / pause
    ///         R              (the timeline's: brain back to its pose at the current time, video untouched)
    ///         ,  /  .        step 1 s back / forward (hold: scrub at Scrub Speed x)
    ///         [  /  ]        step 5 s back / forward
    ///         Home / End     start / end
    ///         H or T         show / hide this bar
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]   // before ModelMoveController, so PointerCaptured is set on the press frame
    public sealed class TimelineTransportUI : MonoBehaviour
    {
        static readonly Color BarBg = new Color(0.106f, 0.129f, 0.169f, 0.96f);   // story-card navy
        static readonly Color ButtonBg = new Color(0.165f, 0.190f, 0.240f, 1f);
        static readonly Color ButtonHot = new Color(0.235f, 0.263f, 0.320f, 1f);
        static readonly Color TrackCol = new Color(0.235f, 0.263f, 0.310f, 1f);
        static readonly Color FillCol = new Color(0.176f, 0.831f, 0.749f, 1f);   // accent teal
        static readonly Color TickCol = new Color(0.60f, 0.64f, 0.70f, 1f);
        static readonly Color KnobCol = new Color(0.96f, 0.965f, 0.972f, 1f);

        [Header("Scene")]
        public NeuronalLossSequence timeline;
        [Tooltip("Camera the mouse ray is cast from. Empty = the SRD WatcherCamera, else Camera.main.")]
        public Camera viewCamera;
        public bool visibleOnStart = true;

        [Header("Placement")]
        [Tooltip("ON = on the display panel at Panel Position (x -1..1, y 0..1). OFF = in front of the view camera.")]
        public bool placeOnDisplayPanel = true;
        [Tooltip("Set by PresentationMenu: the seek bar sits just below this bar (same plane and tilt) and follows it " +
                 "wherever it is placed; Panel Position etc. are then unused.")]
        public RectTransform below;
        [Tooltip("With Below: the seek bar's width as a fraction of that bar's width.")]
        public float belowWidth = 0.6f;
        [Tooltip("With Below: the gap between the two bars, in that bar's canvas units.")]
        public float belowGap = 10f;
        public Vector2 panelPosition = new Vector2(0f, 0.08f);
        [Tooltip("Offset toward the viewer along the panel normal, as a fraction of the panel width.")]
        public float towardViewer = 0.03f;
        [Tooltip("Bar width as a fraction of the display panel's width.")]
        public float widthOfPanel = 0.62f;
        public float viewerDistance = 1.0f;
        public float fallbackPitchDegrees = -14f;

        [Header("Layout (canvas units)")]
        public float barWidth = 640f, barHeight = 52f, padding = 10f;
        public float buttonSize = 34f, trackHeight = 8f, knobSize = 18f;
        public int fontSize = 19;

        [Header("Keys")]
        public bool keysEnabled = true;
        public float stepSeconds = 1f, bigStepSeconds = 5f;
        [Tooltip("Holding , or . scrubs at this many timeline seconds per real second.")]
        public float scrubSpeed = 4f;
        public float holdDelay = 0.35f;

        /// <summary>True while the mouse button is held down on this bar (ModelMoveController then ignores the mouse).</summary>
        public static bool PointerCaptured { get; internal set; }   // also set by PresentationMenu
        public bool Visible { get; private set; }

        Camera _cam;
        GameObject _root;
        RectTransform _bar, _button, _track, _fill, _knob;
        Image _buttonBg, _icon;
        Text _time, _step;
        Sprite _playSprite, _pauseSprite;
        Texture2D _playTex, _pauseTex;
        bool _built, _rebuilt, _scrubbing, _hover;
        double _holdStart = -1;
        int _holdDir;

        Camera ViewCam
        {
            get
            {
                if (viewCamera != null) return viewCamera;
                if (_cam == null || !_cam.isActiveAndEnabled)
                {
                    var w = GameObject.Find("WatcherCamera");
                    _cam = w != null ? w.GetComponent<Camera>() : null;
                    if (_cam == null) _cam = Camera.main;
                }
                return _cam;
            }
        }

        void Start()
        {
            if (timeline == null) timeline = FindFirstObjectByType<NeuronalLossSequence>();
        }

        void Update()
        {
            if (timeline == null || !timeline.Ready) return;
            if (!_built) { Build(); SetVisible(_rebuilt ? Visible : visibleOnStart); _rebuilt = true; }   // a rebuild keeps H/T's choice
            if (keysEnabled) HandleKeys();
            Place();
            if (Visible) HandleMouse();
            else { _scrubbing = false; PointerCaptured = false; }
            Refresh();
        }

        /// <summary>Drive another timeline (PresentationMenu): the bar is rebuilt for its length and step ticks.</summary>
        public void SetTimeline(NeuronalLossSequence t)
        {
            if (t == timeline) return;
            timeline = t;
            if (_root != null) Destroy(_root);
            _root = null;
            _built = false;
            _scrubbing = false;
        }

        public void SetVisible(bool on)
        {
            Visible = on;
            if (_root != null) _root.SetActive(on);
            if (!on) { _scrubbing = false; PointerCaptured = false; }
        }
        public void Toggle() { SetVisible(!Visible); }

        // ------------------------------------------------------------------ keys

        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.hKey.wasPressedThisFrame || kb.tKey.wasPressedThisFrame) Toggle();
            if (kb.spaceKey.wasPressedThisFrame) timeline.TogglePlay();
            if (kb.homeKey.wasPressedThisFrame) timeline.SeekTo(0f);
            if (kb.endKey.wasPressedThisFrame) timeline.SeekTo(timeline.TotalSeconds);
            if (kb.leftBracketKey.wasPressedThisFrame) timeline.SeekTo(timeline.Time - bigStepSeconds);
            if (kb.rightBracketKey.wasPressedThisFrame) timeline.SeekTo(timeline.Time + bigStepSeconds);

            int dir = kb.periodKey.isPressed ? 1 : kb.commaKey.isPressed ? -1 : 0;
            if (kb.periodKey.wasPressedThisFrame || kb.commaKey.wasPressedThisFrame)
            {
                timeline.SeekTo(timeline.Time + dir * stepSeconds);
                _holdStart = Time.unscaledTimeAsDouble; _holdDir = dir;
            }
            else if (dir != 0 && dir == _holdDir && _holdStart >= 0 && Time.unscaledTimeAsDouble - _holdStart > holdDelay)
                timeline.SeekTo(timeline.Time + dir * scrubSpeed * Time.unscaledDeltaTime);
            else if (dir == 0) _holdStart = -1;
        }

        // ------------------------------------------------------------------ mouse

        void HandleMouse()
        {
            var mouse = Mouse.current;
            var cam = ViewCam;
            if (mouse == null || cam == null) { _scrubbing = false; PointerCaptured = false; return; }

            bool onPlane = PointerOnCanvas(cam, mouse.position.ReadValue(), out Vector3 world);
            bool overButton = onPlane && Contains(_button, world, 6f);
            bool overTrack = onPlane && Contains(_track, world, 14f);
            _hover = overButton;

            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (overButton) { timeline.TogglePlay(); PointerCaptured = true; }
                else if (overTrack) { _scrubbing = true; PointerCaptured = true; SeekToPointer(world); }
            }
            if (mouse.leftButton.isPressed)
            {
                if (_scrubbing && onPlane) SeekToPointer(world);
            }
            else
            {
                _scrubbing = false;
                PointerCaptured = false;
            }
        }

        bool PointerOnCanvas(Camera cam, Vector2 screen, out Vector3 world)
        {
            world = Vector3.zero;
            var t = _root.transform;
            Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
            var plane = new Plane(t.forward, t.position);
            if (!plane.Raycast(ray, out float enter)) return false;
            world = ray.GetPoint(enter);
            return true;
        }

        /// <summary>Is the world point inside the element's rect (canvas units), grown by margin on every side.</summary>
        static bool Contains(RectTransform rt, Vector3 world, float margin)
        {
            Vector3 local = rt.InverseTransformPoint(world);
            var r = rt.rect;
            return local.x >= r.xMin - margin && local.x <= r.xMax + margin && local.y >= r.yMin - margin && local.y <= r.yMax + margin;
        }

        void SeekToPointer(Vector3 world)
        {
            Vector3 local = _track.InverseTransformPoint(world);
            float k = Mathf.Clamp01((local.x - _track.rect.xMin) / Mathf.Max(1f, _track.rect.width));
            timeline.SeekTo(k * timeline.TotalSeconds);
        }

        // ------------------------------------------------------------------ placement

        // Also after every Update: the bar followed (PresentationMenu) places itself later in the frame.
        void LateUpdate()
        {
            if (_root != null && below != null) Place();
        }

        void Place()
        {
            var t = _root.transform;
            if (below != null && below.gameObject.activeInHierarchy)
            {
                // same plane as the bar above, centred under it: its bottom edge, the gap, then half this bar
                float unit = below.lossyScale.y;
                float s = belowWidth * below.rect.width * below.lossyScale.x / barWidth;
                t.localScale = Vector3.one * s;
                Quaternion rot = below.rotation;
                Vector3 bottom = below.TransformPoint(new Vector3(below.rect.center.x, below.rect.yMin, 0f));
                t.SetPositionAndRotation(bottom - rot * Vector3.up * (belowGap * unit + 0.5f * barHeight * s), rot);
                return;
            }
            if (placeOnDisplayPanel && timeline.HasPanel)
            {
                float pw = timeline.PanelWidth;
                t.localScale = Vector3.one * (pw * widthOfPanel / barWidth);
                Quaternion rot = timeline.PanelRotation();
                Vector3 pos = timeline.PanelPoint(panelPosition) - rot * Vector3.forward * (towardViewer * pw);
                t.SetPositionAndRotation(pos, rot);
                return;
            }
            var cam = ViewCam;
            if (cam == null) return;
            t.localScale = Vector3.one * 0.001f;
            Vector3 eye = cam.transform.position;
            Vector3 dir = Quaternion.AngleAxis(-fallbackPitchDegrees, cam.transform.right) * cam.transform.forward;
            Vector3 p = eye + dir * viewerDistance;
            t.SetPositionAndRotation(p, Quaternion.LookRotation(p - eye, cam.transform.up));
        }

        // ------------------------------------------------------------------ per-frame visuals

        void Refresh()
        {
            float total = timeline.TotalSeconds, now = timeline.Time;
            float k = total > 0f ? Mathf.Clamp01(now / total) : 0f;
            float w = _track.rect.width;
            _fill.sizeDelta = new Vector2(w * k, trackHeight);
            _knob.anchoredPosition = new Vector2(w * k, 0f);
            _icon.sprite = timeline.IsPlaying ? _pauseSprite : _playSprite;
            _buttonBg.color = _hover ? ButtonHot : ButtonBg;
            _time.text = Fmt(now) + " / " + Fmt(total);
            _step.text = timeline.CurrentMarkName;
        }

        static string Fmt(float s)
        {
            if (s < 0f) s = 0f;
            int m = (int)(s / 60f); int sec = (int)(s - m * 60);
            return m + ":" + sec.ToString("00");
        }

        // ------------------------------------------------------------------ build

        void Build()
        {
            _built = true;
            _root = new GameObject(name + "_Canvas", typeof(Canvas));
            _root.transform.SetParent(transform, false);
            _root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var group = _root.AddComponent<CanvasGroup>();
            group.interactable = false; group.blocksRaycasts = false;

            _bar = UiKit.Rect("Bar", _root.transform);
            _bar.anchorMin = _bar.anchorMax = _bar.pivot = new Vector2(0.5f, 0.5f);
            _bar.sizeDelta = new Vector2(barWidth, barHeight);
            UiKit.Stretch(UiKit.Rounded(UiKit.Panel("Bg", _bar, BarBg), barHeight * 0.5f).rectTransform);

            // play/pause button (left)
            _buttonBg = UiKit.Rounded(UiKit.Panel("Button", _bar, ButtonBg), buttonSize * 0.5f);
            _button = _buttonBg.rectTransform;
            Left(_button, padding, buttonSize, buttonSize);
            _playSprite = MakeIcon(false, out _playTex);
            _pauseSprite = MakeIcon(true, out _pauseTex);
            var iconRt = UiKit.Rect("Icon", _button);
            _icon = iconRt.gameObject.AddComponent<Image>();
            _icon.raycastTarget = false;
            _icon.color = Color.white;
            _icon.sprite = _playSprite;
            iconRt.anchorMin = iconRt.anchorMax = iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(buttonSize * 0.55f, buttonSize * 0.55f);

            // time (right) and step name (left of the time)
            _time = UiKit.Label("Time", _bar, "0:00 / 0:00", fontSize, FontStyle.Bold, UiKit.TextPrimary, TextAnchor.MiddleRight);
            Right(_time.rectTransform, padding, 120f, barHeight);
            _step = UiKit.Label("Step", _bar, "", fontSize, FontStyle.Normal, UiKit.TextDim, TextAnchor.MiddleRight);
            Right(_step.rectTransform, padding + 126f, 150f, barHeight);

            // track with fill, a tick per step and the knob
            float trackX = padding + buttonSize + 14f;
            float trackW = barWidth - trackX - (padding + 126f + 156f) - 8f;
            var trackImg = UiKit.Rounded(UiKit.Panel("Track", _bar, TrackCol), trackHeight * 0.5f);
            _track = trackImg.rectTransform;
            Left(_track, trackX, trackW, trackHeight);
            var fillImg = UiKit.Rounded(UiKit.Panel("Fill", _track, FillCol), trackHeight * 0.5f);
            _fill = fillImg.rectTransform;
            _fill.anchorMin = _fill.anchorMax = new Vector2(0f, 0.5f); _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = Vector2.zero; _fill.sizeDelta = new Vector2(0f, trackHeight);
            float total = timeline.TotalSeconds;
            var marks = timeline.Marks;
            for (int i = 1; i < marks.Length && total > 0f; i++)
            {
                var tr = UiKit.Panel("Tick" + i, _track, TickCol).rectTransform;
                tr.anchorMin = tr.anchorMax = new Vector2(0f, 0.5f); tr.pivot = new Vector2(0.5f, 0.5f);
                tr.sizeDelta = new Vector2(2f, trackHeight + 6f);
                tr.anchoredPosition = new Vector2(marks[i].start / total * trackW, 0f);
            }
            _knob = UiKit.Circle("Knob", _track, KnobCol).rectTransform;
            _knob.anchorMin = _knob.anchorMax = new Vector2(0f, 0.5f); _knob.pivot = new Vector2(0.5f, 0.5f);
            _knob.sizeDelta = new Vector2(knobSize, knobSize);
        }

        static void Left(RectTransform rt, float x, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, 0f);
        }
        static void Right(RectTransform rt, float x, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0.5f); rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(-x, 0f);
        }

        /// <summary>32x32 white icon on transparent: a right-pointing triangle (play) or two bars (pause).</summary>
        internal static Sprite MakeIcon(bool pause, out Texture2D tex)
        {
            const int n = 32;
            tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    bool on;
                    if (pause) on = (x >= 6 && x <= 12) || (x >= 19 && x <= 25);
                    else
                    {
                        float t = (x - 7f) / 20f;               // 0 at base, 1 at apex
                        float half = Mathf.Lerp(13f, 0.5f, Mathf.Clamp01(t));
                        on = x >= 7 && x <= 27 && Mathf.Abs(y - 15.5f) <= half;
                    }
                    px[y * n + x] = on ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            return Sprite.Create(tex, new UnityEngine.Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }

        void OnDestroy()
        {
            if (_playTex != null) Destroy(_playTex);
            if (_pauseTex != null) Destroy(_pauseTex);
            PointerCaptured = false;
        }
    }
}

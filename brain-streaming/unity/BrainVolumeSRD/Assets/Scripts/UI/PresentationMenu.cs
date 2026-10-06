using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace BrainVolume
{
    /// <summary>
    /// "Presentation All": V1-V4 in one scene, sharing one bricked Brain. Each version is its own
    /// NeuronalLossSequence (with its split brains / maps / fibres); all of them load at the start and stay in
    /// standby (NeuronalLossSequence.SetStandby) until selected. At the start only the brain is shown, still, at
    /// the idle pose; nothing plays until a version is selected.
    ///
    /// A pill-shaped menu bar on the display panel's bottom edge, after the user's sketch
    /// (scrnshot/Screenshot 2026-10-03 160451.png):   ( ▷ | V1  V2  V3  V4 )
    /// round play/pause button, then one button per version with a caption; a teal highlight slides to the
    /// selected version and a thin line in it shows the version's progress. Selecting a version starts it from
    /// 0:00 at its start pose (again = restart). The seek bar (TimelineTransportUI) sits just above while a
    /// version is selected. With Leap hands in the scene a "Slice" toggle follows the versions, behind a divider:
    /// it shows / hides the glass slide (LeapSliceSlide) that cuts the brain; then a "Lens" toggle for the
    /// magnifying lens in the hand (LeapLens).
    ///
    /// Mouse: click a button (ray from the SRD WatcherCamera, like the seek bar).
    /// Keys:  1 2 3 4 (also the numpad) select V1..V4, 0 = back to the brain only; Space etc. = the seek bar's keys.
    /// Select(i) / ShowBrainOnly() / TogglePlay() / ButtonCount / ButtonRect(i) are public for other input (hand tracking).
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after TimelineTransportUI (-50), before ModelMoveController
    public sealed class PresentationMenu : MonoBehaviour
    {
        static readonly Color BarBg = new Color(0.055f, 0.071f, 0.102f, 0.90f);
        static readonly Color BarEdge = new Color(1f, 1f, 1f, 0.14f);
        static readonly Color Shadow = new Color(0f, 0f, 0f, 0.55f);
        static readonly Color Accent = new Color(0.176f, 0.831f, 0.749f, 1f);     // accent teal (seek bar fill)
        static readonly Color AccentDark = new Color(0.03f, 0.10f, 0.11f, 1f);
        static readonly Color PlayBg = new Color(0.075f, 0.094f, 0.13f, 1f);
        static readonly Color Hover =new Color(1f, 1f, 1f, 0.08f);
        static readonly Color Clear = new Color(1f, 1f, 1f, 0f);

        [System.Serializable]
        public struct Version
        {
            public string label;
            [Tooltip("Small line under the label.")]
            public string caption;
            public NeuronalLossSequence timeline;
            [Tooltip("Stop at the end and hold there (else the version loops, like V1-V4).")]
            public bool holdAtEnd;
        }

        [Header("V5 (built at play time)")]
        [Tooltip("Add V5 to the menu at play time: a copy of version v5CopyFrom's timeline (its start pose and timings) " +
                 "that turns the brain to the left sagittal view, slices until the cut face is the IIP slide's " +
                 "section (v5Section), then a square comes out of the cut face and zooms into the IIP slide down to " +
                 "single cells (IipSlidePanel), then goes back to the start pose and loops, like V1-V4. No neuronal-loss block, no split.")]
        public bool addV5 = true;
        public string v5Label = "V5";
        public string v5Caption = "IIP Slide";
        [Tooltip("Version (0 = V1) whose timeline settings V5 starts from.")]
        public int v5CopyFrom = 1;
        [Tooltip("Section V5 cuts to (L4 index along z; 150 = the IIP slide SL_354).")]
        public int v5Section = 150;

        public Version[] versions = new Version[0];
        [Tooltip("Version shown at the start (0 = V1). -1 = only the brain, still, until a version is selected.")]
        public int startIndex = -1;
        [Tooltip("The brain-only view uses this version's start pose (3 = V4: centred).")]
        public int idlePoseFrom = 3;
        [Tooltip("Camera the mouse ray is cast from. Empty = the SRD WatcherCamera, else Camera.main.")]
        public Camera viewCamera;
        public bool keysEnabled = true;

        [Header("Placement (on the display panel, like the seek bar)")]
        [Tooltip("Centre of the bar on the panel: x -1..1, y 0..1 (0 = bottom edge).")]
        public Vector2 panelPosition = new Vector2(0f, 0.06f);
        [Tooltip("Offset toward the viewer along the panel normal, as a fraction of the panel width.")]
        public float towardViewer = 0.03f;
        [Tooltip("Extra offset toward the viewer in real metres (× SRDViewSpaceScale): the bar floats in front of " +
                 "the panel, so a fingertip can touch it (LeapMenuInteractor) clear of the brain.")]
        public float touchForwardMetres = 0.04f;
        [Tooltip("Bar width as a fraction of the display panel's width.")]
        public float widthOfPanel = 0.46f;
        [Tooltip("Where the seek bar goes (panel x -1..1, y 0..1) if Seek Bar Below is off.")]
        public Vector2 seekBarPosition = new Vector2(0f, 0.165f);
        [Tooltip("ON = the seek bar sits just below this bar, small, and follows it wherever this bar is placed. " +
                 "This bar is lifted by the seek bar's height so the pair stays above the panel's bottom edge.")]
        public bool seekBarBelow = true;
        [Tooltip("Seek bar width as a fraction of this bar's width.")]
        public float seekBarWidth = 0.6f;
        [Tooltip("Gap between this bar and the seek bar (canvas units).")]
        public float seekBarGap = 10f;
        [Tooltip("OFF = selecting a version doesn't show the seek bar; H or T shows / hides it (the choice is kept " +
                 "when switching versions). Overrides the seek bar's own Visible On Start.")]
        public bool showSeekBar = false;
        public float viewerDistance = 1.0f;
        public float fallbackPitchDegrees = -18f;

        [Header("Layout (canvas units)")]
        public float barHeight = 84f, padding = 10f;
        public float buttonWidth = 150f, gap = 6f;
        public int labelSize = 27, captionSize = 14;
        [Tooltip("Seconds-ish for the highlight to slide to a new version (smoothing time constant).")]
        public float slideTime = 0.09f;

        [Header("Slice button (glass slide, LeapSliceSlide)")]
        [Tooltip("A toggle at the right end of the bar that shows / hides the glass slide (only when the scene has one).")]
        public bool sliceButton = true;
        public string sliceLabel = "Slice";
        public string sliceCaption = "glass slide";
        public float sliceButtonWidth = 130f;

        [Header("Lens button (magnifying lens, LeapLens)")]
        [Tooltip("A toggle after Slice that shows / hides the magnifying lens in the hand (only when the scene has one).")]
        public bool lensButton = true;
        public string lensLabel = "Lens";
        public string lensCaption = "magnify";
        public float lensButtonWidth = 130f;

        public int Selected { get; private set; } = -1;
        public NeuronalLossSequence Current => Selected >= 0 ? versions[Selected].timeline : null;
        /// <summary>Buttons: 0 = play/pause, 1..n = the versions, then the Slice and Lens toggles (if any).</summary>
        public int ButtonCount => versions.Length + 1 + (_slice != null ? 1 : 0) + (_lens != null ? 1 : 0);
        /// <summary>The Slice toggle's button index, -1 = none.</summary>
        public int SliceButton => _slice != null ? versions.Length + 1 : -1;
        /// <summary>The Lens toggle's button index, -1 = none.</summary>
        public int LensButton => _lens != null ? versions.Length + 1 + (_slice != null ? 1 : 0) : -1;
        public RectTransform ButtonRect(int i) => _buttons != null && i >= 0 && i < _buttons.Length ? _buttons[i] : null;

        TimelineTransportUI _transport;
        LeapSliceSlide _slice;
        Image _sliceOn;
        float _sliceAlpha;
        LeapLens _lens;
        Image _lensOn;
        float _lensAlpha;
        global::SRD.Core.SRDManager _srd;
        bool _srdLooked;
        Camera _cam;
        GameObject _root;
        RectTransform _bar, _highlight, _progressTrack, _progressFill;
        RectTransform[] _buttons;
        Image[] _hovers;
        Text[] _labels, _captions;
        Image _playRing, _playFill, _icon;
        Sprite _playSprite, _pauseSprite;
        Texture2D _playTex, _pauseTex;
        int _hover = -1;
        int _flash = -1;
        float _flashT;
        const float FlashSeconds = 0.3f;
        Image _cursor;
        bool _cursorOn, _cursorPressed;
        float _cursorNear;
        bool _captured, _idlePlaced;
        float _hlX, _hlAlpha;

        // Awake: before the timelines' Start, so the ones not selected never prepare or move the brain.
        void Awake()
        {
            if (addV5) AddV5();
            startIndex = Mathf.Clamp(startIndex, -1, versions.Length - 1);
            for (int i = 0; i < versions.Length; i++)
                if (versions[i].timeline != null)
                {
                    versions[i].timeline.transportBar = false;   // one shared seek bar (below)
                    versions[i].timeline.loop = !versions[i].holdAtEnd;
                    if (i != startIndex) versions[i].timeline.SetStandby(true);
                }
            _transport = FindFirstObjectByType<TimelineTransportUI>();
            if (_transport == null) _transport = gameObject.AddComponent<TimelineTransportUI>();
            _transport.panelPosition = seekBarPosition;
            _transport.visibleOnStart = showSeekBar;
        }

        void Start()
        {
            // the slide is added by LeapBrainManipulator in its Awake (before this Start)
            if (sliceButton) _slice = FindFirstObjectByType<LeapSliceSlide>();
            if (lensButton) _lens = FindFirstObjectByType<LeapLens>();
            Build();
            if (seekBarBelow)
            {
                _transport.below = _bar;
                _transport.belowWidth = seekBarWidth;
                _transport.belowGap = seekBarGap;
            }
            if (startIndex >= 0) Select(startIndex);
            else _transport.SetTimeline(null);
        }

        // V5: a new timeline with version v5CopyFrom's settings (copied before its Start, so it prepares as V5),
        // then only Brain -> Rotate (left sagittal) -> Slice to v5Section, then back to the start pose and loop, like V1-V4.
        void AddV5()
        {
            foreach (var v in versions) if (v.label == v5Label) return;   // already in the scene
            if (v5CopyFrom < 0 || v5CopyFrom >= versions.Length || versions[v5CopyFrom].timeline == null)
            {
                Debug.LogWarning($"[Menu] V5: no version {v5CopyFrom + 1} to copy the timeline settings from.");
                return;
            }
            var src = versions[v5CopyFrom].timeline;
            var go = new GameObject("Timeline V5");
            go.transform.SetParent(src.transform.parent, false);
            var t = go.AddComponent<NeuronalLossSequence>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(src), t);
            ConfigureV5(t, v5Section);
            // after the slice: the square that comes out of the cut face and zooms into the IIP slide (sets the hold)
            go.AddComponent<IipSlidePanel>().Init(t);
            AddVersion(new Version { label = v5Label, caption = v5Caption, timeline = t, holdAtEnd = false });
            Debug.Log($"[Menu] V5 added at play time from '{src.name}' (Tools > Presentation > Create V5 Timeline puts it in the scene).");
        }

        /// <summary>V5's timeline settings on top of a copy of another version's: only Brain -> Rotate (left
        /// sagittal) -> Slice to `section`, then (after the IIP square) back to the start pose like V1-V4; no neuronal-loss block, split,
        /// slice-back or final turn.
        /// (Also used by the editor command that puts V5 in the scene.)</summary>
        public static void ConfigureV5(NeuronalLossSequence t, int section)
        {
            t.showNeuronalLoss = false;
            t.splitVolume = null;
            t.splitThird = null;
            t.sliceBack = false;
            t.recede = 0f;
            t.returnToStart = true;
            t.finalTurnSeconds = 0f;
            t.slicing = true;
            t.cutToSection = section;
            t.useCustomCardPose = false;
            t.controlsMoveBlock = false;   // no block: the mouse / keys keep moving the brain
        }

        /// <summary>Append a version to the menu (before Start builds the bar).</summary>
        public void AddVersion(Version v)
        {
            var list = new System.Collections.Generic.List<Version>(versions) { v };
            versions = list.ToArray();
        }

        /// <summary>Show version i (0 = V1) from 0:00. Selecting the version already shown deselects it (ShowBrainOnly).</summary>
        public void Select(int i)
        {
            if (i < 0 || i >= versions.Length || versions[i].timeline == null) return;
            if (i == Selected) { ShowBrainOnly(); Debug.Log($"[Menu] {versions[i].label}: deselected"); return; }
            for (int j = 0; j < versions.Length; j++)
                if (j != i && versions[j].timeline != null && !versions[j].timeline.Standby) versions[j].timeline.SetStandby(true);
            var t = versions[i].timeline;
            if (t.Standby) t.SetStandby(false);
            else if (t.Ready) t.Restart();
            if (Selected < 0) _hlX = ButtonX(i + 1);   // first selection: appear in place, don't slide in from the play button
            Selected = i;
            _transport.SetTimeline(t);
            Debug.Log($"[Menu] {versions[i].label}: '{t.name}'");
        }

        /// <summary>Back to the start: every version in standby, the brain still at the idle pose.</summary>
        public void ShowBrainOnly()
        {
            foreach (var v in versions) if (v.timeline != null && !v.timeline.Standby) v.timeline.SetStandby(true);
            Selected = -1;
            _transport.SetTimeline(null);
            _idlePlaced = false;
        }

        /// <summary>Play/pause the selected version; with none selected, start V1.</summary>
        public void TogglePlay()
        {
            var t = Current;
            if (t == null) Select(0);
            else if (t.Ready) t.TogglePlay();
        }

        void Update()
        {
            if (Selected < 0 && !_idlePlaced && idlePoseFrom >= 0 && idlePoseFrom < versions.Length && versions[idlePoseFrom].timeline != null)
                _idlePlaced = versions[idlePoseFrom].timeline.PlaceAtStart();
            if (keysEnabled) HandleKeys();
            Place();
            HandleMouse();
            Refresh();
        }

        void HandleKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            Key[] top = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
            Key[] pad = { Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9 };
            for (int i = 0; i < versions.Length && i < top.Length; i++)
                if (kb[top[i]].wasPressedThisFrame || kb[pad[i]].wasPressedThisFrame) Select(i);
            if (kb.digit0Key.wasPressedThisFrame || kb.numpad0Key.wasPressedThisFrame) ShowBrainOnly();
            // R with no version selected: the brain back to the idle pose (with a version, its timeline resets
            // the pose at the current time and the video keeps playing; NeuronalLossSequence.ResetPose)
            if (kb.rKey.wasPressedThisFrame && Selected < 0) _idlePlaced = false;
        }

        // ------------------------------------------------------------------ mouse

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

        void HandleMouse()
        {
            var mouse = Mouse.current;
            var cam = ViewCam;
            _hover = -1;
            if (mouse == null || cam == null || _root == null) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            var plane = new Plane(_root.transform.forward, _root.transform.position);
            if (plane.Raycast(ray, out float enter))
            {
                Vector3 world = ray.GetPoint(enter);
                for (int i = 0; i < _buttons.Length; i++)
                    if (Contains(_buttons[i], world)) { _hover = i; break; }
            }
            if (mouse.leftButton.wasPressedThisFrame && _hover >= 0)
            {
                Press(_hover);
                _captured = true;
                TimelineTransportUI.PointerCaptured = true;   // the click must not also move the brain
            }
            if (!mouse.leftButton.isPressed && _captured)
            {
                _captured = false;
                TimelineTransportUI.PointerCaptured = false;
            }
        }

        static bool Contains(RectTransform rt, Vector3 world)
        {
            Vector3 local = rt.InverseTransformPoint(world);
            return rt.rect.Contains(new Vector2(local.x, local.y));
        }

        // ------------------------------------------------------------------ placement / visuals

        float PlaySize => barHeight - 2f * padding;
        // right edge of the last version button, then (behind a divider, like the play button's) the Slice and Lens toggles
        float VersionsEnd => ButtonX(versions.Length + 1) - gap;
        float SliceX => VersionsEnd + 3f * gap + 1f;
        float LensX => SliceX + (_slice != null ? sliceButtonWidth + gap : 0f);
        float BarWidth => (_lens != null ? LensX + lensButtonWidth : _slice != null ? SliceX + sliceButtonWidth : VersionsEnd) + padding;
        // the bar's size is set without the toggles, so it doesn't make the other buttons smaller
        float SizingWidth => VersionsEnd + padding;
        // left edge of button i in bar coordinates (0 = play)
        float ButtonX(int i) => i == 0 ? padding : padding + PlaySize + 2f * gap + 1f + gap + (i - 1) * (buttonWidth + gap);

        void Place()
        {
            if (_root == null) return;
            var t = _root.transform;
            NeuronalLossSequence tl = null;
            foreach (var v in versions) if (v.timeline != null && v.timeline.HasPanel) { tl = v.timeline; break; }
            if (tl != null)
            {
                float pw = tl.PanelWidth;
                t.localScale = Vector3.one * (pw * widthOfPanel / SizingWidth);
                Quaternion rot = tl.PanelRotation();
                if (!_srdLooked) { _srd = global::SRD.Utils.SRDSceneEnvironment.GetSRDManager(); _srdLooked = true; }
                float forward = towardViewer * pw + touchForwardMetres * DisplayFrame.ViewSpaceScale(_srd);
                // the seek bar hangs below this bar: lift this bar by the seek bar's height + gap, so the pair still
                // fits above the panel's bottom edge (always, so the bar doesn't jump when the seek bar appears)
                float lift = 0f;
                if (seekBarBelow && _transport != null)
                    lift = (seekBarGap + _transport.barHeight * seekBarWidth * BarWidth / _transport.barWidth) * t.localScale.x;
                t.SetPositionAndRotation(tl.PanelPoint(panelPosition) - rot * Vector3.forward * forward + rot * Vector3.up * lift, rot);
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

        void Refresh()
        {
            if (_root == null) return;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.001f, slideTime));
            var cur = Current;

            // the teal highlight slides to the selected version and fades in / out
            if (Selected >= 0) _hlX = Mathf.Lerp(_hlX, ButtonX(Selected + 1), k);
            _hlAlpha = Mathf.Lerp(_hlAlpha, Selected >= 0 ? 1f : 0f, k);
            _highlight.anchoredPosition = new Vector2(_hlX, 0f);
            _highlight.GetComponent<Image>().color = new Color(Accent.r, Accent.g, Accent.b, _hlAlpha);
            float prog = cur != null && cur.TotalSeconds > 0f ? Mathf.Clamp01(cur.Time / cur.TotalSeconds) : 0f;
            _progressTrack.GetComponent<Image>().color = new Color(AccentDark.r, AccentDark.g, AccentDark.b, 0.18f * _hlAlpha);
            _progressFill.sizeDelta = new Vector2(_progressTrack.rect.width * prog, _progressFill.sizeDelta.y);
            _progressFill.GetComponent<Image>().color = new Color(AccentDark.r, AccentDark.g, AccentDark.b, 0.75f * _hlAlpha);

            int hover = _hover >= 0 ? _hover : HandHover;   // mouse first, else the hand (LeapMenuInteractor)
            _flashT = Mathf.Max(0f, _flashT - Time.unscaledDeltaTime);
            for (int i = 1; i < _buttons.Length; i++)
            {
                bool sel = i - 1 == Selected;
                // text over the highlight turns dark, by how much of the highlight covers this button
                float cover = sel ? _hlAlpha * Mathf.Clamp01(1f - Mathf.Abs(_hlX - ButtonX(i)) / buttonWidth) : 0f;
                _labels[i].color = Color.Lerp(UiKit.TextPrimary, AccentDark, cover);
                _captions[i].color = Color.Lerp(UiKit.TextDim, new Color(AccentDark.r, AccentDark.g, AccentDark.b, 0.8f), cover);
                Color h = i == _flash && _flashT > 0f ? new Color(1f, 1f, 1f, 0.35f * _flashT / FlashSeconds)
                        : i == hover && !sel ? Hover : Clear;
                _hovers[i].color = Color.Lerp(_hovers[i].color, h, i == _flash && _flashT > 0f ? 1f : k);
            }
            // Slice toggle: teal while the glass slide is on, its text dark (like a selected version)
            if (_sliceOn != null) RefreshToggle(SliceButton, _sliceOn, ref _sliceAlpha, _slice.Active, k);
            if (_lensOn != null) RefreshToggle(LensButton, _lensOn, ref _lensAlpha, _lens.Active, k);
            // the button under a touching fingertip is pushed in
            for (int i = 0; i < _buttons.Length; i++)
                _buttons[i].localScale = Vector3.Lerp(_buttons[i].localScale, Vector3.one * (i == HandPressed ? 0.92f : 1f), k * 2f);

            // play button: teal ring, filled teal on hover / while playing
            bool playing = cur != null && cur.IsPlaying;
            _icon.sprite = playing ? _pauseSprite : _playSprite;
            float fill = hover == 0 ? 1f : playing ? 0.22f : 0.10f;
            _playFill.color = Color.Lerp(_playFill.color, Color.Lerp(PlayBg, Accent, fill), k);   // opaque: the ring stays a ring
            _icon.color = Color.Lerp(_icon.color, hover == 0 ? AccentDark : Color.white, k);

            // the fingertip's spot on the bar: a ring that closes in as the finger nears, teal when touching
            if (_cursor != null)
            {
                _cursor.gameObject.SetActive(_cursorOn);
                if (_cursorOn)
                {
                    float size = _cursorPressed ? 14f : Mathf.Lerp(30f, 14f, _cursorNear);
                    _cursor.rectTransform.sizeDelta = Vector2.Lerp(_cursor.rectTransform.sizeDelta, new Vector2(size, size), Mathf.Clamp01(k * 2f));
                    _cursor.color = _cursorPressed ? Accent : new Color(1f, 1f, 1f, Mathf.Lerp(0.35f, 0.9f, _cursorNear));
                }
            }
        }

        // a toggle button i: teal while on, its text dark (like a selected version)
        void RefreshToggle(int i, Image on, ref float alpha, bool active, float k)
        {
            alpha = Mathf.Lerp(alpha, active ? 1f : 0f, k);
            on.color = new Color(Accent.r, Accent.g, Accent.b, alpha);
            _labels[i].color = Color.Lerp(UiKit.TextPrimary, AccentDark, alpha);
            _captions[i].color = Color.Lerp(UiKit.TextDim, new Color(AccentDark.r, AccentDark.g, AccentDark.b, 0.8f), alpha);
        }

        // ------------------------------------------------------------------ hand input (LeapMenuInteractor)

        /// <summary>Button under the hand's pointer (-1 = none); shown like a mouse hover.</summary>
        public int HandHover { get; set; } = -1;
        /// <summary>Button a fingertip is touching right now (-1 = none); drawn pushed in.</summary>
        public int HandPressed { get; set; } = -1;
        /// <summary>The bar's canvas (its plane is the menu surface).</summary>
        public Transform Surface => _root != null ? _root.transform : null;

        /// <summary>Press button i (0 = play/pause, 1..n = the versions, then Slice / Lens), with a short flash.</summary>
        public void Press(int i)
        {
            if (i < 0 || i >= ButtonCount) return;
            if (i == 0) TogglePlay();
            else if (i == SliceButton) _slice.Toggle();
            else if (i == LensButton) _lens.Toggle();
            else Select(i - 1);
            _flash = i; _flashT = FlashSeconds;
        }

        /// <summary>Which button a world point (on the bar's plane) is over, with a margin in canvas units; -1 = none.</summary>
        public int ButtonAt(Vector3 world, float margin)
        {
            if (_buttons == null) return -1;
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < _buttons.Length; i++)
            {
                Vector3 local = _buttons[i].InverseTransformPoint(world);
                var r = _buttons[i].rect;
                float dx = Mathf.Max(r.xMin - local.x, 0f, local.x - r.xMax), dy = Mathf.Max(r.yMin - local.y, 0f, local.y - r.yMax);
                float d = Mathf.Max(dx, dy);
                if (d <= margin && d < bestD) { best = i; bestD = d; }
            }
            return best;
        }

        /// <summary>Show the hand's pointer on the bar at a world point (on the bar's plane); pressed = touching,
        /// near = how close the fingertip is to the bar (0 = far, 1 = at it).</summary>
        public void SetHandCursor(bool on, Vector3 world = default, bool pressed = false, float near = 1f)
        {
            _cursorOn = on && _bar != null;
            _cursorPressed = pressed;
            _cursorNear = Mathf.Clamp01(near);
            if (!_cursorOn) return;
            Vector3 local = _bar.InverseTransformPoint(world);
            Vector2 half = _bar.rect.size * 0.5f;
            _cursor.rectTransform.anchoredPosition = new Vector2(Mathf.Clamp(local.x, -half.x, half.x), Mathf.Clamp(local.y, -half.y, half.y));
        }

        void Build()
        {
            _root = new GameObject("PresentationMenu_Canvas", typeof(Canvas));
            _root.transform.SetParent(transform, false);
            _root.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var group = _root.AddComponent<CanvasGroup>();
            group.interactable = false; group.blocksRaycasts = false;

            float w = BarWidth, h = barHeight, r = h * 0.5f;
            const float shadowSoft = 22f;

            // soft drop shadow, a hairline edge, then the pill itself
            var shadow = UiKit.Rounded(UiKit.Panel("Shadow", _root.transform, Shadow), r, shadowSoft).rectTransform;
            Centre(shadow, new Vector2(0f, -6f), new Vector2(w + 2f * shadowSoft, h + 2f * shadowSoft));
            var edge = UiKit.Rounded(UiKit.Panel("Edge", _root.transform, BarEdge), r + 1.5f).rectTransform;
            Centre(edge, Vector2.zero, new Vector2(w + 3f, h + 3f));
            _bar = UiKit.Rounded(UiKit.Panel("Bar", _root.transform, BarBg), r).rectTransform;
            Centre(_bar, Vector2.zero, new Vector2(w, h));
            // a faint lighter top half: a glassy sheen
            var sheen = UiKit.Rounded(UiKit.Panel("Sheen", _bar, new Color(1f, 1f, 1f, 0.035f)), r).rectTransform;
            sheen.anchorMin = new Vector2(0f, 0.5f); sheen.anchorMax = Vector2.one;
            sheen.offsetMin = new Vector2(3f, 0f); sheen.offsetMax = new Vector2(-3f, -3f);

            int n = ButtonCount;
            _buttons = new RectTransform[n];
            _hovers = new Image[n];
            _labels = new Text[n];
            _captions = new Text[n];
            float bh = h - 2f * padding;

            // play / pause: a round button with a teal ring
            float ps = PlaySize;
            _playRing = UiKit.Rounded(UiKit.Panel("PlayRing", _bar, new Color(Accent.r, Accent.g, Accent.b, 0.9f)), ps * 0.5f);
            _buttons[0] = _playRing.rectTransform;
            Left(_buttons[0], ButtonX(0), ps, ps);
            _playFill = UiKit.Rounded(UiKit.Panel("PlayFill", _buttons[0], PlayBg), ps * 0.5f - 2.5f);
            var pf = _playFill.rectTransform;
            pf.anchorMin = Vector2.zero; pf.anchorMax = Vector2.one;
            pf.offsetMin = new Vector2(2.5f, 2.5f); pf.offsetMax = new Vector2(-2.5f, -2.5f);
            _playSprite = TimelineTransportUI.MakeIcon(false, out _playTex);
            _pauseSprite = TimelineTransportUI.MakeIcon(true, out _pauseTex);
            var iconRt = UiKit.Rect("Icon", _buttons[0]);
            _icon = iconRt.gameObject.AddComponent<Image>();
            _icon.raycastTarget = false;
            _icon.sprite = _playSprite;
            iconRt.anchorMin = iconRt.anchorMax = iconRt.pivot = new Vector2(0.5f, 0.5f);
            iconRt.sizeDelta = new Vector2(ps * 0.46f, ps * 0.46f);
            iconRt.anchoredPosition = new Vector2(ps * 0.03f, 0f);   // a triangle looks centred a touch right

            // divider
            var div = UiKit.Panel("Divider", _bar, new Color(1f, 1f, 1f, 0.12f)).rectTransform;
            Left(div, padding + ps + gap * 1.5f, 1f, bh * 0.6f);

            // sliding highlight (under the labels) with the progress line
            _highlight = UiKit.Rounded(UiKit.Panel("Highlight", _bar, Clear), 18f).rectTransform;
            Left(_highlight, ButtonX(1), buttonWidth, bh);
            _progressTrack = UiKit.Rounded(UiKit.Panel("Progress", _highlight, Clear), 1.5f).rectTransform;
            _progressTrack.anchorMin = new Vector2(0f, 0f); _progressTrack.anchorMax = new Vector2(1f, 0f);
            _progressTrack.pivot = new Vector2(0.5f, 0f);
            _progressTrack.offsetMin = new Vector2(22f, 7f); _progressTrack.offsetMax = new Vector2(-22f, 10f);
            _progressFill = UiKit.Rounded(UiKit.Panel("Fill", _progressTrack, Clear), 1.5f).rectTransform;
            _progressFill.anchorMin = Vector2.zero; _progressFill.anchorMax = new Vector2(0f, 1f);
            _progressFill.pivot = new Vector2(0f, 0.5f);
            _progressFill.offsetMin = Vector2.zero; _progressFill.offsetMax = Vector2.zero;
            _hlX = ButtonX(1);

            for (int i = 1; i <= versions.Length; i++)
            {
                var v = versions[i - 1];
                TextButton(i, "V" + i, ButtonX(i), buttonWidth, bh, string.IsNullOrEmpty(v.label) ? "V" + i : v.label, v.caption);
            }
            // the highlight must be under the buttons' text but over the bar: right after the divider
            _highlight.SetSiblingIndex(div.GetSiblingIndex() + 1);

            // Slice and Lens toggles, behind their own divider
            if (_slice != null || _lens != null)
            {
                var div2 = UiKit.Panel("Divider", _bar, new Color(1f, 1f, 1f, 0.12f)).rectTransform;
                Left(div2, VersionsEnd + gap * 1.5f, 1f, bh * 0.6f);
            }
            if (_slice != null) _sliceOn = ToggleButton(SliceButton, "Slice", SliceX, sliceButtonWidth, bh, sliceLabel, sliceCaption);
            if (_lens != null) _lensOn = ToggleButton(LensButton, "Lens", LensX, lensButtonWidth, bh, lensLabel, lensCaption);

            // the hand's pointer (LeapMenuInteractor), on top of everything in the bar
            _cursor = UiKit.Circle("HandCursor", _bar, Color.white);
            _cursor.rectTransform.anchorMin = _cursor.rectTransform.anchorMax = _cursor.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _cursor.rectTransform.sizeDelta = new Vector2(18f, 18f);
            _cursor.transform.SetAsLastSibling();
            _cursor.gameObject.SetActive(false);
        }

        // a version-style button i: hover fill, bold label, small caption under it
        void TextButton(int i, string name, float x, float width, float bh, string label, string caption)
        {
            var rt = _buttons[i] = UiKit.Rect(name, _bar);
            Left(rt, x, width, bh);
            _hovers[i] = UiKit.Rounded(UiKit.Panel("Hover", rt, Clear), 18f);
            UiKit.Stretch(_hovers[i].rectTransform);
            _hovers[i].transform.SetAsFirstSibling();
            bool hasCaption = !string.IsNullOrEmpty(caption);
            _labels[i] = UiKit.Label("Label", rt, label, labelSize, FontStyle.Bold, UiKit.TextPrimary, TextAnchor.MiddleCenter);
            var lr = _labels[i].rectTransform;
            UiKit.Stretch(lr);
            if (hasCaption) lr.offsetMin = new Vector2(0f, bh * 0.36f);
            _captions[i] = UiKit.Label("Caption", rt, hasCaption ? caption : "", captionSize, FontStyle.Normal,
                                       UiKit.TextDim, TextAnchor.MiddleCenter);
            var cr = _captions[i].rectTransform;
            UiKit.Stretch(cr);
            cr.offsetMin = new Vector2(0f, 9f); cr.offsetMax = new Vector2(0f, -bh * 0.62f);
        }

        // a toggle (Slice / Lens): a text button with a teal "on" fill under its hover / text
        Image ToggleButton(int i, string name, float x, float width, float bh, string label, string caption)
        {
            TextButton(i, name, x, width, bh, label, caption);
            var on = UiKit.Rounded(UiKit.Panel("On", _buttons[i], Clear), 18f);
            UiKit.Stretch(on.rectTransform);
            on.transform.SetAsFirstSibling();
            return on;
        }

        static void Centre(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size; rt.anchoredPosition = pos;
        }

        static void Left(RectTransform rt, float x, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0.5f); rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = new Vector2(x, 0f);
        }

        void OnDestroy()
        {
            if (_playTex != null) Destroy(_playTex);
            if (_pauseTex != null) Destroy(_pauseTex);
            if (_captured) TimelineTransportUI.PointerCaptured = false;
        }
    }
}

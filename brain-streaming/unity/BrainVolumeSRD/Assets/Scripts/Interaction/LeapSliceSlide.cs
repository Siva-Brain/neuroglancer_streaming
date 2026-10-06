using SRD.Core;
using SRD.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BrainVolume
{
    /// <summary>
    /// A transparent glass slide that cuts the brain. Turned on / off by the menu bar's "Slice" button
    /// (PresentationMenu); while on it is attached to ONE hand (the hand that pressed Slice, else the hand there):
    /// moving that hand moves the slide and everything on the viewer's side of it is cut away. The other hand still
    /// grabs to turn the brain. LeapBrainManipulator picks the hand and calls BeginDrag / Drag / EndDrag.
    ///
    /// The slide runs on a rail along the volume's z axis (sections, the brain's left-right axis) and stays parallel
    /// to the cut: it drives ISliceableVolume.SlicePosition, the same cut the timelines make. It is fixed to the brain,
    /// so turning the brain (grab) turns the slide with it. When nothing is cut it is parked just outside the brain
    /// on the end facing the viewer. Not held, it always shows the volume's current cut, so a timeline's cut or a
    /// reset (R) moves it too. A pinch with the slide's hand stops the slide where it is (edges amber); the next
    /// pinch lets the hand move it again, from there. Turning the slide off keeps the cut it made.
    ///
    /// Mouse: while the slide is on, hold the left button (not on the menu / seek bar) and move the mouse to move the
    /// slide: along the rail as it looks on screen, or up = deeper when the rail points at the viewer. Releasing the
    /// button stops it there. Meanwhile the left drag doesn't move the brain (right drag still turns it).
    ///
    /// Drawn from the volume's Drawn event (like the hands), so it is never hidden by the brain, as a thin pane of
    /// glass (Brain/GlassSlide): clear face-on, more reflective tilted away, bright bevelled edges, light streaks
    /// that move with the viewer's head, and a frosted tab. Edges and tab turn magenta while the hand moves it.
    /// </summary>
    [DefaultExecutionOrder(-30)]   // after the menu / seek bar (a click on them is not a slide drag), before ModelMoveController
    public sealed class LeapSliceSlide : MonoBehaviour
    {
        [Tooltip("Show the slide from the start (else the menu's Slice button turns it on).")]
        public bool activeOnStart = false;

        [Header("Slide (volume unit cube: 1 = the whole brain)")]
        [Tooltip("How far the slide reaches past the brain on each side.")]
        public float margin = 0.05f;
        [Tooltip("Where the slide waits, outside the brain, while nothing is cut.")]
        public float park = 0.06f;
        [Tooltip("Slide movement per hand movement along the rail (1 = follows the hand exactly).")]
        public float gain = 1.5f;

        [Header("Mouse (hold the left button)")]
        public bool mouseDrag = true;
        [Tooltip("When the rail points at the viewer (it looks shorter than this many pixels on screen): moving the " +
                 "mouse up this many pixels cuts through the whole brain.")]
        public float mousePixelsPerBrain = 600f;
        public float minRailPixels = 120f;

        [Header("Tab (real metres, × the hands' scale)")]
        public float tabWidthMetres = 0.03f;
        public float tabHeightMetres = 0.018f;
        [Tooltip("Thickness of the glass pane (its edge shows as a bright line when seen side-on).")]
        public float thicknessMetres = 0.003f;
        [Tooltip("Width of the bevelled, brighter border of the glass.")]
        public float bevelMetres = 0.004f;

        [Header("Glass look (Brain/GlassSlide)")]
        [Tooltip("Body colour; alpha = how see-through the glass is face-on (low = clear).")]
        public Color glassTint = new Color(0.70f, 0.88f, 0.95f, 0.06f);
        [Tooltip("The glass edge and bevel.")]
        public Color edgeColor = new Color(0.78f, 0.97f, 1.00f, 0.85f);
        [Tooltip("Edges and tab while the hand moves the slide.")]
        public Color heldColor = new Color(0.95f, 0.35f, 0.85f, 1f);
        [Tooltip("Edges and tab while the slide is stopped (pinch to stop / move again).")]
        public Color pausedColor = new Color(1.00f, 0.80f, 0.30f, 1f);
        [Tooltip("Extra opacity at grazing angles (glass looks clearer face-on, more reflective tilted away).")]
        [Range(0f, 1f)] public float fresnel = 0.35f;
        [Tooltip("Strength of the light streaks that slide across the glass as the head moves.")]
        [Range(0f, 1f)] public float sheen = 0.22f;
        [Tooltip("The tab is frosted glass: its body is this much more opaque than the pane.")]
        [Range(0f, 1f)] public float tabFrost = 0.25f;
        [Tooltip("Seconds for the highlight to fade in / out.")]
        public float highlightFade = 0.12f;

        /// <summary>Is the slide shown?</summary>
        public bool Active { get; private set; }
        /// <summary>Is a hand or the mouse moving the slide right now?</summary>
        public bool Held => _handHeld || MouseHeld;
        /// <summary>Is the mouse (left button held) moving the slide right now?</summary>
        public bool MouseHeld { get; private set; }
        bool _handHeld;
        Camera _cam;
        /// <summary>Is the slide stopped (a pinch), so the hand doesn't move it until the next pinch?</summary>
        public bool Paused { get; private set; }

        ISliceableVolume _vol;
        LeapBrainManipulator _hands;
        SRDManager _srd;
        Material _mat;
        bool _glass;       // Brain/GlassSlide found (else flat Brain/OverlayUnlit)
        Mesh _pane, _tab;
        float _paneHi, _tabHi;   // highlight amounts (faded)
        float _u;         // slide position along the rail: < 0 = parked outside, 0..1 = the cut
        float _pausedHi;   // stopped highlight amount (faded)

        /// <summary>Called by LeapBrainManipulator once it has the brain.</summary>
        public void Init(ISliceableVolume vol, LeapBrainManipulator hands)
        {
            _vol = vol;
            _hands = hands;
            _srd = SRDSceneEnvironment.GetSRDManager();
            var sh = Shader.Find("Brain/GlassSlide");
            _glass = sh != null;
            if (!_glass)
            {
                Debug.LogWarning("[Slice] Brain/GlassSlide not found (Always Included Shaders?): the slide is drawn flat.");
                sh = Shader.Find("Brain/OverlayUnlit");
            }
            if (sh != null) _mat = new Material(sh);
            _pane = NewMesh("SlidePane"); _tab = NewMesh("SlideTab");
            _vol.Drawn += Draw;
            if (activeOnStart) SetActive(true);
        }

        public void Toggle() => SetActive(!Active);

        public void SetActive(bool on)
        {
            if (on == Active || _vol == null) return;
            Active = on;
            Paused = false;
            if (on)
            {
                if (_hands != null && _hands.lens != null) _hands.lens.SetActive(false);   // one tool per hand
                if (_vol.SlicePosition <= 0f) FaceViewer();
                Debug.Log("[Slice] Slide on.");
            }
            else
            {
                _handHeld = false; EndMouse();   // the cut stays (R resets it)
                Debug.Log("[Slice] Slide off (cut kept).");
            }
        }

        /// <summary>Stop the slide where it is / let the hand move it again (the slide hand's pinch).</summary>
        public void TogglePaused()
        {
            if (!Active) return;
            Paused = !Paused;
            if (Paused) _handHeld = false;
            Debug.Log(Paused ? "[Slice] Stopped." : "[Slice] Moving again.");
        }

        public void BeginDrag()
        {
            if (!Active) return;
            if (_vol.SlicePosition <= 0f) FaceViewer();   // uncut: cut from the end facing the viewer
            _u = CurrentU;
            _handHeld = true;
        }

        /// <summary>Move the slide by the hand's movement (world), along the rail only.</summary>
        public void Drag(Vector3 worldDelta)
        {
            if (!_handHeld) return;
            Vector3 rail = _vol.UnitCubeToWorld.GetColumn(2);   // unit z -> world (the whole depth)
            float dz = Vector3.Dot(worldDelta, rail) / Mathf.Max(1e-8f, rail.sqrMagnitude) * gain;
            MoveBy(_vol.SliceFromHighZ ? -dz : dz);
        }

        public void EndDrag() => _handHeld = false;

        // along the rail, in cut units (+ = deeper)
        void MoveBy(float du)
        {
            _u = Mathf.Clamp(_u + du, -park, 1f);
            _vol.SlicePosition = Mathf.Max(0f, _u);
        }

        // ------------------------------------------------------------------ mouse

        void HandleMouse()
        {
            var mouse = Mouse.current;
            if (!mouseDrag || mouse == null || !Active || _vol == null || !_vol.Loaded) { EndMouse(); return; }
            if (!mouse.leftButton.isPressed) { EndMouse(); return; }
            if (!MouseHeld)
            {
                // a press on the menu / seek bar is theirs; the hand holding the slide keeps it
                if (!mouse.leftButton.wasPressedThisFrame || TimelineTransportUI.PointerCaptured || _handHeld) return;
                if (_vol.SlicePosition <= 0f) FaceViewer();   // uncut: cut from the end facing the viewer
                _u = CurrentU;
                MouseHeld = true;
                Paused = false;
                TimelineTransportUI.PointerCaptured = true;   // the left drag must not also move the brain
                if (_hands != null && _hands.pauseTimelineOnGrab) _hands.PauseTimelines();   // it would pull the cut back
                return;
            }
            Vector2 d = mouse.delta.ReadValue();
            if (d.sqrMagnitude < 1e-6f) return;
            // the rail on screen (pixels per whole depth, toward deeper); too short = it points at the viewer: up = deeper
            var cam = LeapLens.FindWatcher(ref _cam);
            Vector2 rail = Vector2.zero;
            if (cam != null)
            {
                Matrix4x4 m = _vol.UnitCubeToWorld;
                float z0 = _vol.SliceFromHighZ ? 1f : 0f;
                Vector3 a = cam.WorldToScreenPoint(m.MultiplyPoint(new Vector3(0.5f, 0.5f, z0)));
                Vector3 b = cam.WorldToScreenPoint(m.MultiplyPoint(new Vector3(0.5f, 0.5f, 1f - z0)));
                if (a.z > 0f && b.z > 0f) rail = (Vector2)(b - a);
            }
            if (rail.magnitude >= minRailPixels) MoveBy(Vector2.Dot(d, rail) / rail.sqrMagnitude);
            else MoveBy(d.y / Mathf.Max(1f, mousePixelsPerBrain));
        }

        void EndMouse()
        {
            if (!MouseHeld) return;
            MouseHeld = false;
            TimelineTransportUI.PointerCaptured = false;
        }

        float Scale => _hands != null ? _hands.HandScale : 1f;

        // not held: the slide shows the volume's cut (a timeline or a reset may have changed it)
        float CurrentU => Held ? _u : _vol.SlicePosition > 0f ? _vol.SlicePosition : -park;

        // Cut from whichever end of the rail faces the viewer (only while uncut, so the brain doesn't change).
        void FaceViewer()
        {
            Transform f = DisplayFrame.Get(_srd);
            Vector3 toViewer = f != null ? -f.forward : Vector3.back;   // the display frame's +Z points away from the viewer
            Vector3 rail = _vol.UnitCubeToWorld.GetColumn(2);
            float d = Vector3.Dot(rail.normalized, toViewer);
            if (Mathf.Abs(d) > 0.05f) _vol.SliceFromHighZ = d > 0f;
        }

        // The slide in world space: its centre, its two in-plane half-axes (along = the tab's edge, out = toward the
        // tab, i.e. up), the plane normal and the tab's centre.
        void Geometry(out Vector3 centre, out Vector3 along, out Vector3 up, out Vector3 normal, out Vector3 tabCentre)
        {
            Matrix4x4 m = _vol.UnitCubeToWorld;
            float u = CurrentU;
            float z = _vol.SliceFromHighZ ? 1f - u : u;
            centre = m.MultiplyPoint(new Vector3(0.5f, 0.5f, z));
            Vector3 ax = (Vector3)m.GetColumn(0) * (0.5f + margin);
            Vector3 ay = (Vector3)m.GetColumn(1) * (0.5f + margin);
            normal = ((Vector3)m.GetColumn(2)).normalized;
            // the tab goes on the slide's upper edge, whichever volume axis is nearer vertical
            float dx = Vector3.Dot(ax.normalized, Vector3.up), dy = Vector3.Dot(ay.normalized, Vector3.up);
            if (Mathf.Abs(dy) >= Mathf.Abs(dx)) { up = dy >= 0f ? ay : -ay; along = ax; }
            else { up = dx >= 0f ? ax : -ax; along = ay; }
            tabCentre = centre + up + up.normalized * (0.5f * tabHeightMetres * Scale);
        }

        // ------------------------------------------------------------------ drawing

        void Update()
        {
            HandleMouse();
            // highlights fade (once per frame, not per eye)
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.001f, highlightFade));
            _paneHi = Mathf.Lerp(_paneHi, Held ? 1f : 0f, k);
            _tabHi = Mathf.Lerp(_tabHi, Held ? 1f : 0f, k);
            _pausedHi = Mathf.Lerp(_pausedHi, Paused ? 1f : 0f, k);
        }

        void Draw(Camera cam)
        {
            if (!Active || _mat == null || cam == null || !_vol.Loaded || LeapLens.IsLensCamera(cam)) return;
            Geometry(out Vector3 c, out Vector3 along, out Vector3 up, out Vector3 n, out Vector3 tabCentre);
            float s = Scale;
            Vector3 half = n * (0.5f * thicknessMetres * s);

            // the pane: a thin glass box across the brain; the tab: a small frosted one on its upper edge
            Pane(_pane, c, along, up, half);
            Vector3 ta = along.normalized * (0.5f * tabWidthMetres * s), tu = up.normalized * (0.5f * tabHeightMetres * s);
            Pane(_tab, tabCentre, ta, tu, half);

            float bevel = bevelMetres * s;
            DrawGlass(_pane, along, up, bevel, glassTint, _paneHi);
            Color frosted = glassTint; frosted.a = Mathf.Clamp01(glassTint.a + tabFrost);
            DrawGlass(_tab, ta, tu, bevel * 0.6f, frosted, _tabHi);
        }

        void DrawGlass(Mesh mesh, Vector3 along, Vector3 up, float bevel, Color tint, float highlight)
        {
            if (_glass)
            {
                _mat.SetColor("_Tint", tint);
                _mat.SetColor("_EdgeColor", edgeColor);
                _mat.SetColor("_Highlight", Color.Lerp(heldColor, pausedColor, _pausedHi));
                _mat.SetFloat("_HighlightAmount", Mathf.Max(highlight, _pausedHi));
                _mat.SetFloat("_Fresnel", fresnel);
                _mat.SetFloat("_Sheen", sheen);
                // bevel as a fraction of the pane's width / height (uv), so it is equally wide all round
                _mat.SetVector("_Bevel", new Vector4(bevel / Mathf.Max(1e-6f, 2f * along.magnitude),
                                                     bevel / Mathf.Max(1e-6f, 2f * up.magnitude), 0f, 0f));
            }
            else
            {
                Color flat = Color.Lerp(edgeColor, Color.Lerp(heldColor, pausedColor, _pausedHi), Mathf.Max(highlight, _pausedHi));
                flat.a = Mathf.Clamp01(tint.a * 2f);
                _mat.SetColor("_Color", flat);
            }
            _mat.SetPass(0);
            Graphics.DrawMeshNow(mesh, Matrix4x4.identity);
        }

        // A thin box in world space: centre c, half-extents along / up (in the pane) and half (thickness).
        // The two big faces get uv 0..1 across the pane; the four sides are marked with colour r = 1 (the glass edge).
        static void Pane(Mesh mesh, Vector3 c, Vector3 along, Vector3 up, Vector3 half)
        {
            var v = new Vector3[24];
            var nm = new Vector3[24];
            var uv = new Vector2[24];
            var col = new Color[24];
            var tri = new int[36];
            Vector3 n = half.normalized, a = along.normalized, u = up.normalized;
            int q = 0;
            // big faces (front, back)
            Face(v, nm, uv, col, tri, ref q, c + half, along, up, n, false);
            Face(v, nm, uv, col, tri, ref q, c - half, along, up, -n, false);
            // sides: across the thickness
            Face(v, nm, uv, col, tri, ref q, c + up, along, half, u, true);
            Face(v, nm, uv, col, tri, ref q, c - up, along, half, -u, true);
            Face(v, nm, uv, col, tri, ref q, c + along, up, half, a, true);
            Face(v, nm, uv, col, tri, ref q, c - along, up, half, -a, true);
            mesh.Clear();
            mesh.vertices = v; mesh.normals = nm; mesh.uv = uv; mesh.colors = col; mesh.triangles = tri;
        }

        // one quad: centre p, half-extents x / y, normal n
        static void Face(Vector3[] v, Vector3[] nm, Vector2[] uv, Color[] col, int[] tri, ref int q,
                         Vector3 p, Vector3 x, Vector3 y, Vector3 n, bool side)
        {
            int i = q * 4;
            v[i] = p - x - y; v[i + 1] = p + x - y; v[i + 2] = p + x + y; v[i + 3] = p - x + y;
            uv[i] = new Vector2(0f, 0f); uv[i + 1] = new Vector2(1f, 0f); uv[i + 2] = new Vector2(1f, 1f); uv[i + 3] = new Vector2(0f, 1f);
            Color c = side ? Color.red : Color.black;
            for (int k = 0; k < 4; k++) { nm[i + k] = n; col[i + k] = c; }
            int t = q * 6;
            tri[t] = i; tri[t + 1] = i + 1; tri[t + 2] = i + 2; tri[t + 3] = i; tri[t + 4] = i + 2; tri[t + 5] = i + 3;
            q++;
        }

        static Mesh NewMesh(string name)
        {
            var m = new Mesh { name = name };
            m.MarkDynamic();
            return m;
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            if (_mat != null) Destroy(_mat);
            if (_pane != null) Destroy(_pane);
            if (_tab != null) Destroy(_tab);
        }
    }
}

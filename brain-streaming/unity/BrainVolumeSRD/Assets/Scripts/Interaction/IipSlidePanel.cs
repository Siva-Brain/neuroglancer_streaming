using SRD.Utils;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// V5's square: after the slice down to the IIP slide's section, a patch of the cross-section lifts out: the square
    /// starts lying on the cut face (startRegionMm across, its slide picture over the tissue it shows, in the
    /// section's orientation), rises straight out along the cut face's normal toward the viewer while growing to
    /// full size, then zooms into the IIP slide from that patch down to the slide's finest level (JTL 7, 1 um/px:
    /// single cells). Then it holds; with the timeline's returnToStart it zooms back out and goes back into the
    /// cut face (Combine) before the brain turns back to its start pose (Return).
    /// Driven by the timeline's time in its hold (HoldStart): seekable, and shown only while that timeline is selected.
    ///
    /// Two GameObjects place it; move them by hand (in Play, then copy the values, or create them in the scene):
    ///   "V5 Panel Start"  child of the Brain: the point on the cut face the square comes out of (and whose
    ///                     slide region it shows). Made at startOnCut if the scene has none.
    ///   "V5 Panel End"    where the square ends up (world): position, rotation (it faces along its forward,
    ///                     away from the viewer) and size (localScale.x × panelSize). Made comeOut straight out of
    ///                     the cut face from the start point, lying like the section, if the scene has none.
    /// The slide comes from IipSectionOverlay (the fit, the tiles); the square is drawn over the brain (Drawn).
    /// </summary>
    [DefaultExecutionOrder(50)]   // after the timeline has moved the brain this frame
    public sealed class IipSlidePanel : MonoBehaviour
    {
        public NeuronalLossSequence timeline;

        [Header("Timing (seconds into the timeline's hold, after the slice)")]
        public float comeOutSeconds = 3f;
        public float zoomSeconds = 8f;
        public float holdSeconds = 4f;

        [Header("Start point (default)")]
        [Tooltip("Where on the cut face the square comes out (and what it zooms into), 0..1 across the volume's x " +
                 "(columns) and y (rows, 0 = top). (0.742, 0.235) = L4 voxel (1050, 182) on section 150: upper frontal " +
                 "cortex, the spot marked on the 2026-10-05 19:53 screenshot. Used only when the scene " +
                 "has no 'V5 Panel Start'.")]
        public Vector2 startOnCut = new Vector2(0.742f, 0.235f);

        [Header("Square")]
        [Tooltip("Side of the square (world units), × V5 Panel End's localScale.x.")]
        public float panelSize = 0.22f;
        [Tooltip("Default end place: this far (world units) straight out of the cut face, toward the viewer.")]
        public float comeOut = 0.28f;
        public int resolution = 1024;
        public Color borderColor = new Color(0.78f, 0.97f, 1.00f, 0.95f);
        public Color markColor = new Color(0.18f, 0.83f, 0.75f, 0.9f);
        public Color lineColor = new Color(0.92f, 0.94f, 0.97f, 0.9f);
        public float lineWidth = 0.0025f;

        [Header("Zoom (on the slide)")]
        [Tooltip("The patch of section that comes out (mm across): the square starts this big, lying on the cut face " +
                 "over that tissue, and shows that region until the zoom starts.")]
        public float startRegionMm = 10f;
        [Tooltip("At the end, one texture pixel = this many slide pixels at full resolution (1 = 1 um/px, cells).")]
        public float endFullPxPerPixel = 1f;

        Transform _start, _end;
        bool _defaults;          // an anchor was made here: place it once the slice is done
        bool _defStart, _defEnd;
        IipSectionOverlay _iip;
        BrainVolume.SRD.BrickVolumeLoader _brain;
        RenderTexture _rt;
        Material _boxMat, _flatMat;
        Mesh _quad, _line;
        bool _visible;
        Vector3 _pos;
        Quaternion _rot;
        float _size;

        bool _marked;

        // before the timeline computes its phases (its Start): the stage marks on the seek bar and the hold's length
        void Awake()
        {
            if (timeline == null) timeline = GetComponent<NeuronalLossSequence>();
            Setup();
        }

        /// <summary>Called by PresentationMenu when it builds V5 at play time (before the timeline's Start).</summary>
        public void Init(NeuronalLossSequence t)
        {
            timeline = t;
            Setup();
        }

        void Setup()
        {
            if (_marked || timeline == null) return;
            _marked = true;
            timeline.holdMarks.Add(new NeuronalLossSequence.Mark { name = "Slide out", start = 0f });
            timeline.holdMarks.Add(new NeuronalLossSequence.Mark { name = "Zoom to cells", start = comeOutSeconds });
            timeline.hold = comeOutSeconds + zoomSeconds + holdSeconds;
        }

        void Start()
        {
            _boxMat = Shader.Find("Brain/Lens") is Shader b ? new Material(b) : null;
            _flatMat = Shader.Find("Brain/OverlayUnlit") is Shader f ? new Material(f) : null;
            _quad = new Mesh { name = "V5Panel" };
            _quad.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) };
            _quad.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _quad.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _line = new Mesh { name = "V5PanelLine" };
            _line.MarkDynamic();
        }

        bool Ready()
        {
            if (_brain == null) _brain = FindFirstObjectByType<BrainVolume.SRD.BrickVolumeLoader>();
            if (_iip == null) _iip = FindFirstObjectByType<IipSectionOverlay>();
            if (_brain == null || !_brain.Loaded || _iip == null || _boxMat == null) return false;
            if (_start == null) Anchors();
            return true;
        }

        // the two anchors: the scene's, else made here (placed once the slice is done)
        void Anchors()
        {
            var s = GameObject.Find("V5 Panel Start");
            var e = GameObject.Find("V5 Panel End");
            _defStart = s == null; _defEnd = e == null;
            _defaults = _defStart || _defEnd;
            if (s == null) { s = new GameObject("V5 Panel Start"); s.transform.SetParent(_brain.transform, false); }
            if (e == null) { e = new GameObject("V5 Panel End"); e.transform.SetParent(transform, false); }
            _start = s.transform; _end = e.transform;
            if (_defaults) Debug.Log($"[V5] Made {(_defStart ? "'V5 Panel Start' (under the Brain) " : "")}{(_defEnd ? "'V5 Panel End' " : "")}: move to place the square.");
        }

        // default places: the start on the cut face at startOnCut, the end comeOut straight out of the cut face
        // (along its normal, toward the viewer), lying like the section
        void PlaceDefaults()
        {
            _defaults = false;
            ISliceableVolume v = _brain;
            float z = _iip.SectionUnitZ;
            Vector3 startW = v.UnitCubeToWorld.MultiplyPoint(new Vector3(startOnCut.x, startOnCut.y, z));
            if (_defStart) _start.position = startW;
            else startW = _start.position;
            if (!_defEnd) return;
            _end.SetPositionAndRotation(startW + FaceNormal(startW) * comeOut, SectionRotation());
            _end.localScale = Vector3.one;
        }

        // the cut face's normal, the side toward the viewer
        Vector3 FaceNormal(Vector3 at)
        {
            Vector3 n = ((Vector3)((ISliceableVolume)_brain).UnitCubeToWorld.GetColumn(2)).normalized;
            Transform f = DisplayFrame.Get();
            Vector3 toViewer = f != null ? -f.forward : Vector3.back;
            return Vector3.Dot(n, toViewer) >= 0f ? n : -n;
        }

        // A rotation lying in the section, with the slide image's axes: local x = the image's right, local y = its
        // up, as they lie on the cut face (the fit's rotation), so the square's picture matches the tissue under it.
        Quaternion SectionRotation()
        {
            Matrix4x4 m = ((ISliceableVolume)_brain).UnitCubeToWorld;
            float a = _iip.angleDeg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            // thumbnail +x and +y (down) in L4 voxels: R(-a) applied to the image axes
            Vector2 ix = new Vector2(c, -s), iy = new Vector2(s, c);
            Vector3 W(Vector2 dv) => (Vector3)m.GetColumn(0) * (dv.x / _iip.l4Size.x) + (Vector3)m.GetColumn(1) * (dv.y / _iip.l4Size.y);
            Vector3 right = W(ix).normalized, up = (-W(iy)).normalized;
            return Quaternion.LookRotation(Vector3.Cross(right, up), up);
        }

        // world units per mm of the brain
        float WorldPerMm => ((Vector3)((ISliceableVolume)_brain).UnitCubeToWorld.GetColumn(0)).magnitude / Mathf.Max(1e-6f, _brain.VolumeMm.x);

        void LateUpdate()
        {
            _visible = false;
            if (timeline == null || !timeline.Ready || !Ready()) return;
            if (_hooked == null) { _hooked = _brain; _hooked.Drawn += Draw; }   // after the slide on the cut face (drawn over it)
            float p = timeline.Time - timeline.HoldStart;
            if (p < 0f) return;
            // Combine (the timeline's return to the start): the square zooms back out and goes back into the cut face,
            // then it is gone while the brain turns back
            float back = timeline.CombineAmount;
            if (back >= 1f) return;
            if (_defaults) PlaceDefaults();

            // come out of the cross-section: at first the square lies on the cut face, the size of the patch of
            // section it shows (its picture on the tissue it shows), then it rises straight out to the end place,
            // growing to full size
            float g = Ease(Mathf.Clamp01(p / Mathf.Max(0.01f, comeOutSeconds))) * (1f - back);
            Quaternion startRot = SectionRotation();
            Vector3 startPos = _start.position + FaceNormal(_start.position) * 0.002f;   // just above the face
            _pos = Vector3.Lerp(startPos, _end.position, g);
            _rot = Quaternion.Slerp(startRot, _end.rotation, g);
            float startSize = startRegionMm * WorldPerMm;
            _size = Mathf.Lerp(startSize, panelSize * Mathf.Max(1e-4f, _end.localScale.x), g);

            // the slide around the start point, zooming from startRegionMm down to the finest level
            if (_rt == null || _rt.width != resolution)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default) { name = "V5Panel" };
                _rt.Create();
            }
            ISliceableVolume v = _brain;
            Vector3 u = v.UnitCubeToWorld.inverse.MultiplyPoint(_start.position);
            Vector2 centre = _iip.UnitToFullPx(new Vector2(u.x, u.y));
            float wStart = startRegionMm * 1000f * _iip.scale, wEnd = resolution * Mathf.Max(0.1f, endFullPxPerPixel);
            float k = Ease(Mathf.Clamp01((p - comeOutSeconds) / Mathf.Max(0.01f, zoomSeconds))) * (1f - back);
            float width = Mathf.Exp(Mathf.Lerp(Mathf.Log(wStart), Mathf.Log(Mathf.Min(wEnd, wStart)), k));
            _iip.ComposeAround(_rt, centre, width, out _);
            _visible = true;
        }

        void OnDisable() { if (_hooked != null) _hooked.Drawn -= Draw; _hooked = null; }
        ISliceableVolume _hooked;

        void Draw(Camera cam)
        {
            if (!_visible || cam == null || LeapLens.IsLensCamera(cam) || CardOverlay.IsSnapshot(cam)) return;

            // a thin line from the start point to the square, and a dot on the cut face
            if (_flatMat != null)
            {
                Vector3 a = _start.position, b = _pos;
                Vector3 n = (cam.transform.position - 0.5f * (a + b)).normalized;
                Vector3 side = Vector3.Cross(b - a, n).normalized * (0.5f * lineWidth);
                Vector3 dr = cam.transform.right * (lineWidth * 1.6f), du = cam.transform.up * (lineWidth * 1.6f);
                _line.Clear();
                _line.vertices = new[] { a - side, a + side, b + side, b - side, a - dr - du, a + dr - du, a + dr + du, a - dr + du };
                _line.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
                _flatMat.SetColor("_Color", lineColor);
                _flatMat.SetPass(0);
                Graphics.DrawMeshNow(_line, Matrix4x4.identity);
            }

            // the square with the slide (Brain/Lens: rounded border, a small mark at the centre = the start point)
            _boxMat.SetTexture("_MainTex", _rt);
            _boxMat.SetColor("_BorderColor", borderColor);
            _boxMat.SetColor("_MarkColor", markColor);
            _boxMat.SetFloat("_Aspect", 1f);
            _boxMat.SetPass(0);
            Graphics.DrawMeshNow(_quad, Matrix4x4.TRS(_pos, _rot, new Vector3(0.5f * _size, 0.5f * _size, 1f)));
        }

        static float Ease(float x) => x * x * (3f - 2f * x);

        void OnDestroy()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_boxMat != null) Destroy(_boxMat);
            if (_flatMat != null) Destroy(_flatMat);
            if (_quad != null) Destroy(_quad);
            if (_line != null) Destroy(_line);
        }
    }
}

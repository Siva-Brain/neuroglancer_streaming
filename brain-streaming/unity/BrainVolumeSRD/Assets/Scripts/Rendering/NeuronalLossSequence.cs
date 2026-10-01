using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using SRD.Core;
using SRD.Utils;

namespace BrainVolume
{
    /// <summary>
    /// A seekable timeline (TimelineTransportUI is its play/pause + seek bar, like SRD_test's
    /// story transport). Everything it drives is a pure function of the timeline time, so seeking
    /// lands exactly:
    ///   1. "Brain"          show the brain at initialEuler (0, 270, 180)            showBrain s
    ///   2. "Rotate"         turn Euler Y only, 270 -> endY 360 (X 0 and Z 180 held),
    ///                       ending on the LEFT sagittal view                          rotate s
    ///   3. "Slice"          slice from the left to clippingDepth                     slice s
    ///   4. "Neuronal loss"  at one point on the cross-section (pointOnCut: the
    ///                       upper-front cortex by default) the histology block
    ///                       appears tiny, then grows as it heads straight out of
    ///                       the cut face toward the viewer and curves to the left,
    ///                       past the brain's FRONT edge (sideDistance), settling
    ///                       comeOut in front of the cut face, overshooting slightly,
    ///                       without turning (spinDegreesPerSecond 0);
    ///                       then the label fades in                                  grow s (+ label, hold)
    /// The brain's transform POSITION (and scale) is never
    /// touched: only its rotation and slice. ModelMoveController is off until the block is in
    /// place (and ignores the mouse while the seek bar is being dragged).
    ///
    /// The block is SRD_test's neuronal-loss block (StoryController NeuronalLoss beat +
    /// NeuronalLossVolume, 2026-09-29/30): both ROI stacks in one RG volume
    /// (R = neuronal_loss_roi_smooth = grey tissue, G = neuronal_loss_inverse_roi = pink/purple
    /// signal), the same 48x48x117 RG16 .bytes file, the same shader numbers, true voxel
    /// proportions, long axis horizontal, tilted 12 deg, yawed 45 deg (it turned 18 deg/s there;
    /// still here by default), a white "Neuronal loss" card. Sizes that were metres there are scaled by blockLength / 0.56.
    /// </summary>
    public sealed class NeuronalLossSequence : MonoBehaviour
    {
        [Header("Data (under Assets/StreamingAssets)")]
        public string folder = "NeuronalLoss";
        [Tooltip("RG16 voxels, x fastest then y then z (SRD_test's pack_neuronal_loss_volume.py output).")]
        public string volumeFile = "NeuronalLoss_48x48x117_RG.bytes";
        public int width = 48, height = 48, depth = 117;

        [Header("Start point: the ROI in the brain (voxels of hb02 L6)")]
        public int roiX0 = 133, roiX1 = 181, roiY0 = 81, roiY1 = 129;
        public int roiLevelWidth = 354, roiLevelHeight = 194;
        [Tooltip("Start from the ROI centre. Untick to use pointOnCut.")]
        public bool useRoiCentre = false;
        [Tooltip("Start point on the cut face, 0..1 across the volume's x and y (y = 0 is the top of the brain, " +
                 "x = 1 the front). (0.82, 0.28) = the upper-front cortex, the area marked on the 2026-10-01 " +
                 "20:08 screenshot (just inside the tissue edge of the cut section).")]
        public Vector2 pointOnCut = new Vector2(0.82f, 0.28f);

        [Header("Timeline")]
        public bool playOnStart = true;
        [Tooltip("Add the play/pause + seek bar (TimelineTransportUI) if the scene has none.")]
        public bool transportBar = true;
        [Tooltip("Rotation the brain starts from (position/scale untouched).")]
        public Vector3 initialEuler = new Vector3(0f, 270f, 180f);
        [Tooltip("Euler Y the turn ends on (the left sagittal view). Only Y changes: X and Z stay at initialEuler's.")]
        public float endY = 360f;
        [Tooltip("Extra full turns added to the 270 -> 360 Y turn.")]
        [Range(0, 3)] public int extraTurns = 0;
        [Tooltip("Off = the timeline ends after slicing (no neuronal-loss block).")]
        public bool showNeuronalLoss = true;
        [Range(0.05f, 0.95f)] public float clippingDepth = 0.3f;
        public float showBrain = 2.5f;
        [Tooltip("Seconds for the Y rotation (extra turns + the turn to the left sagittal view).")]
        public float rotate = 6f;
        public float slice = 4f;
        [Tooltip("Seconds for the block to grow from the point to full size.")]
        public float grow = 4f;
        public float labelDelay = 0.2f, labelSeconds = 0.6f;
        [Tooltip("Seconds the timeline runs on after the label is in (if spinDegreesPerSecond > 0 the block keeps turning after the end).")]
        public float hold = 3f;

        [Header("Growth")]
        [Tooltip("Size at the start point, as a fraction of the full size.")]
        [Range(0.01f, 0.5f)] public float startScale = 0.02f;
        [Tooltip("How far the path first heads straight out of the cut face toward the viewer before it " +
                 "curves to the side (Unity units; Bezier control point = start + this toward the viewer).")]
        public float arcTowardViewer = 0.30f;
        [Tooltip("Size overshoot before settling (0 = none, 0.1 = 10 % bigger for a moment).")]
        [Range(0f, 0.3f)] public float overshoot = 0.08f;
        [Tooltip("Draw SRD_test's arrow from the start point to the block.")]
        public bool showArrow = false;

        [Header("Block")]
        [Tooltip("Length of the block's long axis (the 117-section stack) in Unity units.")]
        public float blockLength = 0.30f;
        [Tooltip("Where the block settles, sideways: this far (Unity units) past the brain's FRONT edge (the " +
                 "screen-left side in the left sagittal view), level with the start point.")]
        public float sideDistance = 0.20f;
        [Tooltip("Where the block settles, in depth: this far (Unity units) out of the cut face toward the viewer.")]
        public float comeOut = 0.30f;
        public float pivotYawDegrees = 45f;
        public Vector3 blockEuler = new Vector3(0f, 90f, 12f);
        [Tooltip("Turntable speed of the block (deg/s). 0 = the block does not turn.")]
        public float spinDegreesPerSecond = 0f;

        [Header("Final transform (set your own)")]
        [Tooltip("Once the block is in place, the mouse/keyboard move the BLOCK (GameObject 'NeuronalLossBlock'), " +
                 "not the brain. M switches between block and brain.")]
        public bool controlsMoveBlock = true;
        [Tooltip("ON = the block settles at finalPosition / finalEuler / finalScale (world space) instead of " +
                 "the computed place. Find the values in Play: press M, move the block, press K to copy them.")]
        public bool useCustomFinal = false;
        public Vector3 finalPosition = Vector3.zero;
        public Vector3 finalEuler = Vector3.zero;
        [Tooltip("Multiplier on blockLength (1 = blockLength long).")]
        public float finalScale = 1f;

        [Header("Shader (SRD_test M_NeuronalLossVolume)")]
        [Range(24, 256)] public int stepCount = 96;
        public Color tissueColor = new Color(0.42f, 0.42f, 0.45f);
        [Range(0f, 1f)] public float tissueThreshold = 0.04f;
        [Range(0f, 8f)] public float tissueDensity = 0.45f;
        [Range(0f, 1f)] public float tissueShade = 0.8f;
        public Color lossColorLow = new Color(0.48f, 0.30f, 0.62f);
        public Color lossColorHigh = new Color(0.96f, 0.52f, 0.52f);
        [Range(0f, 1f)] public float lossThreshold = 0.62f;
        [Range(0.01f, 1f)] public float lossSoftness = 0.25f;
        [Range(0f, 8f)] public float lossDensity = 2.4f;

        [Header("Arrow (SRD_test metres, scaled)")]
        public Color lineColor = new Color(0.92f, 0.94f, 0.97f, 1f);
        public float lineWidth = 0.0032f;
        public float headLength = 0.035f, headWidth = 0.026f;
        public float tipGap = 0.008f;
        public float anchorDotDiameter = 0.016f;

        [Header("Label card")]
        public string title = "Neuronal loss";
        public string tagLine = "Stroke tissue  ·  3D histology stack";
        [Tooltip("Card plate colour (SRD_test reference: white card, black bold-italic text).")]
        public Color cardColor = Color.white;
        public Color titleColor = new Color(0.04f, 0.04f, 0.06f, 1f);
        public Color tagColor = new Color(0.10f, 0.10f, 0.12f, 1f);
        public int titleFontSize = 30, tagFontSize = 19;
        public float padding = 18f;
        [Tooltip("Gap between the block's top and the label, SRD_test metres (scaled).")]
        public float labelGap = 0.03f;

        const float SrdTestBlockLength = 0.56f;   // SRD_test's block long axis (m) the metre values belong to

        // ------------------------------------------------------------------ timeline API (TimelineTransportUI)

        public struct Mark { public string name; public float start; }
        public Mark[] Marks { get; private set; } = new Mark[0];
        public float TotalSeconds => _tLabelEnd + hold;
        public float Time => _time;
        public bool IsPlaying => _playing;
        public bool Ready => _prepared;
        public void Play() { if (_time >= TotalSeconds) SeekTo(0f); _playing = true; }
        public void Pause() { _playing = false; }
        public void TogglePlay() { if (_playing) Pause(); else Play(); }
        public void Restart() { SeekTo(0f); _playing = true; }
        public void SeekTo(float seconds)
        {
            _time = Mathf.Clamp(seconds, 0f, TotalSeconds);
            _endSpin = 0f;
            _seeked = true;
        }
        public string CurrentMarkName
        {
            get
            {
                string n = "";
                foreach (var m in Marks) if (_time >= m.start) n = m.name;
                return n;
            }
        }

        // display panel (SRD), as in SRD_test's StoryController.PanelPoint / PanelRotation
        public bool HasPanel => _srd != null && _srd.isActiveAndEnabled && _srd.DisplayEdges != null &&
                                _srd.DisplayEdges.LeftBottom != null && _srd.DisplayEdges.RightUp != null;
        public Vector3 PanelPoint(Vector2 p)
        {
            var e = _srd.DisplayEdges;
            float fx = Mathf.Clamp01(p.x * 0.5f + 0.5f), fy = Mathf.Clamp01(p.y);
            Vector3 bottom = Vector3.Lerp(e.LeftBottom.position, e.RightBottom.position, fx);
            Vector3 top = Vector3.Lerp(e.LeftUp.position, e.RightUp.position, fx);
            return Vector3.Lerp(bottom, top, fy);
        }
        /// <summary>Rotation whose forward points AWAY from the viewer through the panel, up along the panel.</summary>
        public Quaternion PanelRotation()
        {
            var e = _srd.DisplayEdges;
            Vector3 up = (e.LeftUp.position - e.LeftBottom.position).normalized;
            Vector3 right = (e.RightBottom.position - e.LeftBottom.position).normalized;
            return Quaternion.LookRotation(Vector3.Cross(right, up), up);
        }
        public float PanelWidth => HasPanel ? Vector3.Distance(_srd.DisplayEdges.LeftBottom.position, _srd.DisplayEdges.RightBottom.position) : 1f;

        // ------------------------------------------------------------------ state

        FusedVolumeLoader _vol;
        Behaviour _mover;
        SRDManager _srd;
        Material _volMat, _overlayMat;
        Mesh _cube, _overlay;
        Texture3D _tex;
        GameObject _canvas;
        CanvasGroup _group;

        bool _prepared, _playing, _seeked;
        float _time, _endSpin, _lastApplied = -1f;
        float _tRotate, _tSlice, _tGrow, _tGrowEnd, _tLabelEnd;   // phase starts
        Quaternion _start, _sagittal, _rootRot;
        float _turn;
        Vector3 _up, _toViewer, _endPos;

        bool _visible;
        Transform _target;                  // the block's final pose; moved by its own ModelMoveController in M mode
        ModelMoveController _blockMover;
        bool _editing;                      // controls on the block (true) or the brain (false); M switches
        string _copied = "";
        Matrix4x4 _block;
        float _alpha, _arrow;
        Vector3 _anchorCut;                 // start point, brain unit-cube coords

        float K => blockLength / SrdTestBlockLength;

        void Start()
        {
            _vol = FindFirstObjectByType<FusedVolumeLoader>();
            if (_vol == null) { Debug.LogError("[Loss] No FusedVolumeLoader in the scene."); return; }
            _mover = _vol.GetComponent("ModelMoveController") as Behaviour;
            _srd = SRDSceneEnvironment.GetSRDManager();

            var vs = Shader.Find("Brain/NeuronalLossVolume");
            var os = Shader.Find("Brain/OverlayUnlit");
            if (vs == null || os == null) { Debug.LogError("[Loss] Shaders Brain/NeuronalLossVolume or Brain/OverlayUnlit not found."); return; }
            _volMat = new Material(vs);
            _overlayMat = new Material(os);
            _cube = BuildUnitCube();
            _overlay = new Mesh { name = "LossOverlay" };
            _overlay.MarkDynamic();
            _tex = LoadVolume(Path.Combine(Application.streamingAssetsPath, folder, volumeFile));
            BuildCard();
            _vol.Drawn += Draw;
            ComputePhases();
            if (transportBar && FindFirstObjectByType<TimelineTransportUI>() == null)
                gameObject.AddComponent<TimelineTransportUI>().timeline = this;
        }

        void ComputePhases()
        {
            _tRotate = showBrain;
            _tSlice = _tRotate + rotate;
            _tGrow = _tSlice + slice;
            bool block = showNeuronalLoss && _tex != null;
            _tGrowEnd = block ? _tGrow + grow : _tGrow;
            _tLabelEnd = block ? _tGrowEnd + labelDelay + labelSeconds : _tGrow;
            var marks = new System.Collections.Generic.List<Mark>
            {
                new Mark { name = "Brain", start = 0f },
                new Mark { name = "Rotate", start = _tRotate },
                new Mark { name = "Slice", start = _tSlice },
            };
            if (block) marks.Add(new Mark { name = "Neuronal loss", start = _tGrow });
            Marks = marks.ToArray();
        }

        // Frame of the timeline, fixed once the brain is loaded and the SRD is up.
        void Prepare()
        {
            _prepared = true;
            GetFrame(out Vector3 right, out _up, out Vector3 fwd);
            _toViewer = -fwd;
            // The turn is Euler Y only: initialEuler.y (270) -> endY (360) (+ extraTurns x 360),
            // X and Z held at initialEuler's (0, 180). Unity applies Y last, so this is a pure
            // rotation about the world Y axis.
            _start = Quaternion.Euler(initialEuler);
            _turn = endY - initialEuler.y + Mathf.Sign(endY - initialEuler.y == 0f ? 1f : endY - initialEuler.y) * 360f * extraTurns;
            _sagittal = YRot(1f);

            _vol.sliceSweeping = false;
            _vol.sliceKeysEnabled = false;            // the timeline owns the slice
            _vol.sliceAxis = FusedVolumeLoader.Axis.Z;
            // cut from whichever end of the left-right axis faces the viewer at the end pose
            _vol.sliceReverse = Vector3.Dot(_sagittal * Vector3.back, _toViewer) < 0f;
            _vol.sliceDirection = -1;
            _vol.holeMin = _vol.holeMax = Vector3.zero;

            _anchorCut = new Vector3(
                useRoiCentre ? 0.5f * (roiX0 + roiX1) / roiLevelWidth : pointOnCut.x,
                useRoiCentre ? 0.5f * (roiY0 + roiY1) / roiLevelHeight : pointOnCut.y,
                _vol.sliceReverse ? 1f - clippingDepth : clippingDepth);
            FinalPose(out _endPos, out _rootRot);
            BuildTarget();
            _editing = controlsMoveBlock;

            _time = 0f; _seeked = true;
            _playing = playOnStart;
        }

        void Update()
        {
            if (_vol == null || !_vol.Loaded) return;
            if (!_prepared) Prepare();
            if (_playing)
            {
                _time += UnityEngine.Time.deltaTime;
                if (_time >= TotalSeconds) { _time = TotalSeconds; _playing = false; }
            }
            else if (_time >= TotalSeconds) _endSpin += spinDegreesPerSecond * UnityEngine.Time.deltaTime;   // keep turning

            var kb = Keyboard.current;
            if (kb != null && _target != null)
            {
                if (kb.mKey.wasPressedThisFrame) SetEditing(!_editing);
                if (kb.kKey.wasPressedThisFrame) CopyFinal();
            }
        }

        // ------------------------------------------------------------------ "move the block yourself" (M / K)

        // The block's final pose lives on a real Transform so it can be moved with ModelMoveController
        // and read in the Inspector. Starts at useCustomFinal's values, else at the computed place.
        void BuildTarget()
        {
            var go = new GameObject("NeuronalLossBlock");
            _target = go.transform;
            if (useCustomFinal)
            {
                _target.SetPositionAndRotation(finalPosition, Quaternion.Euler(finalEuler));
                _target.localScale = Vector3.one * finalScale;
            }
            else
            {
                _target.SetPositionAndRotation(_endPos,
                    _rootRot * Quaternion.Euler(0f, pivotYawDegrees, 0f) * Quaternion.Euler(blockEuler));
                _target.localScale = Vector3.one;
            }
            _blockMover = go.AddComponent<ModelMoveController>();
            _blockMover.enabled = false;
        }

        // Who the mouse/keyboard controls once the block is in place: the block (default,
        // controlsMoveBlock) or the brain. M switches. Switching to the block before it is in place
        // jumps to the end of the timeline and pauses there.
        void SetEditing(bool on)
        {
            _editing = on;
            if (on && _time < _tGrowEnd && showNeuronalLoss) { Pause(); SeekTo(TotalSeconds); }
            UpdateControls(_time);
        }

        void UpdateControls(float t)
        {
            bool settled = t >= _tGrowEnd;
            bool block = _editing && showNeuronalLoss && _tex != null;
            if (_blockMover != null) _blockMover.enabled = settled && block;
            if (_mover != null) _mover.enabled = settled && !block;
        }

        string FinalText()
        {
            Vector3 p = _target.position, e = _target.eulerAngles;
            return $"finalPosition: ({p.x:F3}, {p.y:F3}, {p.z:F3})  finalEuler: ({e.x:F1}, {e.y:F1}, {e.z:F1})  " +
                   $"finalScale: {_target.localScale.x:F3}";
        }

        // K: copy the block's current final transform (world) to the clipboard and the Console.
        void CopyFinal()
        {
            string s = FinalText();
            GUIUtility.systemCopyBuffer = s;
            Debug.Log("[Loss] Block final transform -> " + s);
            _copied = "copied";
        }

        void OnGUI()
        {
            if (!_editing || _target == null || !_blockMover.enabled) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.UpperLeft, wordWrap = true };
            style.normal.textColor = Color.white;
            string help = "MOVING THE NEURONAL-LOSS BLOCK (GameObject NeuronalLossBlock)   M = move the brain instead, K = copy values\n" +
                          "W/S nearer-deeper  A/D left-right  Q/E down-up  left drag = slide\n" +
                          "arrows / right drag = rotate   + / - / scroll = scale\n\n" + FinalText() +
                          (_copied.Length > 0 ? "   [" + _copied + "]" : "");
            GUI.Box(new UnityEngine.Rect(12, 12, 760, 120), help, style);
        }

        // After every Update (FusedVolumeLoader's keys, ModelMoveController), so the timeline wins.
        void LateUpdate()
        {
            if (!_prepared) return;
            bool moved = _time != _lastApplied || _seeked;
            Apply(_time, moved);
            _lastApplied = _time;
            _seeked = false;
        }

        // Everything as a function of timeline time t. The brain's rotation and slice are only
        // written when the time moved, so once the timeline is at rest ModelMoveController can turn
        // the brain freely.
        void Apply(float t, bool timeMoved)
        {
            Transform tr = _vol.transform;
            if (timeMoved)
            {
                if (t < _tRotate) tr.rotation = _start;
                else if (t < _tSlice) tr.rotation = YRot(Ease((t - _tRotate) / rotate));
                else tr.rotation = _sagittal;

                _vol.sliceSweeping = false;
                _vol.slicePosition = t < _tSlice ? 0f : clippingDepth * Ease((t - _tSlice) / slice);

                UpdateControls(t);
            }

            // block
            _visible = showNeuronalLoss && _tex != null && t >= _tGrow;
            if (!_visible) { _alpha = 0f; _arrow = 0f; _canvas.SetActive(false); return; }

            float u = Mathf.Clamp01((t - _tGrow) / grow);
            _alpha = Mathf.Clamp01((t - _tGrow) / (0.15f * grow));
            _arrow = showArrow ? u : 0f;
            float spin = spinDegreesPerSecond * (t - _tGrow) + _endSpin;
            // final pose = the target transform (computed place, your own values, or wherever you moved it)
            Quaternion rot = Quaternion.AngleAxis(spin, Vector3.up) * _target.rotation;
            Vector3 startPos = Anchor();
            Vector3 control = startPos + _toViewer * arcTowardViewer;   // out of the cut face first, then sideways
            Vector3 pos = Bezier(startPos, control, _target.position, EaseInOutCubic(u));
            float sizeK = Mathf.LerpUnclamped(startScale, 1f, EaseOutBack(u, overshoot));
            Vector3 size = new Vector3(blockLength * width / depth, blockLength * height / depth, blockLength)
                           * _target.localScale.x;
            _block = Matrix4x4.TRS(pos, rot, size * sizeK) * Matrix4x4.Translate(-0.5f * Vector3.one);

            float label = Mathf.Clamp01((t - _tGrowEnd - labelDelay) / labelSeconds);
            _canvas.SetActive(label > 0f);
            if (label > 0f)
            {
                _group.alpha = label;
                Bounds b = BlockBounds();
                _canvas.transform.SetPositionAndRotation(
                    new Vector3(b.center.x, b.max.y + labelGap * K, b.center.z), _rootRot);
            }
        }

        // Where the block settles: out of the cross-section toward the viewer (comeOut) and past the
        // brain's FRONT edge (sideDistance), level with the start point -- computed for the brain in
        // its left sagittal pose. Faces the viewer with its vertical axis world-up.
        void FinalPose(out Vector3 pos, out Quaternion rot)
        {
            GetFrame(out Vector3 right, out Vector3 up, out Vector3 fwd);
            Transform tr = _vol.transform;
            Quaternion saved = tr.rotation;
            tr.rotation = _sagittal;                                   // rotation only, restored below
            Matrix4x4 m = _vol.UnitCubeToWorld;
            tr.rotation = saved;

            // front = the x end of the cut face further to the screen-left
            Vector3 x0 = m.MultiplyPoint(new Vector3(0f, _anchorCut.y, _anchorCut.z));
            Vector3 x1 = m.MultiplyPoint(new Vector3(1f, _anchorCut.y, _anchorCut.z));
            Vector3 front = Vector3.Dot(x1 - x0, right) < 0f ? x1 : x0;
            pos = front - right * sideDistance + (-fwd) * comeOut;

            Vector3 f = fwd; f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            rot = Quaternion.LookRotation(f.normalized, Vector3.up);
        }

        // Euler (x, y0 + k * turn, z): only the Y angle moves.
        Quaternion YRot(float k) =>
            Quaternion.Euler(initialEuler.x, initialEuler.y + _turn * k, initialEuler.z);

        Vector3 Anchor() => _vol.UnitCubeToWorld.MultiplyPoint(_anchorCut);   // follows the brain

        Bounds BlockBounds()
        {
            var b = new Bounds(_block.MultiplyPoint(Vector3.zero), Vector3.zero);
            for (int i = 1; i < 8; i++)
                b.Encapsulate(_block.MultiplyPoint(new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)));
            return b;
        }

        // ------------------------------------------------------------------ drawing (after the brain)

        void Draw(Camera cam)
        {
            if (!_visible || cam == null) return;

            if (_alpha > 0.001f)
            {
                _volMat.SetTexture("_Volume", _tex);
                _volMat.SetFloat("_StepCount", stepCount);
                _volMat.SetFloat("_Alpha", _alpha);
                _volMat.SetColor("_TissueColor", tissueColor);
                _volMat.SetFloat("_TissueThreshold", tissueThreshold);
                _volMat.SetFloat("_TissueDensity", tissueDensity);
                _volMat.SetFloat("_TissueShade", tissueShade);
                _volMat.SetColor("_LossColorLow", lossColorLow);
                _volMat.SetColor("_LossColorHigh", lossColorHigh);
                _volMat.SetFloat("_LossThreshold", lossThreshold);
                _volMat.SetFloat("_LossSoftness", lossSoftness);
                _volMat.SetFloat("_LossDensity", lossDensity);
                _volMat.SetPass(0);
                Graphics.DrawMeshNow(_cube, _block);
            }

            // optional arrow from the point on the cut face to the nearest point of the block's box
            if (_arrow > 0f)
            {
                Vector3 anchor = Anchor();
                Vector3 target = BlockBounds().ClosestPoint(anchor);
                Vector3 dir = target - anchor;
                float len = dir.magnitude;
                dir = len > 1e-5f ? dir / len : Vector3.right;
                float drawn = _arrow * Mathf.Max(0f, len - tipGap * K);
                Vector3 end = anchor + dir * drawn;
                Vector3 toEye = (cam.transform.position - end).normalized;
                float headK = Mathf.Clamp01((_arrow - 0.85f) / 0.15f);
                BuildOverlay(anchor, end, dir, toEye, headK);
                var c = lineColor; c.a *= Mathf.Clamp01(_arrow * 4f);
                _overlayMat.SetColor("_Color", c);
                _overlayMat.SetPass(0);
                Graphics.DrawMeshNow(_overlay, Matrix4x4.identity);
            }
        }

        // One camera-facing mesh: line quad (stopping short of the head), arrowhead, anchor disc.
        void BuildOverlay(Vector3 a, Vector3 end, Vector3 dir, Vector3 toEye, float headK)
        {
            float w = 0.5f * lineWidth * K, hl = headLength * K * headK, hw = 0.5f * headWidth * K * headK;
            float r = 0.5f * anchorDotDiameter * K;
            Vector3 side = Vector3.Cross(dir, toEye);
            side = side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.up;
            Vector3 lineEnd = end - dir * hl * 0.9f;
            Vector3 du = Vector3.Cross(toEye, Vector3.up).sqrMagnitude > 1e-6f ? Vector3.Cross(toEye, Vector3.up).normalized : Vector3.right;
            Vector3 dv = Vector3.Cross(du, toEye).normalized;

            const int seg = 24;
            var v = new Vector3[4 + 3 + seg + 1];
            var tri = new int[6 + 3 + seg * 3];
            v[0] = a - side * w; v[1] = a + side * w; v[2] = lineEnd + side * w; v[3] = lineEnd - side * w;
            tri[0] = 0; tri[1] = 1; tri[2] = 2; tri[3] = 0; tri[4] = 2; tri[5] = 3;
            v[4] = end; v[5] = end - dir * hl - side * hw; v[6] = end - dir * hl + side * hw;
            tri[6] = 4; tri[7] = 5; tri[8] = 6;
            int c0 = 7;
            v[c0] = a;
            for (int i = 0; i < seg; i++)
            {
                float ang = i / (float)seg * Mathf.PI * 2f;
                v[c0 + 1 + i] = a + (du * Mathf.Cos(ang) + dv * Mathf.Sin(ang)) * r;
                tri[9 + i * 3] = c0; tri[10 + i * 3] = c0 + 1 + i; tri[11 + i * 3] = c0 + 1 + (i + 1) % seg;
            }
            _overlay.Clear();
            _overlay.vertices = v;
            _overlay.triangles = tri;
            _overlay.RecalculateBounds();
        }

        // ------------------------------------------------------------------ label card (world-space uGUI)

        void BuildCard()
        {
            _canvas = new GameObject("NeuronalLoss_Label", typeof(Canvas));
            _canvas.transform.SetParent(transform, false);
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            _canvas.transform.localScale = Vector3.one * 0.001f * K;   // canvas units = SRD_test mm
            _group = _canvas.AddComponent<CanvasGroup>();
            _group.interactable = false; _group.blocksRaycasts = false;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var card = UiKit.Rect("Card", _canvas.transform);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f);
            card.pivot = new Vector2(0.5f, 0f);                         // bottom-centre on the anchor
            var bg = card.gameObject.AddComponent<Image>();
            bg.color = cardColor;
            var titleText = UiKit.Label("Title", card, title, titleFontSize, FontStyle.BoldAndItalic, titleColor, TextAnchor.UpperCenter, font);
            var tagText = UiKit.Label("Tag", card, tagLine, tagFontSize, FontStyle.BoldAndItalic, tagColor, TextAnchor.UpperCenter, font);

            float w = Mathf.Ceil(Mathf.Max(titleText.preferredWidth, tagText.preferredWidth)) + 2f * padding + 8f;
            float inner = w - 2f * padding, y = padding;
            Top(titleText.rectTransform, y, inner, titleFontSize * 1.25f); y += titleFontSize * 1.25f;
            Top(tagText.rectTransform, y, inner, tagFontSize * 1.3f); y += tagFontSize * 1.3f;
            card.sizeDelta = new Vector2(w, y + padding);
            _canvas.SetActive(false);
        }

        static void Top(RectTransform rt, float top, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(0f, -top);
        }

        // ------------------------------------------------------------------ helpers

        void GetFrame(out Vector3 right, out Vector3 up, out Vector3 fwd)
        {
            Transform f = (_srd != null && _srd.isActiveAndEnabled) ? _srd.transform : null;
            right = f != null ? f.right : Vector3.right;
            up = f != null ? f.up : Vector3.up;
            fwd = f != null ? f.forward : Vector3.forward;   // away from the viewer
        }

        Texture3D LoadVolume(string path)
        {
            if (!File.Exists(path)) { Debug.LogError("[Loss] Missing " + path); return null; }
            byte[] b = File.ReadAllBytes(path);
            if (b.Length != width * height * depth * 2)
            {
                Debug.LogError($"[Loss] {Path.GetFileName(path)} is {b.Length} bytes, expected {width}x{height}x{depth}x2.");
                return null;
            }
            var tex = new Texture3D(width, height, depth, TextureFormat.RG16, false)
            {
                name = "NeuronalLossVolume",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixelData(b, 0);
            tex.Apply(false, true);
            Debug.Log($"[Loss] Loaded {Path.GetFileName(path)} ({width}x{height}x{depth} RG).");
            return tex;
        }

        void OnDestroy()
        {
            if (_vol != null) { _vol.Drawn -= Draw; _vol.sliceKeysEnabled = true; }
            if (_tex != null) Destroy(_tex);
            if (_volMat != null) Destroy(_volMat);
            if (_overlayMat != null) Destroy(_overlayMat);
            if (_overlay != null) Destroy(_overlay);
            if (_cube != null) Destroy(_cube);
        }

        static float EaseInOutCubic(float x)
        {
            x = Mathf.Clamp01(x);
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) * 0.5f;
        }

        // 0 -> 1 with a brief overshoot of about `amount` before settling (amount 0 = ease-out cubic)
        static float EaseOutBack(float x, float amount)
        {
            x = Mathf.Clamp01(x);
            float c1 = amount * 17f;                 // amount 0.1 ~ standard easeOutBack (c1 1.70158)
            float c3 = c1 + 1f;
            float y = x - 1f;
            return 1f + c3 * y * y * y + c1 * y * y;
        }

        static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float m = 1f - t;
            return m * m * a + 2f * m * t * b + t * t * c;
        }

        static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
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
            var m = new Mesh { name = "LossUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

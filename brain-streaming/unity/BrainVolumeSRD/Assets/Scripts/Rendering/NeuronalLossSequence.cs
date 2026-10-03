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
    ///                       appears tiny, then grows as it travels in a straight
    ///                       line (straightPath; off = the old curve out of the cut
    ///                       face and to the left) to its place past the brain's
    ///                       FRONT edge (sideDistance), settling
    ///                       comeOut in front of the cut face, overshooting slightly,
    ///                       without turning (spinDegreesPerSecond 0);
    ///                       then the label fades in                                  grow s (+ label, hold)
    /// Optional (both off by default): with splitVolume set, a "Split" step after Rotate moves the
    /// brain to splitLeftPosition while the second brain (e.g. the label-coloured FusedVolume, same
    /// rotation / scale / cut) fades in and moves to splitRightPosition; with sliceBack the cut is
    /// undone at the end ("Slice back"); with finalTurnSeconds > 0 both brains then turn about world
    /// Y ("Turn"). Scene "Nissl and Labels V1" uses all three (30 s in total).
    /// With useStartPose the brain is put at startPosition / startRotation / startBrainScale once when
    /// the timeline starts and the Rotate step turns it the shortest way to the sagittal view.
    /// Apart from that the brain's transform POSITION (and scale) is never
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
        [Tooltip("The brain to drive (a BrickVolumeLoader or FusedVolumeLoader). Empty = the active Brain " +
                 "(BrickVolumeLoader), else an active FusedVolumeLoader.")]
        public MonoBehaviour volume;
        public bool playOnStart = true;
        [Tooltip("At the end, start again from the beginning (and keep playing).")]
        public bool loop = false;
        [Tooltip("Add the play/pause + seek bar (TimelineTransportUI) if the scene has none.")]
        public bool transportBar = true;
        [Tooltip("ON = the brain starts at startPosition / startRotation / startScale (world space, set once " +
                 "when the timeline starts) and the Rotate step turns it the shortest way from startRotation to " +
                 "the left sagittal view (initialEuler X/Z with Y = endY). OFF = the Y-only turn from initialEuler.")]
        public bool useStartPose = true;
        public Vector3 startPosition = new Vector3(-0.0225f, 0.53528f, -0.57f);
        public Quaternion startRotation = new Quaternion(0.52188f, -0.04395f, -0.84894f, 0.07078f);
        public float startBrainScale = 1.4f;
        [Tooltip("Rotation the brain starts from when useStartPose is off (position/scale untouched).")]
        public Vector3 initialEuler = new Vector3(0f, 270f, 180f);
        [Tooltip("Euler Y the turn ends on (the left sagittal view). Only Y changes: X and Z stay at initialEuler's.")]
        public float endY = 360f;
        [Tooltip("Extra full turns added to the 270 -> 360 Y turn.")]
        [Range(0, 3)] public int extraTurns = 0;
        [Tooltip("Off = the timeline ends after slicing (no neuronal-loss block).")]
        public bool showNeuronalLoss = true;
        [Tooltip("Off = no Slice step: the brain stays whole (e.g. V4, where the fibres are shown inside it).")]
        public bool slicing = true;
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

        [Header("Split: a second brain after the turn (e.g. the label-coloured FusedVolume)")]
        [Tooltip("Second brain (BrickVolumeLoader / FusedVolumeLoader). Empty = no split. It is hidden until the " +
                 "Split step, always copies the brain's rotation, scale and slice, and its position is set by the timeline.")]
        public MonoBehaviour splitVolume;
        [Tooltip("Seconds for the two brains to move apart (after Rotate, before Slice).")]
        public float split = 3f;
        [Tooltip("Where the driven brain (Nissl) ends up after the split, world space.")]
        public Vector3 splitLeftPosition = new Vector3(-0.36f, 0.49f, -0.57f);
        [Tooltip("Where the split brain (labels) ends up after the split, world space.")]
        public Vector3 splitRightPosition = new Vector3(0.36f, 0.49f, -0.57f);
        [Tooltip("Optional third brain that also splits off (e.g. the BFI NIfTI as an NpzDensityVolume): hidden " +
                 "until the Split step, then fades in and moves to splitThirdPosition, same size as the brain. Its " +
                 "rotation and cut come from its own NpzDensityVolume.follow (set that to the brain).")]
        public NpzDensityVolume splitThird;
        public Vector3 splitThirdPosition = new Vector3(-0.5f, 0.49f, -0.3f);
        [Tooltip("The brains' size at the end of the split, as a fraction of the start size (room for three side by side).")]
        [Range(0.3f, 1f)] public float splitScale = 1f;

        [Header("Slice back")]
        [Tooltip("After the slice (and the neuronal-loss block, if shown), undo the cut.")]
        public bool sliceBack = false;
        [Tooltip("Seconds the cut stays at clippingDepth before it is undone.")]
        public float sliceBackDelay = 1.5f;
        public float sliceBackSeconds = 4f;

        [Header("Brain moves back (after the slice, before the neuronal-loss block)")]
        [Tooltip("Seconds for the brain to move back and shrink, making room for what comes out of it. 0 = off.")]
        public float recede = 0f;
        [Tooltip("How far the brain moves (world units; +z = deeper into the display, away from the viewer).")]
        public Vector3 recedeOffset = new Vector3(0f, 0f, 0.35f);
        [Tooltip("The brain's size after moving back, as a fraction of its start size.")]
        [Range(0.2f, 1f)] public float recedeScale = 0.7f;
        [Tooltip("ON = the brain moves back while the block (and maps) come out, starting together. " +
                 "OFF = it moves back first, then they come out.")]
        public bool recedeDuringGrow = false;

        [Header("Return to the start (after the hold)")]
        [Tooltip("At the end: the block (and TimelineDensityOverlay maps) fly back into the cut face and split-off " +
                 "brains slide back into the brain (Combine), then " +
                 "the brain turns back to its start rotation while the cut closes and it comes back from moving back " +
                 "(Return) -- the timeline ends exactly on the start pose, so a loop restarts seamlessly.")]
        public bool returnToStart = false;
        public float combineSeconds = 2.5f;
        public float returnSeconds = 2.5f;

        [Header("Final turn")]
        [Tooltip("Seconds for a turn about world Y at the end (after the slice back), both brains. 0 = off.")]
        public float finalTurnSeconds = 0f;
        [Tooltip("How far the final turn goes (degrees, 360 = one full turn back to the sagittal view).")]
        public float finalTurnDegrees = 360f;

        [Header("Growth")]
        [Tooltip("Size at the start point, as a fraction of the full size.")]
        [Range(0.01f, 0.5f)] public float startScale = 0.02f;
        [Tooltip("ON = the block travels in a straight line from the point on the cut face to its final place. " +
                 "OFF = the curved path (arcTowardViewer).")]
        public bool straightPath = true;
        [Tooltip("Curved path only: how far the path first heads straight out of the cut face toward the viewer before it " +
                 "curves to the side (Unity units; Bezier control point = start + this toward the viewer).")]
        public float arcTowardViewer = 0.30f;
        [Tooltip("Size overshoot before settling (0 = none, 0.1 = 10 % bigger for a moment).")]
        [Range(0f, 0.3f)] public float overshoot = 0.08f;
        [Tooltip("Draw SRD_test's arrow from the start point to the block.")]
        public bool showArrow = false;

        [Header("Block")]
        [Tooltip("Length of the block's long axis (the 117-section stack) in Unity units.")]
        public float blockLength = 0.20f;
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
        [Tooltip("Per-axis multiplier on the block's size (1,1,1 = true proportions, blockLength long). " +
                 "X = across the 48 columns, Y = across the 48 rows, Z = along the 117 sections.")]
        public Vector3 finalScale = Vector3.one;

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
        /// <summary>Extra marks inside the hold (start = seconds after HoldStart), shown on the transport bar.
        /// Fill in Awake (e.g. AxonRepairTimeline), before the phases are computed in Start.</summary>
        [System.NonSerialized] public System.Collections.Generic.List<Mark> holdMarks = new System.Collections.Generic.List<Mark>();
        /// <summary>Extra "loaded?" checks the timeline waits for before it starts (like splitThird). Fill in Awake.</summary>
        [System.NonSerialized] public System.Collections.Generic.List<System.Func<bool>> waitFor = new System.Collections.Generic.List<System.Func<bool>>();
        public float TotalSeconds => returnToStart ? _tReturn + returnSeconds : _tEnd + hold;
        /// <summary>0 -> 1 while the block / maps fly back into the brain (Combine step), else 0.</summary>
        public float CombineAmount =>
            returnToStart && _time >= _tCombine ? EaseInOutCubic((_time - _tCombine) / Mathf.Max(0.01f, combineSeconds)) : 0f;
        public float Time => _time;
        /// <summary>When the hold starts (everything else done): AxonRepairTimeline runs its stages from here.</summary>
        public float HoldStart => _tEnd;
        public float CombineStart => _tCombine;
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

        ISliceableVolume _vol;
        Behaviour _mover;
        SRDManager _srd;
        Material _volMat, _overlayMat;
        Mesh _cube, _overlay;
        Texture3D _tex;
        GameObject _canvas;
        CanvasGroup _group;

        bool _prepared, _playing, _seeked;
        float _time, _endSpin, _lastApplied = -1f;
        float _tRotate, _tSplit, _tSlice, _tRecede, _tGrow, _tGrowEnd, _tLabelEnd, _tBack, _tTurn, _tEnd, _tCombine, _tReturn;   // phase starts
        Quaternion _start, _sagittal, _rootRot;
        float _turn;
        Vector3 _up, _toViewer, _endPos, _startPos, _startScale;

        ISliceableVolume _split;            // second brain (splitVolume), null = no split
        Behaviour _splitRenderer;           // its loader, switched off until the Split step
        float _splitOpacity;                // its opacity as set in the Inspector (faded in during the split)

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
            _vol = FindVolume();
            if (_vol == null) { Debug.LogError("[Loss] No active brain volume (BrickVolumeLoader or FusedVolumeLoader) in the scene."); return; }
            Debug.Log($"[Loss] Timeline drives '{_vol.transform.name}' ({_vol.GetType().Name}).");
            _mover = _vol.transform.GetComponent("ModelMoveController") as Behaviour;
            _srd = SRDSceneEnvironment.GetSRDManager();
            if (splitVolume != null && splitVolume.isActiveAndEnabled && splitVolume is ISliceableVolume sv && sv != _vol)
            {
                _split = sv;
                _splitRenderer = splitVolume;
                _splitOpacity = SplitOpacity;
                // the timeline places it; its own controls would fight that
                if (splitVolume.GetComponent("ModelMoveController") is Behaviour m) m.enabled = false;
                Debug.Log($"[Loss] Split brain: '{splitVolume.name}' ({splitVolume.GetType().Name}).");
            }

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

        // The brain the timeline drives: `volume` if set, else the active bricked Brain
        // (BrickVolumeLoader), else an active FusedVolumeLoader. Inactive objects are ignored, so
        // a switched-off FusedVolume stays in the scene untouched.
        ISliceableVolume FindVolume()
        {
            if (volume != null && volume.isActiveAndEnabled && volume is ISliceableVolume v) return v;
            ISliceableVolume found = FindFirstObjectByType<BrainVolume.SRD.BrickVolumeLoader>();
            if (found == null) found = FindFirstObjectByType<FusedVolumeLoader>();
            return found;
        }

        void ComputePhases()
        {
            _tRotate = showBrain;
            _tSplit = _tRotate + rotate;
            _tSlice = _tSplit + (_split != null ? split : 0f);
            _tRecede = _tSlice + (slicing ? slice : 0f);
            _tGrow = _tRecede + (recedeDuringGrow ? 0f : Mathf.Max(0f, recede));
            bool block = showNeuronalLoss && _tex != null;
            _tGrowEnd = block ? _tGrow + grow : _tGrow;
            _tLabelEnd = block ? _tGrowEnd + labelDelay + labelSeconds : _tGrow;
            _tBack = sliceBack ? _tLabelEnd + sliceBackDelay : _tLabelEnd;
            _tTurn = sliceBack ? _tBack + sliceBackSeconds : _tLabelEnd;
            _tEnd = _tTurn + Mathf.Max(0f, finalTurnSeconds);
            _tCombine = _tEnd + hold;
            _tReturn = _tCombine + Mathf.Max(0f, combineSeconds);
            var marks = new System.Collections.Generic.List<Mark>
            {
                new Mark { name = "Brain", start = 0f },
                new Mark { name = "Rotate", start = _tRotate },
            };
            if (_split != null) marks.Add(new Mark { name = "Split", start = _tSplit });
            if (slicing) marks.Add(new Mark { name = "Slice", start = _tSlice });
            if (recede > 0f && !recedeDuringGrow) marks.Add(new Mark { name = "Brain back", start = _tRecede });
            if (block) marks.Add(new Mark { name = "Neuronal loss", start = _tGrow });
            if (sliceBack) marks.Add(new Mark { name = "Slice back", start = _tBack });
            if (finalTurnSeconds > 0f) marks.Add(new Mark { name = "Turn", start = _tTurn });
            foreach (var m in holdMarks) marks.Add(new Mark { name = m.name, start = _tEnd + m.start });
            if (returnToStart)
            {
                marks.Add(new Mark { name = "Combine", start = _tCombine });
                marks.Add(new Mark { name = "Return", start = _tReturn });
            }
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
            if (useStartPose)
            {
                // the only time the timeline writes the brain's position/scale: the start pose, once
                Transform tr = _vol.transform;
                _start = Quaternion.Normalize(startRotation);
                tr.SetPositionAndRotation(startPosition, _start);
                tr.localScale = Vector3.one * startBrainScale;
            }
            _startPos = _vol.transform.position;
            _startScale = _vol.transform.localScale;

            _vol.SliceKeysEnabled = false;            // the timeline owns the slice
            if (_split != null) _split.SliceKeysEnabled = false;
            // cut from whichever end of the left-right axis faces the viewer at the end pose
            _vol.SliceFromHighZ = Vector3.Dot(_sagittal * Vector3.back, _toViewer) < 0f;

            _anchorCut = new Vector3(
                useRoiCentre ? 0.5f * (roiX0 + roiX1) / roiLevelWidth : pointOnCut.x,
                useRoiCentre ? 0.5f * (roiY0 + roiY1) / roiLevelHeight : pointOnCut.y,
                _vol.SliceFromHighZ ? 1f - clippingDepth : clippingDepth);
            FinalPose(out _endPos, out _rootRot);
            BuildTarget();
            _editing = controlsMoveBlock;

            _time = 0f; _seeked = true;
            _playing = playOnStart;
        }

        void Update()
        {
            if (_vol == null || !_vol.Loaded) return;
            if (!_prepared && _split != null && !_split.Loaded) return;   // start once both brains are in
            if (!_prepared && splitThird != null && splitThird.isActiveAndEnabled && !splitThird.Loaded) return;
            if (!_prepared) foreach (var ready in waitFor) if (!ready()) return;
            if (!_prepared) Prepare();
            if (_playing)
            {
                _time += UnityEngine.Time.deltaTime;
                if (_time >= TotalSeconds)
                {
                    if (loop) SeekTo(0f);
                    else { _time = TotalSeconds; _playing = false; }
                }
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
                _target.localScale = finalScale;
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
            bool hasBlock = showNeuronalLoss && _tex != null;
            // with returnToStart the timeline keeps everything moving to the end: no hand controls
            bool settled = !returnToStart && t >= (hasBlock ? _tGrowEnd : _tEnd);
            bool block = _editing && hasBlock;
            if (_blockMover != null) _blockMover.enabled = settled && block;
            if (_mover != null) _mover.enabled = settled && !block;
        }

        string FinalText()
        {
            Vector3 p = _target.position, e = _target.eulerAngles;
            return $"finalPosition: ({p.x:F3}, {p.y:F3}, {p.z:F3})  finalEuler: ({e.x:F1}, {e.y:F1}, {e.z:F1})  " +
                   $"finalScale: ({_target.localScale.x:F3}, {_target.localScale.y:F3}, {_target.localScale.z:F3})";
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
            // Return step: 0 -> 1 while the brain turns back to its start pose (rotation, cut, position, size)
            float ret = returnToStart && t >= _tReturn ? Ease((t - _tReturn) / Mathf.Max(0.01f, returnSeconds)) : 0f;
            if (timeMoved)
            {
                if (t < _tRotate) tr.rotation = _start;
                else if (t < _tSplit) tr.rotation = TurnRot(Ease((t - _tRotate) / rotate));
                else if (ret > 0f) tr.rotation = TurnRot(1f - ret);   // the Rotate step backwards
                else if (finalTurnSeconds > 0f && t >= _tTurn)
                    tr.rotation = Quaternion.AngleAxis(finalTurnDegrees * Ease((t - _tTurn) / finalTurnSeconds), Vector3.up) * _sagittal;
                else tr.rotation = _sagittal;

                float cut = t < _tSlice || !slicing ? 0f : clippingDepth * Ease((t - _tSlice) / slice);
                if (sliceBack && t >= _tBack) cut = clippingDepth * (1f - Ease((t - _tBack) / sliceBackSeconds));
                _vol.SlicePosition = cut * (1f - ret);

                Vector3 basePos = _startPos, baseScale = _startScale;
                if (_split != null)
                {
                    // both start where the brain is; after the turn they move apart, brain left, split brain right
                    // Combine (returnToStart): the brains slide back together, the split-off ones fading out
                    float merge = CombineAmount;
                    float s = (t < _tSplit ? 0f : EaseInOutCubic((t - _tSplit) / split)) * (1f - merge);
                    basePos = Vector3.Lerp(_startPos, splitLeftPosition, s);
                    tr.position = basePos;
                    baseScale = _startScale * Mathf.Lerp(1f, splitScale, s);
                    tr.localScale = baseScale;
                    _split.transform.position = Vector3.Lerp(_startPos, splitRightPosition, s);
                    bool shown = t >= _tSplit && merge < 1f;
                    if (_splitRenderer.enabled != shown) _splitRenderer.enabled = shown;
                    float fade = Mathf.Clamp01((t - _tSplit) / (0.4f * Mathf.Max(0.01f, split))) *
                                 Mathf.Clamp01((1f - merge) / 0.4f);
                    SplitOpacity = _splitOpacity * fade;
                    if (splitThird != null)
                    {
                        splitThird.transform.position = Vector3.Lerp(_startPos, splitThirdPosition, s);
                        splitThird.visibility = shown ? fade : 0f;
                    }
                }
                if (recede > 0f)
                {
                    // move back and shrink, making room for what comes out of the cut face
                    float k = t < _tRecede ? 0f : EaseInOutCubic((t - _tRecede) / recede);
                    k *= 1f - ret;                                   // and comes forward again on Return
                    tr.position = basePos + recedeOffset * k;
                    tr.localScale = baseScale * Mathf.Lerp(1f, recedeScale, k);
                }

                UpdateControls(t);
            }
            if (_split != null)
            {
                // the split brain shows the same anatomical view: same rotation, size and cut (every frame,
                // so it also follows the brain when it is moved by hand after the timeline)
                Transform st = _split.transform;
                st.rotation = tr.rotation;
                st.localScale = tr.localScale;
                if (splitThird != null) splitThird.transform.localScale = tr.localScale;   // same mm scale as the brain
                _split.SliceFromHighZ = _vol.SliceFromHighZ;
                _split.SlicePosition = _vol.SlicePosition;
            }

            // block
            float back = CombineAmount;   // 0 -> 1: flying back into the cut face at the end
            _visible = showNeuronalLoss && _tex != null && t >= _tGrow && back < 1f;
            if (!_visible) { _alpha = 0f; _arrow = 0f; _canvas.SetActive(false); return; }

            float u = Mathf.Clamp01((t - _tGrow) / grow);
            _alpha = Mathf.Clamp01((t - _tGrow) / (0.15f * grow)) * Mathf.Clamp01((1f - back) / 0.25f);
            _arrow = showArrow ? u : 0f;
            float spin = spinDegreesPerSecond * (t - _tGrow) + _endSpin;
            // final pose = the target transform (computed place, your own values, or wherever you moved it)
            Quaternion rot = Quaternion.AngleAxis(spin, Vector3.up) * _target.rotation;
            Vector3 startPos = Anchor();
            Vector3 pos;
            if (straightPath) pos = Vector3.Lerp(startPos, _target.position, EaseInOutCubic(u));
            else
            {
                Vector3 control = startPos + _toViewer * arcTowardViewer;   // out of the cut face first, then sideways
                pos = Bezier(startPos, control, _target.position, EaseInOutCubic(u));
            }
            float sizeK = Mathf.LerpUnclamped(startScale, 1f, EaseOutBack(u, overshoot));
            if (back > 0f)
            {
                // the way it came: straight back into the cut face, shrinking
                pos = Vector3.Lerp(pos, startPos, back);
                sizeK = Mathf.Lerp(sizeK, startScale, back);
            }
            Vector3 size = Vector3.Scale(new Vector3(blockLength * width / depth, blockLength * height / depth, blockLength),
                                         _target.localScale);   // per axis (NeuronalLossBlock's X/Y/Z scale)
            _block = Matrix4x4.TRS(pos, rot, size * sizeK) * Matrix4x4.Translate(-0.5f * Vector3.one);

            float label = Mathf.Clamp01((t - _tGrowEnd - labelDelay) / labelSeconds) * Mathf.Clamp01(1f - back * 5f);
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

        // The Rotate step at k (0..1): the Y-only turn, or with useStartPose the shortest turn from
        // startRotation to the sagittal pose plus extraTurns full turns about world Y.
        Quaternion TurnRot(float k) => !useStartPose ? YRot(k) :
            Quaternion.AngleAxis(360f * extraTurns * k, Vector3.up) * Quaternion.Slerp(_start, _sagittal, k);

        Vector3 Anchor() => _vol.UnitCubeToWorld.MultiplyPoint(_anchorCut);   // follows the brain

        /// <summary>The point on the cut face the neuronal-loss block comes out of (world, follows the brain).
        /// TimelineDensityOverlay sends its maps out of the same point.</summary>
        public Vector3 CutAnchorWorld => _prepared ? Anchor() : (_vol != null ? _vol.transform.position : transform.position);
        /// <summary>Seconds the block takes to travel out and grow; its start size and overshoot.</summary>
        public float GrowSeconds => grow;
        public float GrowStartScale => startScale;
        public float GrowOvershoot => overshoot;

        // the split brain's opacity (faded in as the two brains move apart)
        float SplitOpacity
        {
            get => splitVolume is FusedVolumeLoader f ? f.opacity
                 : splitVolume is BrainVolume.SRD.BrickVolumeLoader b ? b.opacity : 1f;
            set
            {
                if (splitVolume is FusedVolumeLoader f) f.opacity = value;
                else if (splitVolume is BrainVolume.SRD.BrickVolumeLoader b) b.opacity = value;
            }
        }

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
            if (_vol != null) { _vol.Drawn -= Draw; _vol.SliceKeysEnabled = true; }
            if (_split != null) { _split.SliceKeysEnabled = true; SplitOpacity = _splitOpacity; }
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

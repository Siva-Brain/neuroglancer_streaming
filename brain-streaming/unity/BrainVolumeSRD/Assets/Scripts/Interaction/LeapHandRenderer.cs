using System.Collections.Generic;
using Leap;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Shows the tracked hands as real, smooth 3-D hands (Ultraleap's rigged GenericHand model, the
    /// "GhostHands" prefab) on top of the brain. Ultraleap's HandBinder poses the skinned mesh from the
    /// tracking data as usual, but Unity's own drawing of it is switched off (forceRenderingOff): the volume
    /// shaders draw over everything, so a normally rendered hand would vanish behind the brain. Instead the
    /// posed mesh is baked every frame and drawn from the brain's Drawn event with Brain/HandOverlay (depth
    /// pre-pass + soft shading + rim, slightly see-through). The colour follows LeapBrainManipulator's
    /// gesture: hand colour = tracked, tinted teal = holding the brain (grab), yellow = scaling (two-hand grab),
    /// magenta = moving the glass slide.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // LateUpdate after HandBinder has posed the bones
    public sealed class LeapHandRenderer : MonoBehaviour
    {
        public LeapBrainManipulator manipulator;
        [Tooltip("Empty = the manipulator's brain.")]
        public MonoBehaviour volume;
        [Tooltip("Ultraleap hand prefab with a HandBinder per hand (Hands/Runtime/Prefabs/Built In Render Pipeline/GhostHands).")]
        public GameObject handsPrefab;

        [Header("Look")]
        public Color handColor = new Color(0.93f, 0.91f, 0.89f, 0.88f);
        [Tooltip("Tint while the hand holds the brain (one-hand grab: move + turn).")]
        public Color grabColor = new Color(0.18f, 0.83f, 0.75f, 1f);
        public Color scaleColor = new Color(1.00f, 0.85f, 0.25f, 1f);
        [Tooltip("Tint while the hand moves the glass slide (LeapSliceSlide).")]
        public Color slideColor = new Color(0.95f, 0.35f, 0.85f, 1f);
        [Tooltip("Tint while the hand holds the magnifying lens (LeapLens).")]
        public Color lensColor = new Color(0.45f, 0.75f, 1.00f, 1f);
        [Tooltip("How strongly a gesture tints the hand (0 = never, 1 = fully the gesture colour).")]
        [Range(0f, 1f)] public float gestureTint = 0.55f;
        [Range(0f, 1f)] public float rim = 0.35f;
        [Tooltip("The forearm fades out over this many palm widths past the wrist (0 = show the whole arm).")]
        public float armFade = 0.6f;
        [Tooltip("Seconds for the colour / fade in-out to change.")]
        public float fade = 0.12f;
        [Tooltip("Debug: with no real hand tracked, pose the hands with Ultraleap's test hands (desktop pose).")]
        public bool previewTestHands = false;

        sealed class Part
        {
            public SkinnedMeshRenderer smr;
            public Mesh baked;
            public Matrix4x4 matrix;
            public bool mirrored;
        }

        sealed class HandView
        {
            public HandModelBase model;
            public bool left;
            public readonly List<Part> parts = new List<Part>();
            public bool visible;
            public float alpha;
            public Color color;
            public Vector3 wrist, armDir;
            public float fadeLength = 1f;
        }

        readonly List<HandView> _hands = new List<HandView>();
        ISliceableVolume _vol;
        Material _mat;
        GameObject _instance;
        bool _bound;

        void Start()
        {
            if (manipulator == null) manipulator = GetComponent<LeapBrainManipulator>();
            if (manipulator == null) manipulator = FindFirstObjectByType<LeapBrainManipulator>();
            if (volume == null && manipulator != null) volume = manipulator.volume;
            if (volume == null) volume = FindFirstObjectByType<BrainVolume.SRD.BrickVolumeLoader>();
            _vol = volume as ISliceableVolume;
            var sh = Shader.Find("Brain/HandOverlay");
            if (_vol == null || sh == null || handsPrefab == null)
            {
                Debug.LogError("[Leap] LeapHandRenderer needs the brain volume, the Brain/HandOverlay shader and handsPrefab.");
                enabled = false;
                return;
            }
            _mat = new Material(sh);
            _vol.Drawn += Draw;
        }

        // The hands are created once the manipulator has its provider (it may create one in its own Start).
        void Bind()
        {
            var provider = manipulator != null ? manipulator.provider : FindFirstObjectByType<LeapProvider>();
            if (provider == null) return;
            _bound = true;
            _instance = Instantiate(handsPrefab, transform);
            _instance.name = handsPrefab.name;
            foreach (var model in _instance.GetComponentsInChildren<HandModelBase>(true))
            {
                model.leapProvider = provider;
                var hv = new HandView { model = model, left = model.Handedness == Chirality.Left, color = handColor };
                foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!smr.gameObject.activeSelf || smr.sharedMesh == null) continue;   // the model's unused LODs
                    smr.forceRenderingOff = true;                                         // drawn by us, on top
                    smr.updateWhenOffscreen = true;
                    hv.parts.Add(new Part { smr = smr, baked = new Mesh { name = smr.name + "_baked" } });
                }
                if (hv.parts.Count > 0) _hands.Add(hv);
            }
            Debug.Log($"[Leap] Hand models: {_hands.Count} ({handsPrefab.name}), provider '{provider.name}'.");
        }

        void LateUpdate()
        {
            if (!_bound) Bind();
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, fade));
            bool anyReal = false;
            foreach (var hv in _hands) anyReal |= hv.model.IsTracked && hv.model.gameObject.activeInHierarchy;

            foreach (var hv in _hands)
            {
                if (previewTestHands && !anyReal) PoseTestHand(hv);
                bool on = hv.model.gameObject.activeInHierarchy && (hv.model.IsTracked || (previewTestHands && !anyReal));
                hv.alpha = Mathf.Lerp(hv.alpha, on ? 1f : 0f, k);
                hv.visible = hv.alpha > 0.01f && hv.model.gameObject.activeInHierarchy;
                hv.color = Color.Lerp(hv.color, Target(hv.left), k);
                if (!hv.visible) continue;
                foreach (var p in hv.parts)
                {
                    if (!p.smr.gameObject.activeInHierarchy) continue;
                    // Baked without the renderer's scale, the vertices already sit where the bones put them (the
                    // bones carry the hand's size and the right hand's mirroring), so only the renderer's position /
                    // rotation is applied and the triangles keep their normal facing.
                    p.smr.BakeMesh(p.baked, false);
                    p.matrix = Matrix4x4.TRS(p.smr.transform.position, p.smr.transform.rotation, Vector3.one);
                    p.mirrored = false;
                }
                // the forearm fades out just past the wrist (the model has a whole arm)
                var lh = hv.model.GetLeapHand();
                if (lh != null)
                {
                    hv.wrist = lh.WristPosition;
                    Vector3 toElbow = lh.Arm.ElbowPosition - lh.WristPosition;
                    hv.armDir = toElbow.sqrMagnitude > 1e-8f ? toElbow.normalized : -lh.Direction;
                    hv.fadeLength = armFade * Mathf.Max(0.01f, lh.PalmWidth);
                }
            }
        }

        void PoseTestHand(HandView hv)
        {
            var provider = manipulator != null ? manipulator.provider : null;
            if (provider == null) return;
            if (!hv.model.gameObject.activeSelf) hv.model.gameObject.SetActive(true);
            foreach (var b in hv.model.GetComponents<Behaviour>())
                if (b.GetType().Name == "HandEnableDisable") b.enabled = false;   // it would hide an untracked hand
            hv.model.SetLeapHand(TestHandFactory.MakeTestHand(hv.left, TestHandFactory.TestHandPose.DesktopModeA)
                                                .Transform(new LeapTransform(provider.transform)));
            hv.model.UpdateHand();
        }

        Color Target(bool left)
        {
            Color g = handColor;
            if (manipulator != null)
                switch (manipulator.HandMode(left))
                {
                    case LeapBrainManipulator.Mode.Grab: g = grabColor; break;
                    case LeapBrainManipulator.Mode.Scale: g = scaleColor; break;
                    case LeapBrainManipulator.Mode.Slide: g = slideColor; break;
                    case LeapBrainManipulator.Mode.Lens: g = lensColor; break;
                    default: return handColor;
                }
            Color c = Color.Lerp(handColor, g, gestureTint);
            c.a = handColor.a;
            return c;
        }

        void Draw(Camera cam)
        {
            if (cam == null || _mat == null || LeapLens.IsLensCamera(cam)) return;   // no hands in the zoomed view
            _mat.SetFloat("_Rim", rim);
            foreach (var hv in _hands)
            {
                if (!hv.visible) continue;
                Color c = hv.color; c.a *= hv.alpha;
                _mat.SetColor("_Color", c);
                _mat.SetVector("_FadeOrigin", hv.wrist);
                _mat.SetVector("_FadeDir", armFade > 0f ? hv.armDir : Vector3.zero);
                _mat.SetFloat("_FadeLength", hv.fadeLength);
                foreach (var p in hv.parts)
                {
                    if (!p.smr.gameObject.activeInHierarchy) continue;
                    bool flip = GL.invertCulling;
                    GL.invertCulling = flip ^ p.mirrored;   // a mirrored hand's triangles face the other way
                    for (int sub = 0; sub < p.baked.subMeshCount; sub++)
                    {
                        _mat.SetPass(0); Graphics.DrawMeshNow(p.baked, p.matrix, sub);   // depth
                        _mat.SetPass(1); Graphics.DrawMeshNow(p.baked, p.matrix, sub);   // colour
                    }
                    GL.invertCulling = flip;
                }
            }
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            foreach (var hv in _hands) foreach (var p in hv.parts) if (p.baked != null) Destroy(p.baked);
            if (_mat != null) Destroy(_mat);
            if (_instance != null) Destroy(_instance);
        }
    }
}

using SRD.Core;
using SRD.Utils;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Plays an SRD scene on an ordinary flat screen (e.g. the 85-inch display): "Presentation All Flat".
    ///
    /// The SRDisplayManager stays in the scene but INACTIVE (saved that way, so the Sony plugin never starts). Its
    /// transform is still the display frame (rotated -45° about X, scaled SRDViewSpaceScale), and this component
    /// stands in for the panel the SRD would report: the same four edges (OriginalSize body bounds of the ELF-SR2,
    /// tilted panelTiltDegrees in that frame). So everything placed on the panel (menu bar, seek bar, cards, the
    /// split layout) lands where it does on the SRD (NeuronalLossSequence.HasPanel / PanelPoint, DisplayFrame).
    ///
    /// The camera looks at the panel's centre along its normal from viewerDistanceMetres (real metres, × the
    /// view-space scale) and frames the panel exactly, so the 16:9 output shows what the SRD panel shows, flat.
    /// A screen with another aspect gets the whole panel (fitWholePanel) or fills its height.
    /// </summary>
    [DefaultExecutionOrder(-1000)]   // before everything that asks for the panel
    public sealed class FlatDisplayRig : MonoBehaviour
    {
        public static FlatDisplayRig Instance { get; private set; }

        [Tooltip("The SRDisplayManager (kept inactive): its transform is the display frame. Empty = found in the scene.")]
        public Transform srdFrame;
        [Tooltip("The flat screen's camera. Empty = the Camera on this object.")]
        public Camera viewCamera;

        [Header("Virtual panel (ELF-SR2, real metres)")]
        public Vector2 panelSizeMetres = new Vector2(0.5977f, 0.3362f);
        [Tooltip("Panel tilt inside the display frame (the SR2 panel leans back 45°).")]
        public float panelTiltDegrees = 45f;

        [Header("Camera")]
        [Tooltip("Camera distance from the panel's centre along its normal, in real metres (× the view-space scale). " +
                 "Larger = flatter perspective; the panel always fills the screen.")]
        public float viewerDistanceMetres = 0.7f;
        [Tooltip("ON = a screen narrower than 16:9 still shows the whole panel width (bars above/below). " +
                 "OFF = the panel's height always fills the screen.")]
        public bool fitWholePanel = true;
        [Tooltip("Turn the SRDisplayManager off at start if it was left on (it should be saved inactive).")]
        public bool disableSrd = true;

        [Header("Brain start position (this screen only)")]
        [Tooltip("ON = every version (each NeuronalLossSequence) starts the brain at Brain Start Position, and so does " +
                 "the brain-only view and R; rotation and size stay each version's own.")]
        public bool overrideBrainStart = true;
        public Vector3 brainStartPosition = new Vector3(0f, 0.5f, 0.4f);
        [Tooltip("ON = the brain also stays at Brain Start Position for the whole video (V1's split and V3's recede no " +
                 "longer move it; the split-off brains and the density maps move with it). Hands / mouse / keys can still move it; R puts it back.")]
        public bool keepBrainPosition = true;

        [Header("Hand tracking (Ultraleap, device flat on the desk facing up, in front of the screen)")]
        [Tooltip("Real hand travel (metres, left to right over the device) that spans the whole panel width. " +
                 "Smaller = less arm movement, bigger hands on screen.")]
        public float handReachMetres = 0.45f;
        [Tooltip("Hand position over the device (real metres: x right, y up, z toward the screen) that lands on the panel's centre.")]
        public Vector3 handCentreMetres = new Vector3(0f, 0.25f, 0f);
        [Tooltip("That point lands this far in front of the panel (fraction of the panel width), with the brain and the menu bar.")]
        public float handInFront = 0.05f;

        /// <summary>World units per real metre of hand movement (the hands' view-space scale).</summary>
        public float HandScale
        {
            get
            {
                float w = (RightBottom - LeftBottom).magnitude;
                return w > 1e-6f ? w / Mathf.Max(0.05f, handReachMetres) : 1f;
            }
        }

        /// <summary>
        /// Place the Ultraleap provider (LeapBrainManipulator) for the flat screen: its axes are the panel's (x right,
        /// y up, z into the screen), so moving the hand up / right / toward the screen moves it the same way on screen,
        /// scaled by HandScale, with handCentreMetres over the device at the panel's centre.
        /// </summary>
        public void PlaceHandDevice(Transform t)
        {
            Vector3 lb = LeftBottom, rb = RightBottom, lu = LeftUp;
            Vector3 right = rb - lb, up = lu - lb;
            float w = right.magnitude, h = up.magnitude;
            if (w < 1e-6f || h < 1e-6f) return;
            Vector3 away = Vector3.Cross(right / w, up / h);
            Quaternion rot = Quaternion.LookRotation(away, up / h);
            float s = HandScale;
            Vector3 target = lb + 0.5f * right + 0.5f * up - away * (handInFront * w);
            t.SetPositionAndRotation(target - rot * (handCentreMetres * s), rot);
            t.localScale = Vector3.one * s;
        }

        /// <summary>The display frame (the SRDisplayManager's transform, inactive).</summary>
        public Transform Frame => srdFrame;
        /// <summary>SRDViewSpaceScale stand-in: the frame's scale.</summary>
        public float ViewSpaceScale => srdFrame != null ? srdFrame.lossyScale.x : 1f;
        public Vector3 LeftBottom => Corner(-0.5f, 0f);
        public Vector3 RightBottom => Corner(0.5f, 0f);
        public Vector3 LeftUp => Corner(-0.5f, 1f);
        public Vector3 RightUp => Corner(0.5f, 1f);

        // Like the SRD's DisplayEdges: x across the width, bottom edge at the frame's origin, the panel rising at the tilt.
        Vector3 Corner(float x, float up)
        {
            float t = panelTiltDegrees * Mathf.Deg2Rad, h = panelSizeMetres.y * up;
            var local = new Vector3(x * panelSizeMetres.x, h * Mathf.Cos(t), h * Mathf.Sin(t));
            return srdFrame != null ? srdFrame.TransformPoint(local) : local;
        }

        void Awake()
        {
            Instance = this;
            if (srdFrame == null)
                foreach (var m in FindObjectsByType<SRDManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                { srdFrame = m.transform; break; }
            if (disableSrd && srdFrame != null && srdFrame.gameObject.activeSelf) srdFrame.gameObject.SetActive(false);
            if (viewCamera == null) viewCamera = GetComponent<Camera>();
            if (viewCamera != null) viewCamera.targetDisplay = 0;
            PlaceCamera();

            // before the timelines' and overlays' Start (they read these positions there)
            if (overrideBrainStart) MoveBrainLayout();
        }

        // Every version starts the brain at brainStartPosition. With keepBrainPosition the brain also stays there for
        // the whole video: where a version moved it (V1's split: to splitLeftPosition; V3's recede: back by
        // recedeOffset) it now stays put, and what was laid out around it there (the split-off brains, the density
        // maps that fly out of it) moves by the same amount, so the layout around the brain is unchanged.
        void MoveBrainLayout()
        {
            var overlays = FindObjectsByType<TimelineDensityOverlay>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in FindObjectsByType<NeuronalLossSequence>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (keepBrainPosition)
                {
                    bool split = t.splitVolume != null && t.splitVolume.gameObject.activeInHierarchy;
                    bool recede = t.recede > 0f;
                    // where the old layout held the brain once it had moved
                    Vector3 held = (split ? t.splitLeftPosition : t.startPosition) + (recede ? t.recedeOffset : Vector3.zero);
                    Vector3 d = brainStartPosition - held;
                    if (split)
                    {
                        t.splitLeftPosition = brainStartPosition;
                        t.splitRightPosition += d;
                        t.splitThirdPosition += d;
                    }
                    if (recede) t.recedeOffset = Vector3.zero;   // still shrinks (recedeScale), no longer moves back
                    if (split || recede)
                        foreach (var o in overlays)
                            if (o.timeline == t) o.transform.position += d;
                }
                t.startPosition = brainStartPosition;
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        // Every frame: the window (and so the aspect) can change.
        void LateUpdate() { PlaceCamera(); }

        void PlaceCamera()
        {
            if (viewCamera == null) return;
            Vector3 lb = LeftBottom, rb = RightBottom, lu = LeftUp;
            Vector3 right = rb - lb, up = lu - lb;
            float w = right.magnitude, h = up.magnitude;
            if (w < 1e-6f || h < 1e-6f) return;
            Vector3 centre = lb + 0.5f * right + 0.5f * up;
            Vector3 away = Vector3.Cross(right / w, up / h);           // into the panel, away from the viewer
            float d = Mathf.Max(0.01f, viewerDistanceMetres * ViewSpaceScale);
            viewCamera.transform.SetPositionAndRotation(centre - away * d, Quaternion.LookRotation(away, up / h));

            float halfH = 0.5f * h;
            float aspect = Mathf.Max(0.1f, viewCamera.aspect);
            if (fitWholePanel && aspect < w / h) halfH = 0.5f * w / aspect;   // narrow screen: fit the width
            viewCamera.fieldOfView = 2f * Mathf.Atan(halfH / d) * Mathf.Rad2Deg;
            viewCamera.nearClipPlane = Mathf.Min(viewCamera.nearClipPlane, 0.01f);
        }
    }

    /// <summary>
    /// The display's frame and view-space scale: the SRDisplayManager when it runs, else the FlatDisplayRig standing
    /// in for it (flat-screen scenes), else nothing (world axes, scale 1).
    /// </summary>
    public static class DisplayFrame
    {
        public static Transform Get(SRDManager srd)
        {
            if (srd != null && srd.isActiveAndEnabled) return srd.transform;
            var rig = FlatDisplayRig.Instance;
            return rig != null ? rig.Frame : null;
        }

        public static Transform Get() => Get(SRDSceneEnvironment.GetSRDManager());

        public static float ViewSpaceScale(SRDManager srd)
        {
            if (srd != null && srd.isActiveAndEnabled) return srd.SRDViewSpaceScale;
            var rig = FlatDisplayRig.Instance;
            return rig != null ? rig.ViewSpaceScale : 1f;
        }
    }
}

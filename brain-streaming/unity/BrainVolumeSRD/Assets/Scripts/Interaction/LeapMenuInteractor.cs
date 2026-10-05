using Leap;
using SRD.Core;
using SRD.Utils;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// The menu bar (PresentationMenu) by hand: TOUCH a button with the index fingertip.
    ///
    /// The bar floats a little in front of the display panel (PresentationMenu.touchForwardMetres), and hands map
    /// 1:1 onto the display's space, so the fingertip meets the bar where the bar is seen. The fingertip is
    /// projected straight onto the bar's plane: within hoverMetres in front a ring shows that spot (closing in as
    /// the finger nears) and the button under it lights up; when the fingertip reaches the bar (touchMetres),
    /// coming from the front, that button is pressed and drawn pushed in. Pull back past releaseMetres to touch again.
    ///
    /// Menu zone: a hand whose fingertip or palm is at the bar (within zoneMetres in front, over the bar area) is
    /// the menu's: LeapBrainManipulator does not let it start a grab or a pinch, so reaching for the menu never moves
    /// or scales the brain. A hand already holding the brain doesn't touch the menu.
    /// </summary>
    [DefaultExecutionOrder(-20)]   // before LeapBrainManipulator reads the hands, after PresentationMenu (-40)
    public sealed class LeapMenuInteractor : MonoBehaviour
    {
        public PresentationMenu menu;
        public LeapBrainManipulator manipulator;
        [Tooltip("Unused since touch replaced the eye-ray pointer; kept so scenes stay valid.")]
        public Camera viewCamera;

        [Tooltip("How far outside a button the fingertip still counts (canvas units; a button is 150 x 64).")]
        public float margin = 12f;
        [Tooltip("The pointer only appears this close to the bar (canvas units outside it).")]
        public float showWithin = 70f;

        [Header("Touch (real metres, × SRDViewSpaceScale)")]
        [Tooltip("Fingertip this close to the bar (or through it) = touching.")]
        public float touchMetres = 0.006f;
        [Tooltip("Pull the fingertip back this far in front of the bar to end the touch (hysteresis).")]
        public float releaseMetres = 0.018f;
        [Tooltip("The pointer ring shows while the fingertip is within this distance in front of the bar.")]
        public float hoverMetres = 0.06f;
        [Tooltip("A fingertip pushed through the bar still counts up to this deep behind it.")]
        public float behindMetres = 0.04f;

        [Header("Menu zone (the brain ignores a hand here)")]
        [Tooltip("Fingertip or palm within this distance in front of the bar (over the bar area) = at the menu.")]
        public float zoneMetres = 0.10f;
        [Tooltip("How far around the buttons the zone reaches (canvas units).")]
        public float zoneMargin = 90f;
        [Tooltip("Smoothing time constant for the fingertip (seconds).")]
        public float smoothing = 0.02f;

        struct HandPointer
        {
            public bool tracked, touching, zone;
            public Vector3 tip;
            public float depth;        // fingertip distance in front of the bar (world units; < 0 = behind)
            public int hover, pressed;
            public bool onBar;
            public Vector3 point;      // fingertip projected onto the bar
        }

        HandPointer _l, _r;
        SRDManager _srd;

        /// <summary>Is this hand at the menu bar (so it must not grab / pinch the brain)?</summary>
        public bool InMenuZone(bool left) => left ? _l.zone : _r.zone;
        /// <summary>When a button was last pressed by hand (Time.unscaledTime); LeapBrainManipulator waits a moment after it.</summary>
        public float LastPressTime { get; private set; } = -999f;

        void Start()
        {
            if (menu == null) menu = FindFirstObjectByType<PresentationMenu>();
            if (manipulator == null) manipulator = FindFirstObjectByType<LeapBrainManipulator>();
            _srd = SRDSceneEnvironment.GetSRDManager();
            _l.hover = _l.pressed = _r.hover = _r.pressed = -1;
        }

        void Update()
        {
            if (menu == null || menu.Surface == null) return;
            var provider = manipulator != null ? manipulator.provider : FindFirstObjectByType<LeapProvider>();
            Frame frame = provider != null ? provider.CurrentFrame : null;
            float s = manipulator != null ? manipulator.HandScale : DisplayFrame.ViewSpaceScale(_srd);   // world units per real metre of hand
            Step(frame?.GetHand(Chirality.Left), ref _l, true, s);
            Step(frame?.GetHand(Chirality.Right), ref _r, false, s);

            // one pointer on the bar: a touching hand first, else the fingertip nearer the bar
            bool useL = _l.onBar && (!_r.onBar || _l.touching || (!_r.touching && _l.depth <= _r.depth));
            if (useL) Show(_l, s); else if (_r.onBar) Show(_r, s);
            else { menu.HandHover = -1; menu.SetHandCursor(false); }
            menu.HandPressed = _l.touching && _l.pressed >= 0 ? _l.pressed : _r.touching && _r.pressed >= 0 ? _r.pressed : -1;
        }

        void Show(HandPointer p, float s)
        {
            menu.HandHover = p.hover;
            float near = 1f - Mathf.Clamp01((p.depth - touchMetres * s) / Mathf.Max(1e-5f, (hoverMetres - touchMetres) * s));
            menu.SetHandCursor(true, p.point, p.touching, near);
        }

        void Step(Hand h, ref HandPointer p, bool left, float s)
        {
            if (h == null) { p = default; p.hover = p.pressed = -1; return; }
            Vector3 tip = h.fingers[1].TipPosition;   // index fingertip
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, smoothing));
            bool wasTracked = p.tracked;
            p.tip = wasTracked ? Vector3.Lerp(p.tip, tip, k) : tip;
            p.tracked = true;

            // the bar's plane: its forward points away from the viewer, so "in front" is along -forward
            Transform bar = menu.Surface;
            Vector3 n = -bar.forward;
            float prevDepth = p.depth;
            p.depth = Vector3.Dot(p.tip - bar.position, n);
            p.point = p.tip - n * p.depth;

            bool inRange = p.depth <= hoverMetres * s && p.depth >= -behindMetres * s;
            p.onBar = inRange && menu.ButtonAt(p.point, showWithin) >= 0;
            p.hover = inRange ? menu.ButtonAt(p.point, margin) : -1;
            p.zone = InZone(p.tip, bar, n, s) || InZone(h.PalmPosition, bar, n, s);

            // a hand holding / scaling the brain doesn't use the menu
            bool busy = manipulator != null && (manipulator.IsGrabbing(left) || manipulator.IsPinchScaling);
            if (busy && !p.touching) { p.hover = -1; p.onBar = false; }

            // touch: the fingertip reaches the bar from the front -> press the button under it (once per touch)
            bool was = p.touching;
            p.touching = p.depth <= (was ? releaseMetres : touchMetres) * s && p.depth >= -behindMetres * s;
            if (p.touching && !was)
            {
                bool fromFront = wasTracked && prevDepth > touchMetres * s;
                p.pressed = fromFront && !busy ? p.hover : -1;
                if (p.pressed >= 0) { menu.Press(p.pressed); LastPressTime = Time.unscaledTime; }
            }
            if (!p.touching) p.pressed = -1;
        }

        bool InZone(Vector3 point, Transform bar, Vector3 n, float s)
        {
            float d = Vector3.Dot(point - bar.position, n);
            if (d > zoneMetres * s || d < -behindMetres * s) return false;
            return menu.ButtonAt(point - n * d, zoneMargin) >= 0;
        }

        void OnDisable()
        {
            if (menu != null) { menu.HandHover = -1; menu.HandPressed = -1; menu.SetHandCursor(false); }
        }
    }
}

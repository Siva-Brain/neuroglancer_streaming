using Leap;
using SRD.Core;
using SRD.Utils;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Move and rotate the brain by GRABBING it, scale it by PINCHING with both hands (Ultraleap, desktop mode: the
    /// device lies on the desk in front of the Spatial Reality Display, facing up). Close the hand as if holding it:
    ///
    ///   one hand GRAB, move / turn the hand        the brain moves and turns with the hand, like holding it
    ///   both hands GRAB, move / twist the pair      moves it, twisting turns it about the vertical (no scaling)
    ///   open the hand                               let go (grab again to go further)
    ///   both hands PINCH, drag apart / together     scale, about the brain's centre (the only way to scale)
    ///   Slice on (menu): the glass slide is attached to ONE hand (the one that pressed Slice, else the hand there,
    ///   left or right); moving that hand moves the slide, which cuts the brain (LeapSliceSlide). That hand
    ///   doesn't grab or pinch; the other hand grabs to turn the brain meanwhile (two-hand gestures are off).
    ///   Lens on (menu): the same, with a magnifying glass (LeapLens) in that hand instead: it shows the brain
    ///   behind it zoomed in; a pinch with that hand pins the lens in place, the next pinch takes it back.
    ///   Slice and Lens are one-hand tools: turning one on turns the other off.
    ///
    /// The menu bar is touched with a fingertip (LeapMenuInteractor). A hand at the menu (its zone) never starts a
    /// grab or a pinch and stays the menu's (latched) until it has been fully open and away for menuReleaseSeconds;
    /// for pressLockSeconds after a button press no hand starts one; a pointing hand never takes hold; a hand must be
    /// seen open before it can grab. So reaching for, touching and pulling back from the menu doesn't move or scale
    /// the brain. A fist is not a pinch (pinchMaxGrab). With requireNearBrain a grab only takes the brain
    /// when the hand is at it (within grabReachMetres of its box), else anywhere in front of the display works.
    /// Grabbing while a timeline plays pauses it (the timeline would otherwise pull the brain back); its play
    /// button continues from where it was. A small disc marks the grab point (teal = holding, yellow = scaling,
    /// magenta = moving the glass slide).
    ///
    /// The SRD world is the real world scaled by SRDViewSpaceScale (the SRDisplayManager's frame: Y = up, origin =
    /// the panel's bottom edge centre, +Z = away from the viewer), so the device is placed at its real position
    /// (deviceMetres) relative to the SRDisplayManager and hands map 1:1 onto the display's space.
    /// </summary>
    public sealed class LeapBrainManipulator : MonoBehaviour
    {
        [Tooltip("The brain (BrickVolumeLoader / FusedVolumeLoader). Empty = the active BrickVolumeLoader.")]
        public MonoBehaviour volume;
        [Tooltip("Empty = a LeapServiceProvider in the scene, else one is created and placed at deviceMetres.")]
        public LeapProvider provider;

        [Header("Device placement (only for a provider created here)")]
        [Tooltip("Where the Ultraleap device is, in metres from the display's bottom edge centre: x right, y up, " +
                 "z away from you (negative = in front of the display).")]
        public Vector3 deviceMetres = new Vector3(0f, -0.03f, -0.20f);
        [Tooltip("Device rotation (degrees) relative to the display frame. 0,0,0 = flat on the desk, facing up.")]
        public Vector3 deviceEuler = Vector3.zero;

        [Header("Holding")]
        [Tooltip("ON = moving the held hand(s) moves the brain. OFF = the brain stays where it is (only the timeline " +
                 "moves it); grabbing still turns it and pinching still scales it.")]
        public bool allowMove = false;
        [Tooltip("Brain movement per hand movement (1 = follows the hand exactly).")]
        public float moveGain = 1.5f;
        [Tooltip("ON = one-hand grab turns the brain about the vertical (Y) only, like a turntable: the hand moves left, " +
                 "the brain's front turns left (as right drag with the mouse). OFF = it turns with the palm, any axis.")]
        public bool yawOnly = true;
        [Tooltip("Turntable turn (yawOnly) in degrees per metre of real hand travel left / right.")]
        public float yawDegreesPerMetre = 600f;
        [Tooltip("Brain rotation per hand rotation (1 = turns exactly with the hand; > 1 = less wrist turning).")]
        public float rotateGain = 1.6f;
        [Tooltip("Two hands: also turn the brain when the pair is twisted (about the vertical).")]
        public bool twoHandTwist = true;
        public float minScale = 0.3f, maxScale = 4f;

        [Header("Grab")]
        [Tooltip("Grab strength (0 = open hand, 1 = fist) to take hold / to let go (hysteresis).")]
        public float grabOn = 0.7f, grabOff = 0.45f;
        [Tooltip("ON = a grab only takes the brain when the palm is at the brain (within grabReachMetres of its box).")]
        public bool requireNearBrain = false;
        public float grabReachMetres = 0.05f;
        [Tooltip("Smoothing time constant for the hand data (seconds).")]
        public float smoothing = 0.05f;

        [Header("Two-hand pinch scale")]
        [Tooltip("Both hands pinching (thumb + index): drag them apart / together to scale the brain about its centre.")]
        public bool pinchScale = true;
        [Tooltip("Pinch strength to start / to end a pinch (hysteresis).")]
        public float pinchOn = 0.85f, pinchOff = 0.6f;
        [Tooltip("A pinch only starts while the hand is this open (grab strength below it): a fist is not a pinch.")]
        public float pinchMaxGrab = 0.5f;
        [Tooltip("Scale change per change of the hands' distance (1 = doubles when the hands are twice as far apart).")]
        public float pinchScaleGain = 1f;

        [Header("Menu safety (no grab / pinch from a hand using the menu)")]
        [Tooltip("After being at the menu bar, a hand must be fully open and away from it this long before it may grab or pinch.")]
        public float menuReleaseSeconds = 0.4f;
        [Tooltip("No hand may start a grab or pinch for this long after a menu button was pressed.")]
        public float pressLockSeconds = 0.6f;
        [Tooltip("Grab strength below this (and no pinch) = the hand is fully open.")]
        public float openBelow = 0.3f;
        [Tooltip("A closing hand whose index finger still reads extended gets this long to become a fist, else it must open again.")]
        public float fistWaitSeconds = 0.25f;

        [Header("Glass slide (LeapSliceSlide)")]
        [Tooltip("Empty = the one on this GameObject (added in Awake if missing). It shows after the menu's Slice button.")]
        public LeapSliceSlide slide;
        [Tooltip("The slide's hand lost by tracking keeps the slide this long; then the other hand (if free) takes it.")]
        public float slideReassignSeconds = 1.5f;
        [Tooltip("A playing timeline is paused once the slide's hand has moved this far (real metres) since it took the slide.")]
        public float slidePauseMetres = 0.02f;

        [Header("Magnifying lens (LeapLens)")]
        [Tooltip("Empty = the one on this GameObject (added in Awake if missing). It shows after the menu's Lens button.")]
        public LeapLens lens;

        [Header("Behaviour")]
        public bool pauseTimelineOnGrab = true;
        public bool showCursors = true;
        [Tooltip("Grab-point disc diameter in metres (scaled with SRDViewSpaceScale).")]
        public float cursorMetres = 0.012f;

        /// <summary>Grab = one hand holds, TwoHand = both hands hold (move / twist), Scale = both hands pinch,
        /// Slide = only the glass slide's hand is busy (moving the slide, the cut), Lens = only the lens' hand is busy.</summary>
        public enum Mode { None, Grab, TwoHand, Scale, Slide, Lens }
        public Mode Current { get; private set; }

        /// <summary>Is this hand holding the brain right now?</summary>
        public bool IsGrabbing(bool left) => left ? _l.holding : _r.holding;
        /// <summary>Are both hands pinch-scaling the brain right now?</summary>
        public bool IsPinchScaling => Current == Mode.Scale;
        /// <summary>Is this hand moving the glass slide right now?</summary>
        public bool IsSliding(bool left) => left ? _l.sliding : _r.sliding;

        /// <summary>What this hand is doing right now: Grab (holding, one or both hands), Scale (pinch-scaling) or
        /// None (just tracked or not there). LeapHandRenderer tints by it.</summary>
        public Mode HandMode(bool left)
        {
            HandState h = left ? _l : _r;
            if (!h.tracked) return Mode.None;
            if (h.sliding) return lens != null && lens.Active ? Mode.Lens : Mode.Slide;
            if (Current == Mode.Scale) return h.pinching ? Mode.Scale : Mode.None;
            return h.holding && Current != Mode.None ? Mode.Grab : Mode.None;
        }

        struct HandState
        {
            public bool tracked;
            public bool closed;          // the hand is closed (grab strength, with hysteresis)
            public bool holding;         // ... and it took hold of the brain
            public float pendingUntil;   // just closed but the index still reads extended: may take hold until then
            public bool latched;         // was at the menu: no grab / pinch until open and away for menuReleaseSeconds
            public float openSince;      // when the hand last became fully open (-1 = not open)
            public bool seenOpen;        // seen open since it was found (a hand that appears closed doesn't grab)
            public bool pinching;        // thumb + index together, hand otherwise open (with hysteresis)
            public bool pinchHeld;       // the pinch as read (pinching may be cleared for a tool hand; this keeps the hysteresis)
            public bool sliding;         // the glass slide / lens is attached to this hand (it never grabs / pinches then)
            public Vector3 point;        // smoothed palm position (world)
            public Vector3 pinchPoint;   // smoothed point between thumb and index tips (world)
            public Quaternion rotation;  // smoothed palm rotation (world)
            public Vector3 direction;    // smoothed palm-to-fingers direction (world)
        }

        LeapMenuInteractor _menu;
        ISliceableVolume _vol;
        Transform _target;
        SRDManager _srd;
        HandState _l, _r;
        Mode _last;
        Vector3 _prevPoint, _prevMid;
        Quaternion _prevRot;
        float _prevDist, _prevYaw;
        float _scaleRef = 1f;
        int _slideHand = -1;             // the glass slide's / lens' hand: 0 = left, 1 = right, -1 = none
        float _slideLostAt = -1f;        // when the slide's hand was lost by tracking (-1 = tracked)
        int _toolWas;                    // the one-hand tool last frame: 0 = none, 1 = slide, 2 = lens
        bool _slidePaused, _lensPinchWas;
        Vector3 _slidePrev, _slideStart;
        Material _mat;
        Mesh _disc;
        NeuronalLossSequence[] _timelines;

        void Awake()
        {
            // before every Start, so PresentationMenu finds it for its Slice button
            if (slide == null) slide = GetComponent<LeapSliceSlide>();
            if (slide == null) slide = gameObject.AddComponent<LeapSliceSlide>();
            if (lens == null) lens = GetComponent<LeapLens>();
            if (lens == null) lens = gameObject.AddComponent<LeapLens>();
        }

        void Start()
        {
            if (volume == null) volume = FindFirstObjectByType<BrainVolume.SRD.BrickVolumeLoader>();
            _vol = volume as ISliceableVolume;
            if (_vol == null) { Debug.LogError("[Leap] No brain volume to move."); enabled = false; return; }
            _target = _vol.transform;
            _scaleRef = _target.localScale.x;
            _srd = SRDSceneEnvironment.GetSRDManager();
            if (provider == null) provider = FindFirstObjectByType<LeapServiceProvider>();
            if (provider == null)
            {
                var go = new GameObject("LeapProvider (desktop)");
                go.SetActive(false);                       // place it before it starts transforming frames
                provider = go.AddComponent<LeapServiceProvider>();
                PlaceDevice(go.transform);
                go.SetActive(true);
                Debug.Log($"[Leap] Created a desktop LeapServiceProvider at {go.transform.position} (scale {go.transform.lossyScale.x}).");
            }
            _timelines = FindObjectsByType<NeuronalLossSequence>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _menu = FindFirstObjectByType<LeapMenuInteractor>();

            var sh = Shader.Find("Brain/OverlayUnlit");
            if (sh != null) _mat = new Material(sh);
            _disc = BuildDisc(32);
            _vol.Drawn += Draw;
            if (slide != null) slide.Init(_vol, this);
            if (lens != null) lens.Init(_vol, this);
        }

        /// <summary>World units per real metre of hand movement: the provider's scale (SRDViewSpaceScale on the SRD,
        /// FlatDisplayRig.HandScale on a flat screen). The real-metre settings (reach, discs, touch) use it.</summary>
        public float HandScale => provider != null ? provider.transform.lossyScale.x : 1f;

        void PlaceDevice(Transform t)
        {
            // flat screen (SRD not running): the rig maps a comfortable reach over the device onto its virtual panel
            if ((_srd == null || !_srd.isActiveAndEnabled) && FlatDisplayRig.Instance != null)
            {
                FlatDisplayRig.Instance.PlaceHandDevice(t);
                return;
            }
            Transform f = _srd != null && _srd.isActiveAndEnabled ? _srd.transform : null;
            float s = _srd != null ? _srd.SRDViewSpaceScale : 1f;
            Vector3 origin = f != null ? f.position : Vector3.zero;
            Quaternion rot = f != null ? f.rotation : Quaternion.identity;
            t.SetPositionAndRotation(origin + rot * (deviceMetres * s), rot * Quaternion.Euler(deviceEuler));
            t.localScale = Vector3.one * s;
        }

        void Update()
        {
            Frame frame = provider != null ? provider.CurrentFrame : null;
            // a hand at the menu bar belongs to the menu (LeapMenuInteractor runs first, so this is this frame's)
            Read(frame?.GetHand(Chirality.Left), ref _l, _menu != null && _menu.isActiveAndEnabled && _menu.InMenuZone(true));
            Read(frame?.GetHand(Chirality.Right), ref _r, _menu != null && _menu.isActiveAndEnabled && _menu.InMenuZone(false));

            // glass slide / lens: attached to one hand while on, which then only moves it (the other hand grabs)
            int tool = slide != null && slide.Active ? 1 : lens != null && lens.Active ? 2 : 0;
            UpdateSlideHand(tool);
            // the lens hand's pinch pins the lens where it is / takes it back (on the pinch's start only)
            bool lensPinch = tool == 2 && (_l.sliding && _l.pinching || _r.sliding && _r.pinching);
            if (lensPinch && !_lensPinchWas) lens.TogglePinned();
            _lensPinchWas = lensPinch;
            if (_l.sliding) { _l.holding = false; _l.pinching = false; }
            if (_r.sliding) { _r.holding = false; _r.pinching = false; }
            DriveSlide();
            DriveLens();

            bool pinchPair = pinchScale && _l.pinching && _r.pinching;
            Mode mode = _l.holding && _r.holding ? Mode.TwoHand
                      : _l.holding || _r.holding ? Mode.Grab
                      : pinchPair ? Mode.Scale
                      : _l.sliding || _r.sliding ? (tool == 2 ? Mode.Lens : Mode.Slide)
                      : Mode.None;
            bool fresh = mode != _last;   // a new gesture (or one hand let go): its first frame is the reference

            switch (mode)
            {
                case Mode.Grab:
                {
                    // held in one hand: turns about the vertical as the hand moves left / right (yawOnly), or
                    // follows the palm's turning; moves with the hand only with allowMove
                    HandState h = _l.holding ? _l : _r;
                    if (!fresh)
                    {
                        if (allowMove) _target.position += (h.point - _prevPoint) * moveGain;
                        if (yawOnly)
                        {
                            // the palm's own turn would go the wrong way (sweeping the hand left swings the palm
                            // the other way round), so the left / right travel turns it instead
                            Transform f = DisplayFrame.Get(_srd);
                            Vector3 right = f != null ? f.right : Vector3.right;
                            float dx = Vector3.Dot(h.point - _prevPoint, right) / Mathf.Max(1e-4f, HandScale);   // real metres
                            if (Mathf.Abs(dx) > 1e-6f)
                                _target.rotation = Quaternion.AngleAxis(-dx * yawDegreesPerMetre, Vector3.up) * _target.rotation;   // + about Y: front goes left
                        }
                        else
                        {
                            Quaternion delta = h.rotation * Quaternion.Inverse(_prevRot);
                            delta.ToAngleAxis(out float angle, out Vector3 axis);
                            if (angle > 180f) angle -= 360f;
                            if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x) && Mathf.Abs(angle) > 0.01f)
                                _target.rotation = Quaternion.AngleAxis(angle * rotateGain, axis) * _target.rotation;
                        }
                    }
                    _prevPoint = h.point;
                    _prevRot = h.rotation;
                    break;
                }
                case Mode.TwoHand:
                {
                    // held in both hands: the pair's midpoint moves it, twisting the pair turns it (no scaling)
                    Vector3 mid = 0.5f * (_l.point + _r.point);
                    Vector3 lr = _r.point - _l.point;
                    float yaw = Mathf.Atan2(lr.x, lr.z) * Mathf.Rad2Deg;
                    if (!fresh)
                    {
                        if (allowMove) _target.position += (mid - _prevMid) * moveGain;
                        if (twoHandTwist)
                            _target.rotation = Quaternion.AngleAxis(Mathf.DeltaAngle(_prevYaw, yaw), Vector3.up) * _target.rotation;
                    }
                    _prevMid = mid; _prevYaw = yaw;
                    break;
                }
                case Mode.Scale:
                {
                    // both hands pinching: their distance scales the brain about its centre (no move / turn)
                    float dist = Vector3.Distance(_l.pinchPoint, _r.pinchPoint);
                    if (!fresh && _prevDist > 1e-4f && dist > 1e-4f)
                        ScaleAboutCentre(Mathf.Pow(dist / _prevDist, pinchScaleGain));
                    _prevDist = dist;
                    break;
                }
            }
            // a playing timeline would pull the brain back
            bool gesture = mode != Mode.None && mode != Mode.Slide && mode != Mode.Lens;
            if (fresh && gesture && (_last == Mode.None || _last == Mode.Slide || _last == Mode.Lens) && pauseTimelineOnGrab) PauseTimelines();
            _last = Current = mode;
        }

        // The glass slide (or the lens) belongs to one hand while it is on: the hand that just pressed Slice / Lens,
        // else the hand that is there (the one nearer the brain if both are and neither holds it). A hand lost by
        // tracking keeps it for slideReassignSeconds, then a tracked hand that isn't holding the brain takes it.
        // tool: 0 = none, 1 = slide, 2 = lens.
        void UpdateSlideHand(int tool)
        {
            float now = Time.unscaledTime;
            if (tool == 0) _slideHand = -1;
            else
            {
                if (tool != _toolWas && _menu != null && now - _menu.LastPressTime < 0.5f)
                    _slideHand = _menu.LastPressLeft ? 0 : 1;
                bool kept = _slideHand == 0 ? _l.tracked : _slideHand == 1 && _r.tracked;
                if (kept) _slideLostAt = -1f;
                else if (_slideLostAt < 0f) _slideLostAt = now;
                if (!kept && (_slideHand < 0 || now - _slideLostAt >= slideReassignSeconds))
                {
                    bool l = _l.tracked && !_l.holding, r = _r.tracked && !_r.holding;
                    if (l && r) _slideHand = BrainDistance(_l.point) <= BrainDistance(_r.point) ? 0 : 1;
                    else if (l || r) _slideHand = l ? 0 : 1;
                }
            }
            _toolWas = tool;
            _l.sliding = _slideHand == 0 && _l.tracked;
            _r.sliding = _slideHand == 1 && _r.tracked;
        }

        // The slide's hand moves the slide along its rail (the cut) by the palm's movement, starting from the cut as
        // it is (the slide doesn't jump to the hand). It rests while that hand is at the menu or just pressed a
        // button, and while it is not tracked; it carries on from there when the hand is free again.
        void DriveSlide()
        {
            if (slide == null) return;
            bool left = _slideHand == 0;
            HandState h = left ? _l : _r;
            bool atMenu = _menu != null && _menu.isActiveAndEnabled
                       && (_menu.InMenuZone(left) || Time.unscaledTime - _menu.LastPressTime < pressLockSeconds);
            if (!slide.Active || !h.sliding || atMenu)
            {
                if (slide.Held) slide.EndDrag();
                return;
            }
            if (!slide.Held)
            {
                slide.BeginDrag();
                _slideStart = h.point;
                _slidePaused = false;
            }
            else
            {
                slide.Drag(h.point - _slidePrev);
                // a playing timeline would pull the cut back: pause it once the hand really moves (not on jitter)
                if (!_slidePaused && Vector3.Distance(h.point, _slideStart) >= slidePauseMetres * HandScale)
                {
                    _slidePaused = true;
                    if (pauseTimelineOnGrab) PauseTimelines();
                }
            }
            _slidePrev = h.point;
        }

        // The lens follows its hand (absolutely: it sits just past the fingertips). It hides while that hand is at
        // the menu, so it never covers the buttons, and while the hand is not tracked.
        void DriveLens()
        {
            if (lens == null || !lens.Active) return;
            bool left = _slideHand == 0;
            HandState h = left ? _l : _r;
            bool atMenu = _menu != null && _menu.isActiveAndEnabled && _menu.InMenuZone(left);
            lens.Follow(h.sliding && !atMenu, h.point, h.direction);
        }

        // distance from a point to the brain's box centre
        float BrainDistance(Vector3 p) => Vector3.Distance(p, _vol.UnitCubeToWorld.MultiplyPoint(new Vector3(0.5f, 0.5f, 0.5f)));

        // Multiply the brain's scale by f (clamped) keeping its box centre where it is.
        void ScaleAboutCentre(float f)
        {
            float cur = _target.localScale.x;
            float s = Mathf.Clamp(cur * f, minScale * _scaleRef, maxScale * _scaleRef);
            if (cur <= 0f || Mathf.Approximately(s, cur)) return;
            Vector3 c = _vol.UnitCubeToWorld.MultiplyPoint(new Vector3(0.5f, 0.5f, 0.5f));
            _target.position = c + (_target.position - c) * (s / cur);
            _target.localScale = Vector3.one * s;
        }

        // grab / pinch state with hysteresis + smoothed palm position / rotation and pinch point.
        // atMenu: the hand is at the menu bar, so it may not start a grab or a pinch.
        void Read(Hand h, ref HandState s, bool atMenu)
        {
            if (h == null)
            {
                // tracking often drops near the display: a hand lost at the menu is still the menu's when it returns
                bool latched = s.latched;
                s = default;
                s.latched = latched; s.openSince = -1f; s.pendingUntil = -1f;
                return;
            }
            float now = Time.unscaledTime;

            // Menu latch: a hand that was at the menu stays the menu's until it has been fully open and away from
            // the menu for menuReleaseSeconds (pulling back from a button, the fingers curl and the hand may pass
            // the brain: none of that may grab or pinch). A hand that just appeared must first be seen open.
            bool open = h.GrabStrength < openBelow && h.PinchStrength < pinchOff;
            if (atMenu) { s.latched = true; s.openSince = -1f; }
            else if (open) { if (s.openSince < 0f) s.openSince = now; }
            else s.openSince = -1f;
            if (s.latched && !atMenu && s.openSince >= 0f && now - s.openSince >= menuReleaseSeconds) s.latched = false;
            if (open) s.seenOpen = true;
            bool locked = s.latched || !s.seenOpen || (_menu != null && now - _menu.LastPressTime < pressLockSeconds);

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, smoothing));
            Vector3 p = h.PalmPosition, pp = h.GetPinchPosition();
            s.point = s.tracked ? Vector3.Lerp(s.point, p, k) : p;
            s.pinchPoint = s.tracked ? Vector3.Lerp(s.pinchPoint, pp, k) : pp;
            s.rotation = s.tracked ? Quaternion.Slerp(s.rotation, h.Rotation, k) : h.Rotation;
            s.direction = s.tracked ? Vector3.Slerp(s.direction, h.Direction, k) : h.Direction;
            s.tracked = true;

            bool wasClosed = s.closed;
            s.closed = h.GrabStrength >= (wasClosed ? grabOff : grabOn);
            if (!s.closed) { s.holding = false; s.pendingUntil = -1f; }
            else if (!s.holding)
            {
                // take hold when an open hand closes (at the brain, if requireNearBrain); a hand closed elsewhere
                // stays empty. The index finger may still read as extended for a moment while a fist closes (a
                // pointing hand, three fingers curled, also reads as a grab), so a close waits briefly for a real
                // fist; after that the hand must open again. A locked hand (menu, just pressed, not yet seen
                // open) never takes hold with this close.
                if (!wasClosed) s.pendingUntil = locked ? -1f : now + fistWaitSeconds;
                if (locked) s.pendingUntil = -1f;
                if (s.pendingUntil >= 0f && now <= s.pendingUntil && !h.fingers[(int)Finger.FingerType.INDEX].IsExtended)
                {
                    s.holding = !requireNearBrain || NearBrain(s.point);
                    s.pendingUntil = -1f;
                }
            }

            // pinch: starts only with the other fingers open (a closing fist also brings thumb and index together),
            // never from a locked hand; a hand that reaches the menu stops pinching
            bool wasPinching = s.pinchHeld;
            s.pinching = !atMenu && !s.closed && h.PinchStrength >= (wasPinching ? pinchOff : pinchOn)
                      && (wasPinching || (!locked && h.GrabStrength < pinchMaxGrab));
            s.pinchHeld = s.pinching;
        }

        bool NearBrain(Vector3 p)
        {
            Matrix4x4 m = _vol.UnitCubeToWorld;
            Vector3 local = m.inverse.MultiplyPoint(p);           // 0..1 inside the volume's box
            Vector3 c = new Vector3(Mathf.Clamp01(local.x), Mathf.Clamp01(local.y), Mathf.Clamp01(local.z));
            float reach = grabReachMetres * HandScale;
            return Vector3.Distance(m.MultiplyPoint(c), p) <= reach;
        }

        void PauseTimelines()
        {
            if (_timelines == null) return;
            foreach (var t in _timelines)
                if (t != null && t.isActiveAndEnabled && !t.Standby && t.IsPlaying) t.Pause();
        }

        // ------------------------------------------------------------------ grab-point disc (drawn on top of the volume)

        void Draw(Camera cam)
        {
            if (!showCursors || _mat == null || cam == null || LeapLens.IsLensCamera(cam)) return;
            float s = HandScale * cursorMetres;
            DrawCursor(cam, _l, s);
            DrawCursor(cam, _r, s);
        }

        void DrawCursor(Camera cam, HandState h, float size)
        {
            bool pinch = Current == Mode.Scale && h.pinching;
            bool sliding = h.sliding && slide != null && slide.Held;
            if (!h.tracked || !(h.holding || pinch || sliding)) return;
            Vector3 at = pinch ? h.pinchPoint : h.point;
            Color c = sliding ? new Color(0.95f, 0.35f, 0.85f, 0.95f)
                    : pinch ? new Color(1f, 0.85f, 0.25f, 0.95f) : new Color(0.18f, 0.83f, 0.75f, 0.95f);
            Quaternion face = Quaternion.LookRotation(at - cam.transform.position, cam.transform.up);
            _mat.SetColor("_Color", c);
            _mat.SetPass(0);
            Graphics.DrawMeshNow(_disc, Matrix4x4.TRS(at, face, Vector3.one * size * 1.4f));
        }

        static Mesh BuildDisc(int seg)
        {
            var v = new Vector3[seg + 1];
            var t = new int[seg * 3];
            for (int i = 0; i < seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.5f;
                t[i * 3] = 0; t[i * 3 + 1] = i + 1; t[i * 3 + 2] = (i + 1) % seg + 1;
            }
            var m = new Mesh { name = "LeapCursor" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }

        void OnDestroy()
        {
            if (_vol != null) _vol.Drawn -= Draw;
            if (_mat != null) Destroy(_mat);
            if (_disc != null) Destroy(_disc);
        }
    }
}

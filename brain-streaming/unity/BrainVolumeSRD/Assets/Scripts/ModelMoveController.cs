// Model movement / rotation for the Spatial Reality Display scene.
//
// Translation (display frame)
//   W / S        deeper into / out of the display (Z)
//   A / D        left / right (X)
//   Q / E        down / up (Y) — optional, see enableVertical
//   Left drag    slide the model in the display's X/Y plane (drag right -> moves right, drag up -> moves up)
//
// Rotation (turntable about the model's bounds centre; the surface nearest the viewer follows the input)
//   Left / Right arrow   yaw   (Right arrow -> near face moves right)
//   Up / Down arrow      pitch (Up arrow    -> near face moves up)
//   Right drag           yaw + pitch the same way
//
// Zoom (uniform scale about the bounds centre, clamped to [minZoom, maxZoom] x the start scale)
//   + / =  and numpad +   zoom in
//   - and numpad -        zoom out
//   Mouse scroll          zoom in (up) / out (down)
//
// The "display frame" is the SRDisplayManager transform: X = right, Y = up, Z = away from the
// viewer, in the units of the blue view-space box. If no active SRDManager is found (e.g. Play
// mode without a device and the manager disabled itself), world axes are used instead.
//
// NOTE: with "Run without Spatial Reality Display" on (no device), the plugin's mouse head
// simulator ALSO uses the right button and the scroll wheel, so a right drag rotates the model
// AND moves the simulated viewer, and the wheel zooms the model AND the simulated viewer distance. On the real display the head comes from the face camera and there is no
// overlap. Set rightDragRotates = false if that gets in the way while developing.
//
// Uses the new Input System (the project runs with input handler = Both, so the SRD plugin's
// legacy Input calls keep working alongside this).

using UnityEngine;
using UnityEngine.InputSystem;
using SRD.Core;
using SRD.Utils;

[DisallowMultipleComponent]
public class ModelMoveController : MonoBehaviour
{
    [Header("Model Initial Position (R)")]
    public Vector3 initialPosition;
    public Quaternion initialRotation;
    public Vector3 initScale;
    [Header("Keyboard translation (WASD / QE)")]
    [Tooltip("Metres per second at SRDViewSpaceScale 1. Multiplied by the view-space scale when the toggle below is on.")]
    public float moveSpeed = 0.25f;
    [Tooltip("Scale the speed with SRDManager.SRDViewSpaceScale so the model crosses the box at the same rate at any scale.")]
    public bool scaleSpeedWithViewSpace = true;
    [Tooltip("Also allow Q (down) / E (up).")]
    public bool enableVertical = true;

    [Header("Left mouse drag = translate in the display plane")]
    public bool leftDragTranslates = true;
    [Tooltip("Metres moved per pixel of mouse travel at SRDViewSpaceScale 1 (also scaled by the toggle above).")]
    public float metresPerPixel = 0.0015f;

    [Header("Arrow keys = rotate")]
    public bool arrowKeysRotate = true;
    [Tooltip("Degrees per second while an arrow key is held.")]
    public float arrowDegreesPerSecond = 60f;

    [Header("Right mouse drag = rotate")]
    public bool rightDragRotates = true;
    [Tooltip("Degrees of rotation per pixel of mouse movement.")]
    public float degreesPerPixel = 0.25f;

    [Header("Zoom (+ / - keys and mouse scroll)")]
    public bool zoomKeysEnabled = true;
    [Tooltip("Scale multiplier per second while + or - is held (2 = doubles every second).")]
    public float zoomFactorPerSecond = 1.6f;
    public bool scrollZoomEnabled = true;
    [Tooltip("Scale multiplier per scroll-wheel notch (1.1 = 10 % per notch).")]
    public float zoomFactorPerNotch = 1.1f;
    [Tooltip("Smallest allowed scale, as a multiple of the scale the model started with.")]
    public float minZoom = 0.25f;
    [Tooltip("Largest allowed scale, as a multiple of the scale the model started with.")]
    public float maxZoom = 4f;
    [Tooltip("Scale about the renderer bounds centre so the model grows in place instead of around its pivot.")]
    public bool zoomAroundBoundsCenter = true;

    [Header("Rotation options")]
    [Tooltip("Flip the vertical (pitch) direction for both arrows and drag.")]
    public bool invertPitch = false;
    [Tooltip("Rotate about the centre of the renderer bounds instead of the transform pivot (the brain FBX pivot is off-centre).")]
    public bool rotateAroundBoundsCenter = true;

    private SRDManager _srdManager;
    private Renderer[] _renderers;
    private Vector3 _startScale;

    /// <summary>Current zoom as a multiple of the start scale (1 = as authored in the scene).</summary>
    public float Zoom
    {
        get { return _startScale.x > 0f ? transform.localScale.x / _startScale.x : 1f; }
    }

    void Start()
    {
        _srdManager = SRDSceneEnvironment.GetSRDManager();
        _renderers = GetComponentsInChildren<Renderer>(true);
        _startScale = transform.localScale;

        //Inital Tramsform Values
        initialPosition = transform.position;
        initialRotation = transform.rotation;
        initScale = transform.localScale;
    }

    void Update()
    {
        Transform frame = (_srdManager != null && _srdManager.isActiveAndEnabled) ? _srdManager.transform : null;
        Vector3 right = frame != null ? frame.right : Vector3.right;
        Vector3 up = frame != null ? frame.up : Vector3.up;
        Vector3 forward = frame != null ? frame.forward : Vector3.forward;

        HandleKeyboardMove(right, up, forward);
        HandleArrowRotate(right, up);
        HandleMouse(right, up);
        HandleZoomKeys();

        ResetModelTransform();
    }

    void ResetModelTransform()
    {
        if (Input.GetKey(KeyCode.R))
        {
            transform.position = initialPosition;
            transform.rotation = initialRotation;
            transform.localScale = initScale;
        }
    }

    private float SpeedScale()
    {
        return (scaleSpeedWithViewSpace && _srdManager != null) ? _srdManager.SRDViewSpaceScale : 1f;
    }

    // ---------------------------------------------------------------- translation

    private void HandleKeyboardMove(Vector3 right, Vector3 up, Vector3 forward)
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        Vector3 dir = Vector3.zero;
        if (kb.wKey.isPressed) dir += forward;
        if (kb.sKey.isPressed) dir -= forward;
        if (kb.dKey.isPressed) dir += right;
        if (kb.aKey.isPressed) dir -= right;
        if (enableVertical)
        {
            if (kb.eKey.isPressed) dir += up;
            if (kb.qKey.isPressed) dir -= up;
        }
        if (dir.sqrMagnitude < 1e-6f) return;

        transform.position += dir.normalized * moveSpeed * SpeedScale() * Time.deltaTime;
    }

    // ---------------------------------------------------------------- rotation

    private void HandleArrowRotate(Vector3 right, Vector3 up)
    {
        if (!arrowKeysRotate) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        float h = 0f, v = 0f;
        if (kb.rightArrowKey.isPressed) h += 1f;
        if (kb.leftArrowKey.isPressed) h -= 1f;
        if (kb.upArrowKey.isPressed) v += 1f;
        if (kb.downArrowKey.isPressed) v -= 1f;
        if (h == 0f && v == 0f) return;

        float step = arrowDegreesPerSecond * Time.deltaTime;
        Rotate(right, up, h * step, v * step);
    }

    private void HandleMouse(Vector3 right, Vector3 up)
    {
        var mouse = Mouse.current;
        if (mouse == null) return;
        if (BrainVolume.TimelineTransportUI.PointerCaptured) return;   // the pointer is on the timeline seek bar

        if (scrollZoomEnabled)
        {
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 10f) scroll /= 120f;   // raw Windows wheel units -> notches
            if (scroll != 0f) ApplyZoom(Mathf.Pow(zoomFactorPerNotch, scroll));
        }

        Vector2 delta = mouse.delta.ReadValue();
        if (delta.sqrMagnitude < 1e-6f) return;

        if (rightDragRotates && mouse.rightButton.isPressed)
        {
            Rotate(right, up, delta.x * degreesPerPixel, delta.y * degreesPerPixel);
        }
        else if (leftDragTranslates && mouse.leftButton.isPressed)
        {
            float k = metresPerPixel * SpeedScale();
            transform.position += right * (delta.x * k) + up * (delta.y * k);
        }
    }

    /// <summary>
    /// Turntable rotation. horizontal > 0 makes the surface nearest the viewer move to the RIGHT,
    /// vertical > 0 makes it move UP (unless invertPitch). Both are in degrees.
    /// </summary>
    private void Rotate(Vector3 right, Vector3 up, float horizontal, float vertical)
    {
        float yaw = -horizontal;                                  // -Y rotation moves the near (-Z) face toward +X
        float pitch = (invertPitch ? -1f : 1f) * vertical;         // +X rotation moves the near (-Z) face toward +Y

        Vector3 pivot = rotateAroundBoundsCenter ? BoundsCenter() : transform.position;
        if (yaw != 0f) transform.RotateAround(pivot, up, yaw);
        if (pitch != 0f) transform.RotateAround(pivot, right, pitch);
    }

    // ---------------------------------------------------------------- zoom

    private void HandleZoomKeys()
    {
        if (!zoomKeysEnabled) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        bool zoomIn = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed;
        bool zoomOut = kb.minusKey.isPressed || kb.numpadMinusKey.isPressed;
        if (zoomIn == zoomOut) return;

        float factor = Mathf.Pow(zoomFactorPerSecond, Time.deltaTime);
        ApplyZoom(zoomIn ? factor : 1f / factor);
    }

    /// <summary>
    /// Multiply the model's scale by <paramref name="factor"/>, clamped to [minZoom, maxZoom] x the
    /// start scale, keeping the bounds centre (or the pivot) fixed in world space.
    /// </summary>
    public void ApplyZoom(float factor)
    {
        if (factor <= 0f || _startScale == Vector3.zero) return;

        float current = Zoom;
        float target = Mathf.Clamp(current * factor, minZoom, maxZoom);
        float applied = target / current;
        if (Mathf.Approximately(applied, 1f)) return;

        Vector3 pivot = zoomAroundBoundsCenter ? BoundsCenter() : transform.position;
        transform.localScale = _startScale * target;
        transform.position = pivot + (transform.position - pivot) * applied;
    }

    private Vector3 BoundsCenter()
    {
        if (_renderers == null || _renderers.Length == 0) return transform.position;
        bool any = false;
        Bounds b = new Bounds();
        foreach (var r in _renderers)
        {
            if (r == null) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        return any ? b.center : transform.position;
    }
}

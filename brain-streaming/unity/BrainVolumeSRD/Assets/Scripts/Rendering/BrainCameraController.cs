using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Orbit / pan / zoom / reset around the brain. Camera motion is what drives
    /// streaming (BrainApp reads this camera each frame and posts /view). Kept
    /// minimal on purpose; touch / SRD input can be layered on later.
    /// </summary>
    public sealed class BrainCameraController : MonoBehaviour
    {
        public Vector3 target = Vector3.zero;
        public float distance = 3.0f;
        public float minDistance = 0.4f, maxDistance = 30f;
        public float orbitSpeed = 200f, panSpeed = 1.0f, zoomSpeed = 2.5f;

        float _yaw, _pitch = 10f;
        Vector3 _home; float _homeDist, _homeYaw, _homePitch;

        void Start()
        {
            _home = target; _homeDist = distance; _homeYaw = _yaw; _homePitch = _pitch;
            Apply();
        }

        void Update()
        {
            // orbit (LMB), pan (MMB or RMB), zoom (wheel)
            if (Input.GetMouseButton(0))
            {
                _yaw += Input.GetAxis("Mouse X") * orbitSpeed * Time.deltaTime;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * orbitSpeed * Time.deltaTime, -85f, 85f);
            }
            if (Input.GetMouseButton(2) || Input.GetMouseButton(1))
            {
                var right = transform.right; var up = transform.up;
                target -= (right * Input.GetAxis("Mouse X") + up * Input.GetAxis("Mouse Y")) * panSpeed * distance * Time.deltaTime;
            }
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0f)
                distance = Mathf.Clamp(distance - scroll * zoomSpeed * distance * 0.1f, minDistance, maxDistance);
            if (Input.GetKeyDown(KeyCode.R)) Reset();
            Apply();
        }

        public void Reset()
        {
            target = _home; distance = _homeDist; _yaw = _homeYaw; _pitch = _homePitch;
        }

        void Apply()
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = target + rot * new Vector3(0, 0, -distance);
            transform.LookAt(target, Vector3.up);
        }
    }
}

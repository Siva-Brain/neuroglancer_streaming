using UnityEngine;

namespace BrainStreaming
{
    /// <summary>Simple orbit camera. Camera motion is what drives streaming.</summary>
    public sealed class BrainCameraController : MonoBehaviour
    {
        public Vector3 Target = new Vector3(0, 0, -10);
        public float Distance = 360f;
        public float MinDistance = 80f, MaxDistance = 900f;
        public float OrbitSpeed = 0.3f, ZoomSpeed = 40f;

        private float _yaw = 0f, _pitch = 10f;

        void Start() => Apply();

        void Update()
        {
            if (Input.GetMouseButton(0))
            {
                _yaw += Input.GetAxis("Mouse X") * OrbitSpeed * 60f * Time.deltaTime * 3f;
                _pitch -= Input.GetAxis("Mouse Y") * OrbitSpeed * 60f * Time.deltaTime * 3f;
                _pitch = Mathf.Clamp(_pitch, -85f, 85f);
            }
            Distance = Mathf.Clamp(Distance - Input.mouseScrollDelta.y * ZoomSpeed, MinDistance, MaxDistance);
            Apply();
        }

        void Apply()
        {
            var rot = Quaternion.Euler(_pitch, _yaw, 0);
            transform.position = Target + rot * new Vector3(0, 0, -Distance);
            transform.LookAt(Target);
        }
    }
}

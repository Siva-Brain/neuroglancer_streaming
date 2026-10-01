using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BrainVolume
{
    /// <summary>
    /// Scripted 30 s showcase of the local fused volume, optionally recorded to MP4.
    ///   P   = preview the sequence live (P again stops it)
    ///   F10 = play the sequence and record it to <project>/Recordings/*.mp4 (Editor only)
    ///
    /// Sequence (seconds, all adjustable in the Inspector):
    ///   rotate to the LEFT sagittal view (left hemisphere facing the camera)  5
    ///   hold                                                                   1
    ///   slice in, from the left, to sliceDepth                                 8
    ///   slice back out                                                         8
    ///   one full turntable rotation, ending back on the sagittal view          8
    ///
    /// The volume is driven directly (ModelMoveController is paused while the
    /// sequence runs). The video comes from a dedicated camera rendering into a
    /// RenderTexture, so it is independent of the SRD's eye cameras; the volume
    /// draws itself in OnRenderObject for any camera. Recording uses
    /// Time.captureFramerate, so the video has exactly fps x duration frames however
    /// slow each frame is to render and encode.
    ///
    /// Not in any scene by default: add it to a GameObject in a scene with a
    /// FusedVolumeLoader to use it.
    /// </summary>
    public sealed class BrainShowcase : MonoBehaviour
    {
        [Header("Anatomy of the volume (check with a P preview first)")]
        [Tooltip("Left hemisphere is at section 0 (local -Z). Untick if the preview shows the right side.")]
        public bool leftIsLowZ = true;
        [Tooltip("Superior (top of the brain) is image row 0 (local -Y). Untick if the brain is upside down.")]
        public bool superiorIsLowY = true;

        [Header("Timing (seconds)")]
        public float rotateToSagittal = 5f;
        public float hold = 1f;
        public float sliceIn = 8f;
        public float sliceOut = 8f;
        public float turntable = 8f;
        [Tooltip("How far the cut goes in, 0..1 of the left-right extent.")]
        [Range(0.1f, 0.98f)] public float sliceDepth = 0.85f;

        [Header("Video")]
        public int width = 1920;
        public int height = 1080;
        public int fps = 30;
        [Range(10f, 60f)] public float fieldOfView = 30f;
        [Tooltip("Camera distance as a multiple of the distance that just fits the volume.")]
        [Range(0.6f, 2f)] public float framing = 1.05f;
        public Color background = new Color(0.10f, 0.10f, 0.11f);

        FusedVolumeLoader _vol;
        Behaviour _mover;               // ModelMoveController on the volume, paused while playing
        Camera _cam;
        RenderTexture _rt;
        Coroutine _run;
        public bool Playing => _run != null;

        void Start()
        {
            _vol = FindFirstObjectByType<FusedVolumeLoader>();
            if (_vol != null) _mover = _vol.GetComponent("ModelMoveController") as Behaviour;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || _vol == null) return;
            if (kb.pKey.wasPressedThisFrame)
            {
                if (Playing) Stop();
                else _run = StartCoroutine(Run(false));
            }
            if (kb.f10Key.wasPressedThisFrame && !Playing)
                _run = StartCoroutine(Run(true));
        }

        void Stop()
        {
            if (_run != null) StopCoroutine(_run);
            Finish();
        }

        IEnumerator Run(bool record)
        {
            while (!_vol.Loaded) yield return null;     // L4 takes a few seconds to load

            if (_mover != null) _mover.enabled = false;
            _vol.sliceSweeping = false;
            _vol.sliceAxis = FusedVolumeLoader.Axis.Z;
            _vol.sliceReverse = !leftIsLowZ;            // cut starts on the left, which faces the camera
            _vol.slicePosition = 0f;
            _vol.sliceDirection = -1;

            Transform t = _vol.transform;
            Vector3 centre = t.position;                // the volume is centred on its transform
            SetupCamera(centre);

            // Left-sagittal pose: left-pointing axis -> towards the camera, superior -> camera up.
            Vector3 leftLocal = leftIsLowZ ? Vector3.back : Vector3.forward;
            Vector3 supLocal = superiorIsLowY ? Vector3.down : Vector3.up;
            Vector3 toCam = -_cam.transform.forward, camUp = _cam.transform.up;
            Quaternion sagittal = Quaternion.LookRotation(toCam, camUp) *
                                  Quaternion.Inverse(Quaternion.LookRotation(leftLocal, supLocal));
            Quaternion start = t.rotation;

            float total = rotateToSagittal + hold + sliceIn + sliceOut + turntable;
            Recorder rec = null;
            if (record)
            {
                rec = Recorder.Create(width, height, fps);
                if (rec == null) { Finish(); yield break; }
                Time.captureFramerate = fps;
                Debug.Log($"[Showcase] Recording {total:F0} s at {fps} fps -> {rec.Path}");
            }
            else Debug.Log($"[Showcase] Preview, {total:F0} s (P to stop).");

            float time = 0f;
            int frames = Mathf.RoundToInt(total * fps), frame = 0;
            while (record ? frame < frames : time <= total)
            {
                float s = time;
                if (s < rotateToSagittal)
                {
                    t.rotation = Quaternion.Slerp(start, sagittal, Ease(s / rotateToSagittal));
                    _vol.slicePosition = 0f;
                }
                else if ((s -= rotateToSagittal) < hold)
                {
                    t.rotation = sagittal;
                }
                else if ((s -= hold) < sliceIn)
                {
                    t.rotation = sagittal;
                    _vol.slicePosition = sliceDepth * Ease(s / sliceIn);
                }
                else if ((s -= sliceIn) < sliceOut)
                {
                    t.rotation = sagittal;
                    _vol.slicePosition = sliceDepth * (1f - Ease(s / sliceOut));
                }
                else
                {
                    s -= sliceOut;
                    _vol.slicePosition = 0f;
                    float a = 360f * Ease(Mathf.Clamp01(s / turntable));
                    t.rotation = Quaternion.AngleAxis(a, camUp) * sagittal;
                }

                if (record)
                {
                    yield return new WaitForEndOfFrame();
                    rec.AddFrame(_cam, _rt);
                    frame++;
                    time = frame / (float)fps;
                }
                else
                {
                    yield return null;
                    time += Time.deltaTime;
                }
            }

            t.rotation = sagittal;
            _vol.slicePosition = 0f;
            if (rec != null)
            {
                rec.Dispose();
                Debug.Log($"[Showcase] Saved {frames} frames to {rec.Path}");
            }
            Finish();
        }

        void Finish()
        {
            Time.captureFramerate = 0;
            if (_mover != null) _mover.enabled = true;
            if (_vol != null) _vol.slicePosition = 0f;
            _run = null;
        }

        void SetupCamera(Vector3 centre)
        {
            if (_cam == null)
            {
                var go = new GameObject("ShowcaseCamera");
                go.transform.SetParent(transform, false);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;               // rendered manually, only for the video
                _cam.cullingMask = 0;               // no floor etc. -- the volume draws itself
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.nearClipPlane = 0.01f;
                _cam.farClipPlane = 20f;
            }
            if (_rt == null || _rt.width != width || _rt.height != height)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
                    { sRGB = true });
            }
            _cam.backgroundColor = background;
            _cam.fieldOfView = fieldOfView;
            _cam.aspect = width / (float)height;
            _cam.targetTexture = _rt;

            // Fit the bounding sphere of the volume (whatever its orientation), viewed along world +Z.
            float radius = 0.5f * (_vol.SizeMm * _vol.unitsPerMm).magnitude * _vol.transform.lossyScale.x;
            float halfFov = 0.5f * fieldOfView * Mathf.Deg2Rad;
            float dist = framing * radius / Mathf.Sin(halfFov);
            _cam.transform.position = centre - Vector3.forward * dist;
            _cam.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        }

        void OnDestroy()
        {
            if (Playing) Stop();
            if (_rt != null) _rt.Release();
        }

        static float Ease(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        // MP4 writer. UnityEditor.Media.MediaEncoder (H.264) exists only in the Editor.
        sealed class Recorder : System.IDisposable
        {
            public string Path { get; private set; }
            Texture2D _frame;
#if UNITY_EDITOR
            UnityEditor.Media.MediaEncoder _enc;
#endif
            public static Recorder Create(int w, int h, int fps)
            {
#if UNITY_EDITOR
                w &= ~1; h &= ~1;                       // H.264 needs even sizes
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Recordings"));
                Directory.CreateDirectory(dir);
                var r = new Recorder
                {
                    Path = System.IO.Path.Combine(dir, $"brain_showcase_{System.DateTime.Now:yyyyMMdd_HHmmss}.mp4"),
                    _frame = new Texture2D(w, h, TextureFormat.RGBA32, false),
                };
                var attrs = new UnityEditor.Media.VideoTrackAttributes
                {
                    frameRate = new UnityEditor.Media.MediaRational(fps),
                    width = (uint)w,
                    height = (uint)h,
                    includeAlpha = false,
                };
                r._enc = new UnityEditor.Media.MediaEncoder(r.Path, attrs);
                return r;
#else
                Debug.LogError("[Showcase] MP4 recording works in the Unity Editor only.");
                return null;
#endif
            }

            public void AddFrame(Camera cam, RenderTexture rt)
            {
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                _frame.ReadPixels(new Rect(0, 0, _frame.width, _frame.height), 0, 0, false);
                _frame.Apply(false);
                RenderTexture.active = prev;
#if UNITY_EDITOR
                _enc.AddFrame(_frame);
#endif
            }

            public void Dispose()
            {
#if UNITY_EDITOR
                _enc?.Dispose();
                _enc = null;
#endif
                if (_frame != null) Object.Destroy(_frame);
            }
        }
    }
}

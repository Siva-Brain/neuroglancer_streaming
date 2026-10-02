using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using SRD.Core;

namespace BrainVolume
{
    /// <summary>
    /// Records the NeuronalLossSequence timeline (start pose -> rotate to the left sagittal view ->
    /// slice -> neuronal-loss block + label) to an MP4 in <project>/Recordings/ (Editor only).
    ///   recordOnPlay = record once as soon as the brain is loaded
    ///   F10          = record again from the start
    ///
    /// The view is the SRD's WatcherCamera (the viewer's head between the eyes: same pose and
    /// off-axis projection onto the display panel), copied every frame into a hidden camera that
    /// renders into a RenderTexture. Without an SRD manager it falls back to a camera looking along
    /// world +Z at the brain. The seek bar is hidden while recording. Time.captureFramerate makes
    /// the timeline advance exactly 1/fps per frame, so the video has the timeline's real timing
    /// however slow each frame is to render.
    /// </summary>
    public sealed class TimelineRecorder : MonoBehaviour
    {
        public NeuronalLossSequence timeline;
        [Tooltip("Record once automatically when Play starts (as soon as the brain is loaded).")]
        public bool recordOnPlay = true;
        [Tooltip("Video width; the height follows the panel's aspect (16:9 fallback).")]
        public int width = 1920;
        public int fps = 30;
        [Tooltip("Seconds recorded after the timeline ends.")]
        public float tail = 1.5f;
        [Tooltip("Fallback camera (no SRD): vertical field of view and distance from the brain.")]
        public float fallbackFov = 35f, fallbackDistance = 1.6f;
        public string filePrefix = "neuronal_loss";

        Camera _cam, _watcher;
        RenderTexture _rt;
        Coroutine _run;
        public bool Recording => _run != null;

        void Start()
        {
            if (timeline == null) timeline = FindFirstObjectByType<NeuronalLossSequence>();
            var srd = FindFirstObjectByType<SRDManager>();
            if (srd != null)
                foreach (var c in srd.GetComponentsInChildren<Camera>(true))
                    if (c.name == "WatcherCamera") _watcher = c;
            if (recordOnPlay && timeline != null) _run = StartCoroutine(Run());
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame && !Recording && timeline != null)
                _run = StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            while (!timeline.Ready) yield return null;     // brain loaded, timeline prepared
            yield return new WaitForEndOfFrame();

            var bar = FindFirstObjectByType<TimelineTransportUI>();
            bool barWasVisible = bar != null && bar.Visible;
            if (bar != null) bar.SetVisible(false);

            float aspect = WatcherAspect();
            int w = width & ~1, h = Mathf.RoundToInt(width / aspect) & ~1;   // H.264 needs even sizes
            SetupCamera(w, h);
            var rec = Recorder.Create(w, h, fps, filePrefix);
            if (rec == null) { Finish(bar, barWasVisible); yield break; }

            Time.captureFramerate = fps;
            timeline.SeekTo(0f);
            timeline.Play();
            int frames = Mathf.CeilToInt((timeline.TotalSeconds + tail) * fps);
            Debug.Log($"[Record] {frames / (float)fps:F1} s, {w}x{h} @ {fps} fps, " +
                      $"{(_watcher != null ? "SRD watcher view" : "fallback camera")} -> {rec.Path}");

            for (int i = 0; i < frames; i++)
            {
                yield return new WaitForEndOfFrame();     // timeline has advanced and been applied
                PoseCamera();
                rec.AddFrame(_cam, _rt);
            }
            rec.Dispose();
            Debug.Log($"[Record] Saved {frames} frames to {rec.Path}");
            Finish(bar, barWasVisible);
        }

        void Finish(TimelineTransportUI bar, bool barVisible)
        {
            Time.captureFramerate = 0;
            if (bar != null) bar.SetVisible(barVisible);
            _run = null;
        }

        float WatcherAspect()
        {
            if (_watcher == null) return 16f / 9f;
            Matrix4x4 p = _watcher.projectionMatrix;
            float a = p.m00 != 0f ? p.m11 / p.m00 : 0f;
            return a > 0.2f && a < 5f ? a : 16f / 9f;
        }

        void SetupCamera(int w, int h)
        {
            if (_cam == null)
            {
                var go = new GameObject("RecordCamera");
                go.transform.SetParent(transform, false);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;                       // rendered manually, only for the video
            }
            if (_rt == null || _rt.width != w || _rt.height != h)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 24) { sRGB = true });
            }
            if (_watcher != null) _cam.CopyFrom(_watcher);
            else
            {
                _cam.clearFlags = CameraClearFlags.SolidColor;
                _cam.backgroundColor = Color.black;
                _cam.fieldOfView = fallbackFov;
                _cam.nearClipPlane = 0.01f;
                _cam.farClipPlane = 50f;
            }
            _cam.enabled = false;
            _cam.targetTexture = _rt;
            _cam.aspect = w / (float)h;
        }

        // Follow the watcher (its pose and projection change with the tracked face) every frame.
        void PoseCamera()
        {
            if (_watcher != null)
            {
                _cam.transform.SetPositionAndRotation(_watcher.transform.position, _watcher.transform.rotation);
                _cam.projectionMatrix = _watcher.projectionMatrix;
                return;
            }
            Vector3 c = timeline.volume != null ? timeline.volume.transform.position : timeline.transform.position;
            _cam.transform.SetPositionAndRotation(c - Vector3.forward * fallbackDistance, Quaternion.identity);
        }

        void OnDestroy()
        {
            if (Recording) { StopCoroutine(_run); Time.captureFramerate = 0; }
            if (_rt != null) _rt.Release();
        }

        // MP4 writer. UnityEditor.Media.MediaEncoder (H.264) exists only in the Editor.
        sealed class Recorder : System.IDisposable
        {
            public string Path { get; private set; }
            Texture2D _frame;
#if UNITY_EDITOR
            UnityEditor.Media.MediaEncoder _enc;
#endif
            public static Recorder Create(int w, int h, int fps, string prefix)
            {
#if UNITY_EDITOR
                string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Recordings"));
                Directory.CreateDirectory(dir);
                var r = new Recorder
                {
                    Path = System.IO.Path.Combine(dir, $"{prefix}_{System.DateTime.Now:yyyyMMdd_HHmmss}.mp4"),
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
                Debug.LogError("[Record] MP4 recording works in the Unity Editor only.");
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

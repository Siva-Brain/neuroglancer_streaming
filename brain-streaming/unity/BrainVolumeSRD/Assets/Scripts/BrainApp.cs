using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Orchestrator. The ONLY class that touches both networking and rendering;
    /// each side stays unaware of the other. Boots (info -> coarse baseline),
    /// then each frame posts the camera to the DGX and feeds the returned
    /// priorities to the scheduler. Rendering reads the cache independently.
    /// </summary>
    public sealed class BrainApp : MonoBehaviour
    {
        [Header("DGX server")]
        public string serverUrl = "http://192.168.1.50:8090";
        public string channels = "0,3";                 // grayscale + tissue mask

        [Header("Scene refs")]
        public Camera targetCamera;
        public Transform brainRoot;
        public BrainVolumeRenderer volumeRenderer;
        public BrainCameraController cameraController;
        public SonySRDManager sonyManager;

        [Header("Transform + tuning")]
        public BrainCoordinateSystem coordinates = new BrainCoordinateSystem();
        public float viewIntervalSec = 0.35f;
        public float moveThreshold = 0.01f;
        public int maxConcurrency = 6;
        public int cacheMB = 512;

        public BrainStreamClient Client { get; private set; }
        public BrainChunkCache Cache { get; private set; }
        public RequestScheduler Scheduler { get; private set; }
        public DatasetInfo Info { get; private set; }
        public bool ConnectionOk => Client != null && Client.Connected;

        // telemetry
        public float Fps { get; private set; }
        public float FrameMs { get; private set; }
        public float MbPerSec { get; private set; }
        public float LatencyMs => Client != null ? Client.LastLatencyMs : 0f;

        float _viewTimer, _bwTimer; long _bwLastBytes;
        Vector3 _lastPos; Vector3 _lastFwd; bool _firstView = true;

        async void Start()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (brainRoot == null) brainRoot = transform;
            EnsureMaterial();

            Client = new BrainStreamClient(serverUrl) { Channels = channels };
            Info = await GetInfoRetry();
            if (Info == null)
            {
                Debug.LogError($"[BrainApp] DGX not reachable at {serverUrl} — see warnings above. " +
                    "If error mentions HTTP/insecure: Player Settings > Other Settings > " +
                    "'Allow downloads over HTTP' = Always allowed.");
                return;
            }
            Debug.Log($"[BrainApp] CONNECTED to {serverUrl} — dataset '{Info.name}', " +
                      $"{Info.levels?.Length} levels, {Info.baseline_chunks?.Length} baseline bricks, " +
                      $"extent_mm={(Info.extent_mm != null ? string.Join(",", Info.extent_mm) : "?")}");

            Cache = new BrainChunkCache(coordinates, (long)cacheMB * 1024 * 1024);
            Scheduler = new RequestScheduler(Client, Cache, maxConcurrency);
            if (volumeRenderer != null) { volumeRenderer.brainRoot = brainRoot; volumeRenderer.Bind(Cache); }
            if (cameraController != null) cameraController.distance = coordinates.SuggestedDistance(Info.extent_mm);

            Scheduler.EnqueueBaseline(Info.baseline_chunks);   // coarse whole brain first
        }

        async System.Threading.Tasks.Task<DatasetInfo> GetInfoRetry()
        {
            for (int i = 0; i < 3; i++)
            {
                var info = await Client.GetDatasetInfoAsync();
                if (info != null) return info;
                await System.Threading.Tasks.Task.Delay(500);
            }
            return null;
        }

        void Update()
        {
            FrameMs = Mathf.Lerp(FrameMs, Time.unscaledDeltaTime * 1000f, 0.1f);
            Fps = FrameMs > 0.001f ? 1000f / FrameMs : 0f;

            _bwTimer += Time.unscaledDeltaTime;
            if (_bwTimer >= 1f && Scheduler != null)
            {
                MbPerSec = (Scheduler.BytesReceived - _bwLastBytes) / 1048576f / _bwTimer;
                _bwLastBytes = Scheduler.BytesReceived; _bwTimer = 0f;
            }

            if (Info == null || targetCamera == null) return;
            _viewTimer += Time.unscaledDeltaTime;
            if (_viewTimer < viewIntervalSec) return;
            _viewTimer = 0f;

            bool moved = _firstView
                || (targetCamera.transform.position - _lastPos).magnitude > moveThreshold
                || (targetCamera.transform.forward - _lastFwd).magnitude > moveThreshold;
            if (!moved && Scheduler.Pending == 0 && Scheduler.InFlight == 0) return;

            _lastPos = targetCamera.transform.position; _lastFwd = targetCamera.transform.forward;
            _firstView = false;
            SendView();
        }

        async void SendView()
        {
            var cam = coordinates.ToCameraState(targetCamera, brainRoot);
            var view = await Client.PostViewAsync(cam);
            if (view != null) Scheduler.OnView(view);
        }

        void EnsureMaterial()
        {
            if (volumeRenderer != null && volumeRenderer.raymarchMaterial == null)
            {
                var sh = Shader.Find("Brain/Raymarch");
                if (sh != null) volumeRenderer.raymarchMaterial = new Material(sh);
                else Debug.LogError("[BrainApp] Shader 'Brain/Raymarch' not found.");
            }
        }

        public void CurrentLodRange(out int min, out int max)
        {
            min = -1; max = -1;
            if (Cache == null) return;
            foreach (var b in Cache.Bricks)
            {
                if (min < 0 || b.Level < min) min = b.Level;
                if (max < 0 || b.Level > max) max = b.Level;
            }
        }
    }
}

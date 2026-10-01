using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Orchestrator for the MULTI-BLOCK brain. Boots (dataset/info -> N blocks,
    /// transforms -> per-block placement), gives each block its own cache +
    /// scheduler, and each frame posts the camera (transformed into that block's
    /// local frame) to the DGX and feeds the returned priorities. The renderer
    /// composites every block's bricks into one brain in shared RAS space.
    /// </summary>
    public sealed class BrainApp : MonoBehaviour
    {
        [Header("DGX server")]
        public string serverUrl = "http://dgx3.humanbrain.in:8010";
        public string channels = "0,3";                 // Nissl gray+mask; auto -> "0,1,2" for RGB brains

        [Header("LOD / streaming default")]
        [Tooltip("Pin to the coarsest level (e.g. L7) — instant whole-brain overview. Uncheck for screen-space LOD.")]
        public bool pinCoarsest = true;
        [Tooltip("Load every shard of the level (whole brain resident), not just the view frustum.")]
        public bool wholeBrain = true;
        [Tooltip("Manual LOD clamp when pinCoarsest is off: -1 = auto/screen-space.")]
        public int lodMin = -1;
        public int lodMax = -1;

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
        public int maxConcurrency = 8;                   // total across all blocks
        public int cacheMB = 1024;                       // total budget, split per block

        public BrainStreamClient Client { get; private set; }
        public DatasetInfo Info { get; private set; }
        public List<BrainBlock> Blocks { get; } = new List<BrainBlock>();
        public bool ConnectionOk => Client != null && Client.Connected;

        // telemetry
        public float Fps { get; private set; }
        public float FrameMs { get; private set; }
        public float MbPerSec { get; private set; }
        public float LatencyMs => Client != null ? Client.LastLatencyMs : 0f;

        // aggregate stats (across all blocks) for the HUD
        public int ChunksRequested { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.Requested ?? 0; return n; } }
        public int ChunksReceived  { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.Received ?? 0; return n; } }
        public int BricksLoaded    { get { int n = 0; foreach (var b in Blocks) n += b.Cache?.Count ?? 0; return n; } }
        public long CacheBytes     { get { long n = 0; foreach (var b in Blocks) n += b.Cache?.BytesUsed ?? 0; return n; } }
        public int Evictions       { get { int n = 0; foreach (var b in Blocks) n += b.Cache?.Evictions ?? 0; return n; } }
        public int InFlight        { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.InFlight ?? 0; return n; } }
        public int Pending         { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.Pending ?? 0; return n; } }
        public int TargetLevel     { get { int t = -1; foreach (var b in Blocks) { int v = b.Scheduler?.TargetLevel ?? -1; if (v >= 0 && (t < 0 || v < t)) t = v; } return t; } }
        public long BytesReceivedTotal { get { long n = 0; foreach (var b in Blocks) n += b.Scheduler?.BytesReceived ?? 0; return n; } }
        public int BlockCount => Blocks.Count;

        float _viewTimer, _bwTimer; long _bwLastBytes;
        Vector3 _lastPos; Vector3 _lastFwd; bool _firstView = true;

        async void Start()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (brainRoot == null) brainRoot = transform;
            EnsureMaterial();

            Client = new BrainStreamClient(serverUrl) { Channels = channels };
            Info = await GetInfoRetry();
            var blockInfos = Info?.blocks;
            if (blockInfos == null || blockInfos.Length == 0)
            {
                Debug.LogError($"[BrainApp] DGX not reachable / no blocks at {serverUrl}. " +
                    "If HTTP is blocked: Player Settings > Other Settings > 'Allow downloads over HTTP' = Always.");
                return;
            }

            // RGB brains (hb02 fused) need the colour channels + the RGB shader path.
            bool rgb = Info.rgb;
            Client.Channels = rgb ? "0,1,2" : channels;
            if (volumeRenderer != null) volumeRenderer.isRGB = rgb;

            // streaming LOD default: pin the coarsest level (L7) + whole brain for an
            // instant, fully-resident overview; else a manual clamp or screen-space.
            int coarsest = 0;
            foreach (var bi in blockInfos) if (bi.coarsest_level > coarsest) coarsest = bi.coarsest_level;
            if (pinCoarsest) { Client.LevelMin = Client.LevelMax = coarsest; }
            else { Client.LevelMin = lodMin; Client.LevelMax = lodMax; }
            Client.Whole = wholeBrain;

            // per-block placement matrices (omeToRas) from /api/transforms
            var xf = await Client.GetTransformsAsync();
            var mats = new Dictionary<string, Matrix4x4>();
            if (xf?.list != null)
                foreach (var t in xf.list) mats[t.block] = BrainCoordinateSystem.FromColMajor(t.matrix);

            int n = blockInfos.Length;
            long bytesPerBlock = (long)cacheMB * 1024 * 1024 / Mathf.Max(1, n);
            int concPerBlock = Mathf.Max(2, maxConcurrency / Mathf.Max(1, n));

            Blocks.Clear();
            foreach (var bi in blockInfos)
            {
                if (!mats.TryGetValue(bi.block_id, out var W))
                {
                    W = Matrix4x4.identity;
                    Debug.LogWarning($"[BrainApp] block {bi.block_id}: no /api/transforms matrix — placed at origin.");
                }
                var cache = new BrainChunkCache(coordinates, W, bytesPerBlock);
                var sched = new RequestScheduler(Client, cache, bi.block_id, concPerBlock);
                Blocks.Add(new BrainBlock {
                    Id = bi.block_id, Info = bi, WorldMatrix = W, WorldMatrixInv = W.inverse,
                    Cache = cache, Scheduler = sched,
                });
            }

            Debug.Log($"[BrainApp] CONNECTED to {serverUrl} — {n} blocks: " +
                      string.Join(",", Blocks.ConvertAll(b => b.Id)));

            if (volumeRenderer != null) { volumeRenderer.brainRoot = brainRoot; volumeRenderer.Bind(Blocks); }
            FitView();

            foreach (var b in Blocks) { b.Scheduler.EnqueueBaseline(b.Info.baseline_chunks); }
        }

        async Task<DatasetInfo> GetInfoRetry()
        {
            for (int i = 0; i < 3; i++)
            {
                var info = await Client.GetDatasetInfoAsync();
                if (info?.blocks != null && info.blocks.Length > 0) return info;
                await Task.Delay(500);
            }
            return null;
        }

        void Update()
        {
            FrameMs = Mathf.Lerp(FrameMs, Time.unscaledDeltaTime * 1000f, 0.1f);
            Fps = FrameMs > 0.001f ? 1000f / FrameMs : 0f;

            _bwTimer += Time.unscaledDeltaTime;
            if (_bwTimer >= 1f && Blocks.Count > 0)
            {
                long tot = BytesReceivedTotal;
                MbPerSec = (tot - _bwLastBytes) / 1048576f / _bwTimer;
                _bwLastBytes = tot; _bwTimer = 0f;
            }

            if (Blocks.Count == 0 || targetCamera == null) return;
            _viewTimer += Time.unscaledDeltaTime;
            if (_viewTimer < viewIntervalSec) return;
            _viewTimer = 0f;

            bool anyBusy = InFlight != 0 || Pending != 0;
            bool moved = _firstView
                || (targetCamera.transform.position - _lastPos).magnitude > moveThreshold
                || (targetCamera.transform.forward - _lastFwd).magnitude > moveThreshold;
            if (!moved && !anyBusy) return;

            _lastPos = targetCamera.transform.position; _lastFwd = targetCamera.transform.forward;
            _firstView = false;
            foreach (var b in Blocks) SendView(b);
        }

        async void SendView(BrainBlock block)
        {
            var cam = coordinates.ToCameraState(targetCamera, brainRoot, block.WorldMatrixInv);
            var view = await Client.PostViewAsync(cam, block.Id);
            if (view != null) block.Scheduler.OnView(view);
        }

        /// <summary>Frame the camera on the fused brain (all blocks' RAS-mm boxes).</summary>
        void FitView()
        {
            if (cameraController == null || Blocks.Count == 0) return;
            Vector3 lo = Vector3.one * 1e9f, hi = Vector3.one * -1e9f;
            foreach (var b in Blocks)
            {
                Vector3 h = b.HalfExtentMm();
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? h.x : -h.x,
                                        (i & 2) != 0 ? h.y : -h.y,
                                        (i & 4) != 0 ? h.z : -h.z);
                    Vector3 ras = b.WorldMatrix.MultiplyPoint3x4(c);   // block-local mm -> RAS mm
                    lo = Vector3.Min(lo, ras); hi = Vector3.Max(hi, ras);
                }
            }
            Vector3 centerRas = (lo + hi) * 0.5f;
            float radiusMm = Mathf.Max(hi.x - lo.x, hi.y - lo.y, hi.z - lo.z);
            Matrix4x4 toWorld = brainRoot.localToWorldMatrix * coordinates.MmToUnityMatrix();
            cameraController.target = toWorld.MultiplyPoint3x4(centerRas);
            cameraController.distance = Mathf.Max(0.5f, radiusMm * coordinates.unitsPerMm * 1.6f);
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
            foreach (var blk in Blocks)
            {
                if (blk.Cache == null) continue;
                foreach (var b in blk.Cache.Bricks)
                {
                    if (min < 0 || b.Level < min) min = b.Level;
                    if (max < 0 || b.Level > max) max = b.Level;
                }
            }
        }
    }
}

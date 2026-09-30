using System.Collections.Generic;
using System.Threading;
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
        public string serverUrl = "http://192.168.1.50:8090";
        public string channels = "0,3";                 // grayscale + tissue mask (gray_mask brains)
        [Tooltip("Which brain to load first (id from /api/brains, e.g. 'Stroke_1' or " +
                 "'hb02'). Empty = the server's default brain. A runtime selector " +
                 "(top-left) switches between brains.")]
        public string brain = "";

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
        public int cacheMB = 1024;                       // total budget, split per block (brick path)

        [Header("Rendering path")]
        [Tooltip("Merged per-block volume (one draw per block) instead of ~58 slab " +
                 "bricks. The low-jitter SRD path. Off -> the original per-brick path.")]
        public bool useBlockVolume = true;

        public BrainStreamClient Client { get; private set; }
        public DatasetInfo Info { get; private set; }
        public List<BrainBlock> Blocks { get; } = new List<BrainBlock>();
        public bool ConnectionOk => Client != null && Client.Connected;

        // multi-brain selector state
        BrainDto[] _brains;
        public string CurrentBrain { get; private set; } = "";
        bool _switching;

        // telemetry
        public float Fps { get; private set; }
        public float FrameMs { get; private set; }
        public float MbPerSec { get; private set; }
        public float LatencyMs => Client != null ? Client.LastLatencyMs : 0f;

        // aggregate stats (across all blocks) for the HUD
        public int ChunksRequested { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.Requested ?? 0; return n; } }
        public int ChunksReceived  { get { int n = 0; foreach (var b in Blocks) n += b.Scheduler?.Received ?? 0; return n; } }
        public int BricksLoaded    { get { int n = 0; foreach (var b in Blocks) { n += b.Cache?.Count ?? 0; if (b.Volume != null) n++; } return n; } }
        public long CacheBytes     { get { long n = 0; foreach (var b in Blocks) { n += b.Cache?.BytesUsed ?? 0; if (b.Volume != null) n += b.Volume.Bytes; } return n; } }
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

            // discover the selectable brains; pick the configured one, else the default.
            var bs = await Client.GetBrainsAsync();
            _brains = bs?.brains;
            string startBrain = brain;
            if (string.IsNullOrEmpty(startBrain))
                startBrain = !string.IsNullOrEmpty(bs?.@default) ? bs.@default
                           : (_brains != null && _brains.Length > 0 ? _brains[0].id : "");

            await LoadBrainAsync(startBrain);
        }

        /// <summary>Load one brain: its blocks, placement, caches, renderer binding,
        /// and initial volume/brick fetch. Safe to call again after TeardownBlocks().</summary>
        async Task LoadBrainAsync(string brainId)
        {
            CurrentBrain = brainId ?? "";
            // brains are homogeneous; a brain whose blocks render "rgb" (the fused
            // hb02 volume) is only correct on the merged-volume path (colour is baked
            // server-side into RGBA), so force it and request all colour channels.
            bool rgb = BrainRenders(brainId) == "rgb";
            if (rgb) { useBlockVolume = true; Client.Channels = "0,1,2"; }
            else Client.Channels = channels;

            Info = await GetInfoRetry(brainId);
            var blockInfos = Info?.blocks;
            if (blockInfos == null || blockInfos.Length == 0)
            {
                Debug.LogError($"[BrainApp] DGX not reachable / no blocks for brain '{brainId}' at {serverUrl}. " +
                    "If HTTP is blocked: Player Settings > Other Settings > 'Allow downloads over HTTP' = Always.");
                return;
            }

            // per-block placement matrices (omeToRas) from /api/transforms
            var xf = await Client.GetTransformsAsync(brainId);
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
                    // single-block / unregistered brains (hb02) have no anatomical
                    // matrix; place by the server's default (auto-layout) transform.
                    W = DefaultWorldMatrix(bi);
                }
                var cache = new BrainChunkCache(coordinates, W, bytesPerBlock);
                var sched = new RequestScheduler(Client, cache, bi.block_id, concPerBlock);
                Blocks.Add(new BrainBlock {
                    Id = bi.block_id, Info = bi, WorldMatrix = W, WorldMatrixInv = W.inverse,
                    Cache = cache, Scheduler = sched,
                });
            }

            Debug.Log($"[BrainApp] CONNECTED to {serverUrl} — brain '{CurrentBrain}' — {n} blocks: " +
                      string.Join(",", Blocks.ConvertAll(b => b.Id)));

            if (volumeRenderer != null)
            {
                volumeRenderer.brainRoot = brainRoot;
                volumeRenderer.useBlockVolume = useBlockVolume;
                volumeRenderer.Bind(Blocks);
            }
            FitView();
            _firstView = true;               // re-issue a /view for the new placement

            if (useBlockVolume)
                foreach (var b in Blocks) FetchBlockVolume(b, b.Info.min_streamable_level);
            else
                foreach (var b in Blocks) b.Scheduler.EnqueueBaseline(b.Info.baseline_chunks);
        }

        /// <summary>Tear down the current brain's GPU + network state so another
        /// brain can be loaded cleanly (no leaked Texture3Ds, no stale fetches).</summary>
        void TeardownBlocks()
        {
            foreach (var b in Blocks)
            {
                b.Scheduler?.CancelAll();
                b.Cache?.Clear();
                b.Volume?.Dispose(); b.Volume = null; b.VolumeLevel = -1;
            }
            Blocks.Clear();
            volumeRenderer?.Bind(Blocks);
        }

        /// <summary>Switch to another brain at runtime (from the on-screen selector).</summary>
        public async void SwitchBrain(string brainId)
        {
            if (_switching || brainId == CurrentBrain) return;
            _switching = true;
            try { TeardownBlocks(); await LoadBrainAsync(brainId); }
            finally { _switching = false; }
        }

        string BrainRenders(string brainId)
        {
            if (_brains != null)
                foreach (var b in _brains) if (b.id == brainId) return b.render;
            return "gray_mask";
        }

        /// <summary>Identity placement translated by the block's server default
        /// transform (block-local mm -> RAS mm) for brains without an omeToRas matrix.</summary>
        static Matrix4x4 DefaultWorldMatrix(BlockInfo bi)
        {
            var t = bi.default_transform?.translation;
            var pos = (t != null && t.Length == 3) ? new Vector3(t[0], t[1], t[2]) : Vector3.zero;
            return Matrix4x4.Translate(pos);
        }

        async Task<DatasetInfo> GetInfoRetry(string brainId)
        {
            for (int i = 0; i < 3; i++)
            {
                var info = await Client.GetDatasetInfoAsync(brainId);
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
            if (view == null) return;
            if (useBlockVolume)
            {
                // camera-distance-driven level: refetch the whole block volume only
                // when the target level changes (double-buffered, so no per-frame churn).
                int lvl = Mathf.Clamp(view.target_level,
                                      block.Info.min_streamable_level, block.Info.coarsest_level);
                if (lvl != block.VolumeLevel && !block.VolumeFetching) FetchBlockVolume(block, lvl);
            }
            else block.Scheduler.OnView(view);
        }

        /// <summary>Fetch one merged volume for a block and swap it in. Double-buffered:
        /// the old volume keeps rendering until the new one is uploaded, so a level
        /// change never shows a hole or stalls the frame mid-upload.</summary>
        async void FetchBlockVolume(BrainBlock block, int level)
        {
            block.VolumeFetching = true;
            try
            {
                var data = await Client.GetBlockVolumeAsync(block.Id, level, CancellationToken.None);
                if (data != null)
                {
                    var vol = BlockVolume.Create(data, coordinates, block.WorldMatrix);
                    var old = block.Volume;
                    block.Volume = vol;
                    block.VolumeLevel = level;
                    old?.Dispose();
                }
            }
            finally { block.VolumeFetching = false; }
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

        /// <summary>Minimal on-screen brain selector (top-left). One button per brain
        /// from /api/brains; the active one is highlighted. Switching reloads.</summary>
        void OnGUI()
        {
            if (_brains == null || _brains.Length < 2) return;   // nothing to choose
            const float w = 190f, h = 26f, pad = 6f;
            GUILayout.BeginArea(new Rect(pad, pad, w + pad * 2, (h + 4f) * (_brains.Length + 1) + pad * 2),
                                GUI.skin.box);
            GUILayout.Label(_switching ? "brain — switching…" : "brain");
            foreach (var b in _brains)
            {
                bool active = b.id == CurrentBrain;
                GUI.enabled = !_switching && !active;
                string label = (active ? "● " : "○ ") + (string.IsNullOrEmpty(b.label) ? b.id : b.label);
                if (GUILayout.Button(label, GUILayout.Width(w), GUILayout.Height(h)))
                    SwitchBrain(b.id);
            }
            GUI.enabled = true;
            GUILayout.EndArea();
        }

        void EnsureMaterial()
        {
            if (volumeRenderer == null) return;
            if (volumeRenderer.raymarchMaterial == null)
            {
                var sh = Shader.Find("Brain/Raymarch");
                if (sh != null) volumeRenderer.raymarchMaterial = new Material(sh);
                else Debug.LogError("[BrainApp] Shader 'Brain/Raymarch' not found.");
            }
            if (volumeRenderer.blockRaymarchMaterial == null)
            {
                var sh = Shader.Find("Brain/BlockRaymarch");
                if (sh != null) volumeRenderer.blockRaymarchMaterial = new Material(sh);
                else Debug.LogError("[BrainApp] Shader 'Brain/BlockRaymarch' not found.");
            }
            volumeRenderer.useBlockVolume = useBlockVolume;
        }

        public void CurrentLodRange(out int min, out int max)
        {
            min = -1; max = -1;
            foreach (var blk in Blocks)
            {
                if (blk.Volume != null)
                {
                    if (min < 0 || blk.Volume.Level < min) min = blk.Volume.Level;
                    if (max < 0 || blk.Volume.Level > max) max = blk.Volume.Level;
                }
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

using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Chooses and drives the display adapter, keeping all Sony specifics out of
    /// the streaming/rendering code. Two modes:
    ///   NORMAL_MONITOR  -> NullSRDAdapter (default; always works)
    ///   SONY_ELF_SR2    -> SonySRDAdapter (only when the SDK + SONY_SRD_SDK define
    ///                      are present AND the SRDisplayManager prefab is in scene)
    /// If Sony mode is requested but unavailable, it falls back to normal mode and
    /// says so -- the core prototype never depends on the SDK.
    /// </summary>
    public sealed class SonySRDManager : MonoBehaviour
    {
        public BrainDisplayMode mode = BrainDisplayMode.NormalMonitor;

        [Tooltip("Assign the Sony SRDisplayManager prefab instance here when the SDK is imported.")]
        public GameObject srDisplayManagerObject;

        public ISonySRDAdapter Adapter { get; private set; } = new NullSRDAdapter();
        public bool SonyActive { get; private set; }

        void Awake()
        {
            Adapter = CreateAdapter();
            Adapter.Initialize();
            SonyActive = Adapter.IsAvailable;
            if (mode == BrainDisplayMode.SonyElfSr2 && !SonyActive)
                Debug.LogWarning("[SonySRD] ELF-SR2 requested but SDK/display unavailable -> normal monitor.");
        }

        ISonySRDAdapter CreateAdapter()
        {
            if (mode != BrainDisplayMode.SonyElfSr2) return new NullSRDAdapter();
#if SONY_SRD_SDK
            var mgr = srDisplayManagerObject != null
                ? srDisplayManagerObject.GetComponent<SRD.Core.SRDManager>() : null;
            if (mgr != null) return new SonySRDAdapter(mgr);
            Debug.LogWarning("[SonySRD] SRDisplayManager not assigned; using normal monitor.");
#endif
            return new NullSRDAdapter();
        }

        void OnDestroy() => Adapter?.Shutdown();
    }
}

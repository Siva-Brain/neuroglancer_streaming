using UnityEngine;
using SRD.Core;

namespace BrainVolume
{
    public enum BrainDisplayMode { NormalMonitor, SonyElfSr2 }

    /// <summary>
    /// Minimal isolation boundary for the Sony Spatial Reality Display. The brain
    /// streaming + rendering code never references Sony types directly -- it only
    /// ever sees ISonySRDAdapter. This lets the app run identically on a normal
    /// monitor (NullSRDAdapter) with the real SDK dropped in later.
    /// </summary>
    public interface ISonySRDAdapter
    {
        bool IsAvailable { get; }
        string DisplayName { get; }
        bool Initialize();
        void UpdateCamera(Camera cam);   // logical viewpoint we also send to the DGX
        void RenderFrame();
        void Shutdown();
    }

    /// <summary>Default: no SRD. Normal-monitor rendering; everything still works.</summary>
    public sealed class NullSRDAdapter : ISonySRDAdapter
    {
        public bool IsAvailable => false;
        public string DisplayName => "Normal Monitor";
        public bool Initialize() => true;
        public void UpdateCamera(Camera cam) { }
        public void RenderFrame() { }
        public void Shutdown() { }
    }

#if SONY_SRD_SDK
    // Enabled by adding the scripting define symbol SONY_SRD_SDK once the official
    // "Spatial Reality Display SDK for Unity" (namespace SRD.Core) is imported.
    //
    // The SDK's SRDisplayManager prefab does the actual spatial rendering: it spawns
    // its own SRDCameras and reproduces the scene for the lightfield panel. Because
    // BrainVolumeRenderer draws per rendering camera (OnRenderObject), the brain
    // volume shows up in those SRD cameras automatically -- so this adapter mostly
    // just verifies the display is present and running. Do NOT hand-roll stereo.
    public sealed class SonySRDAdapter : ISonySRDAdapter
    {
        readonly SRDManager _mgr;
        public SonySRDAdapter(SRDManager mgr) { _mgr = mgr; }

        // SRDManager has no IsRunning. It deactivates its own GameObject in Awake when
        // no SRD session/runtime is available, and creates Presence once a session exists.
        public bool IsAvailable => _mgr != null && _mgr.isActiveAndEnabled && _mgr.Presence != null;
        public string DisplayName => "Sony Spatial Reality Display (ELF-SR2)";
        public bool Initialize() { return _mgr != null; }
        public void UpdateCamera(Camera cam) { /* SRDManager tracks the source camera itself */ }
        public void RenderFrame() { /* SDK renders on its own update loop */ }
        public void Shutdown() { }
    }
#endif
}

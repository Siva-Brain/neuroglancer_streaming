using System.Text;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>On-screen diagnostics overlay (IMGUI, zero setup).</summary>
    public sealed class BrainTelemetry : MonoBehaviour
    {
        public BrainApp app;
        public SonySRDManager sony;
        GUIStyle _style; readonly StringBuilder _sb = new StringBuilder(512);

        void OnGUI()
        {
            if (app == null) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = false };

            app.CurrentLodRange(out int lodMin, out int lodMax);
            string conn = app.ConnectionOk
                ? "<color=#39d98a>CONNECTED</color>" : "<color=#ff5c5c>DISCONNECTED</color>";
            string sonyStr = (sony != null && sony.SonyActive)
                ? "<color=#39d98a>AVAILABLE</color>" : "<color=#f5c451>NOT AVAILABLE</color>";

            _sb.Clear();
            _sb.AppendLine("<b>DGX BRAIN VOLUME — UNITY</b>");
            _sb.AppendLine($"DGX connection : {conn}");
            _sb.AppendLine($"Server         : {app.serverUrl}");
            _sb.AppendLine($"GPU            : {SystemInfo.graphicsDeviceName}");
            _sb.AppendLine($"FPS            : {Mathf.RoundToInt(app.Fps)}");
            _sb.AppendLine($"Frame time     : {app.FrameMs:F1} ms");
            _sb.AppendLine($"Blocks         : {app.BlockCount}");
            _sb.AppendLine($"Chunks req     : {app.ChunksRequested}");
            _sb.AppendLine($"Chunks recv    : {app.ChunksReceived}");
            _sb.AppendLine($"Bricks loaded  : {app.BricksLoaded}");
            _sb.AppendLine($"Cache size     : {FmtBytes(app.CacheBytes)} (evict {app.Evictions})");
            _sb.AppendLine($"In-flight/pend : {app.InFlight} / {app.Pending}");
            _sb.AppendLine($"Target LOD     : {(app.TargetLevel >= 0 ? "L" + app.TargetLevel : "-")}");
            _sb.AppendLine($"Loaded LOD     : {(lodMin >= 0 ? $"L{lodMin}–L{lodMax}" : "-")}");
            _sb.AppendLine($"Bandwidth      : {app.MbPerSec:F2} MB/s");
            _sb.AppendLine($"Stream latency : {app.LatencyMs:F0} ms");
            _sb.Append($"Sony SRD       : {sonyStr}");

            GUI.Box(new Rect(8, 8, 300, 320), GUIContent.none);
            GUI.Label(new Rect(18, 14, 300, 320), _sb.ToString(), _style);
        }

        static string FmtBytes(long n)
            => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024f:F1} KB" : $"{n / 1048576f:F1} MB";
    }
}

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace BrainStreaming
{
    /// <summary>On-screen stats overlay (IMGUI, zero setup). Matches Phase 7.</summary>
    public sealed class BrainHud : MonoBehaviour
    {
        private BrainStreamingApp _app;
        private BrainRenderer _rend;
        private HashSet<string> _visible = new HashSet<string>();
        private float _fps;
        private GUIStyle _style;

        public void Refresh(BrainStreamingApp app, BrainRenderer rend, HashSet<string> visible)
        {
            _app = app; _rend = rend; _visible = visible;
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.1f);
        }

        void OnGUI()
        {
            if (_app == null) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };

            var lods = _rend.AllLods.ToList();
            int brainLod = lods.Count > 0 ? lods.Min() : -1;
            var visLods = _visible.Select(id => _rend.CurrentLod(id)).Where(l => l >= 0).ToList();
            int visLod = visLods.Count > 0 ? visLods.Max() : -1;

            string conn = _app.Connected
                ? "<color=#39d98a>CONNECTED</color>" : "<color=#ff5c5c>OFFLINE</color>";

            var s = "<b>DGX BRAIN STREAM</b>\n" +
                    $"Connection : {conn}\n" +
                    $"Server     : {_app.ServerUrl}\n" +
                    $"Chunks recv: {_app.Cache.Count}\n" +
                    $"Chunks cach: {_app.Cache.Count}\n" +
                    $"Bytes recv : {FmtBytes(_app.Cache.BytesReceived)}\n" +
                    $"Brain LOD  : {(brainLod >= 0 ? "LOD" + brainLod : "-")}\n" +
                    $"Visible LOD: {(visLod >= 0 ? "LOD" + visLod : "-")}\n" +
                    $"Active req : {_app.ActiveRequests}\n" +
                    $"Pending req: {_app.PendingRequests}\n" +
                    $"Cache hits : {_app.CacheHits}\n" +
                    $"FPS        : {Mathf.RoundToInt(_fps)}";

            GUI.Box(new Rect(10, 10, 260, 210), GUIContent.none);
            GUI.Label(new Rect(22, 20, 240, 200), s, _style);
        }

        private static string FmtBytes(long n)
            => n < 1024 ? $"{n} B" : n < 1048576 ? $"{n / 1024f:F1} KB" : $"{n / 1048576f:F2} MB";
    }
}

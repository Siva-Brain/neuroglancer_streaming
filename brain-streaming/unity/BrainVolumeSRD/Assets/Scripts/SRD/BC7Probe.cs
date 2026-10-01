using System.Text;
using UnityEngine;

namespace BrainVolume.SRD
{
    /// <summary>
    /// P0 capability probe for the offline-brick plan (docs/brain2-srd-bricking-plan.md).
    /// Answers the gating question: does a BC7 Texture3D actually create + upload on
    /// THIS SRD box, and what are the usable 3D-texture dimensions?
    ///
    /// Drop on any GameObject in a scene and press Play. Reads the results from the
    /// Console and the on-screen overlay. No data files needed.
    ///
    /// If BC7 Texture3D works -> the bricker targets BC7 (1 B/voxel, zero decode).
    /// If not -> coarse levels fall back to RGB24 raw; L0 needs another path.
    /// </summary>
    public sealed class BC7Probe : MonoBehaviour
    {
        string _report = "(probing…)";

        void Start()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"GPU: {SystemInfo.graphicsDeviceName}");
            sb.AppendLine($"API: {SystemInfo.graphicsDeviceType}   VRAM: {SystemInfo.graphicsMemorySize} MB");
            sb.AppendLine($"maxTextureSize(2D): {SystemInfo.maxTextureSize}   maxTexture3DSize: {SystemInfo.maxTexture3DSize}");
            sb.AppendLine($"SupportsFormat BC7:    {SystemInfo.SupportsTextureFormat(TextureFormat.BC7)}");
            sb.AppendLine($"SupportsFormat RGBA32: {SystemInfo.SupportsTextureFormat(TextureFormat.RGBA32)}");
            sb.AppendLine($"SupportsFormat RGB24:  {SystemInfo.SupportsTextureFormat(TextureFormat.RGB24)}");
            sb.AppendLine();

            // XY dimension sweep (small depth so we test the cap, not allocate GBs).
            sb.AppendLine("XY sweep (N x N x 16):");
            foreach (int n in new[] { 256, 512, 1024, 2048 })
            {
                sb.AppendLine($"  {n,4}: BC7 {Tag(TryCreate(n, n, 16, TextureFormat.BC7))}" +
                              $"   RGBA32 {Tag(TryCreate(n, n, 16, TextureFormat.RGBA32))}");
            }
            // Depth sweep (small XY). 485 is Brain-2's native z at every level.
            sb.AppendLine("Z sweep (32 x 32 x D):");
            foreach (int d in new[] { 485, 1024, 2048 })
            {
                sb.AppendLine($"  {d,4}: BC7 {Tag(TryCreate(32, 32, d, TextureFormat.BC7))}" +
                              $"   RGBA32 {Tag(TryCreate(32, 32, d, TextureFormat.RGBA32))}");
            }
            // A realistic BC7 brick (1024 x 1024 x 485) — the planned L3/L0 brick shape.
            sb.AppendLine();
            sb.AppendLine($"BC7 brick 1024x1024x485: {Tag(TryCreate(1024, 1024, 485, TextureFormat.BC7))}");

            _report = sb.ToString();
            Debug.Log("[BC7Probe]\n" + _report);
        }

        static string Tag(bool ok) => ok ? "OK " : "FAIL";

        /// <summary>Try to allocate + upload a Texture3D of the given size/format.
        /// Returns false if construction or upload throws. (Driver-level sampling
        /// failures still print to the Console; check it alongside this result.)</summary>
        static bool TryCreate(int w, int h, int d, TextureFormat fmt)
        {
            Texture3D tex = null;
            try
            {
                tex = new Texture3D(w, h, d, fmt, false);
                int n = ByteCount(w, h, d, fmt);
                var data = new byte[n];                 // zeroed dummy payload
                tex.SetPixelData(data, 0);
                tex.Apply(false, true);                 // upload + make unreadable
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BC7Probe] {w}x{h}x{d} {fmt}: {e.GetType().Name} {e.Message}");
                return false;
            }
            finally { if (tex != null) Destroy(tex); }
        }

        static int ByteCount(int w, int h, int d, TextureFormat fmt)
        {
            if (fmt == TextureFormat.BC7)               // 16 bytes / 4x4 block, per slice
                return ((w + 3) / 4) * ((h + 3) / 4) * 16 * d;
            if (fmt == TextureFormat.RGBA32) return w * h * d * 4;
            if (fmt == TextureFormat.RGB24) return w * h * d * 3;
            return w * h * d * 4;
        }

        void OnGUI()
        {
            GUI.Box(new Rect(8, 8, 560, 320), "BC7 / Texture3D probe");
            GUI.Label(new Rect(18, 30, 544, 290), _report);
        }
    }
}

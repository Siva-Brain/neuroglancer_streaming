using System.IO;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Loads a local RGB fused volume (one OME-Zarr level exported by
    /// tools/zarr_level_to_raw.py) from StreamingAssets and ray-marches it with
    /// Brain/FusedRaymarch. No server, no streaming -- one Texture3D drawn
    /// procedurally in OnRenderObject, so it shows up in the SRD's cameras like
    /// the streamed brain. Same approach as T1VolumeLoader, but 3-channel colour.
    ///
    /// Expects <folder>/<name>.json + <name>.raw. The .raw is width*height*depth*3
    /// bytes, x fastest, then y, then z, RGB interleaved -- i.e. a C-order
    /// (z, y, x, c) uint8 array straight from the zarr, so NO reordering here.
    ///
    /// The fused data is brightfield-style: WHITE background, near-white tissue,
    /// BLACK = no data. The shader turns that into see-through glass (density =
    /// 1 - max(rgb); black and white are both transparent).
    ///
    /// The volume is centred on this transform, so ModelMoveController on the same
    /// GameObject rotates and zooms it about its centre.
    /// </summary>
    public sealed class FusedVolumeLoader : MonoBehaviour
    {
        [Header("Files (under Assets/StreamingAssets)")]
        public string folder = "Fused";
        public string baseName = "hb02_L7";

        [Header("Placement")]
        [Tooltip("Unity units per millimetre. 0.0025 -> a ~180 mm brain is ~0.45 units, fits the SRD box.")]
        public float unitsPerMm = 0.0025f;

        [Header("Rendering (RGB see-through glass)")]
        public Material material;                   // Brain/FusedRaymarch
        [Range(16, 320)] public int steps = 160;
        [Tooltip("Per-sample opacity. Lower = more see-through (near-white tissue is faint).")]
        [Range(0.02f, 3f)] public float density = 0.3f;
        [Tooltip("Density floor: 1-max(rgb) below this is near-white haze and is skipped.")]
        [Range(0f, 0.5f)] public float windowLow = 0.05f;
        [Range(0.05f, 1f)] public float windowHigh = 0.5f;
        [Tooltip("Max(rgb) below this is treated as empty/no-data (black) and skipped.")]
        [Range(0f, 0.2f)] public float emptyCut = 0.04f;
        public bool flipX, flipY, flipZ;

        public Texture3D Texture { get; private set; }
        public Vector3 SizeMm { get; private set; }     // (x, y, z) mm
        public bool Loaded => Texture != null;

        Mesh _cube;
        Matrix4x4 _local;                           // unit cube -> this transform's local space

        [System.Serializable]
        class Meta
        {
            public int width, height, depth, channels;
            public string format;
            public float[] voxelSizeMM;             // (x, y, z)
            public float[] sizeMM;                  // (x, y, z) extent
            public int level;
        }

        void Start()
        {
            _cube = BuildUnitCube();
            if (material == null)
            {
                var sh = Shader.Find("Brain/FusedRaymarch");
                if (sh != null) material = new Material(sh);
                else { Debug.LogError("[Fused] Shader 'Brain/FusedRaymarch' not found."); return; }
            }
            Load();
        }

        void Load()
        {
            string dir = Path.Combine(Application.streamingAssetsPath, folder);
            string jsonPath = Path.Combine(dir, baseName + ".json");
            string rawPath = Path.Combine(dir, baseName + ".raw");
            if (!File.Exists(jsonPath) || !File.Exists(rawPath))
            {
                Debug.LogError("[Fused] Missing " + jsonPath + " or " + rawPath);
                return;
            }

            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(jsonPath));
            if (meta.channels != 3 || meta.format != "RGB24")
            {
                Debug.LogError($"[Fused] Expected 3-channel RGB24 (got {meta.channels} x {meta.format}).");
                return;
            }
            int w = meta.width, h = meta.height, d = meta.depth;
            byte[] raw = File.ReadAllBytes(rawPath);
            long expect = (long)w * h * d * 3;
            if (raw.LongLength != expect)
            {
                Debug.LogError($"[Fused] Raw size {raw.LongLength} != {w}x{h}x{d}x3 = {expect}.");
                return;
            }

            if (meta.sizeMM != null && meta.sizeMM.Length == 3)
                SizeMm = new Vector3(meta.sizeMM[0], meta.sizeMM[1], meta.sizeMM[2]);
            else if (meta.voxelSizeMM != null && meta.voxelSizeMM.Length == 3)
                SizeMm = new Vector3(w * meta.voxelSizeMM[0], h * meta.voxelSizeMM[1], d * meta.voxelSizeMM[2]);
            else
                SizeMm = new Vector3(w, h, d);

            // Raw is already Unity-order (x fastest, y, z), RGB interleaved -> upload directly.
            Texture = new Texture3D(w, h, d, TextureFormat.RGB24, false)
            {
                name = "FusedVolume",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,   // trilinear for 3D
                anisoLevel = 0,
            };
            Texture.SetPixelData(raw, 0);
            Texture.Apply(false, true);

            Vector3 size = SizeMm * unitsPerMm;
            _local = Matrix4x4.TRS(-size * 0.5f, Quaternion.identity, size);  // centred on transform
            Debug.Log($"[Fused] Loaded L{meta.level} {w}x{h}x{d} RGB, " +
                      $"{SizeMm.x:F0}x{SizeMm.y:F0}x{SizeMm.z:F0} mm -> " +
                      $"{size.x:F3}x{size.y:F3}x{size.z:F3} units.");
        }

        void OnRenderObject()
        {
            if (Texture == null || material == null || _cube == null) return;
            if (Camera.current == null) return;

            material.SetTexture("_VolumeTex", Texture);
            material.SetFloat("_Steps", steps);
            material.SetFloat("_Density", density);
            material.SetFloat("_Low", windowLow);
            material.SetFloat("_High", windowHigh);
            material.SetFloat("_EmptyCut", emptyCut);
            material.SetVector("_Flip", new Vector4(flipX ? 1 : 0, flipY ? 1 : 0, flipZ ? 1 : 0, 0));
            material.SetPass(0);
            Graphics.DrawMeshNow(_cube, transform.localToWorldMatrix * _local);
        }

        void OnDestroy()
        {
            if (Texture != null) Destroy(Texture);
        }

        static Mesh BuildUnitCube()
        {
            var v = new Vector3[]{
                new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0),
                new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)};
            var t = new int[]{
                0,2,1, 0,3,2,   4,5,6, 4,6,7,
                0,1,5, 0,5,4,   2,3,7, 2,7,6,
                0,4,7, 0,7,3,   1,2,6, 1,6,5};
            var m = new Mesh { name = "FusedUnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

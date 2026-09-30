using System.IO;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Loads a local single-channel (R8) MRI volume from StreamingAssets and
    /// ray-marches it with Brain/T1Raymarch. Independent of the DGX streaming
    /// path: no cache, no network -- one Texture3D drawn procedurally in
    /// OnRenderObject, so it shows up in the SRD's cameras like the streamed brain.
    ///
    /// Expects <folder>/<name>.json (exported alongside the .raw) with at least
    /// width/height/depth, originalDimensions, originalVoxelSpacingMM. The raw
    /// file is width*height*depth bytes, x fastest, then y, then z.
    ///
    /// nifti_grayscale_to_raw.py already writes the raw in Unity order (x fastest,
    /// then y, then z) with its own axis convention (W = NIfTI i, H = flipped j,
    /// D = k). For t1_mri.nii.gz the NIfTI axes are (superior, posterior, right),
    /// so the raw axes come out as x = superior, y = anterior, z = right. The
    /// remap below turns that into Unity x = right, y = up, z = anterior; the
    /// T1Volume object is rotated 180 deg about Y in the scene so the face looks
    /// at the viewer. Use flipX/Y/Z if a different scan needs mirroring.
    ///
    /// The volume is centred on this transform, so ModelMoveController on the
    /// same GameObject rotates and zooms it about its centre.
    /// </summary>
    public sealed class T1VolumeLoader : MonoBehaviour
    {
        [Header("Files (under Assets/StreamingAssets)")]
        public string folder = "T1";
        public string baseName = "t1_mri";

        [Header("Placement")]
        [Tooltip("Unity units per millimetre. 0.0025 -> a 250 mm head is 0.625 units, which fits the SRD box at view-space scale 3.")]
        public float unitsPerMm = 0.0025f;

        [Header("Rendering")]
        public Material material;               // Brain/T1Raymarch
        [Range(16, 256)] public int steps = 128;
        [Tooltip("Per-sample opacity multiplier. 1 = solid tissue look; lower for see-through.")]
        [Range(0.05f, 5f)] public float density = 1f;
        [Tooltip("Intensity below this is air/background and is skipped.")]
        [Range(0f, 1f)] public float windowLow = 0.1f;
        [Range(0f, 1f)] public float windowHigh = 1f;
        public bool flipX, flipY, flipZ;

        public Texture3D Texture { get; private set; }
        public Vector3 SizeMm { get; private set; }     // (x,y,z) after axis remap
        public bool Loaded => Texture != null;

        Mesh _cube;
        Matrix4x4 _local;                       // unit cube -> this transform's local space

        [System.Serializable]
        class Meta
        {
            public int width, height, depth, channels;
            public string format;
            public float downsampleScale = 1f;
            public int[] originalDimensions;
            public float[] originalVoxelSpacingMM;
        }

        void Start()
        {
            _cube = BuildUnitCube();
            if (material == null)
            {
                var sh = Shader.Find("Brain/T1Raymarch");
                if (sh != null) material = new Material(sh);
                else { Debug.LogError("[T1] Shader 'Brain/T1Raymarch' not found."); return; }
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
                Debug.LogError("[T1] Missing " + jsonPath + " or " + rawPath);
                return;
            }

            var meta = JsonUtility.FromJson<Meta>(File.ReadAllText(jsonPath));
            if (meta.channels != 1 || meta.format != "R8")
            {
                Debug.LogError($"[T1] Only 1-channel R8 volumes are supported (got {meta.channels} x {meta.format}).");
                return;
            }
            int w = meta.width, h = meta.height, d = meta.depth;
            byte[] raw = File.ReadAllBytes(rawPath);
            if (raw.Length != w * h * d)
            {
                Debug.LogError($"[T1] Raw size {raw.Length} != {w}x{h}x{d} = {w * h * d}.");
                return;
            }

            // physical extent of each source axis (mm) = original count * original spacing
            Vector3 srcMm = new Vector3(w, h, d) * 0.65f;   // fallback if the JSON lacks the fields
            if (meta.originalDimensions != null && meta.originalVoxelSpacingMM != null &&
                meta.originalDimensions.Length == 3 && meta.originalVoxelSpacingMM.Length == 3)
            {
                srcMm = new Vector3(meta.originalDimensions[0] * meta.originalVoxelSpacingMM[0],
                                    meta.originalDimensions[1] * meta.originalVoxelSpacingMM[1],
                                    meta.originalDimensions[2] * meta.originalVoxelSpacingMM[2]);
            }

            // Raw (x,y,z) = (superior, anterior, right)  ->  Unity (x,y,z) = (raw z, raw x, raw y).
            // Unity Texture3D is x fastest, then y, then z.
            int ux = d, uy = w, uz = h;
            var vox = new byte[raw.Length];
            for (int j = 0; j < h; j++)
            {
                int uzOff = j * ux * uy;
                for (int i = 0; i < w; i++)
                {
                    int uyOff = uzOff + i * ux;
                    int srcOff = i + j * w;                  // + k*w*h
                    for (int k = 0; k < d; k++)
                        vox[uyOff + k] = raw[srcOff + k * w * h];
                }
            }
            SizeMm = new Vector3(srcMm.z, srcMm.x, srcMm.y);

            Texture = new Texture3D(ux, uy, uz, TextureFormat.R8, false)
            {
                name = "T1Volume",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            Texture.SetPixelData(vox, 0);
            Texture.Apply(false, true);

            Vector3 size = SizeMm * unitsPerMm;
            _local = Matrix4x4.TRS(-size * 0.5f, Quaternion.identity, size);   // centred on the transform
            Debug.Log($"[T1] Loaded {ux}x{uy}x{uz}, {SizeMm.x:F0}x{SizeMm.y:F0}x{SizeMm.z:F0} mm -> {size.x:F3}x{size.y:F3}x{size.z:F3} units.");
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
            var m = new Mesh { name = "T1UnitCube" };
            m.vertices = v; m.triangles = t; m.RecalculateBounds();
            return m;
        }
    }
}

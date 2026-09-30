using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// GPU resource for ONE brick: a Texture3D (RG16 = 2x uint8: grayscale + mask)
    /// plus its unit-cube -> brain-local matrix. This is NOT a GameObject -- the
    /// BrainVolumeRenderer draws all bricks procedurally in one pass, so there is
    /// no scene-object-per-chunk explosion.
    ///
    /// Voxel order in BVX2 is (z,y,x,c) == Unity Texture3D (depth=z, height=y,
    /// width=x, channels=c), so the raw bytes upload directly with no reshuffle.
    /// </summary>
    public sealed class Brick
    {
        public int ZSlab;
        public int Level;
        public Texture3D Texture;
        public Matrix4x4 LocalMatrix;   // unit cube -> brain-local (pre BrainRoot)
        public Vector3 CenterLocal;     // for back-to-front sorting (brain-local)
        public int VoxelBytes;

        public static Brick Create(BrainChunk c, BrainCoordinateSystem coords, Matrix4x4 worldMatrix)
        {
            var tex = new Texture3D(c.Dx, c.Dy, c.Dz, TextureFormat.RG16, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            // BVX2 voxels are (z,y,x,c) with c fastest == Unity's (x + y*w + z*w*h)
            // with 2 bytes/texel. Direct upload.
            tex.SetPixelData(c.Voxels, 0);
            tex.Apply(false, true);      // no mips; makeNoLongerReadable -> free CPU copy

            // unit cube -> block-local mm -> (block omeToRas) RAS mm -> Unity units
            var m = coords.MmToUnityMatrix() * worldMatrix * coords.BrickMmMatrix(c.BBoxMinMm, c.BBoxMaxMm);
            Vector3 center = m.MultiplyPoint3x4(new Vector3(0.5f, 0.5f, 0.5f));
            return new Brick
            {
                ZSlab = c.ZSlab, Level = c.Level, Texture = tex,
                LocalMatrix = m, CenterLocal = center, VoxelBytes = c.Voxels.Length,
            };
        }

        public void Dispose()
        {
            if (Texture != null) { Object.Destroy(Texture); Texture = null; }
        }
    }
}

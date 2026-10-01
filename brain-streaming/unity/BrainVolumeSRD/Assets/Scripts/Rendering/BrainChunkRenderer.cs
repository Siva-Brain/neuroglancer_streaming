using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// GPU resource for ONE brick: a Texture3D whose format follows the channel
    /// count (RGB24 fused / RG16 Nissl gray+mask / R8 single) plus its unit-cube ->
    /// brain-local matrix. This is NOT a GameObject -- the
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
            // format follows the channel count: RGB fused (3) -> RGB24, Nissl
            // gray+mask (2) -> RG16, single channel (1) -> R8. BVX2 voxel order
            // (z,y,x,c) with c fastest == Unity's packed texel layout, so the raw
            // bytes upload directly with no reshuffle for any of these.
            var fmt = c.Nch >= 3 ? TextureFormat.RGB24
                    : c.Nch == 1 ? TextureFormat.R8
                                 : TextureFormat.RG16;
            var tex = new Texture3D(c.Dx, c.Dy, c.Dz, fmt, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
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

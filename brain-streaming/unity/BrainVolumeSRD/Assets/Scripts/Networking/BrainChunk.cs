using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// One decoded volume brick (a Zarr z-slab at one LOD), parsed from a BVX2
    /// payload. Holds raw voxels (CPU) + identity + the true world bounding box
    /// in DGX millimetres. Rendering turns this into a Texture3D; networking
    /// never touches Unity GPU types.
    ///
    /// BVX2 (little-endian, 50-byte header):
    ///   'BVX2' | u8 level | u8 nch | u16 pad | i32 z0,y0,x0
    ///          | u16 dz,dy,dx | f32[6] bbox_mm(xmin,ymin,zmin,xmax,ymax,zmax)
    ///          | u8[dz*dy*dx*nch] voxels (z,y,x,c order)
    /// </summary>
    public sealed class BrainChunk
    {
        public string ChunkId;          // "L{level}.{z}.{y}.{x}.{c}"
        public int Level;
        public int Z, Y, X, C;          // shard grid coords
        public int Dz, Dy, Dx, Nch;     // sent dims (may be GPU-downsampled)
        public Vector3 BBoxMinMm;       // world (x,y,z) mm
        public Vector3 BBoxMaxMm;
        public byte[] Voxels;           // dz*dy*dx*nch, order (z,y,x,c)
        public int SizeBytes;           // total payload size

        public int ZSlab => Z;          // brick identity for the 58-slab volume

        public static bool TryParse(string chunkId, byte[] buf, out BrainChunk chunk)
        {
            chunk = null;
            if (buf == null || buf.Length < 50) return false;
            if (buf[0] != (byte)'B' || buf[1] != (byte)'V' || buf[2] != (byte)'X' || buf[3] != (byte)'2')
                return false;

            int level = buf[4], nch = buf[5];
            int z0 = System.BitConverter.ToInt32(buf, 8);
            int y0 = System.BitConverter.ToInt32(buf, 12);
            int x0 = System.BitConverter.ToInt32(buf, 16);
            int dz = System.BitConverter.ToUInt16(buf, 20);
            int dy = System.BitConverter.ToUInt16(buf, 22);
            int dx = System.BitConverter.ToUInt16(buf, 24);
            float xmin = System.BitConverter.ToSingle(buf, 26);
            float ymin = System.BitConverter.ToSingle(buf, 30);
            float zmin = System.BitConverter.ToSingle(buf, 34);
            float xmax = System.BitConverter.ToSingle(buf, 38);
            float ymax = System.BitConverter.ToSingle(buf, 42);
            float zmax = System.BitConverter.ToSingle(buf, 46);

            int need = dz * dy * dx * nch;
            if (buf.Length - 50 < need) return false;
            var vox = new byte[need];
            System.Buffer.BlockCopy(buf, 50, vox, 0, need);

            var parts = chunkId.Replace("L", "").Split('.');
            chunk = new BrainChunk
            {
                ChunkId = chunkId, Level = level,
                Z = int.Parse(parts[1]), Y = int.Parse(parts[2]),
                X = int.Parse(parts[3]), C = int.Parse(parts[4]),
                Dz = dz, Dy = dy, Dx = dx, Nch = nch,
                BBoxMinMm = new Vector3(xmin, ymin, zmin),
                BBoxMaxMm = new Vector3(xmax, ymax, zmax),
                Voxels = vox, SizeBytes = buf.Length,
            };
            return true;
        }
    }
}

using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Parsed CPU payload of ONE merged block volume (BVX3): the whole block
    /// assembled, tissue-cropped, and colour+opacity-baked into RGBA by the DGX,
    /// plus a tiny occupancy volume for empty-space skipping. Replaces the ~58
    /// per-slab BVX2 bricks with a single volume -- one draw per block.
    ///
    /// BVX3 (little-endian):
    ///   'BVX3' | u8 level | u8 nch(=4) | u16 pad | i32 z0,y0,x0
    ///          | u16 dz,dy,dx | f32[6] bbox_mm(xmin,ymin,zmin,xmax,ymax,zmax)
    ///          | u8[dz*dy*dx*4] rgba (z,y,x,c order)
    ///          | u16 oz,oy,ox | u8 occ_block | u8 pad
    ///          | u8[oz*oy*ox] occupancy
    /// </summary>
    public sealed class BlockVolumeData
    {
        public int Level;
        public int Dz, Dy, Dx;
        public Vector3 BBoxMinMm, BBoxMaxMm;   // block-local mm (offset for the crop)
        public byte[] Rgba;                    // dz*dy*dx*4, order (z,y,x,c)
        public int Oz, Oy, Ox, OccBlock;
        public byte[] Occ;                     // oz*oy*ox

        public static bool TryParse(byte[] buf, out BlockVolumeData vol)
        {
            vol = null;
            if (buf == null || buf.Length < 58) return false;
            if (buf[0] != (byte)'B' || buf[1] != (byte)'V' || buf[2] != (byte)'X' || buf[3] != (byte)'3')
                return false;

            int level = buf[4], nch = buf[5];
            if (nch != 4) return false;
            int dz = System.BitConverter.ToUInt16(buf, 20);
            int dy = System.BitConverter.ToUInt16(buf, 22);
            int dx = System.BitConverter.ToUInt16(buf, 24);
            float xmin = System.BitConverter.ToSingle(buf, 26);
            float ymin = System.BitConverter.ToSingle(buf, 30);
            float zmin = System.BitConverter.ToSingle(buf, 34);
            float xmax = System.BitConverter.ToSingle(buf, 38);
            float ymax = System.BitConverter.ToSingle(buf, 42);
            float zmax = System.BitConverter.ToSingle(buf, 46);

            long voxN = (long)dz * dy * dx * 4;
            long occHdr = 50 + voxN;
            if (buf.Length < occHdr + 8) return false;
            var rgba = new byte[voxN];
            System.Buffer.BlockCopy(buf, 50, rgba, 0, (int)voxN);

            int oz = System.BitConverter.ToUInt16(buf, (int)occHdr);
            int oy = System.BitConverter.ToUInt16(buf, (int)occHdr + 2);
            int ox = System.BitConverter.ToUInt16(buf, (int)occHdr + 4);
            int ob = buf[occHdr + 6];
            long occN = (long)oz * oy * ox;
            long occOff = occHdr + 8;
            if (buf.Length < occOff + occN) return false;
            var occ = new byte[occN];
            System.Buffer.BlockCopy(buf, (int)occOff, occ, 0, (int)occN);

            vol = new BlockVolumeData
            {
                Level = level, Dz = dz, Dy = dy, Dx = dx,
                BBoxMinMm = new Vector3(xmin, ymin, zmin),
                BBoxMaxMm = new Vector3(xmax, ymax, zmax),
                Rgba = rgba, Oz = oz, Oy = oy, Ox = ox, OccBlock = ob, Occ = occ,
            };
            return true;
        }
    }

    /// <summary>GPU resource for one block volume: RGBA Texture3D + occupancy
    /// Texture3D + its unit-cube -> brain-local matrix. Not a GameObject.</summary>
    public sealed class BlockVolume
    {
        public int Level;
        public Texture3D VolumeTex;    // RGBA32: baked colour(rgb) + opacity(a)
        public Texture3D OccTex;       // R8: max opacity per occ_block^3 cell
        public Vector3 OccScale;       // volume [0,1] -> occupancy [0,1] (padding fix)
        public Matrix4x4 LocalMatrix;  // unit cube -> brain-local (pre BrainRoot)
        public Vector3 CenterLocal;
        public long Bytes;

        public static BlockVolume Create(BlockVolumeData d, BrainCoordinateSystem coords, Matrix4x4 worldMatrix)
        {
            var tex = new Texture3D(d.Dx, d.Dy, d.Dz, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 0,
            };
            tex.SetPixelData(d.Rgba, 0);
            tex.Apply(false, true);

            var occ = new Texture3D(d.Ox, d.Oy, d.Oz, TextureFormat.R8, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                anisoLevel = 0,
            };
            occ.SetPixelData(d.Occ, 0);
            occ.Apply(false, true);

            var m = coords.MmToUnityMatrix() * worldMatrix * coords.BrickMmMatrix(d.BBoxMinMm, d.BBoxMaxMm);
            Vector3 center = m.MultiplyPoint3x4(new Vector3(0.5f, 0.5f, 0.5f));
            // occ grid is padded up to a multiple of occ_block; align sampling.
            Vector3 occScale = new Vector3(
                d.Ox > 0 ? (float)d.Dx / (d.Ox * d.OccBlock) : 1f,
                d.Oy > 0 ? (float)d.Dy / (d.Oy * d.OccBlock) : 1f,
                d.Oz > 0 ? (float)d.Dz / (d.Oz * d.OccBlock) : 1f);

            return new BlockVolume
            {
                Level = d.Level, VolumeTex = tex, OccTex = occ, OccScale = occScale,
                LocalMatrix = m, CenterLocal = center,
                Bytes = (long)d.Rgba.Length + (d.Occ != null ? d.Occ.Length : 0),
            };
        }

        public void Dispose()
        {
            if (VolumeTex != null) { Object.Destroy(VolumeTex); VolumeTex = null; }
            if (OccTex != null) { Object.Destroy(OccTex); OccTex = null; }
        }
    }
}

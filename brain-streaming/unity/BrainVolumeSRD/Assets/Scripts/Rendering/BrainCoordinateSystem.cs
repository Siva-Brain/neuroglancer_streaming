using System;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>Camera info the DGX /view endpoint needs, in ONE block's local millimetres.</summary>
    public struct CameraState
    {
        public Vector3 PositionMm;   // block-local (x,y,z) mm, block centred at its origin
        public Vector3 ForwardMm;    // unit direction in that block's local frame
        public Quaternion Rotation;  // Unity camera rotation (passed through)
        public float Fov;
        public int ViewportW, ViewportH;
    }

    /// <summary>
    /// The ONE explicit transform between DGX/Zarr space and Unity space, for the
    /// MULTI-BLOCK brain. Pipeline (matches the browser volume viewer):
    ///
    ///   unit cube --BrickMmMatrix--> block-local mm  (BVX2 bbox, block centred)
    ///             --WorldMatrix----> shared RAS mm    (per-block omeToRas, /api/transforms)
    ///             --MmToUnityMatrix-> Unity units     (uniform scale + optional flips)
    ///             --BrainRoot------> Unity world
    ///
    /// So all 5 blocks reassemble into one brain in RAS mm before the single
    /// uniform mm->Unity scale. Axis sign flips MIRROR the whole assembled brain
    /// (use only to fix L/R against landmarks); orientation is better done by
    /// rotating BrainRoot, which never mirrors.
    /// </summary>
    [Serializable]
    public class BrainCoordinateSystem
    {
        [Tooltip("Unity units per DGX millimetre. 0.01 -> a ~200 mm brain = ~2 units.")]
        public float unitsPerMm = 0.01f;

        [Header("Advanced: axis sign flips (mirror! use only to fix L/R vs landmarks)")]
        public bool flipX = false;
        public bool flipY = false;
        public bool flipZ = false;

        Vector3 Sign => new Vector3(flipX ? -1 : 1, flipY ? -1 : 1, flipZ ? -1 : 1);

        /// <summary>RAS mm point -> Unity-local units (uniform scale + sign flips).</summary>
        public Matrix4x4 MmToUnityMatrix()
        {
            var s = Sign;
            return Matrix4x4.Scale(new Vector3(s.x * unitsPerMm, s.y * unitsPerMm, s.z * unitsPerMm));
        }

        /// <summary>Unit cube [0,1]^3 -> a brick's block-local mm box (raw mm, no scale/flip).</summary>
        public Matrix4x4 BrickMmMatrix(Vector3 minMm, Vector3 maxMm)
            => Matrix4x4.TRS(minMm, Quaternion.identity, maxMm - minMm);

        /// <summary>Build a Unity Matrix4x4 from a col-major flat 4x4 (as /api/transforms sends).</summary>
        public static Matrix4x4 FromColMajor(float[] m)
        {
            if (m == null || m.Length < 16) return Matrix4x4.identity;
            var r = new Matrix4x4();
            r.SetColumn(0, new Vector4(m[0], m[1], m[2], m[3]));
            r.SetColumn(1, new Vector4(m[4], m[5], m[6], m[7]));
            r.SetColumn(2, new Vector4(m[8], m[9], m[10], m[11]));
            r.SetColumn(3, new Vector4(m[12], m[13], m[14], m[15]));
            return r;
        }

        /// <summary>
        /// Convert a Unity camera into ONE block's local mm camera state for /view:
        /// Unity world -> BrainRoot-local -> RAS mm -> (worldMatrixInv) block-local mm.
        /// </summary>
        public CameraState ToCameraState(Camera cam, Transform brainRoot, Matrix4x4 worldMatrixInv)
        {
            Matrix4x4 w2l = brainRoot != null ? brainRoot.worldToLocalMatrix : Matrix4x4.identity;
            Vector3 posUnity = w2l.MultiplyPoint3x4(cam.transform.position);
            Vector3 fwdUnity = w2l.MultiplyVector(cam.transform.forward);
            var s = Sign;                                   // 1/±1 == ±1
            // Unity units -> RAS mm (undo the uniform scale + flips)
            Vector3 posRas = new Vector3(posUnity.x * s.x, posUnity.y * s.y, posUnity.z * s.z) / unitsPerMm;
            Vector3 fwdRas = new Vector3(fwdUnity.x * s.x, fwdUnity.y * s.y, fwdUnity.z * s.z);
            // RAS mm -> this block's local mm
            Vector3 posBlk = worldMatrixInv.MultiplyPoint3x4(posRas);
            Vector3 fwdBlk = worldMatrixInv.MultiplyVector(fwdRas).normalized;
            return new CameraState
            {
                PositionMm = posBlk,
                ForwardMm = fwdBlk,
                Rotation = cam.transform.rotation,
                Fov = cam.fieldOfView,
                ViewportW = Screen.width,
                ViewportH = Screen.height,
            };
        }
    }
}

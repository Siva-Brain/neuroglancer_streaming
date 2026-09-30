using System;
using UnityEngine;

namespace BrainVolume
{
    /// <summary>Camera info the DGX /view endpoint needs, already in DGX millimetres.</summary>
    public struct CameraState
    {
        public Vector3 PositionMm;   // world (x,y,z) mm, brain centred at origin
        public Vector3 ForwardMm;    // unit direction in DGX space
        public Quaternion Rotation;  // Unity camera rotation (passed through)
        public float Fov;
        public int ViewportW, ViewportH;
    }

    /// <summary>
    /// The ONE explicit transform between DGX/Zarr space and Unity space.
    ///
    ///   DGX/Zarr voxels --(server, already applied)--> world millimetres, brain
    ///   at origin, data axes (z,y,x) == world (x?,y?,z?)  [see note]  -->
    ///   THIS layer --> Unity metres under a BrainRoot transform.
    ///
    /// The DGX already delivers each brick's world box in mm (BVX2 bbox as
    /// x,y,z). Here we only apply: a uniform scale (unitsPerMm) and OPTIONAL
    /// per-axis sign flips (for left/right correction, validated against
    /// landmarks -- a flip MIRRORS that axis, so leave off unless needed).
    /// Orientation/placement is better done by rotating the BrainRoot transform,
    /// which never mirrors the volume.
    /// </summary>
    [Serializable]
    public class BrainCoordinateSystem
    {
        [Tooltip("Unity units per DGX millimetre. 0.01 -> 192 mm brain = 1.92 units.")]
        public float unitsPerMm = 0.01f;

        [Header("Advanced: axis sign flips (mirror! use only to fix L/R vs landmarks)")]
        public bool flipX = false;
        public bool flipY = false;
        public bool flipZ = false;

        Vector3 Sign => new Vector3(flipX ? -1 : 1, flipY ? -1 : 1, flipZ ? -1 : 1);

        public Vector3 MmToLocal(Vector3 mm) => Vector3.Scale(mm, Sign) * unitsPerMm;

        public Vector3 LocalToMm(Vector3 local)
        {
            var s = Sign;
            return new Vector3(local.x / (s.x * unitsPerMm),
                               local.y / (s.y * unitsPerMm),
                               local.z / (s.z * unitsPerMm));
        }

        /// <summary>Matrix mapping a unit cube [0,1]^3 to the brick's brain-local box.</summary>
        public Matrix4x4 BrickLocalMatrix(Vector3 minMm, Vector3 maxMm)
        {
            Vector3 a = MmToLocal(minMm), b = MmToLocal(maxMm);
            Vector3 lo = Vector3.Min(a, b), hi = Vector3.Max(a, b);
            return Matrix4x4.TRS(lo, Quaternion.identity, hi - lo);
        }

        /// <summary>Convert a Unity camera into the DGX camera state (mm) for /view.</summary>
        public CameraState ToCameraState(Camera cam, Transform brainRoot)
        {
            Matrix4x4 w2l = brainRoot != null ? brainRoot.worldToLocalMatrix : Matrix4x4.identity;
            Vector3 posLocal = w2l.MultiplyPoint3x4(cam.transform.position);
            Vector3 fwdLocal = w2l.MultiplyVector(cam.transform.forward);
            var s = Sign;
            var fwdMm = new Vector3(fwdLocal.x / s.x, fwdLocal.y / s.y, fwdLocal.z / s.z).normalized;
            return new CameraState
            {
                PositionMm = LocalToMm(posLocal),
                ForwardMm = fwdMm,
                Rotation = cam.transform.rotation,
                Fov = cam.fieldOfView,
                ViewportW = Screen.width,
                ViewportH = Screen.height,
            };
        }

        /// <summary>Extent (mm, as z,y,x from DGX) -> a good starting camera distance in Unity units.</summary>
        public float SuggestedDistance(float[] extentMm)
        {
            float maxMm = extentMm != null && extentMm.Length == 3
                ? Mathf.Max(extentMm[0], extentMm[1], extentMm[2]) : 192f;
            return maxMm * unitsPerMm * 1.6f;
        }
    }
}

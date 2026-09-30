using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// One physical brain slab: its metadata, its placement into shared RAS mm
    /// (omeToRas world matrix), and its own cache + scheduler. BrainApp holds a
    /// list of these; the renderer composites every block's bricks into one brain.
    /// Each block streams independently but they all live in the same RAS space, so
    /// their bricks line up.
    /// </summary>
    public sealed class BrainBlock
    {
        public string Id;
        public BlockInfo Info;
        public Matrix4x4 WorldMatrix;      // block-local mm -> shared RAS mm
        public Matrix4x4 WorldMatrixInv;   // RAS mm -> block-local mm (for /view camera)
        public BrainChunkCache Cache;
        public RequestScheduler Scheduler;

        /// <summary>Block-local mm half-extents in (x,y,z) from extent_mm (z,y,x).</summary>
        public Vector3 HalfExtentMm()
        {
            var e = Info != null ? Info.extent_mm : null;
            if (e == null || e.Length != 3) return new Vector3(96f, 96f, 96f);
            return new Vector3(e[2] * 0.5f, e[1] * 0.5f, e[0] * 0.5f);   // (x,y,z)
        }
    }
}

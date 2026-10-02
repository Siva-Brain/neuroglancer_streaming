using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// What a timeline (NeuronalLossSequence) needs from a brain volume, so it can drive either
    /// the single-texture FusedVolumeLoader or the bricked BrickVolumeLoader. Both are centred on
    /// their transform with the same unit cube: x, y, z in 0..1 across the whole volume (texture
    /// order: x = image columns, y = image rows, top = 0, z = sections).
    /// </summary>
    public interface ISliceableVolume
    {
        Transform transform { get; }
        bool Loaded { get; }
        /// <summary>Whole-volume unit cube (0..1) -> world.</summary>
        Matrix4x4 UnitCubeToWorld { get; }
        /// <summary>How much is cut away along z, 0 = none, 1 = all.</summary>
        float SlicePosition { get; set; }
        /// <summary>false = cut from z = 0 inward, true = from z = 1 inward.</summary>
        bool SliceFromHighZ { get; set; }
        /// <summary>The volume's own slicing keys (if any); off while a timeline owns the slice.</summary>
        bool SliceKeysEnabled { get; set; }
        /// <summary>Raised right after the volume is drawn for a camera (draw overlays on top here).</summary>
        event System.Action<Camera> Drawn;
    }
}

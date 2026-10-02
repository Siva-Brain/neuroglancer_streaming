using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BrainVolume
{
    /// <summary>
    /// Two-screen setup: the Spatial Reality Display on Display 1 and a plain monitor camera
    /// (secondCamera, targetDisplay 1 = "Display 2") on the second screen. In a player build the
    /// second display is activated on Start; in the Editor open a Game view set to Display 2.
    ///
    /// R resets the view on both screens: every active ModelMoveController object (the bricked Brain,
    /// AstrocyteDensity, ...), the SRDisplayManager and the second camera go back to the
    /// position / rotation / scale they had when Play started.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DualScreenView : MonoBehaviour
    {
        [Tooltip("Camera rendered on the second screen (its targetDisplay is forced to 1 = Display 2).")]
        public Camera secondCamera;
        [Tooltip("Activate the second physical display in a player build.")]
        public bool activateSecondDisplay = true;
        [Tooltip("Extra transforms to restore on reset (models with ModelMoveController and the SRDisplayManager are picked up automatically).")]
        public List<Transform> extraTargets = new List<Transform>();

        struct Pose { public Transform t; public Vector3 pos, scale; public Quaternion rot; }
        readonly List<Pose> _poses = new List<Pose>();

        void Start()
        {
            if (secondCamera != null) secondCamera.targetDisplay = 1;
            if (activateSecondDisplay && !Application.isEditor && Display.displays.Length > 1 && !Display.displays[1].active)
                Display.displays[1].Activate();

            var targets = new HashSet<Transform>(extraTargets);
            foreach (var m in FindObjectsByType<ModelMoveController>(FindObjectsSortMode.None)) targets.Add(m.transform);
            var srd = FindFirstObjectByType<global::SRD.Core.SRDManager>();
            if (srd != null) targets.Add(srd.transform);
            if (secondCamera != null) targets.Add(secondCamera.transform);
            foreach (var t in targets)
                if (t != null) _poses.Add(new Pose { t = t, pos = t.position, rot = t.rotation, scale = t.localScale });
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.rKey.wasPressedThisFrame) ResetView();
        }

        public void ResetView()
        {
            foreach (var p in _poses)
            {
                if (p.t == null) continue;
                p.t.SetPositionAndRotation(p.pos, p.rot);
                p.t.localScale = p.scale;
            }
        }
    }
}

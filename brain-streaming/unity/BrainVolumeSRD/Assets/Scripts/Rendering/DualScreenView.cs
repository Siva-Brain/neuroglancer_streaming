using UnityEngine;

namespace BrainVolume
{
    /// <summary>
    /// Two-screen setup: the Spatial Reality Display on Display 1 and a plain monitor camera
    /// (secondCamera, targetDisplay 1 = "Display 2") on the second screen. In a player build the
    /// second display is activated on Start; in the Editor open a Game view set to Display 2.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DualScreenView : MonoBehaviour
    {
        [Tooltip("Camera rendered on the second screen (its targetDisplay is forced to 1 = Display 2).")]
        public Camera secondCamera;
        [Tooltip("Activate the second physical display in a player build.")]
        public bool activateSecondDisplay = true;

        void Start()
        {
            if (secondCamera != null) secondCamera.targetDisplay = 1;
            if (activateSecondDisplay && !Application.isEditor && Display.displays.Length > 1 && !Display.displays[1].active)
                Display.displays[1].Activate();
        }
    }
}

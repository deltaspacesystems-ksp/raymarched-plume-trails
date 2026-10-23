using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // The smoke clips its raymarch against _CameraDepthTexture instead of depth-testing
    // its box (see the occlusion block in SmokeVolume.shader). Unity only fills that
    // texture for cameras that ask for it, and KSP does not guarantee it, so request it.
    //
    // If this does not run the depth sample reads a constant, so the smoke either draws
    // over all geometry or vanishes - obvious, and it points straight back here.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class DepthTextureEnabler : MonoBehaviour
    {
        private float nextCheck;

        private void Start()
        {
            if (ModSettings.Enabled) EnableOnAllCameras();
        }

        // re-asserted every 2 s while the mod is on (also when it is switched on after the scene started); idempotent and cheap. Off: nothing is asked of the cameras.
        private void Update()
        {
            if (!ModSettings.Enabled || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 2f;
            EnableOnAllCameras();
        }

        // cameras get rebuilt on scene/vessel changes, so re-assert periodically
        // rather than only once at Start
        private void OnLevelWasLoaded(int level)
        {
            if (ModSettings.Enabled) EnableOnAllCameras();
        }

        // KSP's flight scene splits the world across several cameras; only the two
        // local-scene ones ever draw our smoke. The scaled-space, UI and internal
        // cameras never do, and asking them for depth is pure cost.
        private static readonly string[] SceneCameraNames = { "Camera 00", "Camera 01" };

        private static void EnableOnAllCameras()
        {
            Camera[] cameras = Camera.allCameras;
            int touched = 0;

            for (int i = 0; i < cameras.Length; i++)
            {
                Camera cam = cameras[i];
                if (cam == null) continue;
                if (!IsSmokeCamera(cam)) continue;
                // |= is idempotent and preserves whatever scatterer/KSP already asked
                // for - never clobber another mod's depth/normals request
                if ((cam.depthTextureMode & DepthTextureMode.Depth) != 0) continue;
                cam.depthTextureMode |= DepthTextureMode.Depth;
                touched++;
            }

            if (touched > 0)
            {
                Debug.Log(string.Format(
                    "[PlumeTrails] Enabled depth texture on {0} of {1} cameras.",
                    touched, cameras.Length));
            }
        }

        // Each camera costs an extra depth prepass and a full-resolution depth buffer.
        // Blanket-enabling it took 6 of 9 cameras here, which exhausted GPU memory outright
        // on a laptop and tanked the framerate well before that. Keep this list tight.
        private static bool IsSmokeCamera(Camera cam)
        {
            if (cam == Camera.main) return true;
            for (int i = 0; i < SceneCameraNames.Length; i++)
            {
                if (cam.name == SceneCameraNames[i]) return true;
            }
            return false;
        }
    }
}

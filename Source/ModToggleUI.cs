using UnityEngine;
using KSP.UI.Screens;

namespace VolumetricContrails
{
    // global on/off switch, read by LaunchSmokeController every FixedUpdate
    public static class ModSettings
    {
        public static bool Enabled = true;
    }

    // toolbar button + tiny window to flip ModSettings.Enabled during flight
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ModToggleUI : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private bool showWindow;
        private Rect windowRect = new Rect(200, 200, 330, 70);
        private Vector2 scroll;
        private Texture2D iconOn;
        private Texture2D iconOff;

        private void Start()
        {
            iconOn = MakeIcon(new Color(0.85f, 0.9f, 1f));
            iconOff = MakeIcon(new Color(0.35f, 0.35f, 0.35f));
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            if (button != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(button);
            }
        }

        private static Texture2D MakeIcon(Color color)
        {
            Texture2D tex = new Texture2D(38, 38);
            Color[] pixels = new Color[38 * 38];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void AddButton()
        {
            if (button != null) return;
            button = ApplicationLauncher.Instance.AddModApplication(
                () => showWindow = true,
                () => showWindow = false,
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT,
                ModSettings.Enabled ? iconOn : iconOff);
        }

        private void OnGUI()
        {
            if (!showWindow) return;
            windowRect = GUILayout.Window(834621, windowRect, DrawWindow, "HairyBlob");
        }

        // one labelled slider, showing its live value - these are aesthetic knobs, so
        // seeing the number matters as much as seeing the effect (it's what gets written
        // back into SmokeTuning's defaults once a look is settled on)
        private static float Slider(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(150));
            GUILayout.Label(value.ToString("F2"), GUILayout.Width(38));
            float result = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return result;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            bool newState = GUILayout.Toggle(ModSettings.Enabled, " Enable smoke");
            if (newState != ModSettings.Enabled)
            {
                ModSettings.Enabled = newState;
                if (button != null) button.SetTexture(newState ? iconOn : iconOff);
            }

            GUILayout.Space(6);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(390));

            GUILayout.Label("<b>Shape</b>");
            SmokeTuning.DetailStrength = Slider("Detail", SmokeTuning.DetailStrength, 0f, 1f);
            SmokeTuning.DetailBias = Slider("Detail add-only", SmokeTuning.DetailBias, 0f, 1f);
            SmokeTuning.SizeFreqTracking = Slider("Size-tracked freq", SmokeTuning.SizeFreqTracking, 0f, 1f);
            SmokeTuning.EdgeErosionStrength = Slider("Edge erosion", SmokeTuning.EdgeErosionStrength, 0f, 1f);
            SmokeTuning.GrainStrength = Slider("Grain", SmokeTuning.GrainStrength, 0f, 1f);
            SmokeTuning.GrainScale = Slider("Grain scale", SmokeTuning.GrainScale, 0.3f, 4f);
            SmokeTuning.SilhouetteWarpStrength = Slider("Warp (m)", SmokeTuning.SilhouetteWarpStrength, 0f, 40f);
            SmokeTuning.SilhouetteWarpScale = Slider("Lobe size", SmokeTuning.SilhouetteWarpScale, 0.01f, 0.4f);
            SmokeTuning.VortexStrength = Slider("Swirl (m)", SmokeTuning.VortexStrength, 0f, 25f);
            SmokeTuning.VortexScale = Slider("Swirl scale", SmokeTuning.VortexScale, 0.01f, 0.6f);
            SmokeTuning.SilhouetteNoiseScale = Slider("Noise scale", SmokeTuning.SilhouetteNoiseScale, 0.02f, 0.5f);


            GUILayout.Space(6);
            GUILayout.Label("<b>Density</b>");
            SmokeTuning.Density = Slider("Density", SmokeTuning.Density, 0.2f, 6f);
            SmokeTuning.Absorption = Slider("Absorption", SmokeTuning.Absorption, 0.2f, 4f);

            GUILayout.Space(6);
            GUILayout.Label("<b>Light</b>");
            SmokeTuning.AmbientFloor = Slider("Ambient floor", SmokeTuning.AmbientFloor, 0f, 1f);
            SmokeTuning.ShadowStrength = Slider("Self-shadow", SmokeTuning.ShadowStrength, 0f, 1f);
            SmokeTuning.ShadowExtinction = Slider("Shadow softness", SmokeTuning.ShadowExtinction, 0.002f, 0.15f);
            SmokeTuning.LightReach = Slider("Light reach (m)", SmokeTuning.LightReach, 20f, 400f);
            SmokeTuning.ShadowDarkness = Slider("Shadow darkness", SmokeTuning.ShadowDarkness, 0f, 1f);
            SmokeTuning.SunlitBrightness = Slider("Lit brightness", SmokeTuning.SunlitBrightness, 0.5f, 1f);
            SmokeTuning.SkyOcclusionStrength = Slider("Sky occlusion", SmokeTuning.SkyOcclusionStrength, 0f, 1f);
            SmokeTuning.SkyOcclusionDistance = Slider("Sky occl. dist (m)", SmokeTuning.SkyOcclusionDistance, 10f, 150f);
            SmokeTuning.ForwardScatterG = Slider("Fwd scatter (g)", SmokeTuning.ForwardScatterG, 0f, 0.99f);
            SmokeTuning.ScatterIntensity = Slider("Scatter int.", SmokeTuning.ScatterIntensity, 0f, 5f);
            SmokeTuning.MultiScatterG = Slider("Multi-scat g", SmokeTuning.MultiScatterG, 0f, 0.6f);
            SmokeTuning.MultiScatterIntensity = Slider("Multi-scat int.", SmokeTuning.MultiScatterIntensity, 0f, 10f);
            SmokeTuning.PowderStrength = Slider("Powder", SmokeTuning.PowderStrength, 0f, 1f);
            SmokeTuning.SkyTintStrength = Slider("Sky tint str.", SmokeTuning.SkyTintStrength, 0f, 1f);
            SmokeTuning.SceneAmbientBlend = Slider("Scene ambient", SmokeTuning.SceneAmbientBlend, 0f, 1f);
            SmokeTuning.Washout = Slider("Washout", SmokeTuning.Washout, 0f, 1f);
            SmokeTuning.WashoutDesaturate = Slider("Washout desat", SmokeTuning.WashoutDesaturate, 0f, 1f);
            SmokeTuning.SkyTint = Slider("Sky tint", SmokeTuning.SkyTint, 0f, 1f);
            SmokeTuning.GroundTint = Slider("Ground tint", SmokeTuning.GroundTint, 0f, 1f);
            SmokeTuning.SpineBlend = Slider("Spine blend", SmokeTuning.SpineBlend, 0f, 0.5f);
            SmokeTuning.MacroStrength = Slider("Macro density", SmokeTuning.MacroStrength, 0f, 1f);
            SmokeTuning.MacroNoiseScale = Slider("Macro scale", SmokeTuning.MacroNoiseScale, 0.002f, 0.08f);

            GUILayout.Space(6);
            if (GUILayout.Button("Print values to log"))
            {
                Debug.Log(string.Format(
                    "[HairyBlob] tuning: Detail={0:F2} Erosion={1:F2} Interior={2:F2} " +
                    "Warp={2:F2} NoiseScale={3:F3} Density={4:F2} Absorption={5:F2} " +
                    "Ambient={6:F2} SelfShadow={7:F2} ShadowSoft={8:F2} ShadowDark={9:F2} SpineBlend={10:F2} LightReach={11:F0} Macro={12:F2}/{13:F3}",
                    SmokeTuning.DetailStrength, SmokeTuning.EdgeErosionStrength,
                    SmokeTuning.SilhouetteWarpStrength,
                    SmokeTuning.SilhouetteNoiseScale, SmokeTuning.Density,
                    SmokeTuning.Absorption, SmokeTuning.AmbientFloor,
                    SmokeTuning.ShadowStrength, SmokeTuning.ShadowExtinction,
                    SmokeTuning.ShadowDarkness, SmokeTuning.SpineBlend,
                    SmokeTuning.LightReach,
                    SmokeTuning.MacroStrength, SmokeTuning.MacroNoiseScale));
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}

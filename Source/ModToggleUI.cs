using System.IO;
using UnityEngine;
using KSP.UI.Screens;

namespace VolumetricContrails
{
    // global on/off switch, read by LaunchSmokeController every FixedUpdate
    public static class ModSettings
    {
        public static bool Enabled = true;

        // One place for the name, so the window title, the launcher tooltip and anything
        // else that shows it cannot drift apart. The GameData folder and the assembly keep
        // their original names on purpose - renaming those breaks existing installs.
        public const string DisplayName = "Raymarched Plume Trails";
    }

    // toolbar button + tiny window to flip ModSettings.Enabled during flight
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ModToggleUI : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private bool showWindow;
        // The tuning sliders are a developer surface, not something a player wants in
        // their face on every launch, so the panel opens as just the on/off switch and
        // everything else lives behind one button.
        private bool showDebug;
        private Rect windowRect = new Rect(200, 200, 330, 70);
        private Vector2 scroll;
        private Texture2D iconOn;
        private Texture2D iconOff;

        private void Start()
        {
            Texture2D logo = LoadLogo();
            if (logo != null)
            {
                iconOn = logo;
                iconOff = Dim(logo, 0.4f);
            }
            else
            {
                // Plain colours if the icon is missing, so a bad install still gets a
                // usable button instead of no button at all.
                iconOn = MakeIcon(new Color(0.85f, 0.9f, 1f));
                iconOff = MakeIcon(new Color(0.35f, 0.35f, 0.35f));
            }
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

        // Icons/logo.png, found relative to the ASSEMBLY rather than a fixed folder name -
        // same reasoning as AssetLoader.ResolveBundlePath, since renaming the mod folder
        // has broken asset lookup here once before.
        // 256, well above the ~38px the launcher actually draws.
        //
        // KSP hands the button texture to a UI shader that samples it with bilinear
        // filtering, so supplying it already reduced to button size throws away the detail
        // before the GPU ever gets it and the logo comes out as mush. Downscaling from the
        // source to 256 keeps enough for the final reduction to stay sharp, and a 256px
        // RGBA texture is a quarter of a megabyte - irrelevant next to the 3D noise volume.
        private const int IconSize = 256;

        private static Texture2D LoadLogo()
        {
            string path = ResolveLogoPath();
            if (path == null)
            {
                Debug.LogWarning("[HairyBlob] Icons/logo.png not found - using a plain button icon.");
                return null;
            }

            Texture2D full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!full.LoadImage(File.ReadAllBytes(path)))
            {
                Debug.LogWarning("[HairyBlob] Icons/logo.png could not be decoded.");
                Destroy(full);
                return null;
            }

            Texture2D icon = Downscale(full, IconSize);
            Destroy(full);
            return icon;
        }

        private static string ResolveLogoPath()
        {
            string pluginDir = AssetLoader.AssemblyDirectory();
            if (string.IsNullOrEmpty(pluginDir)) return null;

            string modDir = Path.GetDirectoryName(pluginDir);
            if (!string.IsNullOrEmpty(modDir))
            {
                string sibling = Path.Combine(Path.Combine(modDir, "Icons"), "logo.png");
                if (File.Exists(sibling)) return sibling;
            }

            string beside = Path.Combine(pluginDir, "logo.png");
            return File.Exists(beside) ? beside : null;
        }

        // Box-filter downscale to IconSize. The art is ~900px square, so point sampling
        // would drop almost all of it and alias badly.
        //
        // Averaging is done on PREMULTIPLIED colour: the transparent pixels around the
        // artwork are transparent BLACK, so averaging straight RGB across an edge pulls
        // it toward black and leaves a dark fringe.
        private static Texture2D Downscale(Texture2D src, int size)
        {
            Color[] source = src.GetPixels();
            int sw = src.width;
            int sh = src.height;
            Color[] dest = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                int y0 = y * sh / size;
                int y1 = Mathf.Max(y0 + 1, (y + 1) * sh / size);
                for (int x = 0; x < size; x++)
                {
                    int x0 = x * sw / size;
                    int x1 = Mathf.Max(x0 + 1, (x + 1) * sw / size);

                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    int n = 0;
                    for (int sy = y0; sy < y1; sy++)
                    {
                        for (int sx = x0; sx < x1; sx++)
                        {
                            Color c = source[sy * sw + sx];
                            r += c.r * c.a;
                            g += c.g * c.a;
                            b += c.b * c.a;
                            a += c.a;
                            n++;
                        }
                    }

                    if (n == 0 || a <= 0.0001f)
                    {
                        dest[y * size + x] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    // divide by the accumulated alpha to undo the premultiply
                    dest[y * size + x] = new Color(r / a, g / a, b / a, a / n);
                }
            }

            Texture2D icon = new Texture2D(size, size, TextureFormat.RGBA32, false);
            icon.SetPixels(dest);
            icon.Apply();
            return icon;
        }

        // Greyed-out copy for the disabled state. Alpha is untouched so the silhouette
        // stays identical and only the button's brightness changes.
        private static Texture2D Dim(Texture2D src, float amount)
        {
            Color[] pixels = src.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                float lum = pixels[i].grayscale * amount;
                pixels[i] = new Color(lum, lum, lum, pixels[i].a);
            }

            Texture2D dimmed = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            dimmed.SetPixels(pixels);
            dimmed.Apply();
            return dimmed;
        }

        private static Texture2D MakeIcon(Color color)
        {
            Texture2D tex = new Texture2D(IconSize, IconSize);
            Color[] pixels = new Color[IconSize * IconSize];
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
            windowRect = GUILayout.Window(834621, windowRect, DrawWindow, ModSettings.DisplayName);
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

        private static void PrintValues()
        {
            Debug.Log(string.Format(
                    "[PlumeTrails] tuning: Detail={0:F2} Erosion={1:F2} Interior={2:F2} " +
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

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            bool newState = GUILayout.Toggle(ModSettings.Enabled, " Enable smoke");
            if (newState != ModSettings.Enabled)
            {
                ModSettings.Enabled = newState;
                if (button != null) button.SetTexture(newState ? iconOn : iconOff);
            }

            GUILayout.Space(4);
            if (GUILayout.Button(showDebug ? "Hide debug menu" : "Debug menu / config"))
            {
                showDebug = !showDebug;
                // Let the layout re-measure instead of keeping the taller rect, otherwise
                // the collapsed panel leaves a window of empty grey behind the switch.
                windowRect.height = 0f;
            }

            if (!showDebug)
            {
                GUILayout.EndVertical();
                GUI.DragWindow();
                return;
            }

            GUILayout.Space(6);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(390));

            GUILayout.Label("<b>Debug</b>");
            SmokeTuning.DebugLogging = GUILayout.Toggle(SmokeTuning.DebugLogging, " Verbose log");
            // Takes effect for engine groups formed after the change - existing groups are
            // only rebuilt when the vessel's part count changes, e.g. at staging.
            SmokeTuning.SrbOnly = GUILayout.Toggle(SmokeTuning.SrbOnly, " Solid motors only");
            SmokeTuning.SrbThinningPower = Slider("SRB spread thinning", SmokeTuning.SrbThinningPower, 0f, 3f);
            SmokeTuning.ShadowCastDebug = Slider("Cast shadow debug", SmokeTuning.ShadowCastDebug, 0f, 5f);
            if (GUILayout.Button("Print values to log")) PrintValues();

            GUILayout.Space(6);
            GUILayout.Label("<b>Shape</b>");
            SmokeTuning.MaxPuffSize = Slider("Puff size (m)", SmokeTuning.MaxPuffSize, 4f, 200f);
            SmokeTuning.SpawnOffset = Slider("Spawn offset (m)", SmokeTuning.SpawnOffset, 2f, 150f);
            SmokeTuning.WindSpeed = Slider("Wind (m/s)", SmokeTuning.WindSpeed, 0f, 8f);
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
            SmokeTuning.ColumnWander = Slider("Column wander", SmokeTuning.ColumnWander, 0f, 0.6f);


            GUILayout.Space(6);
            GUILayout.Label("<b>Density</b>");
            SmokeTuning.Density = Slider("Density", SmokeTuning.Density, 0.2f, 6f);
            SmokeTuning.Absorption = Slider("Absorption", SmokeTuning.Absorption, 0.2f, 4f);

            GUILayout.Space(6);
            GUILayout.Label("<b>Light</b>");
            SmokeTuning.AmbientFloor = Slider("Ambient floor", SmokeTuning.AmbientFloor, 0f, 1f);
            SmokeTuning.ShadowStrength = Slider("Self-shadow", SmokeTuning.ShadowStrength, 0f, 1f);
            SmokeTuning.ShadowExtinction = Slider("Shadow softness", SmokeTuning.ShadowExtinction, 0.002f, 0.15f);
            SmokeTuning.LightMarchSteps = Slider("Light march steps", SmokeTuning.LightMarchSteps, 2f, 12f);
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
            SmokeTuning.NightAmbient = Slider("Night ambient", SmokeTuning.NightAmbient, 0f, 1f);
            SmokeTuning.ShadowCastStrength = Slider("Cast shadow", SmokeTuning.ShadowCastStrength, 0f, 1f);
            SmokeTuning.ShadowCastDistance = Slider("Cast reach (m)", SmokeTuning.ShadowCastDistance, 200f, 6000f);
            SmokeTuning.ShadowCastSteps = Slider("Cast steps", SmokeTuning.ShadowCastSteps, 4f, 48f);
            SmokeTuning.TerminatorSoftness = Slider("Terminator (m)", SmokeTuning.TerminatorSoftness, 100f, 20000f);
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
            SmokeTuning.AirlessBodyPlume = GUILayout.Toggle(SmokeTuning.AirlessBodyPlume, " Emit on airless bodies");

            GUILayout.Space(6);
            GUILayout.Label("<b>Altitude bloom (jellyfish)</b>");
            SmokeTuning.JellyfishEnabled = GUILayout.Toggle(SmokeTuning.JellyfishEnabled, " Enabled");
            SmokeTuning.JellyfishSpread = Slider("Bell spread", SmokeTuning.JellyfishSpread, 0f, 150f);
            SmokeTuning.JellyfishSizeBoost = Slider("Size boost", SmokeTuning.JellyfishSizeBoost, 1f, 600f);
            SmokeTuning.JellyfishExpandTime = Slider("Expand time (s)", SmokeTuning.JellyfishExpandTime, 0.2f, 15f);
            SmokeTuning.JellyfishAlphaFloor = Slider("Thin-air alpha", SmokeTuning.JellyfishAlphaFloor, 0f, 1f);
            SmokeTuning.JellyfishThinningPower = Slider("Bloom thinning", SmokeTuning.JellyfishThinningPower, 0f, 3f);
            SmokeTuning.ThinEdgeSoftness = Slider("Thin edge softness", SmokeTuning.ThinEdgeSoftness, 0.15f, 1f);
            SmokeTuning.ThinDetailFade = Slider("Thin detail fade", SmokeTuning.ThinDetailFade, 0f, 1f);

            GUILayout.Space(6);
            GUILayout.Label("<b>Retro umbrella</b>");
            SmokeTuning.UmbrellaEnabled = GUILayout.Toggle(SmokeTuning.UmbrellaEnabled, " Enabled");
            SmokeTuning.UmbrellaSpread = Slider("Canopy spread", SmokeTuning.UmbrellaSpread, 0f, 120f);
            SmokeTuning.UmbrellaJet = Slider("Nozzle jet", SmokeTuning.UmbrellaJet, 0f, 300f);
            SmokeTuning.UmbrellaBackflowFraction = Slider("Backflow (x speed)", SmokeTuning.UmbrellaBackflowFraction, 0f, 1.5f);
            SmokeTuning.UmbrellaStandoff = Slider("Standoff (m)", SmokeTuning.UmbrellaStandoff, 0f, 80f);

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUI.DragWindow();
        }
    }
}

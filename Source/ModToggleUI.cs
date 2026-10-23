using System.Collections.Generic;
using System.IO;
using UnityEngine;
using KSP.UI.Screens;

namespace RaymarchedPlumeTrails
{
    // global on/off switch, read by LaunchSmokeController every FixedUpdate
    public static class ModSettings
    {
        public static bool Enabled { get { return SmokeTuning.ModEnabled; } set { SmokeTuning.ModEnabled = value; } }

        // One place for the name, so the window title, the launcher tooltip and anything
        // else that shows it cannot drift apart. The GameData folder and the assembly keep
        // their original names on purpose - renaming those breaks existing installs.
        public const string DisplayName = "Raymarched Plume Trails";
        // from the assembly (the csproj <Version>), so the window cannot show a different number than the build
        public static readonly string Version = typeof(ModSettings).Assembly.GetName().Version.ToString(3);
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

        // closing the window saves what was changed (only the changed values: see SmokeTuningConfig)
        private void Close()
        {
            if (showWindow) SmokeTuningConfig.Save();
            showWindow = false;
        }

        private void AddButton()
        {
            if (button != null) return;
            button = ApplicationLauncher.Instance.AddModApplication(
                () => showWindow = true,
                () => Close(),
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT,
                ModSettings.Enabled ? iconOn : iconOff);
        }

        // ------------------------------------------------------------------------------------------------------------
        //  Window: Unity's default IMGUI look, nothing custom. A plain window with its title; on top the Simple / Advanced
        //  switch (and in Advanced the search box and the tab bar); in Advanced the sections of the tab on the left as
        //  plain buttons that scroll to them; the controls in a scroll view; a corner box to resize.
        // ------------------------------------------------------------------------------------------------------------
        private static readonly string[] TabNames = { "General", "Engines", "Physics", "Legacy shape", "Legacy light", "Effects", "Explosions", "Extras", "Trail" };
        private static readonly int[] TabOrder = { 0, 8, 1, 2, 5, 6, 3, 4, 7 };      // the order on the tab bar
        private static string[] tabLabels;
        private static readonly string[] ModeNames = { "Simple", "Advanced" };
        // the window opens in the SIMPLE view (the switches a player wants); every slider is behind "Advanced"
        private static bool advanced;
        private static int tab;
        private static string search = "";
        private static Rect windowRectStatic = new Rect(120, 80, 600, 640);
        private static bool resizing;

        // section bookkeeping (rebuilt every repaint; the left column reads the previous one)
        private static int curTab;
        private static string curSec = "";
        private static bool secDrawn;
        private static List<string> shownSections = new List<string>();
        private static List<string> nextSections = new List<string>();
        private static readonly Dictionary<string, float> secY = new Dictionary<string, float>();
        private static bool anyVisible;

        private static void Sec(int tabIndex, string name)
        {
            curTab = tabIndex; curSec = name; secDrawn = false;
        }

        private static bool Vis(string label)
        {
            bool searching = search.Length > 0;
            if (searching)
            {
                if (label.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0
                    && curSec.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0
                    && TabNames[curTab].IndexOf(search, System.StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            else if (curTab != tab) return false;

            anyVisible = true;
            if (!secDrawn)
            {
                secDrawn = true;
                GUILayout.Space(8);
                GUILayout.Label("<b>" + curSec + "</b>" + (searching ? "  (" + TabNames[curTab] + ")" : ""));
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    secY[curSec] = GUILayoutUtility.GetLastRect().y;
                    if (!nextSections.Contains(curSec)) nextSections.Add(curSec);
                }
            }
            return true;
        }

        private static float Slider(string label, float value, float min, float max)
        {
            if (!Vis(label.Trim())) return value;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(300));
            GUILayout.Label(value.ToString("F2"), GUILayout.Width(52));
            float result = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return result;
        }

        private static void Note(string text)
        {
            if (!Vis(text)) return;
            GUILayout.Label("<color=#e0b050>" + text + "</color>");
        }

        private static bool Tog(bool value, string label)
        {
            if (!Vis(label.Trim())) return value;
            return GUILayout.Toggle(value, label);
        }

        private static bool Btn(string label)
        {
            if (!Vis(label.Trim())) return false;
            return GUILayout.Button(label);
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

        private const float LeftW = 150f;
        private float scrollTo = -1f;          // a section button was clicked: scroll there on the next layout

        private void OnGUI()
        {
            if (!showWindow || EveCalibOverlay.DumpFrames > 0) return;
            GUISkin keepSkin = GUI.skin;
            GUI.skin = null;                       // Unity's default skin, not KSP's
            windowRectStatic = GUILayout.Window(834621, windowRectStatic, DrawWindow, ModSettings.DisplayName + " " + ModSettings.Version,
                GUILayout.Width(windowRectStatic.width), GUILayout.Height(windowRectStatic.height));
            GUI.skin = keepSkin;
        }

        private void SetTab(int i)
        {
            if (tab == i && search.Length == 0) return;
            tab = i; search = ""; scroll = Vector2.zero;
        }

        private void DrawWindow(int id)
        {
            Event ev = Event.current;
            bool repaint = ev.type == EventType.Repaint;
            if (tabLabels == null)
            {
                tabLabels = new string[TabOrder.Length];
                for (int i = 0; i < TabOrder.Length; i++) tabLabels[i] = TabNames[TabOrder[i]];
            }

            // ---- top row: the mode, the search (advanced), close
            GUILayout.BeginHorizontal();
            int mode = GUILayout.Toolbar(advanced ? 1 : 0, ModeNames, GUILayout.Width(180));
            if ((mode == 1) != advanced)
            {
                advanced = mode == 1; search = ""; scroll = Vector2.zero;
                windowRectStatic.width = advanced ? Mathf.Max(windowRectStatic.width, 860f) : 600f;
            }
            GUILayout.FlexibleSpace();
            if (advanced)
            {
                GUILayout.Label("Search", GUILayout.ExpandWidth(false));
                string ns = GUILayout.TextField(search, GUILayout.Width(170));
                if (ns != search) { search = ns; scroll = Vector2.zero; }
            }
            if (GUILayout.Button("Close", GUILayout.ExpandWidth(false)))
            {
                Close();
                if (button != null) button.SetFalse(false);
            }
            GUILayout.EndHorizontal();

            // ---- advanced: the tab bar
            if (advanced)
            {
                int cur = search.Length > 0 ? -1 : System.Array.IndexOf(TabOrder, tab);
                int sel = GUILayout.Toolbar(cur, tabLabels);
                if (sel != cur && sel >= 0) SetTab(TabOrder[sel]);
            }

            GUILayout.BeginHorizontal();
            // ---- advanced: the sections of this tab (or of the matches)
            if (advanced)
            {
                GUILayout.BeginVertical(GUILayout.Width(LeftW));
                for (int i = 0; i < shownSections.Count; i++)
                {
                    float y;
                    if (GUILayout.Button(shownSections[i]) && secY.TryGetValue(shownSections[i], out y)) scrollTo = Mathf.Max(0f, y - 6f);
                }
                GUILayout.EndVertical();
            }

            // ---- the controls
            if (scrollTo >= 0f && repaint) { scroll.y = scrollTo; scrollTo = -1f; }
            scroll = GUILayout.BeginScrollView(scroll);
            anyVisible = false;
            if (repaint) nextSections = new List<string>();
            if (advanced) DrawControls(); else { DrawSimple(); anyVisible = true; }
            if (!anyVisible) GUILayout.Label(search.Length > 0 ? "Nothing matches \"" + search + "\"." : "Nothing here.");
            GUILayout.Space(20);
            GUILayout.EndScrollView();
            GUILayout.EndHorizontal();
            if (repaint) { List<string> t = shownSections; shownSections = nextSections; nextSections = t; }

            // ---- resize: a plain box in the corner
            Rect grip = new Rect(windowRectStatic.width - 18, windowRectStatic.height - 18, 16, 16);
            GUI.Box(grip, "");
            if (ev.type == EventType.MouseDown && grip.Contains(ev.mousePosition)) { resizing = true; ev.Use(); }
            else if (resizing && ev.type == EventType.MouseDrag)
            {
                windowRectStatic.width = Mathf.Clamp(windowRectStatic.width + ev.delta.x, 480f, Screen.width - 20f);
                windowRectStatic.height = Mathf.Clamp(windowRectStatic.height + ev.delta.y, 360f, Screen.height - 20f);
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp) resizing = false;

            if (!resizing) GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ------------------------------------------------------------------------------------------------------------
        //  THE SIMPLE VIEW: what a player switches - each with one line saying what it does. Everything here is also in the
        //  advanced view; nothing here is a different setting.
        // ------------------------------------------------------------------------------------------------------------
        private static GUIStyle descStyle;
        private static int changedCache;
        private static float changedStamp;

        private static void Head(string text)
        {
            GUILayout.Space(10);
            GUILayout.Label("<b>" + text + "</b>");
        }

        private static void Desc(string text)
        {
            if (descStyle == null) { descStyle = new GUIStyle(GUI.skin.label) { wordWrap = true }; descStyle.padding.left = 22; }
            GUILayout.Label(text, descStyle);
        }

        private static bool STog(bool value, string label, string desc)
        {
            bool v = GUILayout.Toggle(value, " " + label);
            if (desc != null) Desc(desc);
            return v;
        }

        private static float SSlider(string label, float value, float min, float max, string format)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(22);
            GUILayout.Label(label, GUILayout.Width(230));
            GUILayout.Label(value.ToString(format), GUILayout.Width(52));
            float r = GUILayout.HorizontalSlider(value, min, max);
            GUILayout.EndHorizontal();
            return r;
        }

        private void DrawSimple()
        {
            Head("Smoke");
            bool newState = STog(ModSettings.Enabled, "Enabled", null);
            if (newState != ModSettings.Enabled)
            {
                ModSettings.Enabled = newState;
                if (newState) AssetLoader.EnsureLoaded();
                if (button != null) button.SetTexture(newState ? iconOn : iconOff);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(22);
            GUILayout.Label("Quality", GUILayout.Width(70));
            SmokeTuning.QualityPreset = GUILayout.SelectionGrid(SmokeTuning.QualityPreset, SmokeQuality.PresetNames, SmokeQuality.PresetNames.Length);
            GUILayout.EndHorizontal();
            SmokeTuning.AutoQuality = STog(SmokeTuning.AutoQuality, "Adapt the quality to the frame rate", "Lowers the quality step by step when the frame takes too long, and raises it again when there is room.");

            Head("Rocket trail");
            SmokeTuning.NewTrail = STog(SmokeTuning.NewTrail, "Volumetric trail", "The smoke column a rocket leaves behind: grows out of the flame, widens and drifts, lit by the sun and the sky.");
            if (SmokeTuning.NewTrail)
            {
                SmokeTuning.NewTrailRoot = STog(SmokeTuning.NewTrailRoot, "Grows out of the flame", "The exhaust leaves along the nozzle and curls into the track of a turning rocket.");
                SmokeTuning.NewTrailDensityK = SSlider("Density of the smoke", SmokeTuning.NewTrailDensityK, 1f, 30f, "F1");
                SmokeTuning.NewTrailGlow = SSlider("Glow of the hot exhaust", SmokeTuning.NewTrailGlow, 0f, 4f, "F2");
                SmokeTuning.NewTrailMaxRadius = SSlider("Width of old smoke (m)", SmokeTuning.NewTrailMaxRadius, 20f, 200f, "F0");
                SmokeTuning.NewTrailLife = SSlider("Lifetime (s)", SmokeTuning.NewTrailLife, 30f, 900f, "F0");
                SmokeTuning.NewTrailEndAltitude = SSlider("Trail ends at altitude (km, 0 = no limit)", SmokeTuning.NewTrailEndAltitude, 0f, 100f, "F0");
                SmokeTuning.NewTrailShadow = STog(SmokeTuning.NewTrailShadow, "Casts a shadow on the ground", null);
                SmokeTuning.HideStockSmoke = STog(SmokeTuning.HideStockSmoke, "Hide the engines' own particle smoke", "Engine parts often bring a particle smoke of their own; it would hang between the flame and the trail.");
            }
            SmokeTuning.SrbOnly = STog(SmokeTuning.SrbOnly, "Solid motors only", "Liquid engines leave almost no visible smoke; switch off to give every engine a trail.");
            SmokeTuning.HideOldTrail = !STog(!SmokeTuning.HideOldTrail, "Legacy trail", "The puff renderer from before 1.0, kept for comparison. Heavier, and drawn in addition to the volumetric trail.");

            Head("Launch pad");
            SmokeTuning.PadFluid = STog(SmokeTuning.PadFluid, "Pad cloud (GPU flow)", "The exhaust that hits the pad flows over the trench, the deflector and the ground and rolls out as a cloud.");
            if (ModCompat.GroundBlastFxPresent) Desc("<color=#e0b050>GroundBlastFx is installed and draws pad steam too: switch one of them off.</color>");
            if (SmokeTuning.PadFluid)
            {
                SmokeTuning.PadFluidShock = STog(SmokeTuning.PadFluidShock, "Ignition shock wave", "The pressure wave of the start pushes the smoke and condenses a ring of vapour.");
                SmokeTuning.PadFluidWater = STog(SmokeTuning.PadFluidWater, "Water deluge", "Spray that evaporates in the hot gas and turns into steam.");
            }

            Head("Engines");
            SmokeTuning.HeatHaze = STog(SmokeTuning.HeatHaze, "Heat haze", "The air over the nozzle shimmers.");
            SmokeTuning.EngineLights = STog(SmokeTuning.EngineLights, "Flame lights the scene", "The pad, the ground and the rocket are lit in the colour of the flame.");
            SmokeTuning.PhysicalPlumes = STog(SmokeTuning.PhysicalPlumes, "Physical plumes", "Shock diamonds, core and afterburning computed from the engine's own numbers (replaces the Waterfall flame while drawn).");

            Head("Explosions");
            SmokeTuning.ExplosionsEnabled = STog(SmokeTuning.ExplosionsEnabled, "Volumetric explosions", "Replace the stock blast: a simulated fireball and cloud, its size from the energy of the fuel.");
            if (SmokeTuning.ExplosionsEnabled)
            {
                SmokeTuning.ExplosionShockLens = STog(SmokeTuning.ExplosionShockLens, "Shock front bends the light", null);
                SmokeTuning.ExplosionDebris = STog(SmokeTuning.ExplosionDebris, "Burning fragments", null);
                SmokeTuning.ExplosionSound = STog(SmokeTuning.ExplosionSound, "The bang arrives with the shock front", null);
            }

            GUILayout.Space(14);
            GUILayout.BeginHorizontal();
            if (Time.unscaledTime >= changedStamp) { changedCache = SmokeTuningConfig.ChangedCount(); changedStamp = Time.unscaledTime + 0.5f; }
            int changed = changedCache;
            GUILayout.Label(changed == 0 ? "All settings at their defaults." : changed + " setting(s) changed from the defaults (saved when the window closes).");
            GUILayout.FlexibleSpace();
            if (changed > 0 && GUILayout.Button("Reset to defaults")) SmokeTuningConfig.ResetToDefaults();
            GUILayout.EndHorizontal();
            Desc("Every slider, the legacy renderer and the debug tools are under Advanced.");
        }

        private void DrawControls()
        {
            // ======================================================================= General
            Sec(0, "Smoke");
            bool newState = Tog(ModSettings.Enabled, " Enable smoke");
            if (newState != ModSettings.Enabled)
            {
                ModSettings.Enabled = newState;
                if (newState) AssetLoader.EnsureLoaded();
                if (button != null) button.SetTexture(newState ? iconOn : iconOff);
            }
            SmokeTuning.AirlessBodyPlume = Tog(SmokeTuning.AirlessBodyPlume, " Emit on airless bodies");

            Sec(0, "Quality");
            if (Vis("Quality preset"))
                SmokeTuning.QualityPreset = GUILayout.SelectionGrid(SmokeTuning.QualityPreset, SmokeQuality.PresetNames, SmokeQuality.PresetNames.Length);
            SmokeTuning.AutoQuality = Tog(SmokeTuning.AutoQuality, " Auto (adapt to frame rate)");
            SmokeTuning.TemporalUpscale = Tog(SmokeTuning.TemporalUpscale, " Temporal upscaling (below full res)");
            if (SmokeTuning.AutoQuality && SmokeQuality.AutoStep > 0 && Vis("Auto quality status"))
                GUILayout.Label(string.Format("   lowered {0} step(s) at {1:F1} ms", SmokeQuality.AutoStep, SmokeQuality.FrameMs));
            SmokeTuning.TargetFrameMs = Slider("Target frame (ms)", SmokeTuning.TargetFrameMs, 10f, 50f);
            SmokeTuning.TemporalFeedback = Slider("Temporal feedback", SmokeTuning.TemporalFeedback, 0.5f, 0.97f);
            if (Btn("  Smoothest look (low grain: smooth density, little jitter, long temporal average)"))
            {
                SmokeTuning.GrainStrength = 0.15f; SmokeTuning.GrainScale = 0.35f; SmokeTuning.FineDetail = 0.2f;
                SmokeTuning.LightJitter = 0.15f; SmokeTuning.TemporalFeedback = 0.95f;
            }
            SmokeTuning.MinTrailPixels = Slider("Minimum width of a distant trail (pixels, 0 = off)", SmokeTuning.MinTrailPixels, 0f, 4f);

            Sec(0, "Config & debug");
            SmokeTuning.DebugLogging = Tog(SmokeTuning.DebugLogging, " Verbose log");
            SmokeTuning.DebugBox = Tog(SmokeTuning.DebugBox, " Debug: show march box (purple) / ran-out rays (red)");
            SmokeTuning.ShadowCastDebug = Slider("Cast shadow debug", SmokeTuning.ShadowCastDebug, 0f, 5f);
            if (Btn("Print values to log")) PrintValues();
            if (Btn("Save settings now (they are also saved when the window closes)")) SmokeTuningConfig.Save();
            if (Btn("Reset ALL to defaults (deletes tuning.cfg)")) SmokeTuningConfig.ResetToDefaults();

            // ======================================================================= Trail
            Sec(8, "Trail");
            SmokeTuning.NewTrail = Tog(SmokeTuning.NewTrail, " Volumetric trail");
            SmokeTuning.HideStockSmoke = Tog(SmokeTuning.HideStockSmoke, " Hide the engines' own particle smoke while the trail is on");
            SmokeTuning.HideOldTrail = !Tog(!SmokeTuning.HideOldTrail, " Legacy trail (the pre-1.0 puff renderer; its settings are on the Legacy tabs)");
            Sec(8, "Look of the trail");
            {
                SmokeTuning.NewTrailMaxRadius = Slider("  end radius (m)", SmokeTuning.NewTrailMaxRadius, 10f, 300f);
                SmokeTuning.NewTrailGrowTau = Slider("  growth time (s)", SmokeTuning.NewTrailGrowTau, 2f, 200f);
                SmokeTuning.NewTrailLife = Slider("  life (s)", SmokeTuning.NewTrailLife, 20f, 1200f);
                SmokeTuning.NewTrailEndAltitude = Slider("  trail ends at altitude (km, 0 = no limit)", SmokeTuning.NewTrailEndAltitude, 0f, 100f);
                SmokeTuning.NewTrailFadeLength = Slider("  thins out over (km below that)", SmokeTuning.NewTrailFadeLength, 1f, 50f);
                if (Vis("trail by engine type"))
                {
                    GUILayout.Label("  Trail by engine type (1 = like a solid motor, 0 = no trail at all)");
                    SmokeTuning.TrailDensSolid = Slider("    solid: smoke", SmokeTuning.TrailDensSolid, 0f, 1.5f);
                    SmokeTuning.TrailSizeSolid = Slider("    solid: width", SmokeTuning.TrailSizeSolid, 0.2f, 1.5f);
                    SmokeTuning.TrailDensKerolox = Slider("    kerosene / RP-1: smoke", SmokeTuning.TrailDensKerolox, 0f, 1.5f);
                    SmokeTuning.TrailSizeKerolox = Slider("    kerosene / RP-1: width", SmokeTuning.TrailSizeKerolox, 0.2f, 1.5f);
                    SmokeTuning.TrailDensHydrolox = Slider("    hydrogen: smoke (steam only)", SmokeTuning.TrailDensHydrolox, 0f, 1.5f);
                    SmokeTuning.TrailSizeHydrolox = Slider("    hydrogen: width", SmokeTuning.TrailSizeHydrolox, 0.2f, 1.5f);
                    SmokeTuning.TrailDensMethalox = Slider("    methane: smoke", SmokeTuning.TrailDensMethalox, 0f, 1.5f);
                    SmokeTuning.TrailSizeMethalox = Slider("    methane: width", SmokeTuning.TrailSizeMethalox, 0.2f, 1.5f);
                    SmokeTuning.TrailDensHypergolic = Slider("    hypergolic / monopropellant: smoke", SmokeTuning.TrailDensHypergolic, 0f, 1.5f);
                    SmokeTuning.TrailSizeHypergolic = Slider("    hypergolic / monopropellant: width", SmokeTuning.TrailSizeHypergolic, 0.2f, 1.5f);
                    SmokeTuning.TrailDensNuclear = Slider("    nuclear: smoke", SmokeTuning.TrailDensNuclear, 0f, 1.5f);
                    SmokeTuning.TrailSizeNuclear = Slider("    nuclear: width", SmokeTuning.TrailSizeNuclear, 0.2f, 1.5f);
                }
                SmokeTuning.NewTrailDensityK = Slider("  column of smoke (density x radius)", SmokeTuning.NewTrailDensityK, 1f, 30f);
                SmokeTuning.NewTrailErosion = Slider("  erosion depth", SmokeTuning.NewTrailErosion, 0.2f, 1f);
                SmokeTuning.NewTrailSharpness = Slider("  edge sharpness", SmokeTuning.NewTrailSharpness, 0f, 0.98f);
                SmokeTuning.NewTrailLightSoft = Slider("  soft shadows inside (edge sharpness of the light; 0 = same as the edge)", SmokeTuning.NewTrailLightSoft, 0f, 0.9f);
                SmokeTuning.NewTrailFill = Slider("  solid core (the noise only shapes the surface; 0 = off)", SmokeTuning.NewTrailFill, 0f, 0.9f);
                SmokeTuning.NewTrailNoiseFreq = Slider("  lobe frequency (1/m at 80 m)", SmokeTuning.NewTrailNoiseFreq, 0.002f, 0.05f);
                SmokeTuning.NewTrailShadowExt = Slider("  self-shadow depth", SmokeTuning.NewTrailShadowExt, 0.05f, 2f);
                SmokeTuning.NewTrailLightReach = Slider("  light march length (radii)", SmokeTuning.NewTrailLightReach, 0.3f, 3f);
                SmokeTuning.NewTrailSkyOcc = Slider("  dark undersides (sky light blocked above)", SmokeTuning.NewTrailSkyOcc, 0f, 1f);
                SmokeTuning.NewTrailEngineLight = Slider("  light of the engine flame", SmokeTuning.NewTrailEngineLight, 0f, 5f);
                SmokeTuning.NewTrailWobble = Slider("  wobble of the width and meander (0 = straight cone)", SmokeTuning.NewTrailWobble, 0f, 2f);
                SmokeTuning.NewTrailGlow = Slider("  glow of the hot exhaust", SmokeTuning.NewTrailGlow, 0f, 6f);
                SmokeTuning.NewTrailPowder = Tog(SmokeTuning.NewTrailPowder, "  powdery fringe at the edge (off by default)");
                if (SmokeTuning.NewTrailPowder) SmokeTuning.NewTrailDust = Slider("    powdery fringe amount", SmokeTuning.NewTrailDust, 0f, 1.5f);
                SmokeTuning.NewTrailRoot = Tog(SmokeTuning.NewTrailRoot, "  grows out of the flame (root)");
                SmokeTuning.NewTrailShadow = Tog(SmokeTuning.NewTrailShadow, "  casts a shadow on the ground (strength: Cast shadow settings)");
                SmokeTuning.NewTrailCloudShadow = Tog(SmokeTuning.NewTrailCloudShadow, "  Shadows of EVE's volumetric clouds on the trail");
                if (SmokeTuning.NewTrailCloudShadow) SmokeTuning.NewTrailCloudShadowStrength = Slider("    cloud shadow strength", SmokeTuning.NewTrailCloudShadowStrength, 0f, 2f);
                SmokeTuning.EveCalibOverlay = Tog(SmokeTuning.EveCalibOverlay, "    EVE cloud shadow calibration: red where the trail sees cloud shadow on the ground (camera under the clouds, look down)");
                if (SmokeTuning.EveCalibOverlay || SmokeTuning.NewTrailCloudShadow)
                {
                    int mvo = SmokeTuning.NewTrailEveMapVariant;
                    SmokeTuning.NewTrailEveMapVariant = Mathf.RoundToInt(Slider("    mapping variant (" + mvo + ": swap " + (mvo & 1) + " flip x " + ((mvo >> 1) & 1) + " flip y " + ((mvo >> 2) & 1) + " flip z " + ((mvo >> 3) & 1) + ")", mvo, 0f, 15f));
                    if (Vis("eve light volume dump") && GUILayout.Button("    Dump EVE light volume + screenshot (for the developer; look at the ground under clouds)")) EveCalibOverlay.DumpFrames = 5;
                    if (EveCalibOverlay.DumpStatus.Length > 0) GUILayout.Label("    " + EveCalibOverlay.DumpStatus);
                    if (Vis("eve light volume status")) GUILayout.Label("    EVE light volume: " + (Shader.GetGlobalTexture("directLightVolume") != null ? "found, cloud base " + (Shader.GetGlobalFloat("innerLightVolumeRadius") - (FlightGlobals.currentMainBody != null ? (float)FlightGlobals.currentMainBody.Radius : 0f)).ToString("F0") + " m, top " + (Shader.GetGlobalFloat("outerLightVolumeRadius") - (FlightGlobals.currentMainBody != null ? (float)FlightGlobals.currentMainBody.Radius : 0f)).ToString("F0") + " m" : "NOT found (no volumetric clouds here?)"));
                }
                SmokeTuning.NewTrailPhysicalLight = Tog(SmokeTuning.NewTrailPhysicalLight, "  Physical light (sun scattering + sky light; off = the old palette)");
                if (SmokeTuning.NewTrailPhysicalLight)
                {
                    SmokeTuning.NewTrailSunBright = Slider("    sun brightness", SmokeTuning.NewTrailSunBright, 0.5f, 12f);
                    SmokeTuning.NewTrailSkyBright = Slider("    sky light", SmokeTuning.NewTrailSkyBright, 0f, 3f);
                }
                SmokeTuning.NewTrailRoundLobes = Slider("  Round lobes (0 = stringy noise, 1+ = round billows)", SmokeTuning.NewTrailRoundLobes, 0f, 1.5f);
                SmokeTuning.NewTrailDetail = Slider("  small lobes on the big ones (cauliflower)", SmokeTuning.NewTrailDetail, 0f, 0.8f);
                SmokeTuning.NewTrailSilver = Slider("  sun shining through (silver lining)", SmokeTuning.NewTrailSilver, 0f, 12f);
                SmokeTuning.NewTrailTint = Slider("  colour of the light kept (0 = neutral)", SmokeTuning.NewTrailTint, 0f, 1f);
                SmokeTuning.NewTrailBillow = Slider("  big lobes (0..1)", SmokeTuning.NewTrailBillow, 0f, 1.2f);
                SmokeTuning.NewTrailBillowFreq = Slider("  big lobes frequency", SmokeTuning.NewTrailBillowFreq, 0.1f, 1f);
                SmokeTuning.NewTrailLightGain = Slider("  light gain (low = more contrast)", SmokeTuning.NewTrailLightGain, 0.03f, 0.5f);
                SmokeTuning.NewTrailSun = Slider("  sun brightness", SmokeTuning.NewTrailSun, 0.5f, 12f);
                SmokeTuning.NewTrailAmbient = Slider("  sky light", SmokeTuning.NewTrailAmbient, 0f, 2f);
                SmokeTuning.NewTrailDarkEdge = Slider("  dark edge (1 = none)", SmokeTuning.NewTrailDarkEdge, 0.02f, 1f);
            }
            Sec(8, "Shadow on the ground");
            SmokeTuning.ShadowCastStrength = Slider("Cast shadow", SmokeTuning.ShadowCastStrength, 0f, 1f);
            SmokeTuning.ShadowIntoSunMask = Tog(SmokeTuning.ShadowIntoSunMask, "  experimental, deferred rendering: put the shadow in the sun's shadow mask instead (did not show in tests - leave off)");
            if (SmokeTuning.ShadowIntoSunMask) SmokeTuning.ShadowMaskFlip = Tog(SmokeTuning.ShadowMaskFlip, "    flip it upside down (only if the shadow lands mirrored)");
            SmokeTuning.ShadowCastDensity = Slider("Cast shadow density", SmokeTuning.ShadowCastDensity, 0.05f, 2f);
            SmokeTuning.ShadowCastFloor = Slider("Cast shadow floor (sky light)", SmokeTuning.ShadowCastFloor, 0f, 0.9f);
            SmokeTuning.ShadowCastDistance = Slider("Cast reach (m)", SmokeTuning.ShadowCastDistance, 200f, 6000f);
            SmokeTuning.ShadowCastSteps = Slider("Cast steps", SmokeTuning.ShadowCastSteps, 4f, 48f);

            // ======================================================================= Plumes
            Sec(1, "Engines");
            // Takes effect for engine groups formed after the change - existing groups are only rebuilt when the
            // vessel's part count changes, e.g. at staging.
            SmokeTuning.SrbOnly = Tog(SmokeTuning.SrbOnly, " Solid motors only");
            SmokeTuning.PhysicalJetBend = Tog(SmokeTuning.PhysicalJetBend, " Plume bends by the motion of the gas (curls into the track of a turning rocket)");
            SmokeTuning.PadStreamWater = Slider("  Water of the deluge in the pad stream (kg per kg of jet)", SmokeTuning.PadStreamWater, 0f, 3f);
            SmokeTuning.PadOpacityRef = Slider("  Pad smoke: extinction at half opacity (1/m, lower = whiter)", SmokeTuning.PadOpacityRef, 2f, 60f);
            SmokeTuning.NozzleThreads = Tog(SmokeTuning.NozzleThreads, " Smoke from every nozzle of a cluster (separate threads at first, mixing into one trail)");
            SmokeTuning.PadAllEngines = Tog(SmokeTuning.PadAllEngines, "   ...but on the pad every engine (hydrolox too) feeds the pad cloud");
            SmokeTuning.LiquidPlumes = Tog(SmokeTuning.LiquidPlumes, " Plumes for liquid engines too");
            SmokeTuning.LiquidSizeScale = Slider("Liquid plume size", SmokeTuning.LiquidSizeScale, 0.05f, 1f);
            SmokeTuning.LiquidDensityScale = Slider("Liquid plume density", SmokeTuning.LiquidDensityScale, 0.02f, 1f);
            SmokeTuning.NozzleFromThrust = Tog(SmokeTuning.NozzleFromThrust, " Liquid nozzle size from thrust (not part width)");
            SmokeTuning.NozzleThrustScale = Slider("  Nozzle size scale", SmokeTuning.NozzleThrustScale, 0.3f, 2.5f);
            SmokeTuning.NozzleSolidScale = Slider("  SRB nozzle scale", SmokeTuning.NozzleSolidScale, 0.3f, 2f);
            SmokeTuning.HydrogenPlumeVisibility = Slider("  H2 visibility (x kerolox)", SmokeTuning.HydrogenPlumeVisibility, 0.02f, 1f);
            SmokeTuning.MethanePlumeVisibility = Slider("  CH4 visibility (x kerolox)", SmokeTuning.MethanePlumeVisibility, 0.02f, 1f);
            SmokeTuning.NozzleScaleEnabled = Tog(SmokeTuning.NozzleScaleEnabled, " Scale plume by engine size (nozzle diameter)");
            SmokeTuning.NozzleReferenceDiameter = Slider("Nozzle ref. diameter (m)", SmokeTuning.NozzleReferenceDiameter, 0.5f, 12f);
            SmokeTuning.NozzleScalePower = Slider("Nozzle scale power", SmokeTuning.NozzleScalePower, 0.3f, 2f);
            SmokeTuning.NozzleScaleMin = Slider("Nozzle scale floor", SmokeTuning.NozzleScaleMin, 0.02f, 0.5f);

            Sec(1, "Waterfall");
            SmokeTuning.WaterfallIntegration = Tog(SmokeTuning.WaterfallIntegration, " Waterfall: smoke starts where the flame ends");
            SmokeTuning.WaterfallHandoff = Slider("  Smoke start (x flame length)", SmokeTuning.WaterfallHandoff, 0f, 1.5f);
            SmokeTuning.WaterfallMatchStart = Tog(SmokeTuning.WaterfallMatchStart, "  smoke starts as wide as the flame end (physics)");
            SmokeTuning.WaterfallStartWidth = Slider("  start width (x flame radius)", SmokeTuning.WaterfallStartWidth, 0.2f, 1.5f);
            SmokeTuning.WaterfallHazeLength = Slider("  Haze length (x flame length)", SmokeTuning.WaterfallHazeLength, 0.8f, 3f);

            Sec(1, "Solid motor plume");
            SmokeTuning.SrbRootEnabled = Tog(SmokeTuning.SrbRootEnabled, " Root from nozzle");
            SmokeTuning.SrbThinningPower = Slider("SRB spread thinning", SmokeTuning.SrbThinningPower, 0f, 3f);
            SmokeTuning.SrbTurnSmoothing = Slider("Turn smoothing (s)", SmokeTuning.SrbTurnSmoothing, 0.02f, 1.5f);
            SmokeTuning.SrbCrossflowBend = Slider("Crossflow bend", SmokeTuning.SrbCrossflowBend, 0f, 6f);
            SmokeTuning.SrbRootFade = Slider("Smoke fade-in at nozzle", SmokeTuning.SrbRootFade, 0f, 1f);
            SmokeTuning.SrbRootRadius = Slider("Root radius", SmokeTuning.SrbRootRadius, 0.1f, 2f);
            SmokeTuning.SpawnInterval = Slider("Puff spawn interval (s)", SmokeTuning.SpawnInterval, 0.005f, 0.2f);
            SmokeTuning.SpawnSpacingScale = Slider("Puff spacing (x radius)", SmokeTuning.SpawnSpacingScale, 0.1f, 2f);
            SmokeTuning.SpineBandSeconds = Slider("Spine detail falloff (s, 0 = off)", SmokeTuning.SpineBandSeconds, 0f, 20f);
            SmokeTuning.NozzleFactorEase = Slider("Nozzle size easing (s)", SmokeTuning.NozzleFactorEase, 0.2f, 12f);
            SmokeTuning.StartSize = Slider("Start radius at nozzle (m)", SmokeTuning.StartSize, 0.2f, 12f);
            SmokeTuning.GrowthTime = Slider("Time to full size (s)", SmokeTuning.GrowthTime, 2f, 90f);
            SmokeTuning.SrbSpreadRate = Slider("Spread rate", SmokeTuning.SrbSpreadRate, 0.1f, 4f);
            SmokeTuning.SrbJetPhaseTime = Slider("Jet phase (s) - slow start", SmokeTuning.SrbJetPhaseTime, 0.1f, 15f);
            SmokeTuning.GrowthSharpness = Slider("Growth curve (non-SRB)", SmokeTuning.GrowthSharpness, 0.5f, 5f);
            SmokeTuning.SpeedBillowMin = Slider("Full size below speed (m/s)", SmokeTuning.SpeedBillowMin, 0f, 300f);
            SmokeTuning.SpeedThinTrailMax = Slider("Thinnest trail at speed (m/s)", SmokeTuning.SpeedThinTrailMax, 50f, 2500f);
            SmokeTuning.ThinTrailSizeMult = Slider("Size at that speed (x)", SmokeTuning.ThinTrailSizeMult, 0.1f, 2f);
            SmokeTuning.SrbPostLimitGrowth = Slider("Growth past limit", SmokeTuning.SrbPostLimitGrowth, 0f, 1f);
            SmokeTuning.SrbTailTime = Slider("Post-burnout tail (s)", SmokeTuning.SrbTailTime, 0f, 12f);

            Sec(1, "Landing cloud");
            SmokeTuning.LandingCloudEnabled = Tog(SmokeTuning.LandingCloudEnabled, " Enabled (bypasses solid-only for any firing engine)");
            SmokeTuning.LandingCloudAltitude = Slider("Trigger altitude (m)", SmokeTuning.LandingCloudAltitude, 20f, 1000f);
            SmokeTuning.LandingCloudVerticalSpeed = Slider("Trigger descent rate (m/s)", SmokeTuning.LandingCloudVerticalSpeed, -30f, -0.5f);
            SmokeTuning.LandingCloudSizeBoost = Slider("Splash puff size", SmokeTuning.LandingCloudSizeBoost, 0.05f, 6f);
            SmokeTuning.LandingCloudDensityBoost = Slider("Splash density (0-1)", SmokeTuning.LandingCloudDensityBoost, 0.05f, 1f);
            SmokeTuning.LandingCloudSpreadSpeed = Slider("Splash outward speed (m/s)", SmokeTuning.LandingCloudSpreadSpeed, 10f, 250f);
            SmokeTuning.LandingSplashReachScale = Slider("Splash reach scale (10-28 m base)", SmokeTuning.LandingSplashReachScale, 0.3f, 4f);
            SmokeTuning.LandingCloudPuffsPerTick = Mathf.RoundToInt(Slider("Splash ray count", SmokeTuning.LandingCloudPuffsPerTick, 1f, 16f));

            // ======================================================================= Physics
            Sec(2, "Real physics");
            SmokeTuning.PhysicsMode = Tog(SmokeTuning.PhysicsMode, " Real physics: entrainment, buoyancy, wind, wall jet");
            SmokeTuning.PhysAlphaJet = Slider("Entrainment, jet (alpha)", SmokeTuning.PhysAlphaJet, 0.02f, 0.2f);
            SmokeTuning.PhysAlphaThermal = Slider("Entrainment, thermal (alpha)", SmokeTuning.PhysAlphaThermal, 0.05f, 0.5f);
            SmokeTuning.PhysJetMomentum = Slider("Exhaust momentum of the trail in flight (x, 0 = at rest, no lean; near the pad always full)", SmokeTuning.PhysJetMomentum, 0f, 1.5f);
            SmokeTuning.PhysWallMomentum = Slider("Wall jet momentum kept (x)", SmokeTuning.PhysWallMomentum, 0f, 1f);
            SmokeTuning.PhysAmbientTurb = Slider("Ambient turbulence (m/s)", SmokeTuning.PhysAmbientTurb, 0f, 1.5f);
            SmokeTuning.PhysExhaustTemp = Slider("Exhaust temperature (K)", SmokeTuning.PhysExhaustTemp, 800f, 3500f);
            SmokeTuning.PhysWindRoughness = Slider("Wind roughness z0 (m)", SmokeTuning.PhysWindRoughness, 0.001f, 2f);
            SmokeTuning.WindSpeed = Slider("Wind (m/s)", SmokeTuning.WindSpeed, 0f, 8f);
            SmokeTuning.PhysRadiusScale = Slider("Puff visual size (x)", SmokeTuning.PhysRadiusScale, 0.2f, 3f);
            SmokeTuning.PhysMaxGrowth = Slider("Max widening (x release radius, 0 = off)", SmokeTuning.PhysMaxGrowth, 0f, 20f);
            SmokeTuning.PhysFarTaper = Slider("Far taper (old smoke narrower, 0 = tube)", SmokeTuning.PhysFarTaper, 0f, 0.9f);
            SmokeTuning.PhysVisibleDilution = Slider("Visible dilution (x kind, 0 = off)", SmokeTuning.PhysVisibleDilution, 0f, 4f);
            SmokeTuning.PhysDensityExp = Slider("Smoke thinning with dilution", SmokeTuning.PhysDensityExp, 0.1f, 1.5f);
            SmokeTuning.PhysMaxDilution = Slider("Max jet dilution at release", SmokeTuning.PhysMaxDilution, 2f, 200f);

            Sec(2, "Ground & pad");
            SmokeTuning.GroundImpingementEnabled = Tog(SmokeTuning.GroundImpingementEnabled, " Ground impingement kick (near-pad bounce) [EXPERIMENTAL]");
            SmokeTuning.PhysRestitution = Slider("Ground rebound", SmokeTuning.PhysRestitution, 0f, 0.8f);
            SmokeTuning.PhysWallJet = Slider("Wall jet fraction", SmokeTuning.PhysWallJet, 0f, 1.2f);
            SmokeTuning.PhysGroundFriction = Slider("Wall friction", SmokeTuning.PhysGroundFriction, 0f, 0.5f);
            SmokeTuning.WallLumpGap = Slider("Side clouds: break into lumps (0 = tube)", SmokeTuning.WallLumpGap, 0f, 3f);
            SmokeTuning.PhysWallChains = Tog(SmokeTuning.PhysWallChains, " Side clouds: split ground-hit flow into branches");
            SmokeTuning.PhysCollide = Tog(SmokeTuning.PhysCollide, " Collide with the scene (pad, trench, buildings)");
            SmokeTuning.PhysCollideBudget = Slider("Scene raycasts per tick", SmokeTuning.PhysCollideBudget, 0f, 1500f);

            Sec(2, "Pad cloud");
            SmokeTuning.PadCloud = Tog(SmokeTuning.PadCloud, " Pad cloud: billows rolling out over the pad along the flow paths (replaces side puffs)");
            SmokeTuning.PadCloudLife = Slider("  Cloud lifetime (s)", SmokeTuning.PadCloudLife, 4f, 40f);
            SmokeTuning.PadCloudScale = Slider("  Billow size (x)", SmokeTuning.PadCloudScale, 0.3f, 3f);
            SmokeTuning.PadCloudDensity = Slider("  Density (x)", SmokeTuning.PadCloudDensity, 0.1f, 2f);
            SmokeTuning.PadCloudSpeed = Slider("  Outflow speed (x)", SmokeTuning.PadCloudSpeed, 0.2f, 3f);
            SmokeTuning.PadCloudLift = Slider("  Buoyant lift (x)", SmokeTuning.PadCloudLift, 0.1f, 4f);

            Sec(2, "Pad flow rays (experimental)");
            SmokeTuning.PadFlowRays = Tog(SmokeTuning.PadFlowRays, " Find the exhaust flow over the pad with rays (impact point, surface paths)");
            SmokeTuning.PadFlowVisibleOnly = Tog(SmokeTuning.PadFlowVisibleOnly, "  Only collide with visible surfaces (ignore colliders with no renderer)");
            SmokeTuning.PadFlowDebug = Tog(SmokeTuning.PadFlowDebug, "  Draw the paths (yellow lines)");
            SmokeTuning.PadFlowRayCount = Mathf.RoundToInt(Slider("  Rays around the impact", SmokeTuning.PadFlowRayCount, 6f, 64f));
            SmokeTuning.PadFlowStep = Slider("  Step along the surface (m)", SmokeTuning.PadFlowStep, 1f, 12f);
            SmokeTuning.PadFlowMaxLength = Slider("  Longest path (m)", SmokeTuning.PadFlowMaxLength, 20f, 600f);

            Sec(2, "Deflector & water");
            SmokeTuning.PadWater = Tog(SmokeTuning.PadWater, " Water deluge: steam in the flow that hits a deflector");
            SmokeTuning.PadWaterAlways = Tog(SmokeTuning.PadWaterAlways, " Always water (on every ground impact, not only a detected deflector)");
            SmokeTuning.PhysWallDecay = Slider("Wall jet speed decay with distance (1 = theory r^-1.1, 0 = none)", SmokeTuning.PhysWallDecay, 0f, 2f);
            SmokeTuning.PhysWallLift = Slider("Wall jet climb (x sideways speed, 0 = free)", SmokeTuning.PhysWallLift, 0f, 1f);
            SmokeTuning.PhysWallSpread = Slider("Wall jet spread (thickness / distance, theory ~0.1)", SmokeTuning.PhysWallSpread, 0f, 0.3f);
            SmokeTuning.PadWaterFrac = Slider("Water in the deflected flow (mass fraction)", SmokeTuning.PadWaterFrac, 0f, 0.95f);
            SmokeTuning.DeflectorFire = Slider("Glow of the hot deflected flame (0 = off)", SmokeTuning.DeflectorFire, 0f, 1.5f);

            Sec(2, "Steam");
            SmokeTuning.PhysHumidity = Slider("Air humidity (steam condensation)", SmokeTuning.PhysHumidity, 0f, 1f);
            SmokeTuning.SteamMinVisibility = Slider("Visibility when uncondensed", SmokeTuning.SteamMinVisibility, 0f, 1f);
            SmokeTuning.SteamCondRef = Slider("Condensate for full density", SmokeTuning.SteamCondRef, 0.002f, 0.05f);
            SmokeTuning.SteamBright = Slider("Steam brightness floor (liquid)", SmokeTuning.SteamBright, 0f, 1f);
            SmokeTuning.SteamWhite = Slider("Steam whiteness (liquid)", SmokeTuning.SteamWhite, 0f, 1f);

            // ======================================================================= Shape
            Sec(3, "Puff shape");
            SmokeTuning.MaxPuffSize = Slider("Puff size (m)", SmokeTuning.MaxPuffSize, 4f, 200f);
            SmokeTuning.SpawnOffset = Slider("Spawn offset (m)", SmokeTuning.SpawnOffset, 2f, 150f);
            SmokeTuning.SilhouetteWarpStrength = Slider("Warp (m)", SmokeTuning.SilhouetteWarpStrength, 0f, 40f);
            SmokeTuning.SilhouetteWarpScale = Slider("Lobe size", SmokeTuning.SilhouetteWarpScale, 0.01f, 0.4f);
            SmokeTuning.VortexStrength = Slider("Swirl (m)", SmokeTuning.VortexStrength, 0f, 25f);
            SmokeTuning.VortexScale = Slider("Swirl scale", SmokeTuning.VortexScale, 0.01f, 0.6f);
            SmokeTuning.ColumnWander = Slider("Column wander", SmokeTuning.ColumnWander, 0f, 0.6f);
            SmokeTuning.NoiseAnimation = Slider("Surface churn", SmokeTuning.NoiseAnimation, 0f, 0.5f);
            SmokeTuning.SpineBlend = Slider("Spine blend", SmokeTuning.SpineBlend, 0f, 0.5f);

            Sec(3, "Noise & detail");
            SmokeTuning.GradientNoise = Tog(SmokeTuning.GradientNoise, " Gradient (simplex-like) noise - applies at next start");
            SmokeTuning.SilhouetteNoiseScale = Slider("Noise scale", SmokeTuning.SilhouetteNoiseScale, 0.02f, 0.5f);
            SmokeTuning.AntiTile = Slider("Noise anti-tiling (0 = off)", SmokeTuning.AntiTile, 0f, 1f);
            SmokeTuning.DetailStrength = Slider("Detail", SmokeTuning.DetailStrength, 0f, 1f);
            SmokeTuning.DetailBias = Slider("Detail add-only", SmokeTuning.DetailBias, 0f, 1f);
            SmokeTuning.SizeFreqTracking = Slider("Size-tracked freq", SmokeTuning.SizeFreqTracking, 0f, 1f);
            SmokeTuning.FineDetail = Slider("Fine detail octave", SmokeTuning.FineDetail, 0f, 1f);
            SmokeTuning.GrainStrength = Slider("Grain", SmokeTuning.GrainStrength, 0f, 1f);
            SmokeTuning.GrainScale = Slider("Grain scale", SmokeTuning.GrainScale, 0.3f, 4f);
            SmokeTuning.MacroStrength = Slider("Macro density", SmokeTuning.MacroStrength, 0f, 1f);
            SmokeTuning.MacroNoiseScale = Slider("Macro scale", SmokeTuning.MacroNoiseScale, 0.002f, 0.08f);
            SmokeTuning.LightJitter = Slider("Grain: light jitter", SmokeTuning.LightJitter, 0f, 1f);
            SmokeTuning.MarchJitter = Slider("Grain: march jitter", SmokeTuning.MarchJitter, 0f, 1f);

            Sec(3, "Edges & billows");
            SmokeTuning.EdgeErosionStrength = Slider("Edge erosion", SmokeTuning.EdgeErosionStrength, 0f, 1f);
            SmokeTuning.ErodeMix = Slider("Threshold erosion mix", SmokeTuning.ErodeMix, 0f, 1f);
            SmokeTuning.ErodeDepth = Slider("Erosion depth", SmokeTuning.ErodeDepth, 0.05f, 1f);
            SmokeTuning.ErodeSharp = Slider("Erosion edge sharpness", SmokeTuning.ErodeSharp, 0f, 0.95f);
            SmokeTuning.ErodeBoost = Slider("Erosion profile boost", SmokeTuning.ErodeBoost, 0.8f, 2.5f);
            SmokeTuning.EdgeProtrusion = Slider("Edge protrusion (lobes stand out)", SmokeTuning.EdgeProtrusion, 0f, 1f);
            SmokeTuning.EdgeRoundLobes = Tog(SmokeTuning.EdgeRoundLobes, " Protrusion: round lobes instead of wisps");
            SmokeTuning.EdgeWispStretch = Slider("  Wisp stretch (small = long streaks)", SmokeTuning.EdgeWispStretch, 0.05f, 1f);
            SmokeTuning.EdgeSwirl = Slider("  Wisp swirl (twist)", SmokeTuning.EdgeSwirl, 0f, 4f);
            SmokeTuning.LobeMix = Slider("Sphere lobes mix", SmokeTuning.LobeMix, 0f, 1f);
            SmokeTuning.LobeCell = Slider("Sphere lobe cell (x radius)", SmokeTuning.LobeCell, 0.2f, 2f);
            SmokeTuning.BillowMix = Slider("Billow mix (0 = old detail)", SmokeTuning.BillowMix, 0f, 1f);
            SmokeTuning.BillowCell = Slider("Billow cell (x radius)", SmokeTuning.BillowCell, 0.2f, 3f);
            SmokeTuning.BillowAmp = Slider("Billow amplitude (x radius)", SmokeTuning.BillowAmp, 0.02f, 0.6f);
            SmokeTuning.BillowErode = Slider("Billow erosion (detached clumps)", SmokeTuning.BillowErode, 0f, 1f);
            SmokeTuning.EdgeFeather = Slider("Edge feather", SmokeTuning.EdgeFeather, 1f, 3f);
            SmokeTuning.ThinEdgeSoftness = Slider("Thin edge softness", SmokeTuning.ThinEdgeSoftness, 0.15f, 1f);
            SmokeTuning.ThinDetailFade = Slider("Thin detail fade", SmokeTuning.ThinDetailFade, 0f, 1f);

            // ======================================================================= Lighting
            Sec(4, "Density");
            SmokeTuning.Opacity = Slider("Opacity (transparency)", SmokeTuning.Opacity, 0f, 1f);
            SmokeTuning.Density = Slider("Density", SmokeTuning.Density, 0.02f, 6f);
            SmokeTuning.Absorption = Slider("Absorption", SmokeTuning.Absorption, 0.2f, 4f);

            Sec(4, "Sun & self-shadow");
            SmokeTuning.AmbientFloor = Slider("Ambient floor", SmokeTuning.AmbientFloor, 0f, 1f);
            SmokeTuning.ShadowStrength = Slider("Self-shadow", SmokeTuning.ShadowStrength, 0f, 1f);
            SmokeTuning.ShadowExtinction = Slider("Shadow softness", SmokeTuning.ShadowExtinction, 0.002f, 0.15f);
            SmokeTuning.ShadowDarkness = Slider("Shadow darkness", SmokeTuning.ShadowDarkness, 0f, 1f);
            SmokeTuning.LightReach = Slider("Light reach (m)", SmokeTuning.LightReach, 20f, 400f);
            SmokeTuning.LightSizeScaling = Slider("Light reach grows w/ puff size", SmokeTuning.LightSizeScaling, 0f, 1f);
            SmokeTuning.DetailShadeStrength = Slider("Detail self-shade", SmokeTuning.DetailShadeStrength, 0f, 3f);
            SmokeTuning.NormalShade = Slider("Sun side / shade side (N.L)", SmokeTuning.NormalShade, 0f, 1.5f);
            SmokeTuning.FacingShade = Slider("Facing shade (normal diffuse)", SmokeTuning.FacingShade, 0f, 1f);
            SmokeTuning.SunlitBrightness = Slider("Lit brightness", SmokeTuning.SunlitBrightness, 0.5f, 1f);
            SmokeTuning.NightAmbient = Slider("Night ambient", SmokeTuning.NightAmbient, 0f, 1f);
            SmokeTuning.TerminatorSoftness = Slider("Terminator (m)", SmokeTuning.TerminatorSoftness, 100f, 20000f);

            Sec(4, "Scattering");
            SmokeTuning.UseMiePhase = Tog(SmokeTuning.UseMiePhase, " Mie phase (HG+Draine) - replaces the g sliders below");
            SmokeTuning.MieParticleDiameter = Slider("Mie particle size (um)", SmokeTuning.MieParticleDiameter, 3f, 50f);
            SmokeTuning.MieFloor = Slider("Mie multi-scatter floor", SmokeTuning.MieFloor, 0f, 1f);
            SmokeTuning.ForwardScatterG = Slider("Fwd scatter (g)", SmokeTuning.ForwardScatterG, 0f, 0.99f);
            SmokeTuning.ScatterIntensity = Slider("Scatter int.", SmokeTuning.ScatterIntensity, 0f, 5f);
            SmokeTuning.MultiScatterG = Slider("Multi-scat g", SmokeTuning.MultiScatterG, 0f, 0.6f);
            SmokeTuning.MultiScatterIntensity = Slider("Multi-scat int.", SmokeTuning.MultiScatterIntensity, 0f, 10f);
            SmokeTuning.MSMix = Slider("Multi-scatter mix", SmokeTuning.MSMix, 0f, 1f);
            SmokeTuning.PowderStrength = Slider("Powder", SmokeTuning.PowderStrength, 0f, 3f);
            SmokeTuning.DarkEdge = Slider("Dark edge minimum", SmokeTuning.DarkEdge, 0f, 1f);
            SmokeTuning.PhaseMax = Slider("Sun hotspot limit (0 = off)", SmokeTuning.PhaseMax, 0f, 3f);

            Sec(4, "Sky & ambient");
            SmokeTuning.ProfileAO = Tog(SmokeTuning.ProfileAO, " Profile ambient occlusion (cheaper)");
            SmokeTuning.SkyOcclusionStrength = Slider("Sky occlusion", SmokeTuning.SkyOcclusionStrength, 0f, 1f);
            SmokeTuning.SkyOcclusionDistance = Slider("Sky occl. dist (m)", SmokeTuning.SkyOcclusionDistance, 10f, 150f);
            SmokeTuning.SkyTintStrength = Slider("Sky tint str.", SmokeTuning.SkyTintStrength, 0f, 1f);
            SmokeTuning.SceneAmbientBlend = Slider("Scene ambient", SmokeTuning.SceneAmbientBlend, 0f, 1f);
            SmokeTuning.AnalyticAmbient = Slider("Analytic sky ambient", SmokeTuning.AnalyticAmbient, 0f, 1f);
            SmokeTuning.SkyTint = Slider("Sky tint", SmokeTuning.SkyTint, 0f, 1f);
            SmokeTuning.GroundTint = Slider("Ground tint", SmokeTuning.GroundTint, 0f, 1f);
            SmokeTuning.Washout = Slider("Washout", SmokeTuning.Washout, 0f, 1f);
            SmokeTuning.WashoutDesaturate = Slider("Washout desat", SmokeTuning.WashoutDesaturate, 0f, 1f);

            // ======================================================================= Effects
            Sec(5, "Physical plumes");
            SmokeTuning.PhysicalPlumes = Tog(SmokeTuning.PhysicalPlumes, " Physical plumes: shock diamonds, core and afterburning from the engine's own numbers");
            SmokeTuning.PlumeHideWaterfall = Tog(SmokeTuning.PlumeHideWaterfall, "  Switch off Waterfall's flame while the physical plume is drawn");
            SmokeTuning.PlumeExposure = Slider("  Brightness (exposure)", SmokeTuning.PlumeExposure, 0.2f, 5f);
            SmokeTuning.PlumeStreak = Slider("  Long streak at altitude (a long exposure: smooth, narrow, hundreds of metres; 0 = off)", SmokeTuning.PlumeStreak, 0f, 1f);
            SmokeTuning.PlumeScale = Slider("  Length (x)", SmokeTuning.PlumeScale, 0.3f, 3f);
            SmokeTuning.PlumeSteps = Mathf.RoundToInt(Slider("  Quality (ray steps)", SmokeTuning.PlumeSteps, 32f, 192f));
            SmokeTuning.PlumeMaxEngines = Mathf.RoundToInt(Slider("  Nozzles drawn (nearest first)", SmokeTuning.PlumeMaxEngines, 1f, 40f));
            SmokeTuning.PlumeDeflect = Tog(SmokeTuning.PlumeDeflect, "  Flame turned by the pad: the plume is deflected by the surface it hits (needs the GPU pad cloud for the geometry)");
            SmokeTuning.PlumeDeflectSpread = Slider("  Turned flame thickening (x)", SmokeTuning.PlumeDeflectSpread, 0.3f, 3f);

            Sec(5, "Pad fluid (GPU)");
            SmokeTuning.PadSurface = Tog(SmokeTuning.PadSurface, " Surface mode: smoke and the root of the trail flow along the pad surface (the pre-rendered height map: trench, deflector, walls), sampled every 30 cm; works with the GPU cloud off");
            SmokeTuning.PadFluid = Tog(SmokeTuning.PadFluid, " GPU pad cloud: real flow over the pad, trench and deflector (replaces the billows on rails)");
            if (ModCompat.GroundBlastFxPresent)
                Note("  GroundBlastFx is installed: both mods draw launch-pad steam. Turn off 'Pad steam' in GroundBlastFx's window (enablePadSteam), or switch this off, so that only one draws it.");
            SmokeTuning.PadFluidCell = Slider("  Grid cell (m, smaller = finer, slower)", SmokeTuning.PadFluidCell, 1.5f, 8f);
            SmokeTuning.PadFluidDensity = Slider("  Density", SmokeTuning.PadFluidDensity, 0.2f, 3f);
            SmokeTuning.PadFluidErode = Slider("  Cauliflower (surface erosion)", SmokeTuning.PadFluidErode, 0f, 1.2f);
            SmokeTuning.PadFluidFire = Slider("  Fire glow", SmokeTuning.PadFluidFire, 0f, 4f);
            SmokeTuning.PadFluidSun = Slider("  Sun", SmokeTuning.PadFluidSun, 0.2f, 4f);
            SmokeTuning.PadFluidAmbient = Slider("  Ambient", SmokeTuning.PadFluidAmbient, 0f, 2f);
            SmokeTuning.PadFluidExposure = Slider("  Exposure", SmokeTuning.PadFluidExposure, 0.3f, 3f);
            SmokeTuning.PadFluidJet = Slider("  Jet speed (x)", SmokeTuning.PadFluidJet, 0.3f, 2.5f);
            SmokeTuning.PadFluidSize = Slider("  Cloud size (jet width, x)", SmokeTuning.PadFluidSize, 0.4f, 2.5f);
            SmokeTuning.PadFluidSpread = Slider("  Spreading speed (x)", SmokeTuning.PadFluidSpread, 0f, 4f);
            SmokeTuning.PadFluidBuoyancy = Slider("  Buoyancy (x)", SmokeTuning.PadFluidBuoyancy, 0f, 3f);
            SmokeTuning.PadFluidVorticity = Slider("  Whirls (vorticity, x)", SmokeTuning.PadFluidVorticity, 0f, 3f);
            SmokeTuning.PadFluidLife = Slider("  Cloud life after liftoff (s)", SmokeTuning.PadFluidLife, 10f, 120f);
            SmokeTuning.PadFluidSteps = Mathf.RoundToInt(Slider("  Quality (ray steps)", SmokeTuning.PadFluidSteps, 24f, 160f));
            SmokeTuning.PadFluidShock = Tog(SmokeTuning.PadFluidShock, " Ignition shock wave: the pressure wave of the start pushes the smoke and condenses a ring of vapour");
            SmokeTuning.PadFluidShockStrength = Slider("  Shock strength (x)", SmokeTuning.PadFluidShockStrength, 0.2f, 3f);
            SmokeTuning.PadFluidShockSize = Slider("  Wave size (reach and thickness, x)", SmokeTuning.PadFluidShockSize, 0.3f, 2.5f);
            SmokeTuning.PadFluidShockSpeed = Slider("  Wave speed (x)", SmokeTuning.PadFluidShockSpeed, 0.3f, 3f);
            SmokeTuning.PadFluidWater = Tog(SmokeTuning.PadFluidWater, " Water physics: deluge spray that evaporates in the hot gas (cooling it) and condenses into steam");
            SmokeTuning.PadFluidDebug = Tog(SmokeTuning.PadFluidDebug, " DEBUG: show what the solver sees (green = rays and render agree, red = rays only, blue = render only, grey = nothing)");

            Sec(5, "Heat haze");
            SmokeTuning.HeatHaze = Tog(SmokeTuning.HeatHaze, " Heat haze over the nozzle (refraction)");
            SmokeTuning.HeatHazeStrength = Slider("  Haze strength", SmokeTuning.HeatHazeStrength, 0f, 1f);
            SmokeTuning.HeatHazeAmount = Slider("  Haze displacement", SmokeTuning.HeatHazeAmount, 0f, 1f);
            SmokeTuning.HeatHazeMaxShift = Slider("  Haze max shift (screen fraction)", SmokeTuning.HeatHazeMaxShift, 0.001f, 0.03f);
            SmokeTuning.HeatHazeLengthDiameters = Slider("  Haze length (nozzle diameters)", SmokeTuning.HeatHazeLengthDiameters, 3f, 40f);
            SmokeTuning.HeatHazeScale = Slider("  Haze noise size (m)", SmokeTuning.HeatHazeScale, 0.3f, 8f);
            SmokeTuning.HeatHazeSpeed = Slider("  Haze stream speed (m/s)", SmokeTuning.HeatHazeSpeed, 0f, 120f);

            Sec(5, "Over the clouds");
            SmokeTuning.SmokeLast = Tog(SmokeTuning.SmokeLast, " Draw the smoke last (after all clouds, before the image effects): EVE's volumetric clouds cover smoke that lies in front of them - try this if the trail has holes at some camera angles");
            SmokeTuning.SmokeBeforeClouds = Tog(SmokeTuning.SmokeBeforeClouds, " TEST: draw the smoke before the transparent queue, so cloud layers (EVE / Scatterer) are drawn OVER it instead of the smoke lying over them");

            Sec(5, "Aerial perspective");
            SmokeTuning.AerialStrength = Slider("Aerial perspective (distant haze)", SmokeTuning.AerialStrength, 0f, 1f);
            SmokeTuning.ScattererSunTint = Tog(SmokeTuning.ScattererSunTint, " Light the smoke with the colour of the sun from Scatterer (orange at sunset)");
            SmokeTuning.ScattererTintStrength = Slider("  Strength of that tint", SmokeTuning.ScattererTintStrength, 0f, 1f);
            SmokeTuning.AerialDistance = Slider("  Haze distance (m, sea level)", SmokeTuning.AerialDistance, 2000f, 60000f);

            Sec(5, "Engine flame light");
            SmokeTuning.EngineLights = Tog(SmokeTuning.EngineLights, " Real scene light from engine flames (pad, ground, rocket)");
            SmokeTuning.EngineLightIntensity = Slider("  Scene light intensity", SmokeTuning.EngineLightIntensity, 0f, 6f);
            SmokeTuning.EngineLightRange = Slider("  Scene light range (x)", SmokeTuning.EngineLightRange, 0.3f, 3f);
            SmokeTuning.FlameLightStrength = Slider("Engine flame light", SmokeTuning.FlameLightStrength, 0f, 4f);
            SmokeTuning.FlameLightRange = Slider("Flame light range (m)", SmokeTuning.FlameLightRange, 3f, 200f);
            SmokeTuning.FlameColorR = Slider("  flame R", SmokeTuning.FlameColorR, 0f, 1f);
            SmokeTuning.FlameColorG = Slider("  flame G", SmokeTuning.FlameColorG, 0f, 1f);
            SmokeTuning.FlameColorB = Slider("  flame B", SmokeTuning.FlameColorB, 0f, 1f);

            Sec(5, "Rim & edge glow");
            SmokeTuning.RimLight = Slider("Rim light (bright edge)", SmokeTuning.RimLight, 0f, 3f);
            SmokeTuning.EdgeGlow = Slider("Edge glow (silver lining)", SmokeTuning.EdgeGlow, 0f, 1f);

            // ======================================================================= Explosions
            Sec(6, "Explosions");
            SmokeTuning.ExplosionsEnabled = Tog(SmokeTuning.ExplosionsEnabled, " Volumetric explosions (replace the stock blast effect)");
            SmokeTuning.ExplosionFluid = Tog(SmokeTuning.ExplosionFluid, " GPU fluid explosions: the blast is a simulated flow (fireball, rising billows, rolling cloud); off = the older blob model");
            SmokeTuning.ExplosionFluidSpeed = Slider("Fluid blast speed (x, 1 = real time; lower = slower and heavier)", SmokeTuning.ExplosionFluidSpeed, 0.2f, 1.5f);
            SmokeTuning.ExplosionShockLens = Tog(SmokeTuning.ExplosionShockLens, " Shock front bends the light (a ring that races over the scenery; speed and strength from the physics of the blast)");
            SmokeTuning.ExplosionShockLensGain = Slider("  Lens strength (x; 1 = the real refraction of air, which is almost invisible)", SmokeTuning.ExplosionShockLensGain, 0f, 20f);
            SmokeTuning.ExplosionSound = Tog(SmokeTuning.ExplosionSound, " The bang: arrives with the front (the speed of sound of THIS air), loudness from the overpressure at your distance");
            SmokeTuning.ExplosionSoundVolume = Slider("  Bang volume (x)", SmokeTuning.ExplosionSoundVolume, 0f, 4f);
            SmokeTuning.ExplosionShake = Slider("Camera shake of the arriving wave (x)", SmokeTuning.ExplosionShake, 0f, 3f);
            SmokeTuning.ExplosionDust = Tog(SmokeTuning.ExplosionDust, " Dust lifted off the ground by the passing shock front (where the wind behind the front is strong enough)");
            SmokeTuning.ExplosionDebris = Tog(SmokeTuning.ExplosionDebris, " Fragments thrown out by a blast (burning, with trails)");
            SmokeTuning.ExplosionDebrisAmount = Slider("  Number of fragments (x)", SmokeTuning.ExplosionDebrisAmount, 0f, 3f);
            SmokeTuning.ExplosionVacuum = Tog(SmokeTuning.ExplosionVacuum, " Blasts in a vacuum: free expansion of the gas (needs an oxidiser aboard), no air wave, no sound");
            SmokeTuning.ExplosionShock = Slider("Shock wave of a blast (x: the push of the air, the ring of vapour and the dust on the ground)", SmokeTuning.ExplosionShock, 0f, 3f);
            SmokeTuning.ExplosionFluidErosion = Slider("Fluid blast: ragged surface", SmokeTuning.ExplosionFluidErosion, 0f, 1.5f);
            SmokeTuning.ExplosionMushroom = Tog(SmokeTuning.ExplosionMushroom, " Mushroom cloud for big tank blasts (vortex ring on a stem); the fuel (hydrogen, methane, kerosene, solid) sets the colour of the smoke and the fire");
            SmokeTuning.ExplosionScale = Slider("Size (x, radius ~ cube root of the energy)", SmokeTuning.ExplosionScale, 0.2f, 4f);
            SmokeTuning.ExplosionLife = Slider("Duration (x)", SmokeTuning.ExplosionLife, 0.3f, 3f);
            SmokeTuning.ExplosionFire = Slider("Fire glow and flash (x)", SmokeTuning.ExplosionFire, 0f, 3f);
            Note("  Test blasts, 120 m in front of the camera (in flight): the same blast with a different fuel");
            if (Btn("  Test: 50 t kerosene (dark soot)")) PlumeExplosions.TestBlast("kerosene", 50000f);
            if (Btn("  Test: 50 t methane (white, more fire)")) PlumeExplosions.TestBlast("methane", 50000f);
            if (Btn("  Test: 50 t hydrogen (white, most fire)")) PlumeExplosions.TestBlast("hydrogen", 50000f);
            if (Btn("  Test: 5 t kerosene (small, no mushroom)")) PlumeExplosions.TestBlast("kerosene", 5000f);
            if (Btn("  Test: 20 t kerosene + LOX in a VACUUM (forced; the same blast as in space)")) PlumeExplosions.TestBlast("kerosene", 20000f, true);

            // ======================================================================= Extras
            Sec(7, "Altitude bloom (jellyfish)");
            SmokeTuning.JellyfishEnabled = Tog(SmokeTuning.JellyfishEnabled, " Enabled");
            SmokeTuning.JellyfishSpread = Slider("Bell spread", SmokeTuning.JellyfishSpread, 0f, 150f);
            SmokeTuning.JellyfishSizeBoost = Slider("Size boost", SmokeTuning.JellyfishSizeBoost, 1f, 600f);
            SmokeTuning.JellyfishExpandTime = Slider("Expand time (s)", SmokeTuning.JellyfishExpandTime, 0.2f, 15f);
            SmokeTuning.JellyfishAlphaFloor = Slider("Thin-air alpha", SmokeTuning.JellyfishAlphaFloor, 0f, 1f);
            SmokeTuning.JellyfishThinningPower = Slider("Bloom thinning", SmokeTuning.JellyfishThinningPower, 0f, 3f);

            Sec(7, "Retro umbrella");
            SmokeTuning.UmbrellaEnabled = Tog(SmokeTuning.UmbrellaEnabled, " Enabled");
            SmokeTuning.UmbrellaSpread = Slider("Canopy spread", SmokeTuning.UmbrellaSpread, 0f, 120f);
            SmokeTuning.UmbrellaJet = Slider("Nozzle jet", SmokeTuning.UmbrellaJet, 0f, 300f);
            SmokeTuning.UmbrellaBackflowFraction = Slider("Backflow (x speed)", SmokeTuning.UmbrellaBackflowFraction, 0f, 1.5f);
            SmokeTuning.UmbrellaStandoff = Slider("Standoff (m)", SmokeTuning.UmbrellaStandoff, 0f, 80f);
        }
    }
}

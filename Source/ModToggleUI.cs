using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
        public static readonly string Version = typeof(ModSettings).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.Split('+')[0];
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
        private static readonly string[] TabNames = { "General", "Engines", "Effects", "Trail" };
        private static readonly int[] TabOrder = { 0, 3, 1, 2 };      // the order on the tab bar
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
                SmokeTuning.NewTrailMaxRadius = SSlider("Old smoke radius cap (m)", SmokeTuning.NewTrailMaxRadius, 2f, 200f, "F0");
                SmokeTuning.NewTrailLife = SSlider("Lifetime (s)", SmokeTuning.NewTrailLife, 30f, 900f, "F0");
                SmokeTuning.NewTrailEndAltitude = SSlider("Trail ends at altitude (km, 0 = no limit)", SmokeTuning.NewTrailEndAltitude, 0f, 100f, "F0");
                SmokeTuning.NewTrailShadow = STog(SmokeTuning.NewTrailShadow, "Casts a shadow on the ground", null);
                SmokeTuning.HideStockSmoke = STog(SmokeTuning.HideStockSmoke, "Hide the engines' own particle smoke", "Engine parts often bring a particle smoke of their own; it would hang between the flame and the trail.");
            }
            SmokeTuning.SrbOnly = STog(SmokeTuning.SrbOnly, "Solid motors only", "Limit trails to solid motors. Turn off to also show enabled liquid fuel profiles.");

            Head("Engines");
            SmokeTuning.HeatHaze = STog(SmokeTuning.HeatHaze, "Heat haze", "The air over the nozzle shimmers.");
            SmokeTuning.EngineLights = STog(SmokeTuning.EngineLights, "Flame lights the scene", "The pad, the ground and the rocket are lit in the colour of the flame.");
            SmokeTuning.PhysicalPlumes = STog(SmokeTuning.PhysicalPlumes, "Physical plumes", "Shock diamonds, core and afterburning computed from the engine's own numbers (replaces the Waterfall flame while drawn).");

            GUILayout.Space(14);
            GUILayout.BeginHorizontal();
            if (Time.unscaledTime >= changedStamp) { changedCache = SmokeTuningConfig.ChangedCount(); changedStamp = Time.unscaledTime + 0.5f; }
            int changed = changedCache;
            GUILayout.Label(changed == 0 ? "All settings at their defaults." : changed + " setting(s) changed from the defaults (saved when the window closes).");
            GUILayout.FlexibleSpace();
            if (changed > 0 && GUILayout.Button("Reset to defaults")) SmokeTuningConfig.ResetToDefaults();
            GUILayout.EndHorizontal();
            Desc("Trail, engine and rendering controls are under Advanced.");
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
            }

            Sec(0, "Config & debug");
            SmokeTuning.DebugLogging = Tog(SmokeTuning.DebugLogging, " Verbose log");
            SmokeTuning.ShadowCastDebug = Slider("Cast shadow debug", SmokeTuning.ShadowCastDebug, 0f, 5f);
            if (Btn("Print values to log")) PrintValues();
            if (Btn("Save settings now (they are also saved when the window closes)")) SmokeTuningConfig.Save();
            if (Btn("Reset ALL to defaults (deletes tuning.cfg)")) SmokeTuningConfig.ResetToDefaults();

            // ======================================================================= Trail
            Sec(3, "Trail");
            SmokeTuning.NewTrail = Tog(SmokeTuning.NewTrail, " Volumetric trail");
            if (Vis("Trail runtime status")) GUILayout.Label(CapsuleTrail.Instance.DiagnosticSummary());
            if (Btn("Print trail diagnostics to KSP.log")) CapsuleTrail.Instance.LogDiagnostics();
            SmokeTuning.HideStockSmoke = Tog(SmokeTuning.HideStockSmoke, " Hide the engines' own particle smoke while the trail is on");
            Sec(3, "Look of the trail");
            SmokeTuning.TrailAdaptiveSampling = Tog(SmokeTuning.TrailAdaptiveSampling, " Adaptive trail sampling");
            SmokeTuning.TrailDebugMode = Mathf.RoundToInt(Slider("Diagnostic (0 normal, 1 budget, 2 bounds off, 3 light, 4 shadow, 5 cost)", SmokeTuning.TrailDebugMode, 0f, 5f));
            {
                SmokeTuning.NewTrailMaxRadius = Slider("  expansion radius cap (m)", SmokeTuning.NewTrailMaxRadius, 2f, 300f);
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
                SmokeTuning.NewTrailDistanceContrastReduction = Slider("  Distant contrast reduction (0 = off)", SmokeTuning.NewTrailDistanceContrastReduction, 0f, 1f);
            }
            Sec(3, "Shadow on the ground");
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
            SmokeTuning.WaterfallStartWidth = Slider("  start width (x flame radius)", SmokeTuning.WaterfallStartWidth, 0.2f, 1.5f);

            Sec(2, "Physical plumes");
            SmokeTuning.PhysicalPlumes = Tog(SmokeTuning.PhysicalPlumes, " Physical plumes: shock diamonds, core and afterburning from the engine's own numbers");
            SmokeTuning.PlumeHideWaterfall = Tog(SmokeTuning.PlumeHideWaterfall, "  Switch off Waterfall's flame while the physical plume is drawn");
            SmokeTuning.PlumeExposure = Slider("  Brightness (exposure)", SmokeTuning.PlumeExposure, 0.2f, 5f);
            SmokeTuning.PlumeStreak = Slider("  Long streak at altitude (a long exposure: smooth, narrow, hundreds of metres; 0 = off)", SmokeTuning.PlumeStreak, 0f, 1f);
            SmokeTuning.PlumeScale = Slider("  Length (x)", SmokeTuning.PlumeScale, 0.3f, 3f);
            SmokeTuning.PlumeSteps = Mathf.RoundToInt(Slider("  Quality (ray steps)", SmokeTuning.PlumeSteps, 32f, 192f));
            SmokeTuning.PlumeMaxEngines = Mathf.RoundToInt(Slider("  Nozzles drawn (nearest first)", SmokeTuning.PlumeMaxEngines, 1f, 40f));
            SmokeTuning.PlumeDeflect = Tog(SmokeTuning.PlumeDeflect, "  Deflect flame at scenery surfaces");
            SmokeTuning.PlumeDeflectSpread = Slider("  Deflected flame spread", SmokeTuning.PlumeDeflectSpread, 0.3f, 3f);

            Sec(2, "Heat haze");
            SmokeTuning.HeatHaze = Tog(SmokeTuning.HeatHaze, " Heat haze over the nozzle (refraction)");
            SmokeTuning.HeatHazeStrength = Slider("  Haze strength", SmokeTuning.HeatHazeStrength, 0f, 1f);
            SmokeTuning.HeatHazeAmount = Slider("  Haze displacement", SmokeTuning.HeatHazeAmount, 0f, 1f);
            SmokeTuning.HeatHazeMaxShift = Slider("  Haze max shift (screen fraction)", SmokeTuning.HeatHazeMaxShift, 0.001f, 0.03f);
            SmokeTuning.HeatHazeLengthDiameters = Slider("  Haze length (nozzle diameters)", SmokeTuning.HeatHazeLengthDiameters, 3f, 40f);
            SmokeTuning.HeatHazeScale = Slider("  Haze noise size (m)", SmokeTuning.HeatHazeScale, 0.3f, 8f);
            SmokeTuning.HeatHazeSpeed = Slider("  Haze stream speed (m/s)", SmokeTuning.HeatHazeSpeed, 0f, 120f);

            Sec(2, "Over the clouds");
            SmokeTuning.SmokeLast = Tog(SmokeTuning.SmokeLast, " Draw the smoke last (after all clouds, before the image effects): EVE's volumetric clouds cover smoke that lies in front of them - try this if the trail has holes at some camera angles");
            SmokeTuning.SmokeBeforeClouds = Tog(SmokeTuning.SmokeBeforeClouds, " TEST: draw the smoke before the transparent queue, so cloud layers (EVE / Scatterer) are drawn OVER it instead of the smoke lying over them");

            Sec(2, "Aerial perspective");
            SmokeTuning.AerialStrength = Slider("Aerial perspective (distant haze)", SmokeTuning.AerialStrength, 0f, 1f);
            SmokeTuning.ScattererSunTint = Tog(SmokeTuning.ScattererSunTint, " Light the smoke with the colour of the sun from Scatterer (orange at sunset)");
            SmokeTuning.ScattererTintStrength = Slider("  Strength of that tint", SmokeTuning.ScattererTintStrength, 0f, 1f);
            SmokeTuning.AerialDistance = Slider("  Haze distance (m, sea level)", SmokeTuning.AerialDistance, 2000f, 60000f);

            Sec(2, "Engine flame light");
            SmokeTuning.EngineLights = Tog(SmokeTuning.EngineLights, " Real scene light from engine flames (pad, ground, rocket)");
            SmokeTuning.EngineLightIntensity = Slider("  Scene light intensity", SmokeTuning.EngineLightIntensity, 0f, 6f);
            SmokeTuning.EngineLightRange = Slider("  Scene light range (x)", SmokeTuning.EngineLightRange, 0.3f, 3f);
            SmokeTuning.FlameLightRange = Slider("Flame light range (m)", SmokeTuning.FlameLightRange, 3f, 200f);

        }
    }
}

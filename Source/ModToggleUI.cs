using System.Collections.Generic;
using System.IO;
using UnityEngine;
using KSP.UI.Screens;

namespace RaymarchedPlumeTrails
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

        // ------------------------------------------------------------------------------------------------------------
        //  Window: a tab bar with a search box on top (it doubles as the title bar and the drag handle), shortcuts to the
        //  sections of the current tab on the left, the controls on the right, and a grip in the corner to resize.
        // ------------------------------------------------------------------------------------------------------------
        private static readonly string[] TabNames = { "General", "Plumes", "Physics", "Shape", "Lighting", "Effects", "Explosions", "Extras" };
        private static int tab;
        private static string search = "";
        private static Rect windowRectStatic = new Rect(120, 80, 860, 600);
        private static bool resizing;
        private static GUIStyle winStyle;

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
                GUILayout.Label("<size=14><b>" + curSec + "</b></size>" + (searching ? "   <color=#8aa>" + TabNames[curTab] + "</color>" : ""));
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

        // ---- look: flat, thin borders, accent colour; every change of state is eased, nothing snaps
        private static readonly Color BgCol = new Color(0.10f, 0.11f, 0.13f, 0.97f);
        private static readonly Color PanelCol = new Color(0.075f, 0.085f, 0.10f, 1f);
        private static readonly Color LineCol = new Color(0.36f, 0.40f, 0.46f, 1f);
        private static readonly Color AccentCol = new Color(0.45f, 0.78f, 1f, 1f);
        private static readonly Dictionary<Color, Texture2D> texCache = new Dictionary<Color, Texture2D>();
        private static Texture2D Tex(Color c)
        {
            Texture2D t;
            if (texCache.TryGetValue(c, out t) && t != null) return t;
            t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            texCache[c] = t;
            return t;
        }
        private static void Fill(Rect r, Color c) { GUI.DrawTexture(r, Tex(c)); }

        private static GUIStyle tabStyle, tabActiveStyle, secStyle, secActiveStyle, closeStyle, searchStyle, hintStyle;
        private static Texture2D RoundTop(Color c)
        {
            const int N = 24; const float R = 9f;
            Texture2D t = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float a = 1f;
                    float cy = N - 1 - R;                       // top corners only (texture y grows upward)
                    if (y > cy)
                    {
                        float dx = x < R ? R - x : (x > N - 1 - R ? x - (N - 1 - R) : 0f);
                        if (dx > 0f) { float d = Mathf.Sqrt(dx * dx + (y - cy) * (y - cy)); a = Mathf.Clamp01(R - d + 0.5f); }
                    }
                    t.SetPixel(x, y, new Color(c.r, c.g, c.b, c.a * a));
                }
            t.Apply();
            return t;
        }

        private static void BuildStyles()
        {
            if (winStyle != null) return;
            // the stock KSP skin, untouched; only the window padding is changed
            winStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(0, 0, 0, 0) };
            // folder tabs: rounded top corners, flat bottom (a rounded square cut in half)
            tabStyle = new GUIStyle(GUI.skin.button) { wordWrap = false, alignment = TextAnchor.MiddleCenter, clipping = TextClipping.Overflow };
            tabStyle.padding = new RectOffset(12, 12, 0, 0); tabStyle.margin = new RectOffset(2, 2, 8, 0);
            tabStyle.border = new RectOffset(9, 9, 9, 1);
            Texture2D tn = RoundTop(new Color(0.20f, 0.22f, 0.26f, 1f)), th = RoundTop(new Color(0.29f, 0.32f, 0.37f, 1f)), ta = RoundTop(new Color(0.16f, 0.38f, 0.56f, 1f));
            tabStyle.normal.background = tn; tabStyle.hover.background = th; tabStyle.active.background = ta;
            tabStyle.onNormal.background = tn; tabStyle.onHover.background = th; tabStyle.onActive.background = ta;
            tabStyle.normal.textColor = new Color(0.78f, 0.82f, 0.88f); tabStyle.hover.textColor = Color.white; tabStyle.active.textColor = Color.white;
            tabActiveStyle = new GUIStyle(tabStyle);
            tabActiveStyle.normal.background = ta; tabActiveStyle.hover.background = ta; tabActiveStyle.onNormal.background = ta; tabActiveStyle.onHover.background = ta;
            tabActiveStyle.normal.textColor = Color.white; tabActiveStyle.hover.textColor = Color.white;
            secStyle = GUI.skin.button; secActiveStyle = GUI.skin.button;
            closeStyle = GUI.skin.button; searchStyle = GUI.skin.textField;
            hintStyle = new GUIStyle(GUI.skin.label);
            hintStyle.normal.textColor = new Color(0.55f, 0.6f, 0.65f);
        }

        // animation state
        private static float openT;                       // window fade-in
        private static float tabFade = 1f;                // content fade after a tab change
        private static float underX, underW;              // sliding underline of the active tab
        private static bool underInit;
        private static readonly Rect[] tabRects = new Rect[8];
        private static readonly Dictionary<string, float> secAnim = new Dictionary<string, float>();
        private static float scrollTarget;
        private static float Ease(float dt, float rate) { return 1f - Mathf.Exp(-dt * rate); }

        private const float HeaderH = 42f;
        private const float LeftW = 176f;

        private void OnGUI()
        {
            if (!showWindow) { openT = 0f; return; }
            BuildStyles();
            openT = Mathf.MoveTowards(openT, 1f, Time.unscaledDeltaTime * 6f);
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, openT));
            windowRectStatic = GUI.Window(834621, windowRectStatic, DrawWindow, GUIContent.none, winStyle);
            GUI.color = old;
        }

        private void SetTab(int i)
        {
            if (tab == i && search.Length == 0) return;
            tab = i; search = ""; scroll = Vector2.zero; scrollTarget = 0f; tabFade = 0f;
        }

        private void DrawWindow(int id)
        {
            Rect wr = windowRectStatic;
            Event ev = Event.current;
            float dt = Time.unscaledDeltaTime;
            bool repaint = ev.type == EventType.Repaint;
            if (repaint) tabFade = Mathf.MoveTowards(tabFade, 1f, dt * 7f);

            // frame, header line, left column
            Fill(new Rect(0, HeaderH, wr.width, 1), LineCol);
            Fill(new Rect(LeftW, HeaderH, 1, wr.height - HeaderH), LineCol);

            // ---- header: tabs on the left, search and close on the right
            GUILayout.BeginArea(new Rect(1, 1, wr.width - 2, HeaderH - 1));
            GUILayout.BeginHorizontal(GUILayout.Height(HeaderH - 1));
            for (int i = 0; i < TabNames.Length; i++)
            {
                bool on = tab == i && search.Length == 0;
                float tw = Mathf.Max(72f, tabStyle.CalcSize(new GUIContent(TabNames[i])).x + 26f);
                if (GUILayout.Button(TabNames[i], on ? tabActiveStyle : tabStyle, GUILayout.Height(HeaderH - 4), GUILayout.Width(tw)))
                    SetTab(i);
                if (repaint) tabRects[i] = GUILayoutUtility.GetLastRect();
            }
            GUILayout.FlexibleSpace();
            GUI.SetNextControlName("pt_search");
            string ns = GUILayout.TextField(search, searchStyle, GUILayout.Width(210), GUILayout.Height(HeaderH - 14));
            if (repaint && search.Length == 0 && GUI.GetNameOfFocusedControl() != "pt_search")
            {
                Rect sr = GUILayoutUtility.GetLastRect();
                GUI.Label(sr, "Search", hintStyle);
            }
            if (ns != search) { search = ns; scroll = Vector2.zero; scrollTarget = 0f; tabFade = 0.35f; }
            GUILayout.Space(40);   // empty strip between search and close: grab it to move the window
            if (GUILayout.Button("X", closeStyle, GUILayout.Width(34), GUILayout.Height(HeaderH - 5)))
            {
                showWindow = false; if (button != null) button.SetFalse(false);
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // ---- left column: sections of this tab (or the matches), the one in view highlighted
            string current = null;
            for (int i = 0; i < shownSections.Count; i++)
            {
                float y;
                if (secY.TryGetValue(shownSections[i], out y) && y <= scroll.y + 40f) current = shownSections[i];
            }
            if (current == null && shownSections.Count > 0) current = shownSections[0];

            GUILayout.BeginArea(new Rect(1, HeaderH + 8, LeftW - 1, wr.height - HeaderH - 12));
            for (int i = 0; i < shownSections.Count; i++)
            {
                string s = shownSections[i];
                bool act = s == current;
                Color cc2 = GUI.contentColor;
                if (act) GUI.contentColor = AccentCol;
                bool clicked = GUILayout.Button(s, secStyle, GUILayout.Height(28));
                GUI.contentColor = cc2;
                if (clicked)
                {
                    float y;
                    if (secY.TryGetValue(s, out y)) scrollTarget = Mathf.Max(0f, y - 6f);
                }
                if (repaint)
                {
                    Rect r = GUILayoutUtility.GetLastRect();
                    float a;
                    secAnim.TryGetValue(s, out a);
                    a = Mathf.Lerp(a, act ? 1f : 0f, Ease(dt, 14f));
                    secAnim[s] = a;
                    if (a > 0.01f) Fill(new Rect(r.x + 1f, r.y + 4f, 3f, r.height - 8f), new Color(AccentCol.r, AccentCol.g, AccentCol.b, a));
                }
            }
            GUILayout.EndArea();

            // ---- controls
            GUILayout.BeginArea(new Rect(LeftW + 1, HeaderH + 1, wr.width - LeftW - 2, wr.height - HeaderH - 2));
            Color keep = GUI.color;
            GUI.color = new Color(keep.r, keep.g, keep.b, keep.a * Mathf.SmoothStep(0f, 1f, tabFade));
            Vector2 got = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            if (Mathf.Abs(got.y - scroll.y) > 0.01f) { scroll = got; scrollTarget = got.y; }   // moved by the user (wheel, bar)
            anyVisible = false;
            if (repaint) nextSections = new List<string>();
            GUILayout.Space(2);
            DrawControls();
            if (!anyVisible) GUILayout.Label(search.Length > 0 ? "Nothing matches \"" + search + "\"." : "Nothing here.");
            GUILayout.Space(30);
            GUILayout.EndScrollView();
            GUI.color = keep;
            GUILayout.EndArea();
            if (repaint)
            {
                List<string> t = shownSections; shownSections = nextSections; nextSections = t;
                // eased scroll toward the clicked section
                if (Mathf.Abs(scroll.y - scrollTarget) > 0.5f) scroll.y = Mathf.Lerp(scroll.y, scrollTarget, Ease(dt, 12f));
                else scroll.y = scrollTarget;
            }

            // ---- resize grip: three diagonal strokes, bottom-right
            Rect grip = new Rect(wr.width - 24, wr.height - 24, 22, 22);
            if (repaint)
            {
                Color gc = resizing ? AccentCol : LineCol;
                for (int gx = 0; gx < 3; gx++)
                    for (int gy = 0; gy <= gx; gy++)
                        Fill(new Rect(wr.width - 6f - gx * 5f, wr.height - 6f - gy * 5f, 3f, 3f), gc);
            }
            if (ev.type == EventType.MouseDown && grip.Contains(ev.mousePosition)) { resizing = true; ev.Use(); }
            else if (resizing && ev.type == EventType.MouseDrag)
            {
                windowRectStatic.width = Mathf.Clamp(windowRectStatic.width + ev.delta.x, 600f, Screen.width - 20f);
                windowRectStatic.height = Mathf.Clamp(windowRectStatic.height + ev.delta.y, 380f, Screen.height - 20f);
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp) resizing = false;

            if (!resizing) GUI.DragWindow(new Rect(0, 0, wr.width, HeaderH));
        }

        private void DrawControls()
        {
            // ======================================================================= General
            Sec(0, "Smoke");
            bool newState = Tog(ModSettings.Enabled, " Enable smoke");
            if (newState != ModSettings.Enabled)
            {
                ModSettings.Enabled = newState;
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

            Sec(0, "Config & debug");
            SmokeTuning.DebugLogging = Tog(SmokeTuning.DebugLogging, " Verbose log");
            SmokeTuning.DebugBox = Tog(SmokeTuning.DebugBox, " Debug: show march box (purple) / ran-out rays (red)");
            SmokeTuning.ShadowCastDebug = Slider("Cast shadow debug", SmokeTuning.ShadowCastDebug, 0f, 5f);
            if (Btn("Print values to log")) PrintValues();
            if (Btn("Save settings (tuning.cfg)")) SmokeTuningConfig.Save();
            if (Btn("Reset ALL to defaults (deletes tuning.cfg)")) SmokeTuningConfig.ResetToDefaults();

            // ======================================================================= Plumes
            Sec(1, "Engines");
            // Takes effect for engine groups formed after the change - existing groups are only rebuilt when the
            // vessel's part count changes, e.g. at staging.
            SmokeTuning.SrbOnly = Tog(SmokeTuning.SrbOnly, " Solid motors only");
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
            SmokeTuning.PhysAmbientTurb = Slider("Ambient turbulence (m/s)", SmokeTuning.PhysAmbientTurb, 0f, 1.5f);
            SmokeTuning.PhysExhaustTemp = Slider("Exhaust temperature (K)", SmokeTuning.PhysExhaustTemp, 800f, 3500f);
            SmokeTuning.PhysWindRoughness = Slider("Wind roughness z0 (m)", SmokeTuning.PhysWindRoughness, 0.001f, 2f);
            SmokeTuning.WindSpeed = Slider("Wind (m/s)", SmokeTuning.WindSpeed, 0f, 8f);
            SmokeTuning.PhysRadiusScale = Slider("Puff visual size (x)", SmokeTuning.PhysRadiusScale, 0.2f, 3f);
            SmokeTuning.PhysMaxGrowth = Slider("Max widening (x release radius, 0 = off)", SmokeTuning.PhysMaxGrowth, 0f, 20f);
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

            Sec(2, "Deflector & water");
            SmokeTuning.PadWater = Tog(SmokeTuning.PadWater, " Water deluge: steam in the flow that hits a deflector");
            SmokeTuning.PadWaterAlways = Tog(SmokeTuning.PadWaterAlways, " Always water (on every ground impact, not only a detected deflector)");
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

            Sec(4, "Cast shadow on terrain");
            SmokeTuning.ShadowCastStrength = Slider("Cast shadow", SmokeTuning.ShadowCastStrength, 0f, 1f);
            SmokeTuning.ShadowCastDensity = Slider("Cast shadow density", SmokeTuning.ShadowCastDensity, 0.05f, 2f);
            SmokeTuning.ShadowCastFloor = Slider("Cast shadow floor (sky light)", SmokeTuning.ShadowCastFloor, 0f, 0.9f);
            SmokeTuning.ShadowCastDistance = Slider("Cast reach (m)", SmokeTuning.ShadowCastDistance, 200f, 6000f);
            SmokeTuning.ShadowCastSteps = Slider("Cast steps", SmokeTuning.ShadowCastSteps, 4f, 48f);

            // ======================================================================= Effects
            Sec(5, "Heat haze");
            SmokeTuning.HeatHaze = Tog(SmokeTuning.HeatHaze, " Heat haze over the nozzle (refraction)");
            SmokeTuning.HeatHazeStrength = Slider("  Haze strength", SmokeTuning.HeatHazeStrength, 0f, 1f);
            SmokeTuning.HeatHazeAmount = Slider("  Haze displacement", SmokeTuning.HeatHazeAmount, 0f, 1f);
            SmokeTuning.HeatHazeMaxShift = Slider("  Haze max shift (screen fraction)", SmokeTuning.HeatHazeMaxShift, 0.001f, 0.03f);
            SmokeTuning.HeatHazeLengthDiameters = Slider("  Haze length (nozzle diameters)", SmokeTuning.HeatHazeLengthDiameters, 3f, 40f);
            SmokeTuning.HeatHazeScale = Slider("  Haze noise size (m)", SmokeTuning.HeatHazeScale, 0.3f, 8f);
            SmokeTuning.HeatHazeSpeed = Slider("  Haze stream speed (m/s)", SmokeTuning.HeatHazeSpeed, 0f, 120f);

            Sec(5, "Aerial perspective");
            SmokeTuning.AerialStrength = Slider("Aerial perspective (distant haze)", SmokeTuning.AerialStrength, 0f, 1f);
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
            SmokeTuning.ExplosionScale = Slider("Size (x, radius ~ cube root of the energy)", SmokeTuning.ExplosionScale, 0.2f, 4f);
            SmokeTuning.ExplosionLife = Slider("Duration (x)", SmokeTuning.ExplosionLife, 0.3f, 3f);
            SmokeTuning.ExplosionFire = Slider("Fire glow and flash (x)", SmokeTuning.ExplosionFire, 0f, 3f);

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

using KSP.UI.Screens;
using UnityEngine;

namespace StockSmokeEnhancer
{
    /// <summary>
    /// Adds a button to the stock AppLauncher (the toolbar on the right side of the
    /// screen in flight) that opens a small window with live sliders for the effect
    /// multipliers, plus Save/Reset buttons. Changes apply immediately (SmokeEnhancer
    /// reads SmokeEnhancerSettings every frame) - Save only persists them to
    /// config.cfg for next time.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SmokeEnhancerUI : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private bool windowVisible;
        private Rect windowRect = new Rect(300, 100, 260, 190);
        private static Texture2D icon;

        private void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            RemoveButton();
        }

        private void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;

            if (icon == null) icon = CreateIcon();

            button = ApplicationLauncher.Instance.AddModApplication(
                ShowWindow, HideWindow,
                null, null, null, null,
                ApplicationLauncher.AppScenes.FLIGHT,
                icon);
        }

        private void RemoveButton()
        {
            if (button != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(button);
            }
            button = null;
        }

        private void ShowWindow()
        {
            windowVisible = true;
        }

        private void HideWindow()
        {
            windowVisible = false;
        }

        private void OnGUI()
        {
            if (!windowVisible) return;
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "Stock Smoke Enhancer");
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            SmokeEnhancerSettings.EmissionMultiplier = LabeledSlider(
                "Emission", SmokeEnhancerSettings.EmissionMultiplier, 1f, 10f);
            SmokeEnhancerSettings.LifetimeMultiplier = LabeledSlider(
                "Lifetime", SmokeEnhancerSettings.LifetimeMultiplier, 1f, 8f);
            SmokeEnhancerSettings.SizeMultiplier = LabeledSlider(
                "Size", SmokeEnhancerSettings.SizeMultiplier, 0.5f, 4f);

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save")) SmokeEnhancerSettings.Save();
            if (GUILayout.Button("Reset to defaults")) SmokeEnhancerSettings.ResetToDefaults();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        private static float LabeledSlider(string label, float value, float min, float max)
        {
            GUILayout.Label(string.Format("{0}: {1:F2}x", label, value));
            return GUILayout.HorizontalSlider(value, min, max);
        }

        private static Texture2D CreateIcon()
        {
            const int size = 38;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color fill = new Color(0.8f, 0.8f, 0.85f, 1f);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}

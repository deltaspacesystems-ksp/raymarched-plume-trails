using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Persists every public static float / int / bool in SmokeTuning to a .cfg, so a look that was
    // dialled in with the in-game sliders survives a restart - and can be read, diffed and pasted
    // back into the defaults. The compiled-in defaults are captured before the file is applied, so
    // "Reset to defaults" always works and a missing or partial file is harmless.
    public static class SmokeTuningConfig
    {
        // Defaults changed in layout 2: the cast shadow was saved as 0 (it had been switched off) and would have kept the
        // new analytic shadow off for ever.
        private static readonly string[] ResetBelow2 = { "ShadowCastStrength", "ShadowCastDensity", "ShadowCastFloor" };

        // Layout 3: smoke expansion and haze defaults were reduced (the plume had become huge); old saved values would undo it.
        private static readonly string[] ResetBelow3 = { "PhysMaxGrowth", "WaterfallStartWidth", "WaterfallHazeLength", "HeatHazeStrength" };

        private static readonly Dictionary<string, object> Defaults = new Dictionary<string, object>();
        private static bool captured;

        public static string FilePath
        {
            get
            {
                string plugin = AssetLoader.AssemblyDirectory();
                string modDir = Path.GetDirectoryName(plugin);
                return Path.Combine(Path.Combine(modDir, "PluginData"), "tuning.cfg");
            }
        }

        private static IEnumerable<FieldInfo> Fields()
        {
            foreach (FieldInfo f in typeof(SmokeTuning).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.IsLiteral || f.IsInitOnly) continue;
                if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool))
                    yield return f;
            }
        }

        private static void Capture()
        {
            if (captured) return;
            captured = true;
            foreach (FieldInfo f in Fields()) Defaults[f.Name] = f.GetValue(null);
        }

        private static string Format(object v)
        {
            if (v is float) return ((float)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "True" : "False";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static void Load()
        {
            Capture();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                ConfigNode root = ConfigNode.Load(path);
                ConfigNode n = root != null ? root.GetNode("PLUME_TUNING") : null;
                if (n == null) return;
                int applied = 0;
                int fileVersion = 1;
                if (n.HasValue("ConfigVersion")) int.TryParse(n.GetValue("ConfigVersion"), out fileVersion);
                foreach (FieldInfo f in Fields())
                {
                    if (!n.HasValue(f.Name)) continue;
                    if (f.Name == "ConfigVersion") continue;
                    // a value whose default was changed on purpose since the file was written is not applied from it
                    if (fileVersion < 2 && Array.IndexOf(ResetBelow2, f.Name) >= 0) continue;
                    if (fileVersion < 3 && Array.IndexOf(ResetBelow3, f.Name) >= 0) continue;
                    string s = n.GetValue(f.Name);
                    try
                    {
                        if (f.FieldType == typeof(float)) f.SetValue(null, float.Parse(s, CultureInfo.InvariantCulture));
                        else if (f.FieldType == typeof(int)) f.SetValue(null, int.Parse(s, CultureInfo.InvariantCulture));
                        else f.SetValue(null, bool.Parse(s));
                        applied++;
                    }
                    catch (Exception) { Debug.LogWarning("[PlumeTrails] tuning.cfg: bad value '" + s + "' for " + f.Name); }
                }
                SmokeTuning.ConfigVersion = 3;
                Debug.Log("[PlumeTrails] tuning.cfg loaded, " + applied + " values from " + path + (fileVersion < 2 ? " (older layout: refreshed defaults for " + string.Join(", ", ResetBelow2) + ")" : ""));
            }
            catch (Exception e) { Debug.LogWarning("[PlumeTrails] tuning.cfg load failed: " + e.Message); }
        }

        public static void Save()
        {
            Capture();
            try
            {
                ConfigNode root = new ConfigNode();
                ConfigNode n = root.AddNode("PLUME_TUNING");
                foreach (FieldInfo f in Fields()) n.AddValue(f.Name, Format(f.GetValue(null)));
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                root.Save(path, "Raymarched Plume Trails - every tuning value; written by 'Save settings' in the debug menu");
                Debug.Log("[PlumeTrails] tuning saved to " + path);
            }
            catch (Exception e) { Debug.LogWarning("[PlumeTrails] tuning.cfg save failed: " + e.Message); }
        }

        // Back to the compiled-in defaults, and removes the file so the next start is clean too.
        public static void ResetToDefaults()
        {
            Capture();
            foreach (FieldInfo f in Fields())
            {
                object d;
                if (Defaults.TryGetValue(f.Name, out d)) f.SetValue(null, d);
            }
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch (Exception) { }
            Debug.Log("[PlumeTrails] tuning reset to defaults");
        }
    }
}

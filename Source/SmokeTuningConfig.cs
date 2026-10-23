using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Persists the public static float / int / bool values of SmokeTuning that the player CHANGED to PluginData/tuning.cfg.
    //
    // Only the differences from the compiled-in defaults are written. A value the player never touched is not in the file, so a
    // new default in a later version reaches everyone; a value they did change keeps their choice. (Before 1.0 the file held
    // every value, which froze the defaults of the day it was written - every retune then needed a list of values to ignore
    // in old files. Those pre-1.0 files are set aside as tuning-pre1.cfg and not read.)
    public static class SmokeTuningConfig
    {
        // 18 = 1.0: the file holds only the changed values
        public const int Layout = 18;

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
                if (f.Name == "ConfigVersion") continue;
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

        private static bool IsDefault(FieldInfo f)
        {
            object d;
            if (!Defaults.TryGetValue(f.Name, out d)) return false;
            object v = f.GetValue(null);
            if (v is float) return Mathf.Abs((float)v - (float)d) <= 1e-6f * Mathf.Max(1f, Mathf.Abs((float)d));
            return v.Equals(d);
        }

        // how many values differ from the defaults (shown in the window)
        public static int ChangedCount()
        {
            Capture();
            int n = 0;
            foreach (FieldInfo f in Fields()) if (!IsDefault(f)) n++;
            return n;
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
                int fileLayout = 1;
                if (n.HasValue("ConfigVersion")) int.TryParse(n.GetValue("ConfigVersion"), out fileLayout);
                if (fileLayout < Layout)
                {
                    // a pre-1.0 file holds every value of its day, not the player's choices: set it aside, start from the 1.0 defaults
                    string backup = Path.Combine(Path.GetDirectoryName(path), "tuning-pre1.cfg");
                    try { if (File.Exists(backup)) File.Delete(backup); File.Move(path, backup); } catch (Exception) { }
                    Debug.Log("[PlumeTrails] tuning.cfg from before 1.0 (layout " + fileLayout + ") set aside as " + backup + "; the 1.0 defaults are used");
                    return;
                }
                int applied = 0;
                foreach (FieldInfo f in Fields())
                {
                    if (!n.HasValue(f.Name)) continue;
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
                Debug.Log("[PlumeTrails] tuning.cfg loaded: " + applied + " changed values from " + path);
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
                n.AddValue("ConfigVersion", Layout);
                int written = 0;
                foreach (FieldInfo f in Fields())
                {
                    if (IsDefault(f)) continue;
                    n.AddValue(f.Name, Format(f.GetValue(null)));
                    written++;
                }
                string path = FilePath;
                if (written == 0) { if (File.Exists(path)) File.Delete(path); return; }      // nothing changed: no file at all
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                root.Save(path, "Raymarched Plume Trails - only the values you changed in the settings window (delete the file to go back to the defaults)");
                Debug.Log("[PlumeTrails] tuning saved: " + written + " changed values to " + path);
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

using System.Globalization;
using System.IO;
using UnityEngine;

namespace StockSmokeEnhancer
{
    /// <summary>
    /// Holds the current multiplier values and persists them to
    /// GameData/StockSmokeEnhancer/config.cfg. Values are static so both the effect
    /// logic (SmokeEnhancer) and the in-game UI (SmokeEnhancerUI) read/write the same
    /// live state - editing a slider takes effect immediately, no restart needed.
    /// </summary>
    public static class SmokeEnhancerSettings
    {
        public const float DefaultEmissionMultiplier = 4f;
        public const float DefaultLifetimeMultiplier = 3f;
        public const float DefaultSizeMultiplier = 1f;

        public static float EmissionMultiplier = DefaultEmissionMultiplier;
        public static float LifetimeMultiplier = DefaultLifetimeMultiplier;
        public static float SizeMultiplier = DefaultSizeMultiplier;

        private const string ConfigRelativePath = "GameData/StockSmokeEnhancer/config.cfg";
        private const string RootNodeName = "SMOKE_ENHANCER_SETTINGS";

        private static string ConfigPath => Path.Combine(KSPUtil.ApplicationRootPath, ConfigRelativePath);

        public static void Load()
        {
            ConfigNode file = ConfigNode.Load(ConfigPath);
            if (file == null || !file.HasNode(RootNodeName)) return;

            ConfigNode settings = file.GetNode(RootNodeName);
            EmissionMultiplier = ReadFloat(settings, "emissionMultiplier", DefaultEmissionMultiplier);
            LifetimeMultiplier = ReadFloat(settings, "lifetimeMultiplier", DefaultLifetimeMultiplier);
            SizeMultiplier = ReadFloat(settings, "sizeMultiplier", DefaultSizeMultiplier);
        }

        public static void Save()
        {
            ConfigNode file = new ConfigNode();
            ConfigNode settings = file.AddNode(RootNodeName);
            settings.AddValue("emissionMultiplier", EmissionMultiplier.ToString(CultureInfo.InvariantCulture));
            settings.AddValue("lifetimeMultiplier", LifetimeMultiplier.ToString(CultureInfo.InvariantCulture));
            settings.AddValue("sizeMultiplier", SizeMultiplier.ToString(CultureInfo.InvariantCulture));
            file.Save(ConfigPath);
        }

        public static void ResetToDefaults()
        {
            EmissionMultiplier = DefaultEmissionMultiplier;
            LifetimeMultiplier = DefaultLifetimeMultiplier;
            SizeMultiplier = DefaultSizeMultiplier;
        }

        private static float ReadFloat(ConfigNode node, string key, float fallback)
        {
            if (node.HasValue(key) &&
                float.TryParse(node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
            return fallback;
        }
    }
}

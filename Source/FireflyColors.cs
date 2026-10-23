using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // THE COLOURS OF RE-ENTRY FROM FIREFLY. Firefly keeps the colours of the plasma of every body in its own configs (ATMOFX_BODY: Color { glow, glow_hot,
    // trail_primary, trail_secondary, ... }, each "r g b intensity" on a scale where 191 is full). They are read here from the game database (the nodes after
    // ModuleManager), for the body of the vessel, with the Default node as the fallback, so that the fragments of a blast that fall back through the atmosphere glow
    // in the colours that the rest of the game's re-entry has. FireflyAPI itself works on vessels (TryFindModule(vessel)); a fragment is not a vessel, so it is the
    // data that is used. Without Firefly nothing is found and the fragments glow with the colour of a black body.
    public static class FireflyColors
    {
        public struct Palette { public Color glow, glowHot, trailPrimary, trailSecondary; public bool found; }

        private static Dictionary<string, Palette> byBody;
        private static Palette fallback;
        private static bool loaded;

        private static Color Parse(string s, Color def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            string[] p = s.Split(new[] { ' ', '\t', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3) return def;
            float r, g, b, k = 1f;
            if (!float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) return def;
            if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out g)) return def;
            if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out b)) return def;
            if (p.Length > 3) float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out k);
            return new Color(r / 191f * k, g / 191f * k, b / 191f * k, 1f);          // the colour times its HDR intensity
        }

        private static void Load()
        {
            loaded = true;
            byBody = new Dictionary<string, Palette>();
            fallback = new Palette { glow = new Color(1.0f, 0.42f, 0.26f) * 1.4f, glowHot = new Color(1.0f, 0.47f, 0.34f) * 2.5f, trailPrimary = new Color(1.0f, 0.52f, 0.38f) * 3f, trailSecondary = new Color(1.0f, 0.37f, 0.22f) * 1.5f, found = false };
            try
            {
                ConfigNode[] nodes = GameDatabase.Instance != null ? GameDatabase.Instance.GetConfigNodes("ATMOFX_BODY") : null;
                if (nodes == null) return;
                for (int i = 0; i < nodes.Length; i++)
                {
                    string name = nodes[i].GetValue("name");
                    ConfigNode col = nodes[i].GetNode("Color");
                    if (string.IsNullOrEmpty(name) || col == null) continue;
                    Palette pl = new Palette
                    {
                        glow = Parse(col.GetValue("glow"), fallback.glow),
                        glowHot = Parse(col.GetValue("glow_hot"), fallback.glowHot),
                        trailPrimary = Parse(col.GetValue("trail_primary"), fallback.trailPrimary),
                        trailSecondary = Parse(col.GetValue("trail_secondary"), fallback.trailSecondary),
                        found = true
                    };
                    byBody[name] = pl;
                }
                Palette def;
                if (byBody.TryGetValue("Default", out def)) fallback = def;
                Debug.Log("[PlumeTrails] Firefly colours: " + byBody.Count + " body configs read from the game database" + (byBody.ContainsKey("Default") ? " (with the Default one)" : ""));
            }
            catch (System.Exception e) { Debug.LogWarning("[PlumeTrails] Firefly colours: " + e.GetType().Name + ": " + e.Message); }
        }

        // the palette of this body (or the default one; found = false if Firefly's configs are not there at all)
        public static Palette For(CelestialBody body)
        {
            if (!loaded) Load();
            Palette pl;
            if (body != null && byBody.TryGetValue(body.name, out pl)) return pl;
            return fallback;
        }
    }
}

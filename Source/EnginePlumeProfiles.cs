using System;
using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // What kind of exhaust an engine makes. Decides whether it smokes at all, how much water vapour it carries
    // (condensation, see PuffPhysics.CondensedFraction) and how visible / how big its plume is drawn.
    public enum PlumeKind { Solid, Kerolox, Hydrolox, Methalox, Hypergolic, Nuclear, None }

    public class EnginePlumeProfile
    {
        public PlumeKind kind = PlumeKind.Kerolox;
        public bool enabled = true;
        public float sizeScale = 1f;      // x the liquid plume size
        public float densityScale = 1f;   // x the liquid plume density
        public float visibilityMul = 1f;  // extra x on top of the kind's visibility
        public float waterFrac = -1f;     // mass fraction of H2O in the exhaust; < 0 = kind default
        public float dilution = -1f;      // dilution (parcel mass / exhaust mass) out to which the plume stays visible; < 0 = kind default

        // Colour of the flame as it lights the surroundings, and how strongly (hydrogen burns almost invisibly).
        public Color FlameColor
        {
            get
            {
                switch (kind)
                {
                    case PlumeKind.Solid: return new Color(1.00f, 0.60f, 0.26f);
                    case PlumeKind.Kerolox: return new Color(1.00f, 0.56f, 0.22f);
                    case PlumeKind.Hydrolox: return new Color(0.62f, 0.72f, 1.00f);
                    case PlumeKind.Methalox: return new Color(1.00f, 0.66f, 0.46f);
                    case PlumeKind.Hypergolic: return new Color(1.00f, 0.72f, 0.52f);
                    case PlumeKind.Nuclear: return new Color(0.70f, 0.80f, 1.00f);
                    default: return new Color(1f, 0.6f, 0.3f);
                }
            }
        }
        public float LightMul
        {
            get
            {
                switch (kind)
                {
                    case PlumeKind.Hydrolox: return 0.45f;
                    case PlumeKind.Methalox: return 0.8f;
                    case PlumeKind.Nuclear: return 0.5f;
                    default: return 1f;
                }
            }
        }

        public bool IsSolid { get { return kind == PlumeKind.Solid; } }
        // water-vapour (liquid-engine) look; solid motors keep the sooty SRB look
        public bool Steam { get { return kind != PlumeKind.Solid; } }

        public float Water
        {
            get
            {
                if (waterFrac >= 0f) return waterFrac;
                switch (kind)
                {
                    case PlumeKind.Solid: return 0.12f;
                    case PlumeKind.Kerolox: return 0.28f;
                    case PlumeKind.Hydrolox: return 0.9f;
                    case PlumeKind.Methalox: return 0.45f;
                    case PlumeKind.Hypergolic: return 0.3f;
                    default: return 0f;
                }
            }
        }

        // How far the plume can be diluted by entrained air and still be seen. Soot and alumina (SRB) stay visible for
        // long; the water vapour of hydrolox disappears quickly into the air.
        public float Dilution
        {
            get
            {
                if (dilution >= 0f) return dilution;
                switch (kind)
                {
                    case PlumeKind.Solid: return 50f;
                    case PlumeKind.Kerolox: return 30f;
                    case PlumeKind.Hydrolox: return 15f;
                    case PlumeKind.Methalox: return 22f;
                    case PlumeKind.Hypergolic: return 25f;
                    default: return 10f;
                }
            }
        }

        public float Visibility
        {
            get
            {
                float v;
                switch (kind)
                {
                    case PlumeKind.Hydrolox: v = SmokeTuning.HydrogenPlumeVisibility; break;
                    case PlumeKind.Methalox: v = SmokeTuning.MethanePlumeVisibility; break;
                    case PlumeKind.Hypergolic: v = 0.6f; break;
                    case PlumeKind.Nuclear: v = 0.15f; break;
                    default: v = 1f; break;
                }
                return v * visibilityMul;
            }
        }
    }

    // Per-engine plume configuration. Every engine is classified automatically from its propellants; a part can
    // override that with a node in any .cfg (see GameData/RaymarchedPlumeTrails/Engines/engines.cfg):
    //
    //   RPT_ENGINE
    //   {
    //       part = restock-engine-torch      // part name, may be repeated
    //       plume = kerolox                  // solid | kerolox | hydrolox | methalox | hypergolic | nuclear | none
    //       sizeScale = 1.2                  // x plume size (liquid engines)
    //       densityScale = 1.3               // x plume density
    //       visibility = 1                   // extra x on the kind's visibility
    //       waterFrac = 0.28                 // H2O mass fraction of the exhaust
    //   }
    public static class EnginePlumeProfiles
    {
        private class Override
        {
            public PlumeKind? kind;
            public float? sizeScale, densityScale, visibility, waterFrac, dilution;
        }

        private static readonly Dictionary<string, Override> overrides = new Dictionary<string, Override>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, EnginePlumeProfile> cache = new Dictionary<string, EnginePlumeProfile>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static void Load()
        {
            overrides.Clear();
            cache.Clear();
            loaded = true;
            try
            {
                ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("RPT_ENGINE");
                foreach (ConfigNode n in nodes)
                {
                    Override o = new Override();
                    string plume = n.GetValue("plume");
                    if (!string.IsNullOrEmpty(plume))
                    {
                        PlumeKind k;
                        if (TryParseKind(plume, out k)) o.kind = k;
                        else Debug.LogWarning("[PlumeTrails] RPT_ENGINE: unknown plume '" + plume + "'");
                    }
                    o.sizeScale = Parse(n, "sizeScale");
                    o.densityScale = Parse(n, "densityScale");
                    o.visibility = Parse(n, "visibility");
                    o.waterFrac = Parse(n, "waterFrac");
                    o.dilution = Parse(n, "dilution");
                    foreach (string part in n.GetValues("part"))
                        overrides[part.Trim().Replace('_', '.')] = o;
                }
                Debug.Log("[PlumeTrails] engine plume overrides: " + overrides.Count + " part(s)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PlumeTrails] engine plume configs not loaded: " + e.Message);
            }
        }

        private static float? Parse(ConfigNode n, string key)
        {
            string v = n.GetValue(key);
            float f;
            if (!string.IsNullOrEmpty(v) && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f))
                return f;
            return null;
        }

        private static bool TryParseKind(string s, out PlumeKind k)
        {
            try { k = (PlumeKind)Enum.Parse(typeof(PlumeKind), s.Trim(), true); return true; }
            catch { k = PlumeKind.Kerolox; return false; }
        }

        public static EnginePlumeProfile For(Part part, ModuleEngines engine)
        {
            if (!loaded) Load();
            string key = part != null && part.partInfo != null ? part.partInfo.name : (part != null ? part.name : "?");
            string cacheKey = key + "|" + (engine != null ? engine.engineID : "");
            EnginePlumeProfile p;
            if (cache.TryGetValue(cacheKey, out p)) return p;

            p = Classify(engine);
            Override o;
            if (overrides.TryGetValue(key.Replace('_', '.'), out o))
            {
                if (o.kind.HasValue) p.kind = o.kind.Value;
                if (o.sizeScale.HasValue) p.sizeScale = o.sizeScale.Value;
                if (o.densityScale.HasValue) p.densityScale = o.densityScale.Value;
                if (o.visibility.HasValue) p.visibilityMul = o.visibility.Value;
                if (o.waterFrac.HasValue) p.waterFrac = o.waterFrac.Value;
                if (o.dilution.HasValue) p.dilution = o.dilution.Value;
                p.enabled = p.kind != PlumeKind.None;
            }
            cache[cacheKey] = p;
            return p;
        }

        // Automatic classification from the propellants.
        private static EnginePlumeProfile Classify(ModuleEngines engine)
        {
            EnginePlumeProfile p = new EnginePlumeProfile();
            if (EngineClusterUtils.IsSolidRocket(engine)) { p.kind = PlumeKind.Solid; return p; }

            bool hydrogen = false, methane = false, lf = false, ox = false, air = false, mono = false, other = false;
            if (engine != null && engine.propellants != null)
            {
                for (int i = 0; i < engine.propellants.Count; i++)
                {
                    string n = engine.propellants[i] != null ? engine.propellants[i].name : null;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0) hydrogen = true;
                    else if (n.IndexOf("Methane", StringComparison.OrdinalIgnoreCase) >= 0) methane = true;
                    else if (n == "LiquidFuel") lf = true;
                    else if (n == "Oxidizer" || n.IndexOf("LqdOxygen", StringComparison.OrdinalIgnoreCase) >= 0) ox = true;
                    else if (n == "IntakeAir" || n == "IntakeAtm") air = true;
                    else if (n == "MonoPropellant") mono = true;
                    else if (n != "ElectricCharge" && n != "EnrichedUranium") other = true;
                }
            }

            // Jets (air-breathing), ion/xenon and similar make no rocket plume.
            if (air) p.kind = PlumeKind.None;
            else if (hydrogen && ox) p.kind = PlumeKind.Hydrolox;
            else if (hydrogen) p.kind = PlumeKind.Nuclear;       // hydrogen alone: a nuclear thermal rocket, pure H2 exhaust
            else if (methane) p.kind = PlumeKind.Methalox;
            else if (lf && ox) p.kind = PlumeKind.Kerolox;
            else if (lf) p.kind = PlumeKind.Nuclear;             // LiquidFuel with no oxidizer (NERV)
            else if (mono) p.kind = PlumeKind.Hypergolic;
            else if (other) p.kind = PlumeKind.None;             // xenon etc.
            else p.kind = PlumeKind.Kerolox;

            p.enabled = p.kind != PlumeKind.None && p.kind != PlumeKind.Nuclear;
            return p;
        }
    }
}

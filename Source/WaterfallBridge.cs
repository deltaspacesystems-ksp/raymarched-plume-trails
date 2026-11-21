using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Integration with Waterfall (the engine-flame mod). Waterfall draws the flame of an engine as models parented to its
    // thrust transform; this mod draws the smoke that continues from the flame's end. Reading the flame's real extent from
    // Waterfall lets the smoke start where the flame ends (not at a guessed distance), lets the heat haze wrap the actual
    // flame, and sizes things from what is on screen.
    //
    // Everything is reflection: there is no reference to Waterfall.dll, so the mod works with or without it, and a
    // Waterfall update that moves a member only disables the integration (one log line), it cannot break the mod.
    internal static class WaterfallBridge
    {
        private static bool resolved;
        private static Type fxType;                 // Waterfall.ModuleWaterfallFX
        private static FieldInfo fAllFX;            // List<WaterfallEffect>
        private static FieldInfo fEngineId;         // string
        private static FieldInfo fEffRenderers;     // WaterfallEffect.effectRenderers : List<Renderer>
        private static bool failedLogged;

        // THE LIGHT OF THE FLAME as Waterfall itself drives it: its flame effects carry point lights (a "Light" object with a LIGHT module and light modifiers on the
        // throttle, the atmosphere...). They are read here - intensity, range and colour as they are NOW - and our scene light follows them instead of a guess from the
        // thrust. Their own rendering is switched off (culling mask 0) while ours is on, so the pad and the ground are not lit twice; the values still run.
        private static readonly Dictionary<int, Light[]> lightCache = new Dictionary<int, Light[]>();
        private static readonly Dictionary<Light, int> savedMask = new Dictionary<Light, int>();
        private static float lightCacheStamp;

        public static void RestoreLights()
        {
            if (savedMask.Count == 0) return;
            foreach (KeyValuePair<Light, int> kv in savedMask) if (kv.Key != null) kv.Key.cullingMask = kv.Value;
            savedMask.Clear();
        }

        public static bool ReadLight(Part part, bool takeOver, out Color color, out float intensity, out float range)
        {
            color = Color.white; intensity = 0f; range = 0f;
            if (part == null || !Available) return false;
            if (Time.time - lightCacheStamp > 5f) { lightCache.Clear(); lightCacheStamp = Time.time; }
            Light[] ls;
            int id = part.GetInstanceID();
            if (!lightCache.TryGetValue(id, out ls))
            {
                ls = null;
                if (part.Modules != null && part.FindModuleImplementing<ModuleEngines>() != null && FindModule(part, part.FindModuleImplementing<ModuleEngines>()) != null)
                {
                    List<Light> found = new List<Light>();
                    foreach (Light l in part.GetComponentsInChildren<Light>(true))
                        if (l != null && l.gameObject.name == "Light" && (l.type == LightType.Point || l.type == LightType.Spot)) found.Add(l);
                    if (found.Count > 0) ls = found.ToArray();
                }
                lightCache[id] = ls;
            }
            if (ls == null) return false;
            Color acc = Color.black; float sumI = 0f, maxR = 0f;
            for (int i = 0; i < ls.Length; i++)
            {
                Light l = ls[i];
                if (l == null) continue;
                if (takeOver) { if (!savedMask.ContainsKey(l)) savedMask[l] = l.cullingMask; l.cullingMask = 0; }
                else { int m; if (savedMask.TryGetValue(l, out m)) { l.cullingMask = m; savedMask.Remove(l); } }
                if (!l.enabled && l.intensity <= 0.0001f) continue;
                float w = l.intensity;
                if (w <= 0.0001f) continue;
                acc += l.color * w; sumI += w; maxR = Mathf.Max(maxR, l.range);
            }
            if (sumI <= 0f) return true;
            color = acc / sumI; intensity = sumI; range = maxR;
            return true;
        }

        public static bool Available { get { Resolve(); return fxType != null && fAllFX != null && fEffRenderers != null; } }

        private static void Resolve()
        {
            if (resolved) return;
            resolved = true;
            try
            {
                foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
                {
                    if (la.assembly == null || la.name != "Waterfall") continue;
                    fxType = la.assembly.GetType("Waterfall.ModuleWaterfallFX");
                    Type effType = la.assembly.GetType("Waterfall.WaterfallEffect");
                    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    if (fxType != null) { fAllFX = fxType.GetField("allFX", F); fEngineId = fxType.GetField("engineID", F); }
                    if (effType != null) fEffRenderers = effType.GetField("effectRenderers", F);
                    break;
                }
                Debug.Log("[PlumeTrails] Waterfall integration: " + (Available ? "available" : "Waterfall not found or changed"));
            }
            catch (Exception e)
            {
                fxType = null;
                Debug.LogWarning("[PlumeTrails] Waterfall integration off: " + e.Message);
            }
        }

        // The Waterfall module that draws this engine's flame, or null.
        public static PartModule FindModule(Part part, ModuleEngines engine)
        {
            if (!Available) return null;
            PartModule any = null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule m = part.Modules[i];
                if (m == null || m.GetType() != fxType) continue;
                string id = fEngineId != null ? fEngineId.GetValue(m) as string : null;
                if (string.IsNullOrEmpty(id) || id == engine.engineID) return m;
                any = any ?? m;
            }
            return any;
        }

        // THE LOOK OF THE FLAME as the config of its author made it: the two tints of its materials (Waterfall's shaders have _StartTint at the nozzle and _EndTint at the
        // tip, and a _Brightness), averaged over the effects of the module weighted by brightness. The physical plume takes its colours from here when asked to.
        public struct FlameLook { public Color start, end; public float brightness; public bool valid; }
        private static readonly Dictionary<PartModule, KeyValuePair<float, FlameLook>> lookCache = new Dictionary<PartModule, KeyValuePair<float, FlameLook>>();

        public static FlameLook ReadLook(PartModule module)
        {
            FlameLook look = new FlameLook();
            if (module == null || fAllFX == null || fEffRenderers == null) return look;
            KeyValuePair<float, FlameLook> hit;
            if (lookCache.TryGetValue(module, out hit) && Time.time - hit.Key < 0.5f) return hit.Value;
            try
            {
                IEnumerable effects = fAllFX.GetValue(module) as IEnumerable;
                if (effects != null)
                {
                    Color s = Color.black, e = Color.black; float wSum = 0f, bSum = 0f;
                    foreach (object eff in effects)
                    {
                        IList renderers = fEffRenderers.GetValue(eff) as IList;
                        if (renderers == null) continue;
                        for (int i = 0; i < renderers.Count; i++)
                        {
                            Renderer r = renderers[i] as Renderer;
                            Material m = r != null ? r.sharedMaterial : null;
                            if (m == null || !m.HasProperty("_StartTint") || !m.HasProperty("_EndTint")) continue;
                            float b = m.HasProperty("_Brightness") ? Mathf.Max(m.GetFloat("_Brightness"), 0.01f) : 1f;
                            s += m.GetColor("_StartTint") * b; e += m.GetColor("_EndTint") * b; wSum += b; bSum += b;
                        }
                    }
                    if (wSum > 0f) { look.start = s / wSum; look.end = e / wSum; look.brightness = bSum; look.valid = true; }
                }
            }
            catch (Exception) { }
            lookCache[module] = new KeyValuePair<float, FlameLook>(Time.time, look);
            return look;
        }

        // Switches the flame models of a Waterfall module on or off WITHOUT touching its own bookkeeping (forceRenderingOff only
        // stops them being drawn), so Waterfall keeps running and the flame returns the moment we stop drawing the physical plume.
        public static void SetFlameVisible(PartModule module, bool visible)
        {
            if (module == null || fAllFX == null || fEffRenderers == null) return;
            try
            {
                IEnumerable effects = fAllFX.GetValue(module) as IEnumerable;
                if (effects == null) return;
                foreach (object eff in effects)
                {
                    IList renderers = fEffRenderers.GetValue(eff) as IList;
                    if (renderers == null) continue;
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        Renderer r = renderers[i] as Renderer;
                        if (r != null) r.forceRenderingOff = !visible;
                    }
                }
            }
            catch (Exception) { }
        }

        // Measures the flame that belongs to one nozzle: the renderers of the module's effects that touch the neighbourhood
        // of the thrust transform. length = how far past the nozzle the flame reaches along the exhaust, width = its widest
        // diameter. Both 0 when nothing is showing.
        public static bool Measure(PartModule module, Vector3 nozzle, Vector3 dir, out float length, out float width)
        {
            length = 0f; width = 0f;
            if (module == null || fAllFX == null || fEffRenderers == null) return false;
            try
            {
                IEnumerable effects = fAllFX.GetValue(module) as IEnumerable;
                if (effects == null) return false;
                float maxAlong = 0f, maxPerp = 0f;
                foreach (object eff in effects)
                {
                    IList renderers = fEffRenderers.GetValue(eff) as IList;
                    if (renderers == null) continue;
                    for (int i = 0; i < renderers.Count; i++)
                    {
                        Renderer r = renderers[i] as Renderer;
                        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                        Bounds b = r.bounds;
                        if (b.size.sqrMagnitude < 1e-4f) continue;
                        // a flame belongs to this nozzle if it starts near it (bounds are boxes, so allow some slack)
                        float slack = 0.35f * b.size.magnitude + 0.5f;
                        if (b.SqrDistance(nozzle) > slack * slack) continue;

                        Vector3 c = b.center, e = b.extents;
                        for (int k = 0; k < 8; k++)
                        {
                            Vector3 corner = c + new Vector3((k & 1) != 0 ? e.x : -e.x, (k & 2) != 0 ? e.y : -e.y, (k & 4) != 0 ? e.z : -e.z);
                            Vector3 v = corner - nozzle;
                            float along = Vector3.Dot(v, dir);
                            float perp = (v - dir * along).magnitude;
                            if (along > maxAlong) maxAlong = along;
                            if (perp > maxPerp) maxPerp = perp;
                        }
                    }
                }
                length = maxAlong;
                width = maxPerp * 2f;
                return length > 0.2f;
            }
            catch (Exception ex)
            {
                if (!failedLogged) { failedLogged = true; Debug.LogWarning("[PlumeTrails] Waterfall measure failed: " + ex.Message); }
                return false;
            }
        }
    }
}

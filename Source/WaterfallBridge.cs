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

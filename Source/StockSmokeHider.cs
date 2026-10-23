using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Hides the particle smoke the engine parts bring themselves (stock and part-pack EFFECTS such as PlumeParty's "trail" and "Fume"
    // emitters) while the new trail is on: otherwise a thin dotted thread of stock smoke hangs between the flame and the start of our
    // trail, and the two kinds of smoke sit on top of each other. Only the RENDERERS of particle systems whose names say smoke are switched
    // off, every frame (the effect code turns the emitters back on with the throttle, it does not touch the renderers): the flames (Waterfall
    // meshes, "blaze" particles), sparks, debris and the sound stay. Switched back on at once when the option or the new trail goes off.
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class StockSmokeHider : MonoBehaviour
    {
        private static readonly string[] SmokeWords = { "smoke", "trail", "fume", "vapor", "vapour", "contrail", "dust" };
        private static readonly string[] KeepWords = { "flame", "fire", "blaze", "spark", "chunk", "flare", "glow", "shock" };

        private readonly List<ParticleSystemRenderer> hidden = new List<ParticleSystemRenderer>();
        private float nextScan;

        private static bool IsSmoke(Transform t, Transform partRoot)
        {
            bool smoke = false;
            for (Transform x = t; x != null && x != partRoot; x = x.parent)
            {
                string n = x.name.ToLowerInvariant();
                for (int i = 0; i < KeepWords.Length; i++) if (n.Contains(KeepWords[i])) return false;
                for (int i = 0; i < SmokeWords.Length; i++) if (n.Contains(SmokeWords[i])) smoke = true;
            }
            return smoke;
        }

        private void Scan()
        {
            hidden.Clear();
            List<Vessel> vs = FlightGlobals.VesselsLoaded;
            if (vs == null) return;
            for (int v = 0; v < vs.Count; v++)
            {
                Vessel vessel = vs[v];
                if (vessel == null || vessel.parts == null) continue;
                for (int p = 0; p < vessel.parts.Count; p++)
                {
                    Part part = vessel.parts[p];
                    if (part == null || part.FindModuleImplementing<ModuleEngines>() == null) continue;
                    ParticleSystemRenderer[] rs = part.GetComponentsInChildren<ParticleSystemRenderer>(true);
                    for (int i = 0; i < rs.Length; i++)
                        if (rs[i] != null && IsSmoke(rs[i].transform, part.transform)) hidden.Add(rs[i]);
                }
            }
        }

        private void Restore()
        {
            for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].enabled = true;
            hidden.Clear();
        }

        private void LateUpdate()
        {
            bool on = SmokeTuning.HideStockSmoke && SmokeTuning.NewTrail && ModSettings.Enabled;
            if (!on) { if (hidden.Count > 0) Restore(); nextScan = 0f; return; }
            if (Time.time >= nextScan)
            {
                Restore();                    // a part that left (staged, destroyed) must not keep a hidden renderer
                Scan();
                nextScan = Time.time + 2f;
            }
            for (int i = 0; i < hidden.Count; i++) if (hidden[i] != null) hidden[i].enabled = false;
        }

        private void OnDestroy() { Restore(); }
    }
}

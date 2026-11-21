using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // PHYSICAL PLUMES. For every burning engine nozzle in physics range: PlumePhysics turns the engine's own numbers (thrust, mass
    // flow, propellant, nozzle area ratio, throttle) and the ambient pressure into the shape of the free jet (shock cells, Mach disk,
    // core, spread), and the PlumeCore shader draws it volumetrically (emission + absorption, blackbody / gas colour, clipped by the
    // scene depth). The plume therefore changes by itself with altitude, throttle and propellant - diamonds stretch, it swells as the
    // air thins - and replaces the flame models of Waterfall (their renderers are switched off while ours is drawn).
#if !EXPLOSIONS_ONLY
    [KSPAddon(KSPAddon.Startup.Flight, false)]
#endif
    public class PlumeCore : MonoBehaviour
    {
        private class Plume
        {
            public GameObject go;
            public MeshRenderer mr;
            public Material mat;
            public MaterialPropertyBlock mb = new MaterialPropertyBlock();
            public float seen;
            public float seed;
            public float born, nextLog;
        }

        private readonly Dictionary<long, Plume> plumes = new Dictionary<long, Plume>();
        private readonly List<long> stale = new List<long>();
        private static readonly HashSet<string> loggedNozzles = new HashSet<string>();
        private readonly Dictionary<PartModule, float> hideStamp = new Dictionary<PartModule, float>();
        private readonly HashSet<PartModule> hiddenWaterfall = new HashSet<PartModule>();   // Waterfall flames switched off by us: given back when the physical plume is off
        private readonly List<Candidate> cands = new List<Candidate>(64);
        private static Mesh cubeMesh;

        private struct Candidate
        {
            public long key; public Part part; public ModuleEngines engine; public EnginePlumeProfile prof; public Transform t;
            public int nozzles; public PartModule fx; public float thrustN; public float dist2;
        }

        private static PropellantKind Kind(PlumeKind k)
        {
            switch (k)
            {
                case PlumeKind.Solid: return PropellantKind.Solid;
                case PlumeKind.Kerolox: return PropellantKind.Kerolox;
                case PlumeKind.Hydrolox: return PropellantKind.Hydrolox;
                case PlumeKind.Methalox: return PropellantKind.Methalox;
                case PlumeKind.Hypergolic: return PropellantKind.Hypergolic;
                default: return PropellantKind.NuclearH2;
            }
        }

        private void OnDestroy()
        {
            foreach (Plume p in plumes.Values) Kill(p);
            plumes.Clear();
            RestoreWaterfall();
        }

        private static void Kill(Plume p)
        {
            if (p.mr != null) SmokeRenderRegistry.Remove(p.mr);
            if (p.go != null) Destroy(p.go);
            if (p.mat != null) Destroy(p.mat);
        }

        private int failures;
        private bool disabledByErrors;

        // A fault in this module must never take the frame down: the first one is logged with its stack, three in a row switch the
        // module off for the rest of the session (the old rendering paths carry on).
        private void LateUpdate()
        {
            if (disabledByErrors) return;
            try { LateUpdateBody(); failures = 0; }
            catch (System.Exception e)
            {
                failures++;
                if (failures == 1) Debug.LogError("[PlumeTrails] plume failed: " + e);
                if (failures >= 3)
                {
                    disabledByErrors = true;
                    Debug.LogError("[PlumeTrails] plume switched off for this session after repeated errors");
                    try { HideAll(); RestoreWaterfall(); } catch { }
                }
            }
        }

        private void LateUpdateBody()
        {
            Camera cam = Camera.main;
            if (!SmokeTuning.PhysicalPlumes || ShaderCache.PlumeCoreShader == null || cam == null || !ModSettings.Enabled)
            {
                HideAll();
                RestoreWaterfall();
                RestoreEveryWaterfall();
                return;
            }
            everyWaterfallRestored = false;
            if (!SmokeTuning.PlumeHideWaterfall) RestoreWaterfall();
            float now = Time.time;
            Vector3 eye = cam.transform.position;

            // collect the burning nozzles of every loaded vessel
            cands.Clear();
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            for (int vi = 0; vi < loaded.Count; vi++)
            {
                Vessel v = loaded[vi];
                if (v == null || v.packed) continue;
                EngineClusterUtils.EnumerateEngines(v, (part, engine, prof, fx) =>
                {
                    if (!engine.EngineIgnited || engine.currentThrottle < 0.01f || prof == null || !prof.enabled || prof.kind == PlumeKind.None) return;
                    if (engine.thrustTransforms == null || engine.thrustTransforms.Count == 0) return;
                    int n = engine.thrustTransforms.Count;
                    for (int ti = 0; ti < n; ti++)
                    {
                        Transform t = engine.thrustTransforms[ti];
                        if (t == null) continue;
                        cands.Add(new Candidate
                        {
                            key = ((long)part.flightID << 4) + ti + ((long)engine.GetInstanceID() & 0xF) * 0,
                            part = part, engine = engine, prof = prof, t = t, nozzles = n, fx = fx,
                            thrustN = engine.maxThrust * 1000f / n,
                            dist2 = (t.position - eye).sqrMagnitude
                        });
                    }
                });
            }
            // the nearest nozzles first, at most PlumeMaxEngines of them
            cands.Sort((a, b) => a.dist2.CompareTo(b.dist2));
            int max = Mathf.Max(1, SmokeTuning.PlumeMaxEngines);

            for (int i = 0; i < cands.Count; i++)
            {
                Candidate c = cands[i];
                if (i >= max) break;
                Vessel v = c.part.vessel;
                // the engine's own throttle: finalThrust / maxThrust would also drop with the ambient pressure (maxThrust is the vacuum thrust)
                float throttle = Mathf.Clamp01(c.engine.currentThrottle);
                if (throttle < 0.01f) continue;
                double isp = c.engine.atmosphereCurve != null ? c.engine.atmosphereCurve.Evaluate(0f) : 300.0;
                if (isp < 50.0) isp = 300.0;
                double pa = v.mainBody != null && v.mainBody.atmosphere ? v.staticPressurekPa * 1000.0 : 0.0;
                if (double.IsNaN(pa) || pa < 0) pa = 0;

                EngineSpec spec = new EngineSpec
                {
                    kind = Kind(c.prof.kind),
                    thrust = c.thrustN,
                    mdot = c.thrustN / (isp * PlumePhysics.G0),
                    throttle = throttle,
                    chamberPressure = c.prof.chamberPressure,
                    expansionRatio = c.prof.expansionRatio,
                    exitDiameter = c.prof.exitDiameter,
                    ispVac = isp,
                    ispSL = c.engine.atmosphereCurve != null ? c.engine.atmosphereCurve.Evaluate(1f) : 0.0,
                    nozzle = c.prof.nozzle,
                    exitHalfAngleDeg = c.prof.exitHalfAngle
                };
                // the atmosphere this jet flows into: its temperature, its gas, whether there is oxygen to burn the fuel-rich exhaust with
                CelestialBody cb = v.mainBody;
                if (cb != null && cb.atmosphere)
                {
                    spec.ambientTemp = v.atmosphericTemperature > 0.0 ? v.atmosphericTemperature : 0.0;
                    double mw = cb.atmosphereMolarMass;                       // kg/mol in the game
                    if (mw > 0.0) spec.ambientMolarWeight = mw < 1.0 ? mw * 1000.0 : mw;
                    spec.noOxygen = !cb.atmosphereContainsOxygen;
                }
                else spec.noOxygen = true;
                PlumeState s = PlumePhysics.Compute(spec, pa);
                if (loggedNozzles.Add(c.part.partInfo != null ? c.part.partInfo.name : c.part.name))
                    Debug.Log("[PlumeTrails] plume nozzle of " + (c.part.partInfo != null ? c.part.partInfo.name : c.part.name) + ": Isp " + spec.ispVac.ToString("F0") + "/" + spec.ispSL.ToString("F0")
                        + " s (vac/SL) -> exit " + s.De.ToString("F2") + " m, exit Mach " + s.Me.ToString("F2") + ", Pe/Pa now " + s.PR.ToString("F2")
                        + ", lip angle " + (s.lipAngle * 57.2958).ToString("F1") + " deg, " + spec.nozzle);
                Vector3 axis = c.t.forward.normalized;
                // the surface the flame hits (the pre-rendered pad: trench floor, flame deflector): the flame is turned by it
                float deflectAt = 0f; Vector3 deflectN = Vector3.up;
                float length, rMax;
                // the long streak of a high launch (a long exposure): from 70 kPa (a bit above the pad) to 25 kPa (about 9 km) it grows in
                float streakW = SmokeTuning.PlumeStreak * Mathf.Clamp01(((float)pa - 70000f) / (25000f - 70000f));
                // a solid booster has no such streak: its plume is a short, wide, billowing cone (the long smooth streak made it a flat yellow wedge)
                if (spec.kind == PropellantKind.Solid) streakW *= 0.08f;
                PlumeLook.RenderBox(s, SmokeTuning.PlumeScale, out length, out rMax, 0f, streakW);
                if (SmokeTuning.PlumeDeflect)
                {
                    float hitD; Vector3 hitN;
                    if (SurfaceProbe.Along(c.t.position, axis, length, out hitD, out hitN) && hitD > 0.5f)
                    {
                        if (Vector3.Dot(hitN, axis) > 0f) hitN = -hitN;
                        deflectAt = hitD; deflectN = hitN;
                        PlumeLook.RenderBox(s, SmokeTuning.PlumeScale, out length, out rMax, deflectAt, streakW);
                    }
                }

                // the size of the flame the config of the Waterfall effect draws for this nozzle: the physical plume takes it (the flame is hidden while ours is drawn, and the
                // two must not be different sizes)
                WaterfallBridge.FlameLook wlook = new WaterfallBridge.FlameLook();
                if (SmokeTuning.PlumeFromWaterfall && c.fx != null && WaterfallBridge.Available)
                {
                    float wl = 0f, ww = 0f; Vector3 cached;
                    // measured five times a second per nozzle (it walks the renderers of the effects), not every frame
                    if (!flameMeasure.TryGetValue(c.key, out cached) || now - cached.z > 0.2f)
                    {
                        float ml, mw;
                        cached = WaterfallBridge.Measure(c.fx, c.t.position, axis, out ml, out mw) ? new Vector3(ml, mw, now) : new Vector3(0f, 0f, now);
                        flameMeasure[c.key] = cached;
                    }
                    wl = cached.x; ww = cached.y;
                    if (wl > 1f)
                    {
                        // THE EXPANSION: Waterfall's flame follows the air (its controllers widen and lengthen it as the pressure drops), and it is measured live, so the
                        // plume widens with it; smoothed, because it is measured five times a second and the flame itself moves continuously
                        Vector2 sm;
                        if (!flameSmooth.TryGetValue(c.key, out sm)) sm = new Vector2(wl, ww);
                        float k = 1f - Mathf.Exp(-Time.deltaTime / 0.35f);
                        sm = new Vector2(Mathf.Lerp(sm.x, wl, k), Mathf.Lerp(sm.y, ww, k));
                        flameSmooth[c.key] = sm;
                        length = Mathf.Clamp(sm.x, 1f, 320f);
                        rMax = Mathf.Clamp(Mathf.Max(sm.y * 0.5f * 1.05f, 0.25f), 0.25f, 140f);
                    }
                    wlook = WaterfallBridge.ReadLook(c.fx);
                }

                Plume p;
                if (!plumes.TryGetValue(c.key, out p) || p.go == null)
                {
                    p = Create();
                    plumes[c.key] = p;
                }
                p.seen = now;
                if (now >= p.nextLog && now - p.born < 8f)
                {
                    // a short diary of a new plume: if it shows late, this says whether the throttle, the size or the drawing is what lags
                    p.nextLog = now + (now - p.born < 2f ? 0.4f : 1.5f);
                    Debug.Log("[PlumeTrails] plume diary " + (c.part.partInfo != null ? c.part.partInfo.name : c.part.name) + " age " + (now - p.born).ToString("F1")
                        + " s: throttle " + throttle.ToString("F2") + ", finalThrust " + c.engine.finalThrust.ToString("F0") + " kN, drawn length " + length.ToString("F1")
                        + " m radius " + rMax.ToString("F1") + " m, Pe/Pa " + s.PR.ToString("F2") + ", brightness x" + (s.Pc / Mathf.Max((float)s.PcFull, 1f)).ToString("F2"));
                }
                p.go.SetActive(true);
                SmokeRenderRegistry.SetActive(p.mr, true);

                Vector3 right = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.95f ? Vector3.right : Vector3.up).normalized;
                Vector3 origin = c.t.position;
                Vector3 y = right, z = Vector3.Cross(axis, right);
                p.go.transform.position = origin + axis * (length * 0.5f);
                p.go.transform.rotation = Quaternion.LookRotation(z, y);
                p.go.transform.localScale = new Vector3(length, 2f * rMax, 2f * rMax);

                // bright daylight washes a flame out; at night / in the shade it glows: scale the exposure with the sun
                Vector3 zAx = Vector3.Cross(axis, right);
                Vector3 nPlume = new Vector3(Vector3.Dot(deflectN, axis), Vector3.Dot(deflectN, right), Vector3.Dot(deflectN, zAx));
                PlumeLook.Fill(p.mb, s, spec.kind, origin, axis, right, length, rMax, SmokeTuning.PlumeExposure, Mathf.Clamp(Mathf.RoundToInt(SmokeTuning.PlumeSteps * SmokeQuality.Current.marchScale), 20, 192), p.seed,
                    0f, deflectAt, nPlume, SmokeTuning.PlumeDeflectSpread, streakW);
                if (wlook.valid)
                {
                    // its tints: the core is the colour at the nozzle, the rim the colour at the tip (kept bright: a tint is an additive colour)
                    float nrm = Mathf.Max(0.001f, Mathf.Max(wlook.start.r, wlook.start.g, wlook.start.b));
                    float nre = Mathf.Max(0.001f, Mathf.Max(wlook.end.r, wlook.end.g, wlook.end.b));
                    p.mb.SetColor("_PlumeCoreCol", new Color(wlook.start.r / nrm, wlook.start.g / nrm, wlook.start.b / nrm, 1f));
                    p.mb.SetColor("_PlumeRimCol", new Color(wlook.end.r / nre, wlook.end.g / nre, wlook.end.b / nre, 1f));
                }
                p.mr.SetPropertyBlock(p.mb);

                if (SmokeTuning.PlumeHideWaterfall && c.fx != null)
                {
                    // Waterfall can rebuild its effect renderers (staging, throttle changes): re-apply twice a second, not every frame per nozzle
                    float last;
                    if (!hideStamp.TryGetValue(c.fx, out last) || now - last > 0.5f)
                    {
                        WaterfallBridge.SetFlameVisible(c.fx, false);
                        hiddenWaterfall.Add(c.fx);
                        hideStamp[c.fx] = now;
                    }
                }
            }

            // a Waterfall flame is hidden only while OUR plume of that engine is drawn: an engine that fell out of the budget, went out of range or was staged away
            // must get its own flame back (it was left invisible, with nothing drawn in its place)
            if (hideStamp.Count > 0)
            {
                restoreScratch.Clear();
                foreach (KeyValuePair<PartModule, float> kv in hideStamp) if (now - kv.Value > 1.2f) restoreScratch.Add(kv.Key);
                for (int i = 0; i < restoreScratch.Count; i++)
                {
                    if (restoreScratch[i] != null) WaterfallBridge.SetFlameVisible(restoreScratch[i], true);
                    hiddenWaterfall.Remove(restoreScratch[i]); hideStamp.Remove(restoreScratch[i]);
                }
            }

            // plumes that were not drawn this frame go away (the engine stopped, or it fell out of the budget)
            stale.Clear();
            foreach (KeyValuePair<long, Plume> kv in plumes)
                if (now - kv.Value.seen > 0.25f) { stale.Add(kv.Key); }
            for (int i = 0; i < stale.Count; i++)
            {
                Plume p = plumes[stale[i]];
                if (p.go != null) p.go.SetActive(false);
                SmokeRenderRegistry.SetActive(p.mr, false);
                if (now - p.seen > 4f) { Kill(p); plumes.Remove(stale[i]); }
            }
        }

        private readonly Dictionary<long, Vector2> flameSmooth = new Dictionary<long, Vector2>();
        private readonly Dictionary<long, Vector3> flameMeasure = new Dictionary<long, Vector3>();      // nozzle -> (flame length, width, time measured)
        private readonly List<PartModule> restoreScratch = new List<PartModule>();
        private bool everyWaterfallRestored;

        // the physical plumes are off: every Waterfall flame on every loaded vessel is shown again, once (not only the ones this component remembers hiding: the set is
        // lost with the component, and a flame left with forceRenderingOff stays invisible for good)
        private void RestoreEveryWaterfall()
        {
            if (everyWaterfallRestored || !WaterfallBridge.Available) return;
            everyWaterfallRestored = true;
            List<Vessel> loaded = FlightGlobals.VesselsLoaded;
            if (loaded == null) return;
            for (int vi = 0; vi < loaded.Count; vi++)
            {
                Vessel v = loaded[vi];
                if (v == null) continue;
                EngineClusterUtils.EnumerateEngines(v, (part, engine, prof, fx) => { if (fx != null) WaterfallBridge.SetFlameVisible(fx, true); });
            }
        }

        private void RestoreWaterfall()
        {
            if (hiddenWaterfall.Count == 0) return;
            foreach (PartModule fx in hiddenWaterfall) if (fx != null) WaterfallBridge.SetFlameVisible(fx, true);
            hiddenWaterfall.Clear();
            hideStamp.Clear();
        }

        private void HideAll()
        {
            foreach (Plume p in plumes.Values)
            {
                if (p.go != null) p.go.SetActive(false);
                SmokeRenderRegistry.SetActive(p.mr, false);
            }
        }

        private Plume Create()
        {
            if (cubeMesh == null)
            {
                GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            Plume p = new Plume { seed = Random.value * 50f, born = Time.time, nextLog = Time.time };
            p.go = new GameObject("PlumeTrails.Plume");
            p.go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
            p.mr = p.go.AddComponent<MeshRenderer>();
            p.mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.mr.receiveShadows = false;
            p.mat = new Material(ShaderCache.PlumeCoreShader);
            p.mat.renderQueue = 3010;
            p.mr.sharedMaterial = p.mat;
            p.go.layer = 0;
            // drawn by HalfResSmokeRenderer into the reduced-resolution buffer (temporal upscaling, the quality profile), not by the camera
            p.mr.enabled = false;
            SmokeRenderRegistry.Register(p.mr, p.mb);
            SmokeRenderRegistry.SetPriority(p.mr, 1);          // the flame is always drawn in front of the smoke
            return p;
        }
    }
}

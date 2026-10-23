using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    public struct EngineSample
    {
        public Vector3 position;
        public Vector3 forward;
        public float throttle;
        // Delivered thrust as a fraction of rated. For a solid motor this is the one that
        // matters: throttle is pinned at 1 for its whole burn, while the thrust itself
        // follows the grain's curve and tails off at the end - which is exactly when the
        // real exhaust thins out.
        public float thrustFraction;
        public float isp; // realIsp, for exhaust ejection speed scaling
        public uint partId;
        // Physical width of the engine part across its thrust axis, in metres, shared out
        // over its thrust transforms so that summing squares over a group gives the total
        // exhaust footprint. The nozzle-size proxy - see NozzleSizeFactor.
        public float nozzleDiameter;
        // Solid motor? Lets the landing cloud tell a liquid engine it was only let in FOR the
        // splash from one that is allowed the full trail.
        public bool isSolid;
        public PlumeKind kind;       // what the engine burns (solid, kerolox, hydrolox ...): the trail is drawn per kind
        // Delivered thrust of this transform in kN (engine.finalThrust shared over its thrust transforms).
        public float thrustKN;
        // Plume profile of the engine (see EnginePlumeProfiles): size and density multipliers (already including the
        // liquid-plume sliders and the kind's visibility), whether it is water vapour (1) or sooty smoke (0), and the
        // H2O mass fraction of its exhaust.
        public float plumeSize, plumeDensity, plumeSteam, plumeWater, plumeDilution;
        public Color flameColor; public float lightMul;
        // The visible flame of this engine as drawn by Waterfall (m past the nozzle, and its widest diameter); 0 without it.
        public float flameLength, flameWidth;
    }

    // engine gathering and clustering shared by contrail/smoke controllers
    public static class EngineClusterUtils
    {
        // verniers below this fraction of the strongest engine are ignored
        private const float RelativeThrustThreshold = 0.2f;

        // Solid rocket motor?
        //
        // Two tests, because each misses something the other catches. engineType is KSP's
        // own flag and survives propellant renames, but not every part pack sets it.
        // Burning SolidFuel covers those. Either is enough.
        //
        // Dense launch smoke is a property of SOLID propellant: the aluminium in the grain
        // burns to aluminium oxide, and those particles are what make the thick white
        // column. Kerolox leaves far less and hydrolox essentially none, so emitting only
        // from these is not a simplification - it is the real distinction.
        public static bool IsSolidRocket(ModuleEngines engine)
        {
            if (engine == null) return false;
            if (engine.engineType == EngineType.SolidBooster) return true;
            if (engine.propellants == null) return false;
            for (int i = 0; i < engine.propellants.Count; i++)
            {
                if (engine.propellants[i] != null && engine.propellants[i].name == "SolidFuel") return true;
            }
            return false;
        }

        // Whether this engine may emit at all under the current mode. forceAllEngines
        // bypasses the SRB-only filter - used for the landing cloud (28 IX), which needs
        // liquid engines too, without reopening the ascent trail to them.
        private static bool Emits(EnginePlumeProfile prof, bool forceAllEngines)
        {
            if (forceAllEngines || !SmokeTuning.SrbOnly) return true;
            if (prof.IsSolid) return true;
            return SmokeTuning.LiquidPlumes && prof.enabled;
        }

        // Thrust-weighted look of a group from its engines' profiles: size / density multipliers for the liquid
        // plumes, the water-vapour share (0 = sooty SRB smoke, 1 = steam) and the exhaust's H2O mass fraction.
        public static void GroupLook(List<EngineSample> cluster, out float size, out float density, out float steam, out float water, out float dilution)
        {
            float w = 0f, sz = 0f, de = 0f, st = 0f, wa = 0f, di = 0f;
            for (int i = 0; i < cluster.Count; i++)
            {
                float t = Mathf.Max(cluster[i].thrustKN, 0.001f);
                w += t; sz += t * cluster[i].plumeSize; de += t * cluster[i].plumeDensity;
                st += t * cluster[i].plumeSteam; wa += t * cluster[i].plumeWater; di += t * cluster[i].plumeDilution;
            }
            if (w <= 0f) { size = 1f; density = 1f; steam = 0f; water = 0.12f; dilution = 90f; return; }
            size = sz / w; density = de / w; steam = st / w; water = wa / w; dilution = di / w;
        }

        // The engines of a vessel, collected once and rebuilt only when its part count changes. The per-tick
        // gathering used to call FindModulesImplementing on every part (a new List each time, dozens of parts, twice
        // per tick), which is steady garbage and wasted time on a big rocket.
        private sealed class VesselEngines
        {
            public int partCount = -1;
            public readonly List<Part> parts = new List<Part>();
            public readonly List<ModuleEngines> engines = new List<ModuleEngines>();
            public readonly List<EnginePlumeProfile> profiles = new List<EnginePlumeProfile>();
            // Waterfall module drawing each engine's flame (null without Waterfall) and the last flame measurement
            public readonly List<PartModule> fx = new List<PartModule>();
            public float[] fLen = new float[0], fWid = new float[0], fTime = new float[0];
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Vessel, VesselEngines> engineCaches =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Vessel, VesselEngines>();

        private static VesselEngines EnginesOf(Vessel vessel)
        {
            VesselEngines c;
            if (!engineCaches.TryGetValue(vessel, out c))
            {
                c = new VesselEngines();
                engineCaches.Add(vessel, c);
            }
            if (c.partCount != vessel.Parts.Count)
            {
                c.partCount = vessel.Parts.Count;
                c.parts.Clear(); c.engines.Clear(); c.profiles.Clear(); c.fx.Clear();
                foreach (Part part in vessel.Parts)
                {
                    List<ModuleEngines> found = part.FindModulesImplementing<ModuleEngines>();
                    for (int i = 0; i < found.Count; i++)
                    {
                        c.parts.Add(part);
                        c.engines.Add(found[i]);
                        c.profiles.Add(EnginePlumeProfiles.For(part, found[i]));
                        c.fx.Add(WaterfallBridge.FindModule(part, found[i]));
                    }
                }
            }
            return c;
        }

        // Visits the engines of a vessel (the cached list; nothing is allocated per call).
        public static void EnumerateEngines(Vessel vessel, System.Action<Part, ModuleEngines, EnginePlumeProfile, PartModule> visit)
        {
            VesselEngines c = EnginesOf(vessel);
            for (int i = 0; i < c.engines.Count; i++) visit(c.parts[i], c.engines[i], c.profiles[i], c.fx[i]);
        }

        // Largest flame length / width among a group's engines (Waterfall), 0 when none is known.
        // Thrust-weighted flame colour and light strength of a group
        public static void GroupFlameColor(List<EngineSample> cluster, out Color color, out float mul)
        {
            float w = 0f, r = 0f, g = 0f, b = 0f, m = 0f;
            for (int i = 0; i < cluster.Count; i++)
            {
                float t = Mathf.Max(cluster[i].thrustKN, 0.001f);
                w += t; r += t * cluster[i].flameColor.r; g += t * cluster[i].flameColor.g; b += t * cluster[i].flameColor.b; m += t * cluster[i].lightMul;
            }
            if (w <= 0f) { color = new Color(1f, 0.56f, 0.22f); mul = 1f; return; }
            color = new Color(r / w, g / w, b / w); mul = m / w;
        }

        public static void GroupFlame(List<EngineSample> cluster, out float length, out float width)
        {
            length = 0f; width = 0f;
            for (int i = 0; i < cluster.Count; i++)
            {
                if (cluster[i].flameLength > length) length = cluster[i].flameLength;
                if (cluster[i].flameWidth > width) width = cluster[i].flameWidth;
            }
        }

        public static bool HasEngine(Vessel vessel)
        {
            return EnginesOf(vessel).engines.Count > 0;
        }

        public static float GetVesselMaxEngineThrust(Vessel vessel)
        {
            VesselEngines c = EnginesOf(vessel);
            float maxThrust = 0f;
            for (int i = 0; i < c.engines.Count; i++)
            {
                if (c.engines[i].maxThrust > maxThrust) maxThrust = c.engines[i].maxThrust;
            }
            return maxThrust;
        }

        // The launch pad phase: the vehicle is on the pad or just off it. The exhaust of every engine meets the pad then, and a hydrolox engine's (hot
        // steam, cooled and condensed by the deluge) makes a cloud like any other; in flight only the solids / the liquid engines with a plume profile emit.
        private static bool PadPhase(Vessel vessel)
        {
            return SmokeTuning.PadAllEngines && vessel != null && vessel.mainBody != null && vessel.mainBody.atmosphere && vessel.radarAltitude < 250.0 && vessel.srfSpeed < 150.0;
        }

        public static List<EngineSample> GatherEngineSamples(Vessel vessel, bool forceAllEngines = false)
        {
            if (!forceAllEngines && PadPhase(vessel)) forceAllEngines = true;
            List<EngineSample> samples = new List<EngineSample>();
            VesselEngines c = EnginesOf(vessel);
            float maxThrust = 0f;
            for (int i = 0; i < c.engines.Count; i++)
            {
                if (c.engines[i].maxThrust > maxThrust) maxThrust = c.engines[i].maxThrust;
            }
            float minThrust = maxThrust * RelativeThrustThreshold;

            if (c.fLen.Length != c.engines.Count)
            {
                c.fLen = new float[c.engines.Count]; c.fWid = new float[c.engines.Count]; c.fTime = new float[c.engines.Count];
            }
            for (int ei = 0; ei < c.engines.Count; ei++)
            {
                ModuleEngines engine = c.engines[ei];
                Part part = c.parts[ei];
                if (!engine.EngineIgnited || engine.flameout) continue;
                if (engine.currentThrottle <= 0.001f) continue;
                if (engine.maxThrust < minThrust) continue;
                EnginePlumeProfile prof = c.profiles[ei];
                if (!Emits(prof, forceAllEngines)) continue;
                bool liquidLook = SmokeTuning.SrbOnly && SmokeTuning.LiquidPlumes && !prof.IsSolid;
                int nTransforms = Mathf.Max(1, engine.thrustTransforms.Count);

                // the flame Waterfall draws for this engine (re-measured 5x a second: it follows the throttle)
                float flameL = 0f, flameW = 0f;
                if (SmokeTuning.WaterfallIntegration && c.fx[ei] != null && engine.thrustTransforms.Count > 0 && engine.thrustTransforms[0] != null)
                {
                    if (Time.time - c.fTime[ei] > 0.2f)
                    {
                        Transform t0 = engine.thrustTransforms[0];
                        float l, w;
                        if (WaterfallBridge.Measure(c.fx[ei], t0.position, t0.forward, out l, out w)) { c.fLen[ei] = l; c.fWid[ei] = w; }
                        else { c.fLen[ei] = 0f; c.fWid[ei] = 0f; }
                        c.fTime[ei] = Time.time;
                    }
                    flameL = c.fLen[ei]; flameW = c.fWid[ei];
                }

                for (int ti = 0; ti < engine.thrustTransforms.Count; ti++)
                {
                    Transform t = engine.thrustTransforms[ti];
                    if (t == null) continue;
                    samples.Add(new EngineSample
                    {
                        position = t.position,
                        forward = t.forward,
                        throttle = engine.currentThrottle,
                        thrustFraction = engine.maxThrust > 0.001f
                            ? Mathf.Clamp01(engine.finalThrust / engine.maxThrust)
                            : 0f,
                        isp = engine.realIsp,
                        partId = part.flightID,
                        isSolid = prof.IsSolid,
                        kind = prof.kind,
                        plumeSize = liquidLook ? SmokeTuning.LiquidSizeScale * prof.sizeScale : 1f,
                        plumeDensity = liquidLook ? SmokeTuning.LiquidDensityScale * prof.densityScale * prof.Visibility : 1f,
                        plumeSteam = liquidLook && prof.Steam ? 1f : 0f,
                        plumeWater = prof.Water,
                        plumeDilution = prof.Dilution,
                        flameColor = prof.FlameColor, lightMul = prof.LightMul,
                        flameLength = flameL, flameWidth = flameW,
                        thrustKN = engine.finalThrust / nTransforms,
                        // Thrust, not part width: the width of a part is whatever the model happens to be (a whole stage, or
                        // a booster measured along the wrong axis - one SRB came out 24 m wide).
                        nozzleDiameter = SmokeTuning.NozzleFromThrust
                            ? ThrustNozzleDiameter(engine.maxThrust / nTransforms) * (prof.IsSolid ? SmokeTuning.NozzleSolidScale : 1f)
                            : PartDiameter(part, t) / Mathf.Sqrt(nTransforms)
                    });
                }
            }

            return samples;
        }

        // Nozzle exit diameter (m) from the rated thrust of one nozzle (kN). Real exits cluster on D ~ F^0.4:
        // RL10 (110 kN) ~1 m, Merlin 1D (845) ~1-1.7 m, RS-25 (2280) 2.4 m, F-1 (7770) 3.7 m.
        public static float ThrustNozzleDiameter(float thrustKN)
        {
            float d = 2.4f * Mathf.Pow(Mathf.Max(thrustKN, 1f) / 2280f, 0.4f) * SmokeTuning.NozzleThrustScale;
            return Mathf.Clamp(d, 0.15f, 14f);
        }

        // single-linkage clustering by distance
        public static List<List<EngineSample>> ClusterEngines(List<EngineSample> samples, float threshold)
        {
            int n = samples.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            float sqrThreshold = threshold * threshold;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if ((samples[i].position - samples[j].position).sqrMagnitude <= sqrThreshold)
                    {
                        Union(parent, i, j);
                    }
                }
            }

            Dictionary<int, List<EngineSample>> groups = new Dictionary<int, List<EngineSample>>();
            for (int i = 0; i < n; i++)
            {
                int root = Root(parent, i);
                List<EngineSample> list;
                if (!groups.TryGetValue(root, out list))
                {
                    list = new List<EngineSample>();
                    groups[root] = list;
                }
                list.Add(samples[i]);
            }

            return new List<List<EngineSample>>(groups.Values);
        }

        private static int Root(int[] parent, int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Root(parent, a);
            int rb = Root(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        public static Vector3 ComputeCentroid(List<EngineSample> cluster)
        {
            Vector3 sum = Vector3.zero;
            foreach (EngineSample s in cluster) sum += s.position;
            return sum / cluster.Count;
        }

        public static Vector3 ComputeAverageForward(List<EngineSample> cluster)
        {
            Vector3 sum = Vector3.zero;
            foreach (EngineSample s in cluster) sum += s.forward;
            return sum.sqrMagnitude > 0.0001f ? sum.normalized : Vector3.forward;
        }

        public static float ComputeMaxThrottle(List<EngineSample> cluster)
        {
            float max = 0f;
            foreach (EngineSample s in cluster) if (s.throttle > max) max = s.throttle;
            return max;
        }

        public static float ComputeAverageIsp(List<EngineSample> cluster)
        {
            float sum = 0f;
            int count = 0;
            foreach (EngineSample s in cluster)
            {
                if (s.isp <= 0f) continue;
                sum += s.isp;
                count++;
            }
            return count > 0 ? sum / count : 0f;
        }

        public static float ClusterSizeFactor(int engineCount)
        {
            return Mathf.Sqrt(engineCount);
        }

        private static readonly Dictionary<uint, float> partDiameterCache = new Dictionary<uint, float>();

        // Width of the part perpendicular to the thrust axis, from its renderer bounds in
        // the part's own frame (so vessel attitude does not inflate it). For a solid booster
        // the part IS the motor, so this is the motor diameter; cached because bounds are
        // not free and a part's size does not change in flight.
        private static float PartDiameter(Part part, Transform thrust)
        {
            float cached;
            if (partDiameterCache.TryGetValue(part.flightID, out cached)) return cached;

            float diameter = 1.25f;
            try
            {
                Bounds[] renderers = part.GetRendererBounds();
                if (renderers != null && renderers.Length > 0)
                {
                    Bounds b = PartGeometryUtil.MergeBounds(renderers, part.transform);
                    Vector3 axis = part.transform.InverseTransformDirection(thrust.forward);
                    float ax = Mathf.Abs(axis.x), ay = Mathf.Abs(axis.y), az = Mathf.Abs(axis.z);
                    float w = 0f;
                    if (ax < 0.5f) w = Mathf.Max(w, b.size.x);
                    if (ay < 0.5f) w = Mathf.Max(w, b.size.y);
                    if (az < 0.5f) w = Mathf.Max(w, b.size.z);
                    if (w > 0.05f) diameter = w;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PlumeTrails] PartDiameter failed for " + part.partInfo.name + ": " + e.Message);
            }
            Debug.Log(string.Format("[PlumeTrails] nozzle size: part={0} diameter={1:F2}m",
                part.partInfo != null ? part.partInfo.name : "?", diameter));
            partDiameterCache[part.flightID] = diameter;
            return diameter;
        }

        // Plume size from how big the engines physically are. ClusterSizeFactor only
        // counts engines, so a micro motor smoked exactly like a huge booster. Plume width
        // follows nozzle diameter linearly; a cluster's combined footprint is the root of
        // the summed squares (N equal engines -> d*sqrt(N), the old sqrt(N) behaviour).
        // Clamped below: a plume under a few tens of centimetres is sub-pixel noise.
        public static float NozzleSizeFactor(List<EngineSample> cluster, int fallbackCount)
        {
            if (!SmokeTuning.NozzleScaleEnabled) return ClusterSizeFactor(fallbackCount);
            float sumSq = 0f;
            foreach (EngineSample s in cluster) sumSq += s.nozzleDiameter * s.nozzleDiameter;
            if (sumSq <= 0.0001f) return ClusterSizeFactor(fallbackCount);
            float ratio = Mathf.Sqrt(sumSq) / Mathf.Max(SmokeTuning.NozzleReferenceDiameter, 0.1f);
            return Mathf.Max(Mathf.Pow(ratio, SmokeTuning.NozzleScalePower), SmokeTuning.NozzleScaleMin);
        }

        public static HashSet<uint> GetPartIds(List<EngineSample> cluster)
        {
            HashSet<uint> ids = new HashSet<uint>();
            foreach (EngineSample s in cluster) ids.Add(s.partId);
            return ids;
        }

        // structural grouping - stable regardless of throttle, only recomputed on part count change

        public struct EnginePartInfo
        {
            public uint partId;
            public Vector3 position;
        }

        public static List<EnginePartInfo> GatherAllEngineParts(Vessel vessel, bool forceAllEngines = false)
        {
            if (!forceAllEngines && PadPhase(vessel)) forceAllEngines = true;
            List<EnginePartInfo> result = new List<EnginePartInfo>();
            float minThrust = GetVesselMaxEngineThrust(vessel) * RelativeThrustThreshold;
            VesselEngines c = EnginesOf(vessel);

            Part lastPart = null;
            for (int ei = 0; ei < c.engines.Count; ei++)
            {
                Part part = c.parts[ei];
                if (part == lastPart) continue; // already added through an earlier engine module
                if (!(c.engines[ei].maxThrust >= minThrust && Emits(c.profiles[ei], forceAllEngines))) continue;
                lastPart = part;
                result.Add(new EnginePartInfo
                {
                    partId = part.flightID,
                    position = part.transform.position
                });
            }
            return result;
        }

        public static List<HashSet<uint>> GroupEnginePartsStructurally(Vessel vessel, float threshold, bool forceAllEngines = false)
        {
            List<EnginePartInfo> parts = GatherAllEngineParts(vessel, forceAllEngines);
            int n = parts.Count;

            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            float sqrThreshold = threshold * threshold;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if ((parts[i].position - parts[j].position).sqrMagnitude <= sqrThreshold)
                    {
                        Union(parent, i, j);
                    }
                }
            }

            Dictionary<int, HashSet<uint>> groups = new Dictionary<int, HashSet<uint>>();
            for (int i = 0; i < n; i++)
            {
                int root = Root(parent, i);
                HashSet<uint> set;
                if (!groups.TryGetValue(root, out set))
                {
                    set = new HashSet<uint>();
                    groups[root] = set;
                }
                set.Add(parts[i].partId);
            }

            return new List<HashSet<uint>>(groups.Values);
        }

        public static List<EngineSample> FilterSamplesByPartIds(List<EngineSample> samples, HashSet<uint> partIds)
        {
            List<EngineSample> result = new List<EngineSample>();
            foreach (EngineSample s in samples)
            {
                if (partIds.Contains(s.partId)) result.Add(s);
            }
            return result;
        }
    }
}

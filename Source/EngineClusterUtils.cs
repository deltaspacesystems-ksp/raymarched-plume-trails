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

        // Whether this engine may emit at all under the current mode.
        private static bool Emits(ModuleEngines engine)
        {
            return !SmokeTuning.SrbOnly || IsSolidRocket(engine);
        }

        public static float GetVesselMaxEngineThrust(Vessel vessel)
        {
            float maxThrust = 0f;
            foreach (Part part in vessel.Parts)
            {
                List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
                foreach (ModuleEngines engine in engines)
                {
                    if (engine.maxThrust > maxThrust) maxThrust = engine.maxThrust;
                }
            }
            return maxThrust;
        }

        public static List<EngineSample> GatherEngineSamples(Vessel vessel)
        {
            List<EngineSample> samples = new List<EngineSample>();
            float minThrust = GetVesselMaxEngineThrust(vessel) * RelativeThrustThreshold;

            foreach (Part part in vessel.Parts)
            {
                List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
                foreach (ModuleEngines engine in engines)
                {
                    if (!engine.EngineIgnited || engine.flameout) continue;
                    if (engine.currentThrottle <= 0.001f) continue;
                    if (engine.maxThrust < minThrust) continue;
                    if (!Emits(engine)) continue;

                    foreach (Transform t in engine.thrustTransforms)
                    {
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
                            partId = part.flightID
                        });
                    }
                }
            }

            return samples;
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

        public static List<EnginePartInfo> GatherAllEngineParts(Vessel vessel)
        {
            List<EnginePartInfo> result = new List<EnginePartInfo>();
            float minThrust = GetVesselMaxEngineThrust(vessel) * RelativeThrustThreshold;

            foreach (Part part in vessel.Parts)
            {
                List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
                bool hasRealEngine = false;
                foreach (ModuleEngines engine in engines)
                {
                    if (engine.maxThrust >= minThrust && Emits(engine)) { hasRealEngine = true; break; }
                }
                if (!hasRealEngine) continue;

                result.Add(new EnginePartInfo
                {
                    partId = part.flightID,
                    position = part.transform.position
                });
            }
            return result;
        }

        public static List<HashSet<uint>> GroupEnginePartsStructurally(Vessel vessel, float threshold)
        {
            List<EnginePartInfo> parts = GatherAllEngineParts(vessel);
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

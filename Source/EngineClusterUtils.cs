using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    public struct EngineSample
    {
        public Vector3 position;
        public Vector3 forward;
        public float throttle;
        public float isp; // realIsp (aktualny, przy bieżącym ciśnieniu atmosferycznym) - do skalowania prędkości wyrzutu dymu
        public uint partId; // Part.flightID - stałe przez cały lot, niezależne od pozycji
    }

    /// <summary>
    /// Wspólna logika zbierania aktywnych silników statku i grupowania ich w klastry
    /// (single-linkage po odległości). Używana przez ContrailVesselController (cienki
    /// contrail wysokościowy). Dym startowy jest teraz obsługiwany przez wzmocnienie
    /// stockowego efektu (StockSmokeEnhancer), nie przez własny system - patrz tam.
    /// </summary>
    public static class EngineClusterUtils
    {
        public static List<EngineSample> GatherEngineSamples(Vessel vessel)
        {
            List<EngineSample> samples = new List<EngineSample>();

            foreach (Part part in vessel.Parts)
            {
                List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
                foreach (ModuleEngines engine in engines)
                {
                    if (!engine.EngineIgnited || engine.flameout) continue;
                    if (engine.currentThrottle <= 0.001f) continue;

                    foreach (Transform t in engine.thrustTransforms)
                    {
                        if (t == null) continue;
                        samples.Add(new EngineSample
                        {
                            position = t.position,
                            forward = t.forward,
                            throttle = engine.currentThrottle,
                            isp = engine.realIsp,
                            partId = part.flightID
                        });
                    }
                }
            }

            return samples;
        }

        /// <summary>
        /// Single-linkage clustering: silniki są w tym samym klastrze jeśli istnieje
        /// łańcuch sąsiadów bliżej niż threshold. 9 silników blisko siebie = 1 klaster,
        /// oddalony SRB = osobny klaster.
        /// </summary>
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

        /// <summary>Średni realIsp klastra - do skalowania prędkości wyrzutu dymu (wyższy ISP = bardziej "energetyczny" wygląd).</summary>
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

        /// <summary>Więcej silników w klastrze = grubsza smuga u podstawy, malejący przyrost.</summary>
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

        // ---- Grupowanie strukturalne (stabilne, niezależne od throttle) ----
        //
        // Poniższe metody grupują silniki na podstawie WSZYSTKICH części z ModuleEngines
        // obecnych na statku (niezależnie czy aktualnie pracują), więc wynik nie zmienia
        // się przy zmianie throttle - tylko przy faktycznej zmianie struktury statku
        // (odpięcie stopnia, zniszczenie części, dokowanie). To eliminuje całą klasę
        // błędów z poprzedniego podejścia (przeliczanie klastrów co klatkę z samych
        // aktywnych silników prowadziło do "skoków" centroidu przy wyłączaniu silników).

        public struct EnginePartInfo
        {
            public uint partId;
            public Vector3 position;
        }

        public static List<EnginePartInfo> GatherAllEngineParts(Vessel vessel)
        {
            List<EnginePartInfo> result = new List<EnginePartInfo>();
            foreach (Part part in vessel.Parts)
            {
                List<ModuleEngines> engines = part.FindModulesImplementing<ModuleEngines>();
                if (engines.Count == 0) continue;

                result.Add(new EnginePartInfo
                {
                    partId = part.flightID,
                    position = part.transform.position
                });
            }
            return result;
        }

        /// <summary>
        /// Grupuje WSZYSTKIE części z silnikiem (nie tylko aktywne) po pozycji -
        /// wynik to stabilna definicja grup, do której later filtrujemy tylko żywe,
        /// aktywne próbki. Wywoływać tylko przy zmianie struktury statku, nie co klatkę.
        /// </summary>
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

        /// <summary>Filtruje listę żywych próbek do tych należących do danej grupy strukturalnej.</summary>
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

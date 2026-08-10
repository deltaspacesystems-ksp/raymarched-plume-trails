using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Zastępuje ContrailEmitterModule. Jeden kontroler na cały statek zamiast jednego
    /// PartModule na silnik.
    ///
    /// v3: grupowanie STRUKTURALNE zamiast dopasowywania klastrów co klatkę. Grupy
    /// silników (które silniki należą do której smugi) są liczone raz, na podstawie
    /// WSZYSTKICH części z silnikiem na statku (niezależnie od throttle), i przeliczane
    /// tylko gdy zmienia się liczba części na statku (odpięcie stopnia, zniszczenie
    /// części) - nie co klatkę. Dzięki temu wyłączenie/przydiszenie throttle części
    /// silników w klastrze nie zmienia już przynależności do grupy ani nie powoduje
    /// "skoku" i utraty ciągłości smugi - throttle wpływa tylko na to, czy grupa aktualnie
    /// coś spawnuje, nigdy na definicję samej grupy.
    ///
    /// VesselModule nie wymaga MM patcha - KSP automatycznie dopina go do każdego
    /// załadowanego Vessel, jeśli assembly jest w GameData/.../Plugins.
    /// </summary>
    public class ContrailVesselController : VesselModule
    {
        // ---- Konfiguracja ----

        public float clusterDistanceThreshold = 3.5f; // części z silnikiem bliżej siebie niż to -> jedna grupa

        public float offsetDistance = 10f;
        public float minAltitude = 6000f;
        public float maxAltitude = 12000f;
        public float minThrottle = 0.15f;
        public float spawnInterval = 0.15f;

        public float pointLifeTime = 180f;
        public int maxPointsPerTrail = 300;

        public float startRadius = 0.4f;
        public float maxRadius = 5.5f;
        public float growthSharpness = 2.2f;
        public float dragToAirVelocity = 0.85f;

        public bool debugLogging = true;
        private float debugLogTimer;

        // ---- Stan wewnętrzny ----

        private class TrackedGroup
        {
            public int id;
            public HashSet<uint> partIds; // stała definicja grupy - zmienia się TYLKO przy RecomputeGroups
            public Vector3 lastCentroid;  // tylko do logowania/offsetu spawnu, nie do dopasowania
            public PuffTrailMesh trailMesh;
            public float spawnTimer;
        }

        private readonly List<TrackedGroup> trackedGroups = new List<TrackedGroup>();
        private int nextGroupId;
        private int lastPartCount = -1;

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || !vessel.loaded) return;
            if (vessel.vesselType == VesselType.Debris) return;

            // Przeliczamy grupy TYLKO gdy zmieniła się liczba części na statku - to jest
            // tani, przybliżony sygnał "coś strukturalnie się zmieniło" (odpięcie stopnia,
            // zniszczenie części, dokowanie). Zwykła zmiana throttle NIE zmienia liczby
            // części, więc nie wywoła przeliczenia - to jest sedno naprawy tego buga.
            if (vessel.Parts.Count != lastPartCount)
            {
                RecomputeGroups();
                lastPartCount = vessel.Parts.Count;
            }

            List<EngineSample> liveSamples = EngineClusterUtils.GatherEngineSamples(vessel);
            bool globalConditionsMet = CheckGlobalConditions();

            bool logThisFrame = false;
            if (debugLogging)
            {
                debugLogTimer -= TimeWarp.fixedDeltaTime;
                if (debugLogTimer <= 0f)
                {
                    logThisFrame = true;
                    debugLogTimer = 1f;
                }
            }

            if (logThisFrame)
            {
                Debug.Log(string.Format(
                    "[VolumetricContrails] vessel={0} alt={1:F0} atmDensity={2:F3} liveEngines={3} grup={4} globalConditionsMet={5}",
                    vessel.vesselName, vessel.altitude, vessel.atmDensity, liveSamples.Count, trackedGroups.Count, globalConditionsMet));

                if (!globalConditionsMet)
                {
                    Debug.Log(string.Format(
                        "[VolumetricContrails]   -> warunki globalne NIE spełnione (minAltitude={0} maxAltitude={1} minAtmDensity=0.05)",
                        minAltitude, maxAltitude));
                }
            }

            foreach (TrackedGroup g in trackedGroups)
            {
                List<EngineSample> groupSamples = EngineClusterUtils.FilterSamplesByPartIds(liveSamples, g.partIds);

                if (groupSamples.Count > 0)
                {
                    Vector3 centroid = EngineClusterUtils.ComputeCentroid(groupSamples);
                    Vector3 avgForward = EngineClusterUtils.ComputeAverageForward(groupSamples);
                    float aggThrottle = EngineClusterUtils.ComputeMaxThrottle(groupSamples);
                    g.lastCentroid = centroid;

                    bool conditionsMet = globalConditionsMet && aggThrottle >= minThrottle;

                    if (logThisFrame)
                    {
                        Debug.Log(string.Format(
                            "[VolumetricContrails]   grupa id={0} silniki={1}/{2} throttle={3:F2} conditionsMet={4} spawnTimer={5:F2}",
                            g.id, groupSamples.Count, g.partIds.Count, aggThrottle, conditionsMet, g.spawnTimer));
                    }

                    if (conditionsMet)
                    {
                        g.spawnTimer -= TimeWarp.fixedDeltaTime;
                        if (g.spawnTimer <= 0f)
                        {
                            Vector3 spawnPos = centroid + avgForward * offsetDistance;
                            Vector3 initialVelocity = vessel.GetSrfVelocity() * (1f - dragToAirVelocity);
                            g.trailMesh.AddPoint(spawnPos, initialVelocity);

                            g.spawnTimer = spawnInterval / Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(aggThrottle));

                            if (debugLogging)
                            {
                                Debug.Log(string.Format(
                                    "[VolumetricContrails]   -> spawn punktu grupa id={0} pos={1} throttle={2:F2}",
                                    g.id, spawnPos, aggThrottle));
                            }
                        }
                    }
                }

                g.trailMesh.Tick(TimeWarp.fixedDeltaTime);
            }

            // Sprzątanie: grupy, których definicja partIds jest pusta (osierocone przy
            // RecomputeGroups - patrz niżej) i których smuga już wygasła.
            for (int i = trackedGroups.Count - 1; i >= 0; i--)
            {
                TrackedGroup g = trackedGroups[i];
                if (g.partIds.Count == 0 && !g.trailMesh.HasActivePoints)
                {
                    Object.Destroy(g.trailMesh.gameObject);
                    trackedGroups.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Przelicza definicje grup od zera na podstawie aktualnej struktury statku, i
        /// mapuje je na istniejące TrackedGroup po jak największym pokryciu partIds -
        /// zachowując ciągłość smugi dla grup, których zestaw części się nie zmienił
        /// (typowy przypadek: coś niezwiązanego odpadło gdzie indziej na statku).
        /// Grupy, które nie znalazły dopasowania w nowym układzie, dostają puste partIds
        /// (osierocone - ich smuga dogasa i zostaje posprzątana normalnie).
        /// Nowe grupy bez odpowiednika dostają świeży TrackedGroup.
        /// </summary>
        private void RecomputeGroups()
        {
            List<HashSet<uint>> newGroups = EngineClusterUtils.GroupEnginePartsStructurally(vessel, clusterDistanceThreshold);

            // WAŻNE: trackedGroups.Count PRZED pętlą - trackedGroups.Add() poniżej
            // powiększa listę W TRAKCIE tego samego foreach (gdy tworzymy nową grupę
            // dla newPartIds bez dopasowania), więc claimed[] musi mieć stały rozmiar
            // zgodny ze stanem SPRZED pętli, inaczej kolejna iteracja foreach (po
            // dodaniu nowej grupy) próbuje odczytać claimed[i] dla i >= claimed.Length
            // -> IndexOutOfRangeException, przerywający RecomputeGroups w połowie.
            int originalGroupCount = trackedGroups.Count;
            bool[] claimed = new bool[originalGroupCount];

            foreach (HashSet<uint> newPartIds in newGroups)
            {
                int bestIndex = -1;
                int bestOverlap = 0;

                for (int i = 0; i < originalGroupCount; i++)
                {
                    if (claimed[i]) continue;

                    int overlap = 0;
                    foreach (uint id in newPartIds)
                    {
                        if (trackedGroups[i].partIds.Contains(id)) overlap++;
                    }

                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    trackedGroups[bestIndex].partIds = newPartIds;
                    claimed[bestIndex] = true;

                    if (debugLogging)
                    {
                        Debug.Log(string.Format(
                            "[VolumetricContrails] RecomputeGroups: grupa id={0} zaktualizowana, {1} części",
                            trackedGroups[bestIndex].id, newPartIds.Count));
                    }
                }
                else
                {
                    TrackedGroup g = new TrackedGroup
                    {
                        id = nextGroupId++,
                        partIds = newPartIds
                    };

                    GameObject trailObj = new GameObject("ContrailGroup_" + g.id);
                    g.trailMesh = trailObj.AddComponent<PuffTrailMesh>();

                    float sizeFactor = EngineClusterUtils.ClusterSizeFactor(newPartIds.Count);
                    g.trailMesh.Initialize(
                        startRadius * sizeFactor,
                        maxRadius * sizeFactor,
                        growthSharpness,
                        pointLifeTime,
                        maxPointsPerTrail,
                        vessel.mainBody);

                    trackedGroups.Add(g);

                    if (debugLogging)
                    {
                        Debug.Log(string.Format(
                            "[VolumetricContrails] RecomputeGroups: nowa grupa id={0}, {1} części",
                            g.id, newPartIds.Count));
                    }
                }
            }

            // Grupy niepodklaimowane przez żadną nową grupę są strukturalnie martwe
            // (wszystkie ich części odpadły/zniknęły) - osierocamy (pusty partIds),
            // ich smuga dogasa naturalnie i zostanie posprzątana w FixedUpdate.
            for (int i = 0; i < originalGroupCount; i++)
            {
                if (!claimed[i])
                {
                    trackedGroups[i].partIds = new HashSet<uint>();
                }
            }
        }

        private bool CheckGlobalConditions()
        {
            double altitude = vessel.altitude;
            if (altitude < minAltitude || altitude > maxAltitude) return false;
            if (vessel.atmDensity < 0.05) return false;
            return true;
        }

        private void OnDestroy()
        {
            foreach (TrackedGroup g in trackedGroups)
            {
                if (g.trailMesh != null) Object.Destroy(g.trailMesh.gameObject);
            }
            trackedGroups.Clear();
        }
    }
}

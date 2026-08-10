using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Dym startowy jako jeden raymarchowany volume (SmokeVolumeGroup). Kontroler decyduje
    /// TYLKO kiedy/gdzie spawnować kolejny kłąb, na bazie klastrowania silników
    /// (EngineClusterUtils - ten sam sprawdzony mechanizm co ContrailVesselController).
    /// </summary>
    public class LaunchSmokeController : VesselModule
    {
        public float clusterDistanceThreshold = 3.5f;

        public float offsetDistance = 15f;
        public float spawnMaxAltitude = 15000f;
        public float minThrottle = 0.15f;
        public float spawnInterval = 0.2f;

        // Wyrzut z silnika - LEKKI, nie gwałtowny (zgodnie z ostatnią wskazówką:
        // "ma tylko lekko go wyrzucać").
        public float ejectionSpeed = 12f;

        // Podniesione (było 25) - teraz gdy budżet aktywnych kłębów nie jest już
        // sztucznie ograniczony do rozmiaru tablicy shadera (patrz EnforcePuffBudget
        // w SmokeVolumeGroup), dłuższy lifeTime pozwala śladowi realnie "kumulować
        // się" gdy rakieta leci wolno, zamiast być ograniczonym przez sam czas życia
        // kłębu niezależnie od budżetu.
        public float lifeTime = 40f;
        // Chmura u ziemi żyje dużo dłużej niż ogon - inaczej gasła zanim zdążyła
        // urosnąć do czegoś trwałego (rakieta przestaje dokładać nowe kłęby do bazy
        // już ok. 10-20s po starcie, powyżej ~500m).
        public float settledLifeTime = 100f;
        // Log (ActiveDebug) pokazał, że aktywny budżet (maxPuffs - maxSettled = 160
        // przy starej wartości 400) był realnym wąskim gardłem - saturował się już
        // przy 244m i zostawał tam przez CAŁY lot, więc podniesienie lifeTime (25->40)
        // nic nie dawało, bo budżet ucinał historię DUŻO wcześniej niż wiek. Podniesione
        // mocno - render i tak przycina do garstki punktów kontrolnych (patrz
        // SmokeVolumeGroup.UpdateActiveLayer), więc to praktycznie darmowe.
        public int maxPuffsPerGroup = 3000;

        public float clusterStartSize = 1.8f;
        public float maxSize = 18f;
        // Niżej = wolniejszy wzrost na początku (kłąb zostaje cienki dłużej, zanim
        // spuchnie) - daje ostrzejszą "igłę" tuż za silnikiem i mocniejszy kontrast
        // z grubą, kłębiastą podstawą, jak na referencjach KSA.
        public float growthSharpness = 0.6f;

        public float minSpeedForBillowing = 20f;
        public float maxSpeedForThinTrail = 500f;
        // Dawniej 0.35 - przy maxSpeedForThinTrail=250 m/s (osiąganym już ok. 2-2.5km
        // wysokości w typowym wznoszeniu) smuga nagle chudła do 35% promienia,
        // wyglądając jak przejście na zupełnie inny, cienki efekt zamiast ciągłej,
        // kłębiastej chmury. Płytsze ścienianie (i wyższy próg prędkości powyżej)
        // zostawia jeden spójny wygląd na całej trasie.
        public float thinTrailSizeMultiplier = 0.8f;

        public float buoyancySpeed = 2.2f;
        public Vector3 windDrift = new Vector3(1f, 0f, 0f);

        public float fadeStartAltitude = 12000f;
        public float fadeEndAltitude = 15000f;

        public bool debugLogging = true;
        private float debugLogTimer;

        private class TrackedGroup
        {
            public int id;
            public HashSet<uint> partIds;
            public Vector3 centroid;
            public SmokeVolumeGroup smokeMesh;
            public float spawnTimer;
            public Vector3? lastSpawnPos; // do wykrywania, że statek "uciekł" spawnom przy dużej prędkości
            public bool wasEmitting; // patrz wstawianie znacznika przerwania ciągłości przy wznowieniu spalania
        }

        // Jaki ułamek promienia kłębu może maksymalnie dzielić środki dwóch kolejnych
        // kłębów, żeby ich miękkie krawędzie (patrz _BlendRadiusMultiplier w shaderze)
        // nadal się nachodziły. Poniżej tej wartości - wymuszamy dodatkowy spawn
        // niezależnie od spawnTimer, bo inaczej przy dużej prędkości rakiety odległość
        // między kolejnymi kłębami (czysto czasowy spawnInterval) rośnie wraz z
        // prędkością i kłęby przestają się zlewać ("paciorki na sznurku").
        private const float MaxSpawnSpacingFraction = 0.5f;
        private const float MinSpawnSpacing = 1.0f;

        // Jeśli odcinek do "dogonienia" jest podejrzanie długi (np. silniki chwilowo
        // przestały spełniać warunki na kilka sekund, po czym wróciły daleko od
        // ostatniego spawnu) - NIE łączymy go interpolacją (wyszłaby prosta "wstęga"
        // dymu donikąd, widoczna jako pętle/mosty nad i pod rakietą). Zamiast tego po
        // prostu zaczynamy od nowa od aktualnej pozycji.
        private const float MaxBridgeableSegment = 60f;

        private readonly List<TrackedGroup> trackedGroups = new List<TrackedGroup>();
        private int nextGroupId;
        private int lastPartCount = -1;

        private float SizeMultiplierForSpeed(float speed)
        {
            if (speed <= minSpeedForBillowing) return 1f;
            if (speed >= maxSpeedForThinTrail) return thinTrailSizeMultiplier;
            float t = (speed - minSpeedForBillowing) / (maxSpeedForThinTrail - minSpeedForBillowing);
            return Mathf.Lerp(1f, thinTrailSizeMultiplier, t);
        }

        private float SizeMultiplierForAltitude(double altitude)
        {
            if (altitude <= fadeStartAltitude) return 1f;
            if (altitude >= fadeEndAltitude) return 0.35f;
            float t = (float)((altitude - fadeStartAltitude) / (fadeEndAltitude - fadeStartAltitude));
            return Mathf.Lerp(1f, 0.35f, t);
        }

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || !vessel.loaded) return;
            if (vessel.vesselType == VesselType.Debris) return;

            if (vessel.Parts.Count != lastPartCount)
            {
                RecomputeGroups();
                lastPartCount = vessel.Parts.Count;
            }

            bool canSpawn = vessel.altitude <= spawnMaxAltitude;

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

            List<EngineSample> liveSamples = canSpawn
                ? EngineClusterUtils.GatherEngineSamples(vessel)
                : new List<EngineSample>();

            float currentSpeed = (float)vessel.srfSpeed;
            float sizeMultiplier = SizeMultiplierForSpeed(currentSpeed) * SizeMultiplierForAltitude(vessel.altitude);

            if (logThisFrame)
            {
                Debug.Log(string.Format(
                    "[VolumetricContrails][Smoke] vessel={0} alt={1:F0} speed={2:F0} sizeMult={3:F2} canSpawn={4} liveEngines={5} grup={6}",
                    vessel.vesselName, vessel.altitude, currentSpeed, sizeMultiplier, canSpawn, liveSamples.Count, trackedGroups.Count));
            }

            foreach (TrackedGroup g in trackedGroups)
            {
                if (canSpawn)
                {
                    List<EngineSample> groupSamples = EngineClusterUtils.FilterSamplesByPartIds(liveSamples, g.partIds);

                    if (groupSamples.Count > 0)
                    {
                        float aggThrottle = EngineClusterUtils.ComputeMaxThrottle(groupSamples);
                        g.centroid = EngineClusterUtils.ComputeCentroid(groupSamples);

                        if (aggThrottle >= minThrottle)
                        {
                            Vector3 centroid = EngineClusterUtils.ComputeCentroid(groupSamples);
                            Vector3 avgForward = EngineClusterUtils.ComputeAverageForward(groupSamples);
                            Vector3 spawnPos = centroid + avgForward * offsetDistance;

                            float sizeFactor = EngineClusterUtils.ClusterSizeFactor(g.partIds.Count);
                            float currentRadius = clusterStartSize * sizeFactor * sizeMultiplier;

                            // Co KLATKĘ FIZYKI (nie tylko co spawn) - patrz komentarz przy
                            // SmokeVolumeGroup.SetLiveTip.
                            g.smokeMesh.SetLiveTip(spawnPos, currentRadius);

                            // Dolny próg na maxSpacing - inaczej przy dużej prędkości (mały
                            // promień z thinTrailSizeMultiplier) próg robi się tak mały, że
                            // wymagałby dziesiątek kłębów na klatkę, żeby go dogonić.
                            float maxSpacing = Mathf.Max(currentRadius * MaxSpawnSpacingFraction, MinSpawnSpacing);

                            g.spawnTimer -= TimeWarp.fixedDeltaTime;
                            bool timerElapsed = g.spawnTimer <= 0f;
                            bool outranSpacing = g.lastSpawnPos.HasValue
                                && Vector3.Distance(spawnPos, g.lastSpawnPos.Value) >= maxSpacing;

                            if (timerElapsed || outranSpacing)
                            {
                                // Znacznik przerwania ciągłości: samo wyzerowanie
                                // lastSpawnPos przy wyłączeniu silnika NIE wystarczyło -
                                // krzywa Catmull-Rom po stronie renderu i tak łączy stare
                                // i nowe kłęby w jedną gładką linię, bo są po prostu
                                // kolejnymi wpisami na tej samej liście (nie wie, że był
                                // między nimi martwy okres). Kłąb o WYMUSZONYM promieniu 0
                                // (sizeScale=0) przerywa segmenty po obu stronach w
                                // shaderze (pomija segmenty z promieniem<=0), więc stary i
                                // nowy odcinek trasy faktycznie się nie łączą.
                                if (!g.wasEmitting)
                                {
                                    g.smokeMesh.AddPuff(spawnPos, Vector3.zero, 0f);
                                }
                                g.wasEmitting = true;

                                // BEZ prędkości rakiety - kłąb ma być wyrzucony z silnika
                                // i zostać "zostawiony w tyle" gdy rakieta odlatuje (tak jak
                                // stockowy dym), a nie ciągnięty razem z nią. Ślad/trail
                                // powstaje przez to, że rakieta ucieka od własnego dymu, nie
                                // przez to, że dym leci razem z nią.
                                //
                                // ejectionSpeed skalowany realIsp silnika względem 300s
                                // (typowy dobry silnik) - wydajniejsze silniki wyglądają
                                // bardziej "energetycznie" przy wyrzucie, bez używania
                                // dosłownej prędkości spalin (2000-4500 m/s - absurdalnie
                                // szybkie wizualnie, jak laser, nie dym).
                                float avgIsp = EngineClusterUtils.ComputeAverageIsp(groupSamples);
                                float ispScale = avgIsp > 0f ? Mathf.Clamp(avgIsp / 300f, 0.5f, 2f) : 1f;
                                Vector3 initialVelocity = avgForward * (ejectionSpeed * ispScale);

                                // Przy dużej prędkości statku odległość pokonana w JEDNYM
                                // FixedUpdate może przekraczać maxSpacing wielokrotnie -
                                // spawnowanie tylko jednego kłębu i tak zostawiłoby
                                // odstępy większe niż maxSpacing. Interpolujemy więc
                                // brakujące punkty wzdłuż odcinka lastSpawnPos->spawnPos.
                                if (g.lastSpawnPos.HasValue && maxSpacing > 0.01f)
                                {
                                    float segmentLength = Vector3.Distance(spawnPos, g.lastSpawnPos.Value);

                                    if (segmentLength <= MaxBridgeableSegment)
                                    {
                                        int extraPoints = Mathf.FloorToInt(segmentLength / maxSpacing);
                                        extraPoints = Mathf.Min(extraPoints, 40); // bezpiecznik

                                        for (int p = 1; p <= extraPoints; p++)
                                        {
                                            float u = p / (float)(extraPoints + 1);
                                            Vector3 interpPos = Vector3.Lerp(g.lastSpawnPos.Value, spawnPos, u);
                                            g.smokeMesh.AddPuff(interpPos, initialVelocity, sizeMultiplier);
                                        }
                                    }
                                }

                                g.smokeMesh.AddPuff(spawnPos, initialVelocity, sizeMultiplier);
                                g.lastSpawnPos = spawnPos;

                                g.spawnTimer = spawnInterval / Mathf.Lerp(0.6f, 1.4f, Mathf.Clamp01(aggThrottle));
                            }
                        }
                        else
                        {
                            // Silnik przestał spełniać warunki (throttle spadł poniżej progu) -
                            // czyścimy lastSpawnPos, żeby przy ponownym zapłonie NIE
                            // mostkować interpolacją przez martwy okres (kiedy spalanie
                            // faktycznie nie zachodziło, więc nie powinno tam być dymu) -
                            // inaczej powstawał "natychmiastowy" sztuczny odcinek między
                            // starą a nową pozycją zapłonu.
                            g.smokeMesh.ClearLiveTip();
                            g.lastSpawnPos = null;
                            g.wasEmitting = false;
                        }
                    }
                    else
                    {
                        g.smokeMesh.ClearLiveTip();
                        g.lastSpawnPos = null;
                        g.wasEmitting = false;
                    }
                }
                else
                {
                    g.smokeMesh.ClearLiveTip();
                    g.lastSpawnPos = null;
                    g.wasEmitting = false;
                }

                g.smokeMesh.Tick(TimeWarp.fixedDeltaTime);
            }

            for (int i = trackedGroups.Count - 1; i >= 0; i--)
            {
                TrackedGroup g = trackedGroups[i];
                if (g.partIds.Count == 0 && !g.smokeMesh.HasActivePuffs)
                {
                    Object.Destroy(g.smokeMesh.gameObject);
                    trackedGroups.RemoveAt(i);
                }
            }
        }

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
                }
                else
                {
                    TrackedGroup g = new TrackedGroup
                    {
                        id = nextGroupId++,
                        partIds = newPartIds
                    };

                    GameObject smokeObj = new GameObject("SmokeBillboardGroup_" + g.id);
                    g.smokeMesh = smokeObj.AddComponent<SmokeVolumeGroup>();

                    float sizeFactor = EngineClusterUtils.ClusterSizeFactor(newPartIds.Count);

                    g.smokeMesh.Initialize(
                        clusterStartSize * sizeFactor,
                        maxSize * sizeFactor,
                        growthSharpness,
                        lifeTime,
                        settledLifeTime,
                        maxPuffsPerGroup,
                        buoyancySpeed,
                        windDrift,
                        vessel.mainBody,
                        fadeStartAltitude,
                        fadeEndAltitude);

                    trackedGroups.Add(g);

                    if (debugLogging)
                    {
                        Debug.Log(string.Format(
                            "[VolumetricContrails][Smoke] RecomputeGroups: nowa grupa id={0}, {1} części",
                            g.id, newPartIds.Count));
                    }
                }
            }

            for (int i = 0; i < originalGroupCount; i++)
            {
                if (!claimed[i])
                {
                    trackedGroups[i].partIds = new HashSet<uint>();
                }
            }
        }

        private void OnDestroy()
        {
            foreach (TrackedGroup g in trackedGroups)
            {
                if (g.smokeMesh != null) Object.Destroy(g.smokeMesh.gameObject);
            }
            trackedGroups.Clear();
        }
    }
}

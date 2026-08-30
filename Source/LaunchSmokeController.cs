using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    // launch smoke as one raymarched volume per engine cluster
    public class LaunchSmokeController : VesselModule
    {
        public float clusterDistanceThreshold = 3.5f;

        public float offsetDistance = 15f;
        public float spawnMaxAltitude = 15000f;
        public float minThrottle = 0.15f;
        // Target spacing in time; gaps between frames are filled by interpolation.
        public float spawnInterval = 0.08f;
        // Closest a new puff may sit to the last one, as a fraction of its radius. Below
        // this it lies entirely inside its predecessor and adds nothing but overlap.
        private const float MinSpawnSpacingFraction = 0.35f;
        // hard cap so a lag spike or a very fast vessel cannot emit hundreds in one frame
        public int maxPuffsPerFrame = 12;
        // how far back in time a gap may reach before it stops being treated as continuous
        private const float MaxBridgeSeconds = 0.5f;

        // Real launch smoke hangs around for minutes. Decimation thins old puffs as they
        // grow, so a long life costs much less than linearly.
        public float lifeTime = 150f;
        public int maxPuffsPerGroup = 8000;

        public float clusterStartSize = 1.8f;
        // Box volume scales cubically with radius, so this is a big lever on shader cost.
        public float maxSize = 18f;
        // 1-(1-t)^n. The exponent is the growth rate at t=0, so above 1 grows fast right
        // after spawn (closing gaps) then eases off. Too high and puffs reach full size in
        // half their life, which reads as a fixed-width column rather than billowing.
        public float growthSharpness = 2f;
        // Time to reach maxSize, independent of lifeTime.
        public float growthTime = 8f;

        // Sideways kick near the pad, random per puff - exhaust hitting the ground and
        // spreading out. Deliberately not axial: pushing the whole chain one way stretches
        // it open, since puffs of different ages decay at different rates.
        public float groundImpingementSpeed = 16f;

        // --- ground cloud, emitted where the exhaust hits the pad ---
        // Shares the trail's volume rather than having its own renderer: ground puffs go
        // first in the spine array, spatially sorted, with a zero-radius separator before
        // the trail. One draw, one density field, no compositing seam.
        //
        // OFF for now - it flickers worse than anything else, being the biggest, slowest
        // thing on screen and sitting right where new puffs keep appearing. A hold, not a
        // decision; leaving it on made judging everything else harder.
        public bool groundCloudEnabled = false;
        // how far down the thrust axis to look for the deflector/deck
        public float groundCloudMaxReach = 120f;
        // Outward speed along the pad axis - near exhaust speed, not a gentle push.
        // GroundVelocityConvergeRate brakes it hard, so reach is roughly speed/rate.
        public float groundCloudOutwardSpeed = 230f;
        // Kept low: the 200-point spine is shared with the trail, so a big cloud has to
        // come from puff SIZE, not from thousands of small puffs crowding the trail out.
        public int groundCloudPuffsPerTick = 5;
        public float groundCloudSizeScale = 3.2f;
        public float groundImpingementRange = 80f;

        // Real exhaust jets DOWN the thrust axis and only spreads after hitting the pad.
        // Without this nothing ever had downward velocity, so the bounce code never ran.
        // Scoped to the same altitude ramp as the impingement kick.
        public float exhaustDownwardSpeed = 38f;

        public float minSpeedForBillowing = 20f;
        public float maxSpeedForThinTrail = 500f;
        public float thinTrailSizeMultiplier = 0.8f;

        public float buoyancySpeed = 2.2f;
        public Vector3 windDrift = new Vector3(1f, 0f, 0f);

        public float fadeStartAltitude = 12000f;
        public float fadeEndAltitude = 15000f;

        // Off by default - every line is a Debug.Log with a stack trace and a disk write,
        // landing exactly in the window where frame drops were reported.
        public bool debugLogging = false;
        private float debugLogTimer;

        private class TrackedGroup
        {
            public int id;
            public HashSet<uint> partIds;
            public Vector3 centroid;
            public SmokeVolumeGroup smokeMesh;
            public float spawnTimer;
            // Bumped whenever emission resumes after a pause, so the renderer can break
            // the capsule chain between bursts without guessing from geometry.
            public int burnId;
            public float lastSpawnTime;
            public Vector3? lastSpawnPos;
            public Vector3? smoothedForward;
        }

        // spawn trigger: distance since last puff, timer as a fallback
        private const float MaxSpawnSpacingFraction = 0.5f;
        private const float MinSpawnSpacing = 1.0f;

        // Thrust transforms carry SAS's constant gimbal corrections. spawnPos sits 15m
        // along that vector, so a tiny angle becomes a metre of lateral swing - and it is
        // coherent across frames, so the spine's smoothing does not touch it. Damping the
        // direction itself filters the wobble while still tracking a gravity turn.
        private const float SpawnForwardSmoothRate = 1.2f;

        // Speed window over which the spawn offset hands over from the thrust axis to the
        // surface-velocity direction - see the lasso note at the spawn site.
        private const float LassoVelocityMinSpeed = 15f;
        private const float LassoVelocityFullSpeed = 60f;

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

            if (!ModSettings.Enabled)
            {
                foreach (TrackedGroup g in trackedGroups) g.smokeMesh.HideAll();
                return;
            }

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
                    "[HairyBlob] vessel={0} alt={1:F0} speed={2:F0} sizeMult={3:F2} canSpawn={4} engines={5} groups={6}",
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
                            Vector3 rawForward = EngineClusterUtils.ComputeAverageForward(groupSamples);
                            Vector3 avgForward = g.smoothedForward.HasValue
                                ? Vector3.Slerp(g.smoothedForward.Value, rawForward, Mathf.Clamp01(TimeWarp.fixedDeltaTime * SpawnForwardSmoothRate))
                                : rawForward;
                            g.smoothedForward = avgForward;

                            // LASSO FIX. The offset direction is a 15m lever, so a
                            // 1-degree gimbal twitch throws the spawn point ~26cm sideways
                            // and damping alone cannot remove it.
                            //
                            // The column marks where the rocket HAS BEEN, and that path is
                            // smooth by construction. Surface velocity is its tangent, and
                            // gimballing barely moves it - it changes attitude, not
                            // instantaneous velocity. So once moving, steer by where the
                            // vessel came from, not by where the nozzle points. On the pad
                            // velocity is noise, and the two agree there anyway.
                            Vector3 offsetDir = avgForward;
                            Vector3 srfVel = vessel.srf_velocity;
                            float srfSpeed = srfVel.magnitude;
                            if (srfSpeed > LassoVelocityMinSpeed)
                            {
                                float blend = Mathf.Clamp01(
                                    (srfSpeed - LassoVelocityMinSpeed)
                                    / (LassoVelocityFullSpeed - LassoVelocityMinSpeed));
                                // exhaust trails the vessel, so "back along the path" is
                                // the direction the offset should go
                                offsetDir = Vector3.Slerp(avgForward, -srfVel / srfSpeed, blend);
                            }

                            Vector3 spawnPos = centroid + offsetDir * offsetDistance;

                            float sizeFactor = EngineClusterUtils.ClusterSizeFactor(g.partIds.Count);
                            float currentRadius = clusterStartSize * sizeFactor * sizeMultiplier;

                            g.smokeMesh.SetLiveTip(spawnPos, currentRadius);

                            float maxSpacing = Mathf.Max(currentRadius * MaxSpawnSpacingFraction, MinSpawnSpacing);
                            g.spawnTimer -= TimeWarp.fixedDeltaTime;
                            bool outranSpacing = !g.lastSpawnPos.HasValue
                                || Vector3.Distance(spawnPos, g.lastSpawnPos.Value) >= maxSpacing;

                            // MINIMUM spacing, against the maximum above. The timer path
                            // fires on time alone, so creeping at 1 m/s lays a puff every
                            // 8cm - dozens stacked inside one radius, which is what made
                            // the big overlapping shells. Measured against the live radius,
                            // since that is what decides what counts as too close.
                            float minSpacing = currentRadius * MinSpawnSpacingFraction;
                            bool movedEnough = !g.lastSpawnPos.HasValue
                                || Vector3.Distance(spawnPos, g.lastSpawnPos.Value) >= minSpacing;

                            if ((outranSpacing || g.spawnTimer <= 0f) && movedEnough)
                            {
                                Vector3 initialVelocity = Vector3.zero;
                                if (vessel.altitude < groundImpingementRange)
                                {
                                    // Independent random direction per puff. A smoothly
                                    // rotating one barely turns within the impingement
                                    // window, so every puff got pushed the same way and it
                                    // compounded into one coherent drift.
                                    Vector3 up = (spawnPos - vessel.mainBody.position).normalized;
                                    Vector3 lateral = Vector3.ProjectOnPlane(Random.onUnitSphere, up).normalized;
                                    float t = (float)(vessel.altitude / groundImpingementRange);
                                    initialVelocity = lateral * Mathf.Lerp(groundImpingementSpeed, 0f, t) * aggThrottle
                                        + avgForward * Mathf.Lerp(exhaustDownwardSpeed, 0f, t) * aggThrottle;
                                }
                                if (debugLogging)
                                {
                                    float actualDist = g.lastSpawnPos.HasValue ? Vector3.Distance(spawnPos, g.lastSpawnPos.Value) : -1f;
                                    Debug.Log(string.Format(
                                        "[HairyBlob] spawn: group={0} t={1:F3} dist={2:F2} maxSpacing={3:F2} speed={4:F0} pos={5} vel={6} |vel|={7:F2}",
                                        g.id, Time.time, actualDist, maxSpacing, currentSpeed, spawnPos, initialVelocity, initialVelocity.magnitude));
                                }
                                // Fill the whole distance covered since the last spawn.
                                // One puff per physics frame meant the faster the vessel
                                // flew the coarser the trail got.
                                int fill = 1;
                                if (g.lastSpawnPos.HasValue)
                                {
                                    float gap = Vector3.Distance(spawnPos, g.lastSpawnPos.Value);

                                    // Only bridge a gap the vessel plausibly just flew.
                                    // After a restart or a scene change lastSpawnPos can be
                                    // stale, and interpolating to it lays a line of puffs
                                    // across empty space.
                                    float plausible = Mathf.Max(currentSpeed * MaxBridgeSeconds, 25f);
                                    if (gap > plausible)
                                    {
                                        g.lastSpawnPos = null;
                                    }
                                    else
                                    {
                                        float wanted = Mathf.Max(currentSpeed * spawnInterval, maxSpacing);
                                        fill = Mathf.Clamp(Mathf.CeilToInt(gap / Mathf.Max(wanted, 0.5f)),
                                                           1, maxPuffsPerFrame);
                                    }
                                }

                                // A pause in emission starts a new burst. Same threshold
                                // as the stale-gap guard, so the two stay consistent.
                                if (g.lastSpawnTime > 0f
                                    && Time.time - g.lastSpawnTime > MaxBridgeSeconds)
                                {
                                    g.burnId++;
                                    g.lastSpawnPos = null;
                                }
                                g.lastSpawnTime = Time.time;

                                for (int f = 1; f <= fill; f++)
                                {
                                    Vector3 p = g.lastSpawnPos.HasValue
                                        ? Vector3.Lerp(g.lastSpawnPos.Value, spawnPos, f / (float)fill)
                                        : spawnPos;
                                    g.smokeMesh.AddPuff(p, initialVelocity, sizeMultiplier, g.burnId);
                                }

                                // Ground cloud: trace the exhaust down to whatever it is
                                // hitting and emit there. Raw thrust axis, not the
                                // lasso-damped one - this is about where the plume lands.
                                if (groundCloudEnabled)
                                {
                                    RaycastHit gHit;
                                    if (Physics.Raycast(centroid, avgForward, out gHit,
                                            groundCloudMaxReach,
                                            SmokeVolumeGroup.GetSceneryCollisionMaskPublic(),
                                            QueryTriggerInteraction.Ignore)
                                        && gHit.collider.GetComponentInParent<Part>() == null)
                                    {
                                        Vector3 gUp = (gHit.point - vessel.mainBody.position).normalized;
                                        for (int gp = 0; gp < groundCloudPuffsPerTick; gp++)
                                        {
                                            g.smokeMesh.AddGroundPuff(gHit.point, gUp,
                                                groundCloudOutwardSpeed * aggThrottle,
                                                groundCloudSizeScale * sizeMultiplier);
                                        }
                                    }
                                }

                                g.lastSpawnPos = spawnPos;
                                // Below ~50 m/s the distance test never fires, so this
                                // timer is the only trigger - at 1f that meant one burst
                                // per second, filled by a dozen puffs at once.
                                g.spawnTimer = spawnInterval;
                            }
                        }
                        else
                        {
                            g.smokeMesh.ClearLiveTip();
                        }
                    }
                    else
                    {
                        g.smokeMesh.ClearLiveTip();
                    }
                }
                else
                {
                    g.smokeMesh.ClearLiveTip();
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

            // snapshot count before the loop, trackedGroups grows inside it
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
                        growthTime,
                        lifeTime,
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
                            "[HairyBlob] new engine group id={0} with {1} parts",
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

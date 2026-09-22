using System.Collections.Generic;
using UnityEngine;

namespace VolumetricContrails
{
    // launch smoke as one raymarched volume per engine cluster
    public class LaunchSmokeController : VesselModule
    {
        public float clusterDistanceThreshold = 3.5f;

        // moved to SmokeTuning.SpawnOffset so it can be tuned in flight
        // Was 15km, which is below where the plume gets interesting - the bloom lives
        // between roughly 40km and the edge of the atmosphere.
        public float spawnMaxAltitude = 120000f;
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
        // moved to SmokeTuning.MaxPuffSize so it can be tuned in flight
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

        // These no longer delete the smoke, they thin it - see AlphaForAltitude.
        public float fadeStartAltitude = 45000f;
        public float fadeEndAltitude = 90000f;

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

        // --- retropropulsion detection ---
        // How closely the exhaust has to point along the flight path to count as a retro
        // burn. Ascending, the exhaust points BACKWARD, so this dot is near -1; firing
        // retrograde it is near +1. The two cases are as far apart as the measure allows,
        // which is why one dot product is enough to tell them apart.
        private const float RetroDotThreshold = 0.25f;
        // The umbrella is aerodynamic, so it is driven by dynamic pressure rather than by
        // speed alone - no air, no canopy, however fast the vessel is going.
        private const float RetroMinPressure = 1500f;    // Pa
        private const float RetroFullPressure = 25000f;

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

        // Pressure at which the nozzle starts to run visibly underexpanded, and where the
        // plume is fully bloomed. In kPa.
        // Raised from 10 kPa (~16km on Earth) to 40 (~7km), so the ramp covers far more of
        // the climb and the mist regime has room to exist between the pad and the bloom.
        private const float BloomHighPressure = 40f;
        private const float BloomLowPressure = 0.05f;

        // How far from a tight column toward a fully-open bell, 0..1.
        //
        // Ramped in LOG pressure, because pressure falls exponentially with altitude: a
        // linear ramp spends almost its entire range in the first few kilometres and the
        // plume would flare while the rocket was still low.
        private static float PlumeBloom(double staticPressureKPa)
        {
            if (!SmokeTuning.JellyfishEnabled) return 0f;
            float pressure = Mathf.Max((float)staticPressureKPa, 1e-4f);
            if (pressure >= BloomHighPressure) return 0f;
            if (pressure <= BloomLowPressure) return 1f;
            return Mathf.Clamp01(
                Mathf.Log(BloomHighPressure / pressure)
                / Mathf.Log(BloomHighPressure / BloomLowPressure));
        }

        // The plume grows with altitude. The old version of this SHRANK it, which is
        // backwards: thin air lets the exhaust expand, it does not compress it.
        private static float SizeMultiplierForBloom(float bloom)
        {
            // GEOMETRIC in bloom, not linear.
            //
            // A linear ramp reached 30x by a fifth of the way up, so the plume did not grow
            // into the bloom - it snapped into it, and the trail showed launch smoke and
            // jellyfish meeting at a seam with no mist between them. Exhaust expands with
            // the RATIO of pressures, so equal steps in the log-pressure ramp should be
            // equal MULTIPLES of width. That is a power, and it starts slow: at a fifth of
            // the ramp this gives 2.7x rather than 30x, and still reaches full width at the
            // top. The thinning follows it, so density eases in at the same rate.
            return Mathf.Pow(Mathf.Max(SmokeTuning.JellyfishSizeBoost, 1f), bloom);
        }

        // How much smoke a motor puts out for the thrust it is actually delivering.
        //
        // Shape taken from Avalanche's SolidSmoke emission curve (power: 1 -> 1,
        // 0.01 -> 0.2, 0 -> 0) so the two agree on where a motor goes quiet. The step up
        // to 0.2 at the bottom is deliberate: a motor still coughing out its last few
        // percent of thrust is still visibly smoking.
        private static float EmissionForThrust(float thrustFraction)
        {
            if (thrustFraction <= 0f) return 0f;
            if (thrustFraction < 0.01f) return Mathf.Lerp(0f, 0.2f, thrustFraction / 0.01f);
            return Mathf.Lerp(0.2f, 1f, (thrustFraction - 0.01f) / 0.99f);
        }

        // And for the air it is emitted into, relative to sea level. Again Avalanche's
        // curve (density: 1 -> 2.5, 0.05 -> 2, 0 -> 0, normalised): nearly flat through
        // the lower atmosphere, then falling to nothing in vacuum, where there is no air
        // left to hold the particles in a visible column.
        // Air density at the vessel as a fraction of the body's sea level, 0..1.
        private static float RelativeAirDensity(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            double asl = v.mainBody.atmDensityASL;
            if (asl <= 0.0) return 0f;
            return Mathf.Clamp01((float)(v.atmDensity / asl));
        }

        // Where a solid motor's exhaust comes to rest in the air, measured from the nozzle.
        //
        // This is the whole of the SRB emission model, and it replaces every hand-tuned
        // offset and velocity term the old path carried.
        //
        // Exhaust leaves the nozzle at ve = Isp * g0 relative to the ROCKET. Relative to
        // the AIR it therefore moves at u = vRocket + ve * exhaustDir, and it travels along
        // u until drag stops it. That single vector does what the lasso fix, the retro
        // detector and the offset smoothing were each approximating:
        //
        //  - At liftoff vRocket is ~0, so u is the full jet pointing out of the nozzle and
        //    the smoke is thrown well clear of it - down onto the pad.
        //  - Climbing, u = (ve - v) backwards along the path. As the rocket accelerates
        //    toward ve the jet slows in the air frame and the smoke is laid down closer
        //    and closer to the nozzle, until it is left hanging where the nozzle WAS.
        //    That is why a real trail traces the flight path instead of the rocket's
        //    attitude: turning the vehicle barely moves u once v is a sizeable part of ve.
        //
        // The stop distance scales with |u| (a slower jet travels less far) and grows as
        // the air thins (less to stop it with), which is the plume flaring at altitude.
        private const float MinJetLength = 3f;
        private const float MaxThinAirStretch = 8f;

        private static void SolidJet(Vessel v, List<EngineSample> samples, Vector3 exhaustDir,
                                     out Vector3 jetDir, out float jetLength, out float airSpeed)
        {
            float ispSum = 0f;
            for (int i = 0; i < samples.Count; i++) ispSum += samples[i].isp;
            float isp = samples.Count > 0 ? ispSum / samples.Count : 0f;
            // An SRB in KSP sits around 250-270s; if a part reports nothing usable, assume
            // a typical one rather than collapsing the jet to zero.
            float ve = isp > 1f ? isp * 9.80665f : 2500f;

            Vector3 u = (Vector3)v.srf_velocity + exhaustDir * ve;
            airSpeed = u.magnitude;

            // Only ever lay smoke BEHIND the nozzle. u points forward only if the rocket
            // outruns its own exhaust, which no solid motor does inside an atmosphere, but
            // following it there would put the smoke inside the vehicle.
            jetDir = (airSpeed > 1f && Vector3.Dot(u, exhaustDir) > 0f) ? u / airSpeed : exhaustDir;

            float speedFraction = Mathf.Clamp01(airSpeed / ve);
            float rel = RelativeAirDensity(v);
            float thinAir = rel > 1e-4f ? Mathf.Clamp(1f / Mathf.Sqrt(rel), 1f, MaxThinAirStretch) : MaxThinAirStretch;
            jetLength = Mathf.Max(SmokeTuning.SpawnOffset * speedFraction * thinAir, MinJetLength);
        }

        private static float EmissionForAtmosphere(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            float rel = RelativeAirDensity(v);
            if (rel < 0.05f) return Mathf.Lerp(0f, 0.8f, rel / 0.05f);
            return Mathf.Lerp(0.8f, 1f, (rel - 0.05f) / 0.95f);
        }

        private static bool HasEngine(Vessel v)
        {
            for (int i = 0; i < v.Parts.Count; i++)
            {
                if (v.Parts[i].FindModuleImplementing<ModuleEngines>() != null) return true;
            }
            return false;
        }

        private void FixedUpdate()
        {
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || !vessel.loaded) return;
            // Debris is skipped only when it has no engines. A jettisoned booster becomes
            // VesselType.Debris the moment it separates - so a blanket skip here is exactly
            // why a still-burning booster left no trail. Emission is already gated on a
            // running engine further down, so letting debris through costs one list walk
            // on the tumbling tanks and nothing else.
            if (vessel.vesselType == VesselType.Debris && !HasEngine(vessel)) return;

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

            bool hasAir = SmokeTuning.AirlessBodyPlume
                || (vessel.mainBody != null && vessel.mainBody.atmosphere);
            bool canSpawn = hasAir && vessel.altitude <= spawnMaxAltitude;

            bool logThisFrame = false;
            if (SmokeTuning.DebugLogging)
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
            float bloom = PlumeBloom(vessel.staticPressurekPa);
            // Bloom is NOT folded in here: sizeMultiplier scales the puff from birth, and
            // the nozzle end of a bloomed plume is still narrow. It travels as a separate
            // expansion factor that SizeForPuff eases in over the puff's growth.
            float sizeMultiplier = SizeMultiplierForSpeed(currentSpeed);
            float bloomExpansion = SizeMultiplierForBloom(bloom);
            // Thinning is NOT computed here any more. It used to scale the whole volume,
            // which meant the current altitude's bloom was applied to every puff in the
            // trail including the ones sitting on the pad - so past ~24km the entire
            // column faded out at once while detached stages, whose groups are no longer
            // updated, kept their density and stayed visible. Each puff now carries its
            // own factor from the air it was born into; see DensityForPuff.

            if (logThisFrame)
            {
                Debug.Log(string.Format(
                    "[PlumeTrails] vessel={0} alt={1:F0} speed={2:F0} sizeMult={3:F2} canSpawn={4} engines={5} groups={6}",
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

                            float thrustSum = 0f;
                            for (int si = 0; si < groupSamples.Count; si++) thrustSum += groupSamples[si].thrustFraction;
                            float groupThrust = groupSamples.Count > 0 ? thrustSum / groupSamples.Count : 0f;
                            // Fixed onto each puff at birth, so a trail laid down during the
                            // tail-off stays thin behind the vessel instead of thickening
                            // retroactively when the next stage lights.
                            float emitDensity = EmissionForThrust(groupThrust) * EmissionForAtmosphere(vessel);
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
                            Vector3 flightDir = srfSpeed > 0.01f ? srfVel / srfSpeed : Vector3.zero;

                            // How much this looks like a retro burn: exhaust pointing along
                            // the flight path rather than against it, with enough dynamic
                            // pressure for the air to actually stop it.
                            float retro = 0f;
                            if (SmokeTuning.UmbrellaEnabled && srfSpeed > LassoVelocityMinSpeed)
                            {
                                float alignment = Vector3.Dot(avgForward, flightDir);
                                float q = 0.5f * (float)vessel.atmDensity * srfSpeed * srfSpeed;
                                retro = Mathf.Clamp01((alignment - RetroDotThreshold) / (1f - RetroDotThreshold))
                                      * Mathf.Clamp01((q - RetroMinPressure) / (RetroFullPressure - RetroMinPressure));
                            }

                            if (srfSpeed > LassoVelocityMinSpeed)
                            {
                                float blend = Mathf.Clamp01(
                                    (srfSpeed - LassoVelocityMinSpeed)
                                    / (LassoVelocityFullSpeed - LassoVelocityMinSpeed));
                                // Ascending, exhaust trails the vessel, so "back along the
                                // path" is where the offset goes. Retro-burning that is
                                // exactly false - the exhaust goes AHEAD, into the
                                // airstream - so the lasso correction has to be backed off
                                // in proportion, or the plume is laid on the wrong side of
                                // the vehicle entirely.
                                blend *= 1f - retro;
                                offsetDir = Vector3.Slerp(avgForward, -srfVel / srfSpeed, blend);
                            }

                            float standoff = SmokeTuning.SpawnOffset + SmokeTuning.UmbrellaStandoff * retro;
                            float jetAirSpeed = 0f;
                            if (SmokeTuning.SrbOnly)
                            {
                                // Replaces the lasso blend and the retro standoff outright;
                                // see SolidJet.
                                SolidJet(vessel, groupSamples, avgForward,
                                         out offsetDir, out standoff, out jetAirSpeed);
                            }

                            // Which of the velocity terms is actually firing.
                            //
                            // "Looks like the rocket is burning when you turn hard" can come
                            // from the retro umbrella triggering on a steep manoeuvre, from
                            // the jellyfish bell spraying sideways off the nozzle axis, or
                            // from the offset still steering by attitude instead of by the
                            // flight path. All three look the same from outside and only
                            // these numbers tell them apart.
                            if (logThisFrame)
                            {
                                Debug.Log(string.Format(
                                    "[PlumeTrails] emit: group={0} align={1:F2} retro={2:F2} lassoBlend={3:F2} "
                                    + "bloom={4:F2} bellSpread={5:F0} standoff={6:F1}m spawnOff={7:F1}deg thrust={8:F2} emit={9:F2} jetAirSpeed={10:F0}",
                                    g.id,
                                    srfSpeed > 0.01f ? Vector3.Dot(avgForward, flightDir) : 0f,
                                    retro,
                                    srfSpeed > LassoVelocityMinSpeed
                                        ? Mathf.Clamp01((srfSpeed - LassoVelocityMinSpeed)
                                            / (LassoVelocityFullSpeed - LassoVelocityMinSpeed)) * (1f - retro)
                                        : 0f,
                                    bloom,
                                    SmokeTuning.SrbOnly ? 0f : SmokeTuning.JellyfishSpread * bloom,
                                    standoff,
                                    Vector3.Angle(offsetDir, -flightDir),
                                    groupThrust, emitDensity, jetAirSpeed));
                            }

                            Vector3 spawnPos = centroid + offsetDir * standoff;

                            float sizeFactor = EngineClusterUtils.ClusterSizeFactor(g.partIds.Count);
                            // Push the live sizes every tick; Initialize only ran once.
                            g.smokeMesh.SetSizes(clusterStartSize * sizeFactor,
                                                 SmokeTuning.MaxPuffSize * sizeFactor);
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

                                // Bloom. Underexpanded exhaust pushes outward from the
                                // nozzle axis, which is what opens the bell. Perpendicular
                                // to the EXHAUST here, where the umbrella below works off
                                // the flight path - they are different axes and both can
                                // apply at once during a high retro burn.
                                // SRB mode: nothing here. The puff is placed where the jet
                                // has already stopped, so it starts at rest in the air.
                                if (bloom > 0.001f && !SmokeTuning.SrbOnly)
                                {
                                    Vector3 bell = Vector3.ProjectOnPlane(
                                        Random.onUnitSphere, avgForward).normalized;
                                    initialVelocity += bell * SmokeTuning.JellyfishSpread * bloom;
                                }

                                // Umbrella. The puff stagnates against the freestream and
                                // turns outward, then gets swept back past the vehicle.
                                // Azimuth is random per puff, so the canopy fills in as a
                                // cone rather than as a single sheet.
                                if (retro > 0.001f)
                                {
                                    Vector3 radial = Vector3.ProjectOnPlane(
                                        Random.onUnitSphere, flightDir).normalized;

                                    // Three parts, and they happen in this order along the
                                    // plume rather than all at the nozzle:
                                    //
                                    // 1. A jet straight out of the nozzle, into the
                                    //    airstream. Without it the canopy floats detached
                                    //    with nothing connecting it to the engine.
                                    initialVelocity += avgForward * SmokeTuning.UmbrellaJet * retro;

                                    // 2. Turning outward as it stagnates - the canopy.
                                    initialVelocity += radial * SmokeTuning.UmbrellaSpread * retro;

                                    // 3. Swept back. This one has to be a FRACTION OF THE
                                    //    VESSEL'S OWN SPEED, not a fixed number of m/s:
                                    //    once the air has stopped the exhaust, that gas is
                                    //    at rest in the AIR, so in the vehicle's frame it
                                    //    streams past at whatever the vehicle is doing.
                                    //    A constant 25 m/s against a vessel falling at 400
                                    //    leaves the canopy hanging where it was born while
                                    //    the rocket drops away from it - which is exactly
                                    //    the detached blob with a gap under it.
                                    initialVelocity -= flightDir * srfSpeed
                                                     * SmokeTuning.UmbrellaBackflowFraction * retro;
                                }

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
                                if (SmokeTuning.DebugLogging)
                                {
                                    float actualDist = g.lastSpawnPos.HasValue ? Vector3.Distance(spawnPos, g.lastSpawnPos.Value) : -1f;
                                    Debug.Log(string.Format(
                                        "[PlumeTrails] spawn: group={0} t={1:F3} dist={2:F2} maxSpacing={3:F2} speed={4:F0} pos={5} vel={6} |vel|={7:F2}",
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
                                    g.smokeMesh.AddPuff(p, initialVelocity, sizeMultiplier, g.burnId, bloomExpansion, emitDensity);
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
                        SmokeTuning.MaxPuffSize * sizeFactor,
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

                    if (SmokeTuning.DebugLogging)
                    {
                        Debug.Log(string.Format(
                            "[PlumeTrails] new engine group id={0} with {1} parts",
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

        // Smoke outlives the thing that made it.
        //
        // Destroying the volumes here killed the trail the instant its vessel went away -
        // most visibly when a booster explodes, taking a perfectly good kilometres-long
        // trail with it. The puffs already expire on their own timer, so handing the
        // volume over and letting it finish is both simpler and correct: it fades exactly
        // as it would have.
        private void ReleaseOrphanedTrails()
        {
            foreach (TrackedGroup g in trackedGroups)
            {
                if (g.smokeMesh != null) g.smokeMesh.Orphan();
            }
            trackedGroups.Clear();
        }

        private void OnDestroy()
        {
            ReleaseOrphanedTrails();
        }
    }
}

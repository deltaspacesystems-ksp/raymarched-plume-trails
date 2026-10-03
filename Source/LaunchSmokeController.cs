using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
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
        // Closest a new puff may sit to the last one, as a fraction of its radius. Below
        // this it lies entirely inside its predecessor and adds nothing but overlap.
        private static float MinSpawnSpacingFraction { get { return 0.35f * SmokeTuning.SpawnSpacingScale; } }
        // hard cap so a lag spike or a very fast vessel cannot emit hundreds in one frame
        public int maxPuffsPerFrame = 12;
        // how far back in time a gap may reach before it stops being treated as continuous
        private const float MaxBridgeSeconds = 0.5f;

        // Real launch smoke hangs around for minutes. Decimation thins old puffs as they
        // grow, so a long life costs much less than linearly.
        public float lifeTime = 150f;
        public int maxPuffsPerGroup = 8000;

        // Wider than the old 1.8 - a Shuttle-style SRB plume is already a proper billow
        // within the first metre off the nozzle, not a thin needle that thickens later.
        // KSA-style (30 IX): the smoke leaves the nozzle NARROW (about the nozzle's own width) and
        // widens slowly into a teardrop, instead of starting as a blunt billow behind the rocket.
        // Box volume scales cubically with radius, so this is a big lever on shader cost.
        // moved to SmokeTuning.MaxPuffSize so it can be tuned in flight
        // 1-(1-t)^n. The exponent is the growth rate at t=0, so above 1 grows fast right
        // after spawn (closing gaps) then eases off. Too high and puffs reach full size in
        // half their life, which reads as a fixed-width column rather than billowing.
        // Time to reach maxSize, independent of lifeTime.
        // Raised back up from 5 (27 IX): both screenshots this session showed the same
        // symptom at two different points on the trail - a sudden swell right below the
        // vehicle AND a sudden knee further down - and it is the same cause both times.
        // r^2 = r0^2 + K*t^2/(t+t0) is smooth but STEEP: at SmokeTuning.GrowthTime=5 it was already
        // at 60% of max width by 2 seconds of age, which at any real ascent speed is a
        // short stretch of trail, so the "fast growth, then barely widening" shape reads
        // as an abrupt jump rather than a taper. Stretched to 14s so the same curve
        // spreads over roughly 3x the distance instead of changing its shape.

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
            public float smoothFactor;      // nozzle-size factor, smoothed
            public float smoothFlame;       // flame length reported by Waterfall, smoothed (m)
            public float smoothFlameW;      // flame width reported by Waterfall, smoothed (m)
            public float lastLoggedFactor;
            // Bumped whenever emission resumes after a pause, so the renderer can break
            // the capsule chain between bursts without guessing from geometry.
            public int burnId;
            public float lastSpawnTime;
            public Vector3? lastSpawnPos;
            public Vector3? smoothedForward;
            // The jet's offset from the nozzle, low-passed. Reset whenever emission stops so
            // the next burn starts from where the jet actually is, not from a stale vector.
            public Vector3? smoothedJetOffset;

            // --- post-burnout tail: see TailTick ---
            // The nozzle point at the last tick a real engine sample existed, in VESSEL
            // space, so it keeps tracking the airframe as it coasts after flameout instead
            // of being left behind at a world-space point.
            public Vector3? tailLocalPos;
            public float tailLastDensity;
            public float tailLastSizeMul;
            public float tailTimer;
        }

        // spawn trigger: distance since last puff, timer as a fallback
        private static float MaxSpawnSpacingFraction { get { return 0.5f * SmokeTuning.SpawnSpacingScale; } }
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
        // Tracks the landing-cloud state across ticks so a transition (not just a part
        // count change) can trigger RecomputeGroups - a liquid engine's TrackedGroup would
        // otherwise not exist yet the moment landing starts, since structural grouping is
        // normally keyed off part count alone.
        private bool wasLanding;

        // Descending, low, throttled up - independent of engine type or SrbOnly. See
        // SmokeTuning's landing-cloud block for why this exists as a bypass rather than
        // touching the ascent trail's own SRB-only scoping.
        //
        // Hysteresis against wasLanding (28 IX fix). A flat threshold flickered hard right
        // at liftoff: low altitude and a vertical speed sitting near zero (or briefly
        // negative from settling/clamp release) crossed the boundary every physics tick,
        // and each crossing calls RecomputeGroups - which tears down and rebuilds the
        // engine group structure, visible as the trail cycling through several broken
        // intermediate looks in a loop. Entering landing needs the configured thresholds;
        // staying in it only needs roughly a third of the margin, so noise near the
        // boundary cannot flip the state every frame.
        private static bool IsLanding(Vessel v, bool wasLanding)
        {
            if (!SmokeTuning.LandingCloudEnabled) return false;
            float altitudeLimit = wasLanding
                ? SmokeTuning.LandingCloudAltitude * 1.5f
                : SmokeTuning.LandingCloudAltitude;
            if (v.altitude > altitudeLimit) return false;
            float speedLimit = wasLanding
                ? SmokeTuning.LandingCloudVerticalSpeed * 0.35f
                : SmokeTuning.LandingCloudVerticalSpeed;
            return v.verticalSpeed < speedLimit;
        }

        private float SizeMultiplierForSpeed(float speed)
        {
            if (speed <= SmokeTuning.SpeedBillowMin) return 1f;
            if (speed >= SmokeTuning.SpeedThinTrailMax) return SmokeTuning.ThinTrailSizeMult;
            float t = (speed - SmokeTuning.SpeedBillowMin) / (SmokeTuning.SpeedThinTrailMax - SmokeTuning.SpeedBillowMin);
            return Mathf.Lerp(1f, SmokeTuning.ThinTrailSizeMult, t);
        }

        // Physically grounded, replacing the old hand-tuned log-ramp-then-power-law.
        //
        // An underexpanded jet's characteristic plume radius scales with the SQUARE ROOT
        // of the pressure ratio between the exhaust and the surrounding air - the same
        // relation used for the Mach-disk distance of a free supersonic jet (Ashkenas &
        // Sherman, 1966; it is why a fixed-geometry bell nozzle only reaches its design
        // efficiency at one altitude and visibly over/under-expands everywhere else). The
        // exit condition is fixed by the motor, so only the ambient pressure changes as
        // the vehicle climbs - the whole altitude-driven widening reduces to one real
        // number: how much thinner the air has gotten since the pad, square-rooted.
        //
        // GetPressure(0.0) is the body's OWN sea-level pressure, not a hardcoded Earth
        // value - this is what makes the curve correct on any real-scale body without a
        // planet-specific constant.
        //
        // JellyfishSizeBoost is now a pure ARTISTIC dial on the excess over 1x, not a
        // target the curve is stretched to reach: 1 reproduces the physical relation
        // exactly, higher exaggerates it if the honest version still reads too subtle
        // next to a reference photo.
        private static float PlumeBloomWidth(Vessel v)
        {
            if (!SmokeTuning.JellyfishEnabled) return 1f;
            if (v.mainBody == null || !v.mainBody.atmosphere) return 1f;
            float pSeaLevel = (float)v.mainBody.GetPressure(0.0);
            if (pSeaLevel <= 0.0001f) return 1f;
            float pAmbient = Mathf.Max((float)v.staticPressurekPa, SmokeTuning.PlumeBloomPressureFloor);
            float physical = Mathf.Sqrt(pSeaLevel / pAmbient);
            return 1f + (physical - 1f) * Mathf.Max(SmokeTuning.JellyfishSizeBoost, 0f);
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

            Vector3 vel = v.srf_velocity;
            float speed = vel.magnitude;

            // How far the jet carries before the air stops it depends on how fast it is
            // moving through the AIR, which is the exhaust velocity plus the rocket's own.
            Vector3 u = vel + exhaustDir * ve;
            airSpeed = u.magnitude;

            // DIRECTION is a different question, and the one that matters in a turn.
            //
            // In the rocket's frame the exhaust leaves straight down the nozzle axis and
            // the relative wind then bends it round to run along the wind, which blows
            // against the velocity. How much of that bending happens near the nozzle is set
            // by how much of the wind CROSSES the axis: flying straight, the wind runs down
            // the axis and nothing bends; in a hard turn the nozzle is pointing well off the
            // flight path and the smoke is swept sideways almost immediately.
            //
            // The first version of this scaled the rocket's lateral velocity into the exit
            // velocity. That pushed the smoke AWAY from the flight path - the exhaust is
            // faster than the rocket, so the sum stays close to the axis and the small
            // lateral term only tilts it the wrong way. Bending toward the wind is the
            // physically right sign, and it is the one that makes the plume leave through
            // the flame and then go sideways.
            Vector3 windDir = speed > 1f ? -vel / speed : exhaustDir;
            Vector3 cross = vel - exhaustDir * Vector3.Dot(vel, exhaustDir);
            float crossSpeed = cross.magnitude;
            // Saturating in crossSpeed: a fraction of the way from the axis to the wind.
            float bend = Mathf.Clamp01(SmokeTuning.SrbCrossflowBend * crossSpeed / (crossSpeed + 0.25f * ve));
            // Slerp is undefined for opposite vectors. That only happens flying tail-first,
            // where there is no crossflow to speak of anyway.
            jetDir = Vector3.Dot(exhaustDir, windDir) > -0.9f
                ? Vector3.Slerp(exhaustDir, windDir, bend)
                : exhaustDir;

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
            return EngineClusterUtils.HasEngine(v);
        }

        // A few seconds of thinning, decaying smoke after a solid motor goes quiet,
        // before the trail is frozen and left to drift - see StopEmission.
        //
        // A real SRB does not stop smoking the instant thrust reads zero: the nozzle and
        // the last unburnt grain keep shedding a little exhaust as they cool, tapering off
        // over a few seconds rather than cutting off in one frame. Without this the trail
        // ended abruptly right where the last puff under real thrust had been laid, which
        // reads as the smoke being switched off rather than the motor running out.
        //
        // Puffs are placed at the CACHED nozzle position (see where tailLocalPos is set),
        // carried in the vessel's own frame so it keeps riding along as the stage coasts,
        // with no jet velocity of its own - by now there is nothing left to eject it.
        private void SrbTail(TrackedGroup g)
        {
            if (!SmokeTuning.SrbOnly || SmokeTuning.SrbTailTime <= 0.01f || !g.tailLocalPos.HasValue)
            {
                StopEmission(g);
                return;
            }

            // First silent tick: arm the taper instead of spending it immediately, so a
            // single dropped frame of engine samples (there have been none observed, but
            // nothing guarantees KSP never produces one) does not truncate the tail.
            if (g.tailTimer <= 0f)
            {
                g.tailTimer = SmokeTuning.SrbTailTime;

                // Freeze the live tip and its root NOW, not when the tail finishes.
                //
                // Both are still sitting at whatever position they last had under real
                // thrust. Leaving them live until the tail's StopEmission would commit them
                // AFTER every tail puff instead of before - CommitLiveTip stamps whatever it
                // freezes with the newest spawnIndex, so the chain would jump from the
                // tail's newest puff back to this stale point and then out along the old
                // root, a visible kink at the exact moment the motor died. Committing it
                // here puts it in its correct place in time: right before the tail begins.
                g.smokeMesh.CommitLiveTip();
            }

            g.tailTimer -= TimeWarp.fixedDeltaTime;
            if (g.tailTimer <= 0f)
            {
                g.tailLocalPos = null;
                StopEmission(g);
                return;
            }

            g.spawnTimer -= TimeWarp.fixedDeltaTime;
            if (g.spawnTimer > 0f) return;
            g.spawnTimer = SmokeTuning.SpawnInterval;

            // Smoothstep, not linear: a linear taper still ends at a visible non-zero puff
            // on its last tick, which pops out of existence the frame after. Smoothstep
            // eases into zero, so the very last puffs are already too faint to notice going.
            float frac = Mathf.Clamp01(g.tailTimer / SmokeTuning.SrbTailTime);
            float eased = frac * frac * (3f - 2f * frac);
            float density = g.tailLastDensity * eased;
            if (density <= 0.005f)
            {
                g.tailLocalPos = null;
                StopEmission(g);
                return;
            }

            Vector3 worldPos = vessel.transform.TransformPoint(g.tailLocalPos.Value);
            if (g.lastSpawnTime > 0f && Time.time - g.lastSpawnTime > MaxBridgeSeconds)
            {
                g.burnId++;
                g.lastSpawnPos = null;
            }
            g.lastSpawnTime = Time.time;
            g.smokeMesh.AddPuff(worldPos, Vector3.zero, g.tailLastSizeMul, g.burnId, 1f, density);
            g.lastSpawnPos = worldPos;
        }

        // Emission has stopped for this group. Freeze what the plume was doing into real
        // puffs so it ages and spreads on its own, instead of deleting its live end - see
        // CommitLiveTip. A no-op after the first call, so it is safe to run every tick.
        private static void StopEmission(TrackedGroup g)
        {
            g.smokeMesh.CommitLiveTip();
            g.smoothedJetOffset = null;
        }

        private void FixedUpdate()
        {
            var perfSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                FixedUpdateBody();
            }
            finally
            {
                LogTickPerf(perfSw);
            }
        }

        private float perfLogTimer;

        // Wall-clock CPU cost of this vessel's whole tick, independent of the "laguje mimo
        // ze rakieta jest 4km od dymu" report - that could be OUR cost (this) or the
        // render side (see HalfResSmokeRenderer's own perf log). Unconditional, throttled
        // to 1s like the other always-on diagnostics, so it is there on the very first
        // repro instead of needing DebugLogging pre-armed and the bug reproduced twice.
        private void LogTickPerf(System.Diagnostics.Stopwatch sw)
        {
            perfLogTimer -= Time.unscaledDeltaTime;
            if (perfLogTimer > 0f) return;
            perfLogTimer = 1f;
            double ms = sw.Elapsed.TotalMilliseconds;
            if (ms < 0.5) return; // not worth a line every second while idle/cheap
            Debug.Log(string.Format(
                "[PlumeTrails] tick: vessel={0} cpuMs={1:F2} groups={2} | puff ticks: ms={3:F2} steps={4} terrainQueries={5} raycasts={6}",
                vessel != null ? vessel.vesselName : "(none)", ms, trackedGroups.Count,
                SmokeVolumeGroup.PerfTickMs, SmokeVolumeGroup.PerfSteps, SmokeVolumeGroup.PerfPqs, SmokeVolumeGroup.PerfRays));
            SmokeVolumeGroup.PerfTickMs = 0; SmokeVolumeGroup.PerfSteps = 0; SmokeVolumeGroup.PerfPqs = 0; SmokeVolumeGroup.PerfRays = 0;
        }

        private void FixedUpdateBody()
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

            bool isLanding = IsLanding(vessel, wasLanding);

            if (vessel.Parts.Count != lastPartCount || isLanding != wasLanding)
            {
                RecomputeGroups(isLanding);
                lastPartCount = vessel.Parts.Count;
                wasLanding = isLanding;
            }

            bool hasAir = SmokeTuning.AirlessBodyPlume
                || (vessel.mainBody != null && vessel.mainBody.atmosphere);
            // Landing altitude (default 150m) is always well under spawnMaxAltitude, so
            // this ceiling never actually excludes it - hasAir still applies. A landing
            // dust cloud on an airless body is real and worth doing, but it needs its own
            // look (dust, not white smoke), which is exactly the "configs for other
            // engines/materials" work being deferred, not folded in blind here.
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
                ? EngineClusterUtils.GatherEngineSamples(vessel, isLanding)
                : new List<EngineSample>();

            float currentSpeed = (float)vessel.srfSpeed;
            float bloomWidth = PlumeBloomWidth(vessel);
            // Bloom is NOT folded in here: sizeMultiplier scales the puff from birth, and
            // the nozzle end of a bloomed plume is still narrow. It travels as a separate
            // expansion factor that SizeForPuff eases in over the puff's growth.
            float sizeMultiplier = SizeMultiplierForSpeed(currentSpeed);
            float bloomExpansion = bloomWidth;
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
                            float lookSize, lookDensity, steamShare, exhaustWater, visDilution;
                            EngineClusterUtils.GroupLook(groupSamples, out lookSize, out lookDensity, out steamShare, out exhaustWater, out visDilution);
                            float emitDensity = EmissionForThrust(groupThrust) * EmissionForAtmosphere(vessel) * lookDensity;
                            // NOT boosted here any more for isLanding - LandingCloudDensityBoost
                            // now sets the GROUND-SPLASH puffs' density (see the raycast block
                            // below), not the live tip's. Boosting it here is what grew a dense
                            // sphere in place instead of a cloud spreading along the ground.
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
                                Vector3 rawDir;
                                float rawLen;
                                SolidJet(vessel, groupSamples, avgForward,
                                         out rawDir, out rawLen, out jetAirSpeed);

                                // Low-pass the OFFSET VECTOR, not direction and length
                                // apart. Steering an attitude change straight into the spawn
                                // point makes the newest smoke whip round the nozzle while
                                // the older smoke stays where it was, and the seam between
                                // them reads as a kink. Filtering the vector handles
                                // direction and length together, has no singularity when
                                // the jet swings a long way, and - with the time constant in
                                // seconds - behaves the same at any physics rate.
                                Vector3 target = rawDir * rawLen;
                                float alpha = 1f - Mathf.Exp(-TimeWarp.fixedDeltaTime
                                                              / Mathf.Max(SmokeTuning.SrbTurnSmoothing, 0.01f));
                                g.smoothedJetOffset = g.smoothedJetOffset.HasValue
                                    ? Vector3.Lerp(g.smoothedJetOffset.Value, target, alpha)
                                    : target;
                                Vector3 smoothed = g.smoothedJetOffset.Value;
                                standoff = Mathf.Max(smoothed.magnitude, MinJetLength);
                                offsetDir = smoothed.sqrMagnitude > 1e-6f ? smoothed.normalized : rawDir;
                            }

                            // WATERFALL: the smoke continues from where the flame ends. The flame's own length (read from the
                            // engine's Waterfall effect) sets the earliest point the smoke may start, and what the heat haze and
                            // the flame light wrap around. Smoothed, because the flame follows the throttle.
                            float flameLen = 0f, flameWid = 0f;
                            if (SmokeTuning.WaterfallIntegration) EngineClusterUtils.GroupFlame(groupSamples, out flameLen, out flameWid);
                            g.smoothFlame += (flameLen - g.smoothFlame) * (1f - Mathf.Exp(-TimeWarp.fixedDeltaTime / 0.5f));
                            if (g.smoothFlame < 0.05f) g.smoothFlame = 0f;
                            g.smoothFlameW += (flameWid - g.smoothFlameW) * (1f - Mathf.Exp(-TimeWarp.fixedDeltaTime / 0.5f));
                            if (g.smoothFlameW < 0.05f) g.smoothFlameW = 0f;
                            float flameHandoff = g.smoothFlame * SmokeTuning.WaterfallHandoff;
                            if (flameHandoff > standoff) standoff = flameHandoff;

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
                                    + "bloom={4:F2} bellSpread={5:F0} standoff={6:F1}m spawnOff={7:F1}deg thrust={8:F2} emit={9:F2} jetAirSpeed={10:F0} bend={11:F1}deg",
                                    g.id,
                                    srfSpeed > 0.01f ? Vector3.Dot(avgForward, flightDir) : 0f,
                                    retro,
                                    srfSpeed > LassoVelocityMinSpeed
                                        ? Mathf.Clamp01((srfSpeed - LassoVelocityMinSpeed)
                                            / (LassoVelocityFullSpeed - LassoVelocityMinSpeed)) * (1f - retro)
                                        : 0f,
                                    bloomWidth,
                                    SmokeTuning.SrbOnly ? 0f : SmokeTuning.JellyfishSpread * bloomWidth,
                                    standoff,
                                    Vector3.Angle(offsetDir, -flightDir),
                                    groupThrust, emitDensity, jetAirSpeed,
                                    Vector3.Angle(offsetDir, avgForward)));
                            }

                            Vector3 spawnPos = centroid + offsetDir * standoff;

                            // A liquid engine is only let in during a landing so it can throw the
                            // ground splash. It must NOT also run the SRB-style trail (live tip,
                            // root, puff chain) - that is the white column that reappeared on an
                            // LF engine right after the splash, which is not what a landing burn
                            // does. Only meaningful under SrbOnly; with it off every engine
                            // trails anyway.
                            bool splashOnly = false;
                            if (isLanding && SmokeTuning.SrbOnly && !SmokeTuning.LiquidPlumes)
                            {
                                splashOnly = true;
                                for (int si = 0; si < groupSamples.Count; si++)
                                {
                                    if (groupSamples[si].isSolid) { splashOnly = false; break; }
                                }
                            }

                            // The nozzle-size factor follows the engines that are firing right now, so it jumps whenever
                            // one stops, throttles below the cut-off or a booster burns out. It used to rescale the group's
                            // sizes live, which resized the WHOLE existing trail at once (a twitch, and the smoke got
                            // thinner). Now each puff takes the factor at its own birth (see AddPuff below), and the
                            // factor itself is eased so the tip does not pop either.
                            float targetFactor = EngineClusterUtils.NozzleSizeFactor(groupSamples, g.partIds.Count)
                                * lookSize;
                            if (g.smoothFactor <= 0f) { g.smoothFactor = targetFactor; g.lastLoggedFactor = targetFactor; }
                            if (Mathf.Abs(targetFactor - g.lastLoggedFactor) > 0.05f * Mathf.Max(g.lastLoggedFactor, 0.01f))
                            {
                                Debug.Log(string.Format("[PlumeTrails] nozzle size factor {0:F2} -> {1:F2} (firing engines={2}, group parts={3}, throttle={4:F2})",
                                    g.lastLoggedFactor, targetFactor, groupSamples.Count, g.partIds.Count, aggThrottle));
                                g.lastLoggedFactor = targetFactor;
                            }
                            g.smoothFactor += (targetFactor - g.smoothFactor) * (1f - Mathf.Exp(-TimeWarp.fixedDeltaTime / Mathf.Max(SmokeTuning.NozzleFactorEase, 0.05f)));
                            float sizeFactor = g.smoothFactor;
                            // Landing cloud no longer touches the live tip's own size - see
                            // the ground-splash raycast block below for where the "flame
                            // diverter" look actually comes from now. Inflating the tip in
                            // place looked right for a vessel still descending fast (there
                            // was somewhere for the size to go, along the trail behind it),
                            // but for the last, nearly-stationary metres before touchdown -
                            // exactly when this kicks in - it just grew a sphere sitting on
                            // one point, reported as a mushroom/balloon.
                            // Push the live sizes every tick; Initialize only ran once.
                            g.smokeMesh.SetSizes(SmokeTuning.StartSize, SmokeTuning.MaxPuffSize);
                            g.smokeMesh.SetGrowth(SmokeTuning.GrowthSharpness, SmokeTuning.GrowthTime);
                            // The smoke starts as wide as the END of the flame it continues (Waterfall), instead of at the
                            // physical release radius: a thin pencil after a fat flame read as a stretched, shrinking plume.
                            float startScale = lookSize;
                            if (SmokeTuning.PhysicsMode && SmokeTuning.SrbOnly && SmokeTuning.WaterfallMatchStart
                                && g.smoothFlame > 0.5f && g.smoothFlameW > 0.2f && g.smokeMesh.LastPhysR0Raw > 0.05f)
                            {
                                float wantR = g.smoothFlameW * 0.5f * SmokeTuning.WaterfallStartWidth;
                                startScale = Mathf.Clamp(wantR / (g.smokeMesh.LastPhysR0Raw * Mathf.Max(SmokeTuning.PhysRadiusScale, 0.05f)), 0.3f, 3.5f);
                            }
                            g.smokeMesh.SetSteam(steamShare, exhaustWater, visDilution, startScale);
                            if (SmokeTuning.HeatHaze && !splashOnly)
                            {
                                // the hot region is a cone from the nozzle: as wide as the nozzle at the exit, widening as it
                                // mixes; fades out in thin air (no air, no refraction) and with throttle
                                float hazeD = Mathf.Max(EngineClusterUtils.NozzleSizeFactor(groupSamples, g.partIds.Count) * SmokeTuning.NozzleReferenceDiameter, 0.3f);
                                float hazeAir = Mathf.Sqrt(Mathf.Clamp01((float)vessel.atmDensity));
                                float hazeLen = Mathf.Clamp(hazeD * SmokeTuning.HeatHazeLengthDiameters, 4f, 150f);
                                float hazeR0 = hazeD * 0.5f, hazeR1 = hazeD * 1.3f;
                                if (g.smoothFlame > 0.5f)
                                {
                                    // wrap the flame that is actually on screen
                                    hazeLen = Mathf.Max(g.smoothFlame * SmokeTuning.WaterfallHazeLength, hazeD * 3f);
                                    hazeR0 = Mathf.Max(flameWid * 0.3f, hazeD * 0.4f);
                                    hazeR1 = Mathf.Max(hazeR0 * 1.6f, flameWid * 0.6f);
                                }
                                HeatHazeRegistry.Report(unchecked((int)vessel.persistentId * 31 + g.id), centroid, avgForward, hazeLen,
                                    hazeR0, hazeR1, aggThrottle * hazeAir * SmokeTuning.HeatHazeStrength);
                            }
                            // the colour of what is burning, for the smoke's glow and for a real light in the scene
                            Color flameRgb; float lightMul;
                            EngineClusterUtils.GroupFlameColor(groupSamples, out flameRgb, out lightMul);
                            g.smokeMesh.SetFlameColor(flameRgb);
                            {
                                float lightRange = Mathf.Clamp(Mathf.Max(SmokeTuning.FlameLightRange * Mathf.Max(sizeFactor, 0.3f) * 2.5f, g.smoothFlame * 6f), 30f, 500f);
                                float airDim = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01((float)vessel.atmDensity * 2f));
                                float lightPower = 1.4f * Mathf.Sqrt(Mathf.Clamp01(aggThrottle) * Mathf.Max(sizeFactor, 0.2f)) * lightMul * airDim;
                                Vector3 lightPos = centroid + avgForward * Mathf.Max(g.smoothFlame * 0.4f, 2f);
                                EngineLights.Report(unchecked((int)vessel.persistentId * 31 + g.id), lightPos, flameRgb, lightRange, lightPower);
                            }
                            g.smokeMesh.SetFlameLight(centroid, aggThrottle * sizeFactor, Mathf.Max(SmokeTuning.FlameLightRange * Mathf.Max(sizeFactor, 0.3f), g.smoothFlame * 0.7f));
                            float currentRadius = SmokeTuning.StartSize * sizeFactor * sizeMultiplier;
                            // REAL PHYSICS: the jet parameters of this emission, and the tip takes the radius the
                            // parcels are actually being released with, so the chain starts continuous.
                            bool physMode = SmokeTuning.PhysicsMode && SmokeTuning.SrbOnly;
                            float physVe = 2500f, physMdot0 = 0f;
                            Vector3 physU0 = Vector3.zero;
                            float physD = 1f, physXs = 3f;
                            if (physMode)
                            {
                                float ispS = 0f, thrustN = 0f;
                                for (int si = 0; si < groupSamples.Count; si++) { ispS += groupSamples[si].isp; thrustN += groupSamples[si].thrustKN * 1000f; }
                                float ispAvg = groupSamples.Count > 0 ? ispS / groupSamples.Count : 0f;
                                physVe = ispAvg > 1f ? ispAvg * 9.80665f : 2500f;
                                physU0 = vessel.srf_velocity + avgForward * physVe;
                                physMdot0 = thrustN / physVe;
                                physD = Mathf.Max(sizeFactor * SmokeTuning.NozzleReferenceDiameter, 0.3f);
                                physXs = Mathf.Max(standoff, 3f);
                                if (g.smokeMesh.LastPhysR0 > 0.05f) currentRadius = g.smokeMesh.LastPhysR0;
                            }

                            if (splashOnly)
                            {
                                // end any trail this group had going, so it freezes cleanly
                                // instead of the stale-tip watchdog tripping half a second on
                                if (g.smokeMesh.HasLiveTip) g.smokeMesh.CommitLiveTip();
                            }
                            else
                            g.smokeMesh.SetLiveTip(spawnPos, currentRadius);

                            // Cache where this is, every tick a real engine fired - not
                            // just on ticks a puff was actually placed - so the tail below
                            // starts from the truest last position rather than possibly a
                            // SmokeTuning.SpawnInterval's worth behind it.
                            g.tailLocalPos = vessel.transform.InverseTransformPoint(spawnPos);
                            g.tailLastDensity = emitDensity;
                            g.tailLastSizeMul = sizeMultiplier;
                            // The root runs from the nozzle itself through the flame to the
                            // tip. A radius of 0 (or SRB mode off) leaves the tip alone.
                            if (!splashOnly)
                            g.smokeMesh.SetLiveRoot(centroid, avgForward,
                                SmokeTuning.SrbOnly && SmokeTuning.SrbRootEnabled
                                    ? SmokeTuning.StartSize * sizeFactor * SmokeTuning.SrbRootRadius : 0f,
                                emitDensity);

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
                                // Legacy path (liquid-engine jellyfish), off by default under
                                // SrbOnly. bloomWidth is now a width MULTIPLIER (>=1), not a
                                // 0..1 fraction, so approximate one here rather than carry two
                                // different bloom representations through the rest of the file.
                                float legacyBloomFraction = Mathf.Clamp01(bloomWidth - 1f);
                                if (legacyBloomFraction > 0.001f && !SmokeTuning.SrbOnly)
                                {
                                    Vector3 bell = Vector3.ProjectOnPlane(
                                        Random.onUnitSphere, avgForward).normalized;
                                    initialVelocity += bell * SmokeTuning.JellyfishSpread * legacyBloomFraction;
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

                                if (SmokeTuning.GroundImpingementEnabled && vessel.altitude < groundImpingementRange)
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
                                        float wanted = Mathf.Max(currentSpeed * SmokeTuning.SpawnInterval, maxSpacing);
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
                                float physSliceDt = g.lastSpawnTime > 0f
                                    ? Mathf.Clamp(Time.time - g.lastSpawnTime, 0.005f, 0.25f) / Mathf.Max(fill, 1)
                                    : SmokeTuning.SpawnInterval;
                                g.lastSpawnTime = Time.time;

                                for (int f = 1; f <= fill; f++)
                                {
                                    Vector3 p = g.lastSpawnPos.HasValue
                                        ? Vector3.Lerp(g.lastSpawnPos.Value, spawnPos, f / (float)fill)
                                        : spawnPos;
                                    if (!splashOnly)
                                    {
                                        if (physMode)
                                            g.smokeMesh.AddPhysicalJetPuff(p, physU0, physVe, physXs, physMdot0, physSliceDt, g.burnId, emitDensity);
                                        else
                                            g.smokeMesh.AddPuff(p, initialVelocity, sizeMultiplier * sizeFactor, g.burnId, bloomExpansion, emitDensity);
                                    }
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
                                                groundCloudSizeScale * sizeMultiplier * sizeFactor);
                                        }
                                    }
                                }

                                // Landing cloud, second pass: reuses the same
                                // raycast-then-radial-puffs primitive as the pad-deflector
                                // ground cloud above (that mechanic itself was never the
                                // problem; what was rejected earlier was gating the LANDING
                                // feature on that system's own flaky pad-survey trigger).
                                // isLanding is already a clean altitude+vspeed detector, so
                                // this fires independently of groundCloudEnabled.
                                //
                                // Same reasoning as SolidJet's raw thrust axis: this is
                                // about where the plume physically lands, not where the
                                // lasso-damped trail offset points.
                                if (isLanding)
                                {
                                    RaycastHit lHit;
                                    // Ground effect only happens CLOSE to the ground, in units
                                    // of how big the nozzle is - not at any height the raycast
                                    // can see. A 0.8 m engine throwing a splash from 115 m up
                                    // was the "worms appearing 100 m above the ground".
                                    // Reach modelled on KerbalFX ImpactPuffs (Unlicense, bimo1d):
                                    // 10 m at low thrust up to 28 m at full thrust, in metres,
                                    // grown for engines bigger than the reference so a large
                                    // booster is not stuck with a micro lander's reach.
                                    float splashReach = Mathf.Clamp(
                                        Mathf.Lerp(10f, 28f, aggThrottle) * Mathf.Max(1f, sizeFactor)
                                            * SmokeTuning.LandingSplashReachScale,
                                        5f, SmokeTuning.LandingCloudRaycastReach);
                                    if (Physics.Raycast(centroid, avgForward, out lHit,
                                            splashReach,
                                            SmokeVolumeGroup.GetSceneryCollisionMaskPublic(),
                                            QueryTriggerInteraction.Ignore)
                                        && lHit.collider.GetComponentInParent<Part>() == null
                                        // Also as in KerbalFX: the exhaust has to actually point
                                        // into the surface (not skim it), and the surface must
                                        // not be a near-vertical wall.
                                        && Vector3.Dot(avgForward, -lHit.normal) >= 0.18f
                                        && Vector3.Dot(lHit.normal,
                                               (lHit.point - vessel.mainBody.position).normalized) > 0.3f)
                                    {
                                        Vector3 lUp = (lHit.point - vessel.mainBody.position).normalized;
                                        // Cubic falloff with height, KerbalFX's distanceFactor
                                        // (1 - d/reach)^3, and nothing at all below its 0.04
                                        // emit threshold. Much sharper than the linear fade it
                                        // replaces: strong only in the last few metres.
                                        float distFactor = Mathf.Pow(1f - Mathf.Clamp01(lHit.distance / splashReach), 3f);
                                        if (distFactor > 0.04f)
                                        {
                                        // One puff added to EACH ray, every tick - not
                                        // LandingCloudPuffsPerTick random directions. A
                                        // fixed rayId keeps extending the same line (see
                                        // AddGroundPuff), which is what makes it chain into
                                        // a "jet stream" streak instead of a scatter of
                                        // independent blobs with no two puffs ever sharing
                                        // a heading to connect along.
                                        int rayCount = Mathf.Max(1, SmokeTuning.LandingCloudPuffsPerTick);
                                        for (int lp = 0; lp < rayCount; lp++)
                                        {
                                            // Fades with height: full strength on touchdown,
                                            // a third of it at the edge of the reach.
                                            float splashIntensity = Mathf.Lerp(0.25f, 1f, distFactor);
                                            g.smokeMesh.AddGroundPuff(lHit.point, lUp,
                                                SmokeTuning.LandingCloudSpreadSpeed * aggThrottle * Mathf.Sqrt(sizeFactor) * splashIntensity,
                                                SmokeTuning.LandingCloudSizeBoost * sizeMultiplier * sizeFactor * sizeFactor,
                                                SmokeTuning.LandingCloudDensityBoost * splashIntensity,
                                                lp, rayCount);
                                        }
                                        if (logThisFrame)
                                        {
                                            Debug.Log(string.Format(
                                                "[PlumeTrails] landing splash: group={0} impact={1} dist={2:F0}m "
                                                + "speed={3:F0} density={4:F2} puffs={5}",
                                                g.id, lHit.point, lHit.distance,
                                                SmokeTuning.LandingCloudSpreadSpeed * aggThrottle,
                                                SmokeTuning.LandingCloudDensityBoost,
                                                SmokeTuning.LandingCloudPuffsPerTick));
                                        }
                                        }
                                    }
                                    else if (logThisFrame)
                                    {
                                        Debug.Log(string.Format(
                                            "[PlumeTrails] landing splash: group={0} raycast missed "
                                            + "(reach {1:F0}m) - no ground puffs this tick",
                                            g.id, splashReach));
                                    }
                                }

                                g.lastSpawnPos = spawnPos;
                                // Below ~50 m/s the distance test never fires, so this
                                // timer is the only trigger - at 1f that meant one burst
                                // per second, filled by a dozen puffs at once.
                                g.spawnTimer = SmokeTuning.SpawnInterval;
                            }
                        }
                        else
                        {
                            SrbTail(g);
                        }
                    }
                    else
                    {
                        // Zero live samples has two different causes that must NOT be
                        // treated the same. partIds still non-empty means the engine is
                        // still physically part of THIS vessel and simply produced no
                        // sample this tick (flamed out, not yet ignited) - that is our own
                        // motor going quiet, and it gets the tail. partIds EMPTY means the
                        // structural regroup above just found that this group's parts left
                        // the vessel entirely (staging separation) - there is no motor here
                        // to taper, and tailing it anyway drew a few seconds of ghost smoke
                        // out of the bare interstage where the booster used to be.
                        if (g.partIds.Count > 0) SrbTail(g);
                        else StopEmission(g);
                    }
                }
                else
                {
                    StopEmission(g);
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

        private void RecomputeGroups(bool forceAllEngines = false)
        {
            List<HashSet<uint>> newGroups = EngineClusterUtils.GroupEnginePartsStructurally(
                vessel, clusterDistanceThreshold, forceAllEngines);

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
                        SmokeTuning.StartSize,
                        SmokeTuning.MaxPuffSize,
                        SmokeTuning.GrowthSharpness,
                        SmokeTuning.GrowthTime,
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

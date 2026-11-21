using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // The emitter of the volumetric trail. One tracked group per engine cluster; every physics tick each running group says where its smoke starts, how wide and
    // how dense it is, and the trail (CapsuleTrail) lays the nodes. Also reports the heat haze and the scene light of the flame.
    // (The pre-1.0 puff renderer that used to live here is in old/legacy_renderer.)
    public class LaunchSmokeController : VesselModule
    {
        public float clusterDistanceThreshold = 3.5f;
        public float spawnMaxAltitude = 120000f;
        public float minThrottle = 0.15f;

        private class TrackedGroup
        {
            public int id;
            public HashSet<uint> partIds;
            public float smoothFactor;      // nozzle-size factor, smoothed
            public float smoothFlame;       // flame length reported by Waterfall, smoothed (m)
            public float smoothFlameW;      // flame width reported by Waterfall, smoothed (m)
            public float lastLoggedFactor;
            public Vector3? smoothedForward;
            // The jet's offset from the nozzle, low-passed; reset when emission stops so the next burn starts from where the jet is, not from a stale vector.
            public Vector3? smoothedJetOffset;
        }

        // Thrust transforms carry SAS's constant gimbal corrections. The spawn point sits metres along that vector, so a tiny angle becomes a metre of lateral swing:
        // damping the direction filters the wobble while still tracking a gravity turn.
        private const float SpawnForwardSmoothRate = 1.2f;

        private readonly List<TrackedGroup> trackedGroups = new List<TrackedGroup>();
        private int nextGroupId;
        private int lastPartCount = -1;
        private bool hiddenWhenOff;
        private float debugLogTimer;
        // a line in the log every few seconds while engines fire, so a report from a player says what the emitter did (not only that the shader ran)
        private float emitLogTimer;
        private int trailsPushed, trailsSkippedByType, groupsEmitting;

        private int EmitterKey(TrackedGroup g) { return g.id + 1000 * vessel.GetInstanceID(); }

        private float SizeMultiplierForSpeed(float speed)
        {
            if (speed <= SmokeTuning.SpeedBillowMin) return 1f;
            if (speed >= SmokeTuning.SpeedThinTrailMax) return SmokeTuning.ThinTrailSizeMult;
            float t = (speed - SmokeTuning.SpeedBillowMin) / (SmokeTuning.SpeedThinTrailMax - SmokeTuning.SpeedBillowMin);
            return Mathf.Lerp(1f, SmokeTuning.ThinTrailSizeMult, t);
        }

        // How much smoke a motor puts out for the thrust it is delivering. Shape from Avalanche's SolidSmoke emission curve (power: 1 -> 1, 0.01 -> 0.2, 0 -> 0): a
        // motor still coughing out its last few percent of thrust is still visibly smoking.
        private static float EmissionForThrust(float thrustFraction)
        {
            if (thrustFraction <= 0f) return 0f;
            if (thrustFraction < 0.01f) return Mathf.Lerp(0f, 0.2f, thrustFraction / 0.01f);
            return Mathf.Lerp(0.2f, 1f, (thrustFraction - 0.01f) / 0.99f);
        }

        // Air density at the vessel as a fraction of the body's sea level, 0..1.
        private static float RelativeAirDensity(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            double asl = v.mainBody.atmDensityASL;
            if (asl <= 0.0) return 0f;
            return Mathf.Clamp01((float)(v.atmDensity / asl));
        }

        // Avalanche's density curve (1 -> 2.5, 0.05 -> 2, 0 -> 0, normalised): nearly flat through the lower atmosphere, then falling to nothing in vacuum.
        private static float EmissionForAtmosphere(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            float rel = RelativeAirDensity(v);
            if (rel < 0.05f) return Mathf.Lerp(0f, 0.8f, rel / 0.05f);
            return Mathf.Lerp(0.8f, 1f, (rel - 0.05f) / 0.95f);
        }

        // Where a solid motor's exhaust comes to rest in the air, measured from the nozzle. Exhaust leaves the nozzle at ve = Isp * g0 relative to the ROCKET; relative to
        // the AIR it moves at u = vRocket + ve * exhaustDir and travels along u until drag stops it. At liftoff vRocket is ~0, so the smoke is thrown well clear of the
        // nozzle; climbing, the jet slows in the air frame and the smoke is laid closer and closer to the nozzle, until it is left hanging where the nozzle WAS. That is
        // why a real trail traces the flight path instead of the rocket's attitude. The stop distance scales with |u| and grows as the air thins (the plume flaring).
        private const float MinJetLength = 3f;
        private const float MaxThinAirStretch = 8f;

        private static void SolidJet(Vessel v, List<EngineSample> samples, Vector3 exhaustDir,
                                     out Vector3 jetDir, out float jetLength, out float airSpeed)
        {
            float ispSum = 0f;
            for (int i = 0; i < samples.Count; i++) ispSum += samples[i].isp;
            float isp = samples.Count > 0 ? ispSum / samples.Count : 0f;
            // An SRB in KSP sits around 250-270 s; if a part reports nothing usable, assume a typical one rather than collapsing the jet to zero.
            float ve = isp > 1f ? isp * 9.80665f : 2500f;

            Vector3 vel = v.srf_velocity;
            float speed = vel.magnitude;

            Vector3 u = vel + exhaustDir * ve;
            airSpeed = u.magnitude;

            // DIRECTION: in the rocket's frame the exhaust leaves straight down the nozzle axis and the relative wind then bends it round to run along the wind. How
            // much of that happens near the nozzle is set by how much of the wind CROSSES the axis: flying straight nothing bends, in a hard turn the smoke is swept
            // sideways almost immediately. Bending toward the wind is the physically right sign.
            Vector3 windDir = speed > 1f ? -vel / speed : exhaustDir;
            Vector3 cross = vel - exhaustDir * Vector3.Dot(vel, exhaustDir);
            float crossSpeed = cross.magnitude;
            float bend = Mathf.Clamp01(SmokeTuning.SrbCrossflowBend * crossSpeed / (crossSpeed + 0.25f * ve));
            // Slerp is undefined for opposite vectors; that only happens flying tail-first, where there is no crossflow anyway.
            jetDir = Vector3.Dot(exhaustDir, windDir) > -0.9f ? Vector3.Slerp(exhaustDir, windDir, bend) : exhaustDir;

            float speedFraction = Mathf.Clamp01(airSpeed / ve);
            float rel = RelativeAirDensity(v);
            float thinAir = rel > 1e-4f ? Mathf.Clamp(1f / Mathf.Sqrt(rel), 1f, MaxThinAirStretch) : MaxThinAirStretch;
            jetLength = Mathf.Max(SmokeTuning.SpawnOffset * speedFraction * thinAir, MinJetLength);

            // THE BENT PLUME, from the motion of the gas. In the frame of the air the gas leaves at u0 = v + ve * e and flies straight on in that direction, slowing down
            // as it takes in air (u ~ r0 / (r0 + 0.1 s)); the time it needs for a path s is tau(s) = (s + 0.05 s^2 / r0) / u0 and the rocket has moved on by v * tau. In the
            // frame of the rocket the gas is at X(s) = u0_hat * s - v * tau(s): along the axis first, then turning over towards the wake. The chord of this curve, as long
            // as the jet is, sets the direction of the smoke's start.
            if (SmokeTuning.PhysicalJetBend && speed > 3f && airSpeed > 10f)
            {
                float sumD2 = 0f;
                for (int i = 0; i < samples.Count; i++) { float rd = 0.5f * Mathf.Max(samples[i].nozzleDiameter, 0.3f); sumD2 += rd * rd; }
                float r0 = Mathf.Clamp(Mathf.Sqrt(sumD2), 0.6f, 8f);
                Vector3 uHat = u / airSpeed;
                float k = Mathf.Max(SmokeTuning.SrbCrossflowBend, 0f) * 0.5f;       // 2 = the physical curve, less = a straighter plume
                float lo = 0f, hi = Mathf.Min(jetLength * 40f, 4000f);
                Vector3 xe = uHat * hi - vel * k * (hi + 0.05f * hi * hi / r0) / airSpeed;
                if (xe.magnitude > jetLength)
                {
                    for (int it = 0; it < 26; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 xm = uHat * mid - vel * k * (mid + 0.05f * mid * mid / r0) / airSpeed;
                        if (xm.magnitude < jetLength) lo = mid; else hi = mid;
                    }
                    Vector3 xs = uHat * hi - vel * k * (hi + 0.05f * hi * hi / r0) / airSpeed;
                    if (xs.sqrMagnitude > 1e-6f) jetDir = xs.normalized;
                }
            }
        }

        // THE SHAPE OF THE JET from the nozzle to the emission point, for the root of the trail: it leaves the nozzle along the exhaust axis and arrives at the emission
        // point already running along the trail (against the flight path). A cubic Hermite curve with these two tangents: flying straight it is a straight line, in a hard
        // turn the exhaust leaves sideways and curls round into the track of the rocket.
        private static void JetCurve(Vessel v, Vector3 exhaustDir, Vector3 nozzle, Vector3 emission, int n, List<Vector3> pts)
        {
            pts.Clear();
            Vector3 chord = emission - nozzle;
            float len = chord.magnitude;
            Vector3 vel = v.srf_velocity;
            Vector3 wake = vel.magnitude > 3f ? -vel.normalized : exhaustDir;
            Vector3 m0 = exhaustDir * len, m1 = wake * len;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1), t2 = t * t, t3 = t2 * t;
                pts.Add((2f * t3 - 3f * t2 + 1f) * nozzle + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * emission + (t3 - t2) * m1);
            }
        }
        private readonly List<Vector3> jetPts = new List<Vector3>();

        // The radius the smoke is released with: the jet entrains air on its way to the release point (Ricou & Spalding), which dilutes and cools it; the mass of one
        // slice of it at the resulting density gives the radius. The equivalent nozzle exit comes from mass continuity (mdot = rho_e pi d^2 / 4 ve), so it follows the
        // thrust and Isp KSP reports, whatever size the part model has. The atmosphere depends only on altitude: cached on a 4 m grid, flushed twice a second.
        private readonly Dictionary<int, Vector2> ambCache = new Dictionary<int, Vector2>();
        private float ambCacheFlush;

        private float PhysicalJetRadius(Vector3 releasePos, float ve, float xs, float mdot0, float lookScale)
        {
            CelestialBody body = vessel.mainBody;
            if (body == null) return 0f;
            if (Time.time - ambCacheFlush > 0.5f) { ambCache.Clear(); ambCacheFlush = Time.time; }
            double alt = body.GetAltitude(releasePos);
            int key = (int)System.Math.Floor(alt * 0.25);
            Vector2 pt;
            if (!ambCache.TryGetValue(key, out pt))
            {
                double aq = key * 4.0 + 2.0;
                double pKpa = body.atmosphere ? body.GetPressure(aq) : 0.0;
                double tK = body.atmosphere ? body.GetTemperature(aq) : 3.0;
                pt = new Vector2(Mathf.Max((float)(pKpa * 1000.0), 0.5f), Mathf.Max((float)tK, 3f));
                ambCache[key] = pt;
            }
            float P = pt.x, T = pt.y;
            float rhoAir = Mathf.Max(P / (PuffPhysics.RAir * T), 1e-6f);
            float Te = SmokeTuning.PhysExhaustTemp;
            float rhoE = P / (PuffPhysics.RExhaust * Te);
            float dEff = Mathf.Sqrt(4f * Mathf.Max(mdot0, 1f) / (Mathf.PI * rhoE * Mathf.Max(ve, 100f)));
            float D0 = PuffPhysics.JetDilution(xs, dEff, rhoAir, rhoE, SmokeTuning.PhysMaxDilution);
            float T0 = (Te + (D0 - 1f) * T) / D0;
            float M0 = Mathf.Max(D0 * mdot0 * SmokeTuning.SpawnInterval, 1f);
            float Me = M0 / D0;
            float rho0 = PuffPhysics.Density(P, T0, Me, M0);
            return PuffPhysics.RadiusFromMass(M0, rho0) * SmokeTuning.PhysRadiusScale * lookScale;
        }

        private void FixedUpdate()
        {
            // the mod is off: the smoke is hidden once and nothing else is done (no timer, no log, no walk over the parts)
            if (!ModSettings.Enabled)
            {
                if (!hiddenWhenOff) { CapsuleTrail.Instance.HideWhenOff(); hiddenWhenOff = true; }
                return;
            }
            hiddenWhenOff = false;
            if (!HighLogic.LoadedSceneIsFlight) return;
            if (vessel == null || !vessel.loaded) return;
            // Debris is skipped only when it has no engines: a jettisoned booster becomes VesselType.Debris the moment it separates, and a blanket skip is why a
            // still-burning booster left no trail.
            if (vessel.vesselType == VesselType.Debris && !EngineClusterUtils.HasEngine(vessel)) return;

            if (vessel.Parts.Count != lastPartCount)
            {
                RecomputeGroups();
                lastPartCount = vessel.Parts.Count;
            }

            bool hasAir = SmokeTuning.AirlessBodyPlume || (vessel.mainBody != null && vessel.mainBody.atmosphere);
            // and not above the atmosphere of the body (spawnMaxAltitude is one fixed number; bodies differ)
            bool inAir = vessel.mainBody != null && (vessel.mainBody.atmosphere ? vessel.altitude < vessel.mainBody.atmosphereDepth : SmokeTuning.AirlessBodyPlume);
            bool canSpawn = hasAir && inAir && vessel.altitude <= spawnMaxAltitude;

            float dt0 = TimeWarp.fixedDeltaTime;
            bool logThisFrame = false;
            if (SmokeTuning.DebugLogging)
            {
                debugLogTimer -= TimeWarp.fixedDeltaTime;
                if (debugLogTimer <= 0f) { logThisFrame = true; debugLogTimer = 1f; }
            }

            List<EngineSample> liveSamples = canSpawn ? EngineClusterUtils.GatherEngineSamples(vessel, false) : new List<EngineSample>();
            float sizeMultiplier = SizeMultiplierForSpeed((float)vessel.srfSpeed);
            if (logThisFrame)
                Debug.Log(string.Format("[PlumeTrails] vessel={0} alt={1:F0} speed={2:F0} sizeMult={3:F2} canSpawn={4} engines={5} groups={6}",
                    vessel.vesselName, vessel.altitude, vessel.srfSpeed, sizeMultiplier, canSpawn, liveSamples.Count, trackedGroups.Count));

            foreach (TrackedGroup g in trackedGroups)
            {
                if (!canSpawn) { g.smoothedJetOffset = null; continue; }
                List<EngineSample> groupSamples = EngineClusterUtils.FilterSamplesByPartIds(liveSamples, g.partIds);
                if (groupSamples.Count == 0) { g.smoothedJetOffset = null; continue; }
                float aggThrottle = EngineClusterUtils.ComputeMaxThrottle(groupSamples);
                if (aggThrottle < minThrottle) { g.smoothedJetOffset = null; continue; }      // the chain closes by itself half a second later
                groupsEmitting++;
                EmitGroup(g, groupSamples, aggThrottle, sizeMultiplier);
            }

            emitLogTimer -= dt0;
            if (emitLogTimer <= 0f && liveSamples.Count > 0)
            {
                emitLogTimer = 5f;
                int solid = 0;
                for (int i = 0; i < liveSamples.Count; i++) if (liveSamples[i].kind == PlumeKind.Solid) solid++;
                Debug.Log(string.Format("[PlumeTrails] emit: {0} firing nozzles ({1} solid), {2} groups, {3} emitting, trails pushed {4}, skipped by engine type {5} (trail only from solids: {6})",
                    liveSamples.Count, solid, trackedGroups.Count, groupsEmitting, trailsPushed, trailsSkippedByType, SmokeTuning.TrailOnlySolid));
                trailsPushed = 0; trailsSkippedByType = 0; groupsEmitting = 0;
            }

            for (int i = trackedGroups.Count - 1; i >= 0; i--)
                if (trackedGroups[i].partIds.Count == 0) { CapsuleTrail.Instance.EndChain(EmitterKey(trackedGroups[i])); trackedGroups.RemoveAt(i); }
        }

        private void EmitGroup(TrackedGroup g, List<EngineSample> groupSamples, float aggThrottle, float sizeMultiplier)
        {
            Vector3 centroid = EngineClusterUtils.ComputeCentroid(groupSamples);
            float dt = TimeWarp.fixedDeltaTime;

            float thrustSum = 0f;
            for (int si = 0; si < groupSamples.Count; si++) thrustSum += groupSamples[si].thrustFraction;
            float groupThrust = thrustSum / groupSamples.Count;
            float lookSize, lookDensity, steamShare, exhaustWater, visDilution;
            EngineClusterUtils.GroupLook(groupSamples, out lookSize, out lookDensity, out steamShare, out exhaustWater, out visDilution);
            float emitDensity = EmissionForThrust(groupThrust) * EmissionForAtmosphere(vessel) * lookDensity;

            Vector3 rawForward = EngineClusterUtils.ComputeAverageForward(groupSamples);
            Vector3 avgForward = g.smoothedForward.HasValue
                ? Vector3.Slerp(g.smoothedForward.Value, rawForward, Mathf.Clamp01(dt * SpawnForwardSmoothRate))
                : rawForward;
            g.smoothedForward = avgForward;

            // where the smoke starts: the jet stops in the air (SolidJet), the offset VECTOR low-passed (steering an attitude change straight into the spawn point makes the
            // newest smoke whip round the nozzle while the older smoke stays; filtering the vector handles direction and length together)
            Vector3 rawDir; float rawLen, jetAirSpeed;
            SolidJet(vessel, groupSamples, avgForward, out rawDir, out rawLen, out jetAirSpeed);
            Vector3 target = rawDir * rawLen;
            float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(SmokeTuning.SrbTurnSmoothing, 0.01f));
            g.smoothedJetOffset = g.smoothedJetOffset.HasValue ? Vector3.Lerp(g.smoothedJetOffset.Value, target, alpha) : target;
            Vector3 smoothed = g.smoothedJetOffset.Value;
            float standoff = Mathf.Max(smoothed.magnitude, MinJetLength);
            Vector3 offsetDir = smoothed.sqrMagnitude > 1e-6f ? smoothed.normalized : rawDir;

            // WATERFALL: the smoke continues from where the flame ends. The flame's own length (read from the engine's Waterfall effect) sets the earliest point the smoke may
            // start, and what the heat haze and the flame light wrap around. Smoothed, because the flame follows the throttle.
            float flameLen = 0f, flameWid = 0f;
            if (SmokeTuning.WaterfallIntegration) EngineClusterUtils.GroupFlame(groupSamples, out flameLen, out flameWid);
            g.smoothFlame += (flameLen - g.smoothFlame) * (1f - Mathf.Exp(-dt / 0.5f));
            if (g.smoothFlame < 0.05f) g.smoothFlame = 0f;
            g.smoothFlameW += (flameWid - g.smoothFlameW) * (1f - Mathf.Exp(-dt / 0.5f));
            if (g.smoothFlameW < 0.05f) g.smoothFlameW = 0f;
            float flameHandoff = g.smoothFlame * SmokeTuning.WaterfallHandoff;
            if (flameHandoff > standoff) standoff = flameHandoff;

            // The smoke cannot start BELOW the surface the exhaust hits (the flame of a big rocket ends in the trench): it is released where the jet meets the ground.
            bool surfaceClamped = false;
            float surfDist = -1f;
            if (SmokeTuning.WaterfallIntegration)
            {
                float surf = SurfaceProbe.Along(centroid, offsetDir, 500f);
                if (surf > 0f) surfDist = surf;
                if (surf > 0f && surf - 1f < standoff) { standoff = Mathf.Max(surf - 1f, 3f); surfaceClamped = true; }
            }
            Vector3 spawnPos = centroid + offsetDir * standoff;

            // The nozzle-size factor follows the engines that are firing right now, so it jumps whenever one stops or a booster burns out; it is eased so the tip does not pop.
            float targetFactor = EngineClusterUtils.NozzleSizeFactor(groupSamples, g.partIds.Count) * lookSize;
            if (g.smoothFactor <= 0f) { g.smoothFactor = targetFactor; g.lastLoggedFactor = targetFactor; }
            if (Mathf.Abs(targetFactor - g.lastLoggedFactor) > 0.05f * Mathf.Max(g.lastLoggedFactor, 0.01f))
            {
                if (SmokeTuning.DebugLogging)
                    Debug.Log(string.Format("[PlumeTrails] nozzle size factor {0:F2} -> {1:F2} (firing engines={2}, group parts={3}, throttle={4:F2})",
                        g.lastLoggedFactor, targetFactor, groupSamples.Count, g.partIds.Count, aggThrottle));
                g.lastLoggedFactor = targetFactor;
            }
            g.smoothFactor += (targetFactor - g.smoothFactor) * (1f - Mathf.Exp(-dt / Mathf.Max(SmokeTuning.NozzleFactorEase, 0.05f)));
            float sizeFactor = g.smoothFactor;

            if (SmokeTuning.HeatHaze)
            {
                // the hot region is a cone from the nozzle: as wide as the nozzle at the exit, widening as it mixes; fades out in thin air (no air, no refraction) and with throttle
                float hazeD = Mathf.Max(EngineClusterUtils.NozzleSizeFactor(groupSamples, g.partIds.Count) * SmokeTuning.NozzleReferenceDiameter, 0.3f);
                float hazeAir = Mathf.Sqrt(Mathf.Clamp01((float)vessel.atmDensity));
                float hazeLen = Mathf.Clamp(hazeD * SmokeTuning.HeatHazeLengthDiameters, 4f, 150f);
                float hazeR0 = hazeD * 0.5f, hazeR1 = hazeD * 1.3f;
                if (g.smoothFlame > 0.5f)
                {
                    // wrap the flame that is actually on screen: a shimmer over the flame, not over the whole column
                    hazeLen = Mathf.Clamp(g.smoothFlame * SmokeTuning.WaterfallHazeLength, hazeD * 3f, 80f);
                    hazeR0 = Mathf.Clamp(flameWid * 0.2f, hazeD * 0.4f, 4f);
                    hazeR1 = Mathf.Clamp(flameWid * 0.4f, hazeR0 * 1.6f, 9f);
                }
                HeatHazeRegistry.Report(unchecked((int)vessel.persistentId * 31 + g.id), centroid, avgForward, hazeLen, hazeR0, hazeR1, aggThrottle * hazeAir * SmokeTuning.HeatHazeStrength);
            }

            // the colour of what is burning, for the glow of the trail's root and for a real light in the scene
            Color flameRgb; float lightMul;
            EngineClusterUtils.GroupFlameColor(groupSamples, out flameRgb, out lightMul);
            ReportLight(g, groupSamples, aggThrottle, ref flameRgb, centroid, avgForward, offsetDir, surfaceClamped, surfDist, sizeFactor);

            // The radius the smoke is released with: from the jet physics (see PhysicalJetRadius), else a fixed fraction of the nozzle size.
            float currentRadius = SmokeTuning.StartSize * sizeFactor * sizeMultiplier;
            if (SmokeTuning.PhysicsMode)
            {
                float ispS = 0f, thrustN = 0f;
                for (int si = 0; si < groupSamples.Count; si++) { ispS += groupSamples[si].isp; thrustN += groupSamples[si].thrustKN * 1000f; }
                float ispAvg = ispS / groupSamples.Count;
                float ve = ispAvg > 1f ? ispAvg * 9.80665f : 2500f;
                float r = PhysicalJetRadius(spawnPos, ve, Mathf.Max(standoff, 3f), thrustN / ve, lookSize);
                if (r > 0.05f) currentRadius = r;
            }

            if (!SmokeTuning.NewTrail) return;
            EmitTrail(g, groupSamples, aggThrottle, emitDensity, currentRadius, sizeFactor, centroid, avgForward, spawnPos, flameRgb, surfaceClamped);
        }

        // The scene light of the flame: with Waterfall it follows Waterfall's own flame light (read, not guessed from the thrust), without it a physical estimate;
        // where the jet meets the ground a second light lights the structure around it.
        private void ReportLight(TrackedGroup g, List<EngineSample> groupSamples, float aggThrottle, ref Color flameRgb, Vector3 centroid, Vector3 avgForward,
                                 Vector3 offsetDir, bool surfaceClamped, float surfDist, float sizeFactor)
        {
            // the luminous power of the flame from the jet power of the engines (F * ve / 2) times the share that leaves as visible light (the hot alumina of a solid
            // motor glows brightly, hydrogen hardly at all), inverse-square physics: the intensity is the illuminance at 10 m, the range is where the light drops below
            // what the eye notices
            float lumW = 0f;
            for (int li = 0; li < groupSamples.Count; li++)
            {
                PlumeKind lk = groupSamples[li].kind;
                float radFrac = lk == PlumeKind.Solid ? 0.06f : lk == PlumeKind.Kerolox ? 0.025f : lk == PlumeKind.Methalox ? 0.012f : lk == PlumeKind.Hypergolic ? 0.02f : 0.003f;
                float jetW = 0.5f * groupSamples[li].thrustKN * 1000f * Mathf.Max(groupSamples[li].isp, 150f) * 9.80665f;
                lumW += jetW * radFrac * Mathf.Clamp01(groupSamples[li].thrustFraction > 0f ? groupSamples[li].thrustFraction : aggThrottle);
            }
            float airDim = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01((float)vessel.atmDensity * 2f));       // in thin air the flame is bigger but fainter
            float lightRange = Mathf.Clamp(Mathf.Sqrt(lumW / (4f * Mathf.PI * 0.35f)), 25f, 1500f);
            float lightPower = Mathf.Clamp(lumW / (4f * Mathf.PI * 100f) / 1.1e4f, 0f, 10f) * airDim;
            if (SmokeTuning.WaterfallLight && SmokeTuning.EngineLights && WaterfallBridge.Available)
            {
                Color wc = Color.black; float wi = 0f, wr = 0f; int wn = 0; HashSet<uint> seen = new HashSet<uint>();
                for (int li = 0; li < groupSamples.Count; li++)
                {
                    uint pid = groupSamples[li].partId;
                    if (!seen.Add(pid)) continue;
                    Part wp = null;
                    for (int pi = 0; pi < vessel.parts.Count; pi++) if (vessel.parts[pi].flightID == pid) { wp = vessel.parts[pi]; break; }
                    Color c1; float i1, r1;
                    if (WaterfallBridge.ReadLight(wp, true, out c1, out i1, out r1) && i1 > 0f) { wc += c1 * i1; wi += i1; wr = Mathf.Max(wr, r1); wn++; }
                }
                if (wn > 0)
                {
                    flameRgb = wc / wi;
                    lightPower = Mathf.Clamp(wi * SmokeTuning.WaterfallLightGain, 0f, 10f);
                    lightRange = Mathf.Clamp(wr, 25f, 1500f);
                }
            }
            int key = unchecked((int)vessel.persistentId * 31 + g.id);
            EngineLights.Report(key, centroid + avgForward * Mathf.Max(g.smoothFlame * 0.4f, 2f), flameRgb, lightRange, lightPower);
            if (surfaceClamped && surfDist > 2f)
            {
                Vector3 hitL = centroid + offsetDir * Mathf.Max(surfDist - 1.5f, 1f);
                float impactRange = Mathf.Clamp(Mathf.Max(lightRange * 0.85f, 16f * Mathf.Max(sizeFactor, 0.3f)), 45f, 360f);
                Color hot = Color.Lerp(flameRgb, new Color(1f, 0.92f, 0.78f), 0.4f);
                EngineLights.Report(key + 100003, hitL, hot, impactRange, lightPower * 1.5f);
            }
        }

        // The trail: by engine type a solid motor leaves the full trail, the others what the type table says (thrust-weighted over the group); engines far apart (side
        // boosters) each leave their own trail from their own nozzle, not one from the middle of the rocket.
        private void EmitTrail(TrackedGroup g, List<EngineSample> groupSamples, float aggThrottle, float emitDensity, float currentRadius, float sizeFactor,
                               Vector3 centroid, Vector3 avgForward, Vector3 spawnPos, Color flameRgb, bool surfaceClamped)
        {
            int emitterKey = EmitterKey(g);
            float trailDens = 0f, trailSize = 0f, trailW = 0f;
            for (int ki = 0; ki < groupSamples.Count; ki++)
            {
                float kw = Mathf.Max(groupSamples[ki].thrustKN, 0.01f);
                trailDens += kw * SmokeTuning.TrailDensity(groupSamples[ki].kind);
                trailSize += kw * SmokeTuning.TrailSize(groupSamples[ki].kind);
                trailW += kw;
            }
            if (trailW > 0f) { trailDens /= trailW; trailSize /= trailW; } else { trailDens = 1f; trailSize = 1f; }
            if (trailDens < 0.02f) { CapsuleTrail.Instance.EndChain(emitterKey); trailsSkippedByType++; return; }
            trailsPushed++;
            float capR = Mathf.Max(currentRadius * trailSize, 0.5f), capD = Mathf.Clamp(emitDensity, 0.9f, 1.5f) * trailDens;
            float glowK = Mathf.Clamp01(aggThrottle) * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01((float)vessel.atmDensity * 3f));
            bool root = SmokeTuning.NewTrailRoot && !surfaceClamped;      // no tube from the nozzle down to the pad

            float spread = 0f;
            for (int si = 0; si < groupSamples.Count; si++) spread = Mathf.Max(spread, Vector3.Distance(groupSamples[si].position, centroid));
            if (groupSamples.Count > 1 && spread > 0.8f && groupSamples.Count < 16)
            {
                float capBase = Mathf.Max(currentRadius / Mathf.Sqrt(groupSamples.Count), 0.5f);
                float nozR1 = Mathf.Max(sizeFactor * SmokeTuning.NozzleReferenceDiameter * 0.5f / Mathf.Sqrt(groupSamples.Count), 0.3f);
                float baseD = Mathf.Clamp(emitDensity, 0.9f, 1.5f);
                for (int si = 0; si < groupSamples.Count; si++)
                {
                    int key1 = emitterKey + (si + 1) * 100003;
                    // each engine by ITS type: a liquid core between two boosters leaves no trail when only solids do
                    float dens1 = SmokeTuning.TrailDensity(groupSamples[si].kind);
                    if (dens1 < 0.02f) { CapsuleTrail.Instance.EndChain(key1); trailsSkippedByType++; continue; }
                    trailsPushed++;
                    float capR1 = Mathf.Max(capBase * SmokeTuning.TrailSize(groupSamples[si].kind), 0.5f), capD1 = baseD * dens1;
                    Vector3 off = groupSamples[si].position - centroid;
                    // the start radius of a trail from one nozzle is its own, not the 5 m minimum of the group trail: boosters a few metres apart would overlap into one
                    // column from the first metre (the trails still merge as they widen, like the real plumes)
                    CapsuleTrail.Instance.Push(key1, vessel.mainBody, spawnPos + off, capR1, capD1, false, Mathf.Max(capR1 * 1.6f, 1.2f));
                    if (root) JetCurve(vessel, avgForward, centroid + off, spawnPos + off, CapsuleTrail.HeadPoints, jetPts);
                    else { jetPts.Clear(); jetPts.Add(spawnPos + off); }
                    CapsuleTrail.Instance.SetHead(key1, jetPts, nozR1, capR1, capD1, flameRgb * glowK);
                }
                CapsuleTrail.Instance.EndChain(emitterKey);
                return;
            }
            CapsuleTrail.Instance.Push(emitterKey, vessel.mainBody, spawnPos, capR, capD, false);
            // the root: the jet from the nozzle to the emission point, bent by the motion of the rocket (see JetCurve), as wide as the nozzle at the start; the hot exhaust in
            // it glows in the colour of the flame
            float nozR = Mathf.Max(sizeFactor * SmokeTuning.NozzleReferenceDiameter * 0.5f, 0.3f);
            if (root) JetCurve(vessel, avgForward, centroid, spawnPos, CapsuleTrail.HeadPoints, jetPts);
            else { jetPts.Clear(); jetPts.Add(spawnPos); }
            CapsuleTrail.Instance.SetHead(emitterKey, jetPts, nozR, capR, capD, flameRgb * glowK);
        }

        private void RecomputeGroups()
        {
            List<HashSet<uint>> newGroups = EngineClusterUtils.GroupEnginePartsStructurally(vessel, clusterDistanceThreshold, false);

            // snapshot count before the loop, trackedGroups grows inside it
            int originalGroupCount = trackedGroups.Count;
            bool[] claimed = new bool[originalGroupCount];

            foreach (HashSet<uint> newPartIds in newGroups)
            {
                int bestIndex = -1, bestOverlap = 0;
                for (int i = 0; i < originalGroupCount; i++)
                {
                    if (claimed[i]) continue;
                    int overlap = 0;
                    foreach (uint id in newPartIds) if (trackedGroups[i].partIds.Contains(id)) overlap++;
                    if (overlap > bestOverlap) { bestOverlap = overlap; bestIndex = i; }
                }
                if (bestIndex >= 0)
                {
                    trackedGroups[bestIndex].partIds = newPartIds;
                    claimed[bestIndex] = true;
                }
                else
                {
                    TrackedGroup g = new TrackedGroup { id = nextGroupId++, partIds = newPartIds };
                    trackedGroups.Add(g);
                    if (SmokeTuning.DebugLogging) Debug.Log(string.Format("[PlumeTrails] new engine group id={0} with {1} parts", g.id, newPartIds.Count));
                }
            }
            // a group whose parts left the vessel (staging) has no motor to taper: it is dropped by FixedUpdate and its chain ends
            for (int i = 0; i < originalGroupCount; i++)
                if (!claimed[i]) trackedGroups[i].partIds = new HashSet<uint>();
        }
    }
}

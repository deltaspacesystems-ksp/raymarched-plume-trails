using System.Collections.Generic;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Feeds the v1 capsule trail directly. No puff objects, density tiles or legacy renderer.
    public class LaunchSmokeController : VesselModule
    {
        private const float MinJetLength = 3f, MaxThinAirStretch = 8f;
        private readonly Dictionary<Transform, NozzleState> nozzles = new Dictionary<Transform, NozzleState>();
        private readonly List<Transform> retired = new List<Transform>();
        private readonly List<EngineSample> samples = new List<EngineSample>();
        private readonly List<Vector3> jetPts = new List<Vector3>();
        private float nextDiagnostic;

        private sealed class NozzleState
        {
            public readonly int id = EmitterIdentity.Next();
            public bool seen;
            public float flameLength, flameWidth, size;
        }

        private void FixedUpdate()
        {
            if (vessel == null || !vessel.loaded || vessel.packed || vessel.mainBody == null) return;
            bool inAir = vessel.mainBody.atmosphere
                ? vessel.altitude < vessel.mainBody.atmosphereDepth : SmokeTuning.AirlessBodyPlume;
            if (!ModSettings.Enabled || !SmokeTuning.NewTrail || !inAir)
            {
                StopAll();
                return;
            }

            foreach (NozzleState state in nozzles.Values) state.seen = false;
            EngineClusterUtils.GatherEngineSamples(vessel, samples);
            float smooth = 1f - Mathf.Exp(-TimeWarp.fixedDeltaTime / 0.5f);
            Vector3 surfaceVelocity = vessel.srf_velocity;
            Rigidbody rootBody = vessel.rootPart != null ? vessel.rootPart.rb : null;
            int liquidNozzles = 0;
            foreach (EngineSample sample in samples)
            {
                float density = SmokeTuning.TrailDensity(sample.kind);
                if (density < 0.02f || sample.nozzle == null) continue;
                Vector3 axis = sample.forward.normalized;
                if (axis.sqrMagnitude < 0.5f) continue;
                NozzleState state;
                if (!nozzles.TryGetValue(sample.nozzle, out state))
                {
                    state = new NozzleState();
                    nozzles.Add(sample.nozzle, state);
                }
                // Engine modes sometimes share one physical exhaust transform.
                if (state.seen) continue;
                state.seen = true;
                if (!sample.isSolid) liquidNozzles++;
                Vector3 nozzle = sample.position;
                float diameter = Mathf.Max(sample.nozzleDiameter, 0.15f);
                float referenceDiameter = Mathf.Max(SmokeTuning.NozzleReferenceDiameter, 0.1f);
                float size = SmokeTuning.NozzleScaleEnabled
                    ? Mathf.Max(Mathf.Pow(diameter / referenceDiameter, SmokeTuning.NozzleScalePower), SmokeTuning.NozzleScaleMin)
                    : EngineClusterUtils.ClusterSizeFactor(1);
                size *= sample.plumeSize;
                state.size = state.size <= 0f ? size : Mathf.Lerp(state.size, size, smooth);
                Vector3 direction;
                float length;
                float baseLength = sample.isSolid ? SmokeTuning.SpawnOffset
                    : Mathf.Min(SmokeTuning.SpawnOffset, Mathf.Max(MinJetLength, 6f * diameter));
                ExhaustJet(vessel, sample, axis, baseLength, out direction, out length);
                state.flameLength = state.flameLength > 0f
                    ? Mathf.Lerp(state.flameLength, sample.flameLength, smooth) : sample.flameLength;
                state.flameWidth = state.flameWidth > 0f
                    ? Mathf.Lerp(state.flameWidth, sample.flameWidth, smooth) : sample.flameWidth;
                length = Mathf.Max(length, state.flameLength * SmokeTuning.WaterfallHandoff);
                float surface = SceneryGeometry.SurfaceAlong(nozzle, direction, length + 1f);
                bool clamped = surface > 0f;
                if (clamped) length = Mathf.Min(length, Mathf.Max(surface - 1f, 0.5f * surface));
                Vector3 emission = nozzle + direction * length;
                float radius = SmokeTuning.StartSize * state.size;
                if (SmokeTuning.PhysicsMode && sample.isSolid)
                    radius = ReleaseRadius(sample.thrustKN, length, state.size, Mathf.Max(sample.isp * 9.80665f, 100f));
                if (SmokeTuning.WaterfallIntegration && state.flameWidth > 0.2f)
                    radius = Mathf.Max(radius, state.flameWidth * 0.5f * SmokeTuning.WaterfallStartWidth);
                float emissionDensity = EmissionForThrust(sample.thrustFraction) * EmissionForAtmosphere(vessel) * sample.plumeDensity;
                // Small solid motors get a slightly narrower smoke column.
                // Let larger nozzles retain more of their original diameter.
                float solidWidth = sample.isSolid
                    ? Mathf.SmoothStep(0.85f, 0.93f, Mathf.InverseLerp(0.3f, 3.5f, diameter))
                    : 1f;
                float capR = Mathf.Max(radius * SmokeTuning.TrailSize(sample.kind) * solidWidth, 0.5f);
                float capD = Mathf.Clamp(emissionDensity, 0.9f, 1.5f) * density;
                int key = state.id;
                // Gas is already mixed by the time it exits the live jet. Keep
                // a bounded residual speed, including the motion of the nozzle.
                float exitSpeed = sample.isp > 1f ? sample.isp * 9.80665f : 2500f;
                float residualSpeed = exitSpeed * diameter / (diameter + 0.2f * length);
                Vector3 velocity = surfaceVelocity + direction * residualSpeed;
                if (rootBody != null)
                    velocity += Vector3.Cross(rootBody.angularVelocity, nozzle - rootBody.worldCenterOfMass);
                // A jet clipped against the ground deposits its trail at the impact.
                velocity = clamped ? Vector3.zero : Vector3.ClampMagnitude(velocity, 25f);
                CapsuleTrail.Instance.Push(key, vessel.mainBody, emission, capR, capD, false, velocity);
                if (SmokeTuning.NewTrailRoot && !clamped)
                    JetCurve(vessel, axis, nozzle, emission, CapsuleTrail.HeadPoints, jetPts);
                else
                {
                    jetPts.Clear();
                    if (SmokeTuning.NewTrailRoot) jetPts.Add(nozzle);
                    jetPts.Add(emission);
                }
                Color flame = sample.flameColor;
                float throttle = sample.throttle;
                float glow = Mathf.Clamp01(throttle) * Mathf.Lerp(0.4f, 1f, Mathf.Clamp01((float)vessel.atmDensity * 3f));
                float rootRadius = Mathf.Max(diameter * 0.5f * solidWidth, 0.3f);
                CapsuleTrail.Instance.SetHead(key, sample.nozzle, jetPts, rootRadius, capR, capD, flame * glow);
                if (SmokeTuning.HeatHaze)
                {
                    float d = Mathf.Max(state.size * SmokeTuning.NozzleReferenceDiameter, 0.3f);
                    HeatHazeRegistry.Report(key, nozzle, axis, Mathf.Clamp(d * SmokeTuning.HeatHazeLengthDiameters, 4f, 150f), d * 0.5f, d * 1.3f,
                        SmokeTuning.HeatHazeStrength * throttle * Mathf.Sqrt(Mathf.Clamp01((float)vessel.atmDensity)));
                }
                if (SmokeTuning.EngineLights) EngineLights.Report(key, nozzle, flame, Mathf.Clamp(Mathf.Max(SmokeTuning.FlameLightRange * state.size * 2f, state.flameLength * 6f) * SmokeTuning.EngineLightRange, 30f, 500f), SmokeTuning.EngineLightIntensity * throttle);
            }
            retired.Clear();
            foreach (var entry in nozzles)
                if (!entry.Value.seen) retired.Add(entry.Key);
            foreach (Transform nozzle in retired)
            {
                CapsuleTrail.Instance.EndChain(nozzles[nozzle].id);
                nozzles.Remove(nozzle);
            }
            if (SmokeTuning.DebugLogging && vessel == FlightGlobals.ActiveVessel && Time.unscaledTime >= nextDiagnostic)
            {
                nextDiagnostic = Time.unscaledTime + 5f;
                Debug.Log("[PlumeTrails] trail: nozzles=" + nozzles.Count + " liquid=" + liquidNozzles + " nodes=" + CapsuleTrail.Instance.NodeCount);
            }
        }
        // Original parcel release equations retained only to size the v1 trail's emission point.
        private float ReleaseRadius(float thrustKN, float length, float scale, float ve)
        {
            float pressure = Mathf.Max((float)vessel.staticPressurekPa * 1000f, 1f);
            float temperature = Mathf.Max((float)vessel.externalTemperature, 100f);
            float te = SmokeTuning.PhysExhaustTemp, mdot = thrustKN * 1000f / ve;
            float rhoE = pressure / (SmokeRelease.RExhaust * te);
            float diameter = Mathf.Sqrt(4f * Mathf.Max(mdot, 1f) / (Mathf.PI * rhoE * ve));
            float dilution = SmokeRelease.JetDilution(length, diameter, Mathf.Max((float)vessel.atmDensity, 1e-6f), rhoE, SmokeTuning.PhysMaxDilution);
            float t = (te + (dilution - 1f) * temperature) / dilution;
            float mass = Mathf.Max(dilution * mdot * Mathf.Clamp(SmokeTuning.SpawnInterval, 0.005f, 0.25f), 1f);
            return SmokeRelease.RadiusFromMass(mass, SmokeRelease.Density(pressure, t, mass / dilution, mass)) * SmokeTuning.PhysRadiusScale * scale;
        }
        private void StopAll()
        {
            foreach (NozzleState state in nozzles.Values)
                CapsuleTrail.Instance.EndChain(state.id);
            nozzles.Clear();
            retired.Clear();
            samples.Clear();
        }
        public override void OnUnloadVessel() { StopAll(); }
        public override void OnGoOnRails() { StopAll(); }
        private void OnDestroy() { StopAll(); }
        private static void ExhaustJet(Vessel v, EngineSample sample, Vector3 exhaustDir, float baseLength,
                                     out Vector3 jetDir, out float jetLength)
        {
            float isp = sample.isp;
            // An SRB in KSP sits around 250-270s; if a part reports nothing usable, assume
            // a typical one rather than collapsing the jet to zero.
            float ve = isp > 1f ? isp * 9.80665f : 2500f;

            Vector3 vel = v.srf_velocity;
            float speed = vel.magnitude;

            // How far the jet carries before the air stops it depends on how fast it is
            // moving through the AIR, which is the exhaust velocity plus the rocket's own.
            Vector3 u = vel + exhaustDir * ve;
            float airSpeed = u.magnitude;

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
            jetLength = Mathf.Max(baseLength * speedFraction * thinAir, MinJetLength);

            // THE BENT PLUME, from the motion of the gas. In the frame of the air the gas leaves at u0 = v + ve * e and flies straight on in that direction,
            // slowing down as it takes in air (a jet's momentum is kept while its radius grows, u ~ r0 / (r0 + 0.1 s)); the time it needs for a path s is
            //     tau(s) = integral of ds / u = (s + 0.05 s^2 / r0) / u0
            // and the rocket has moved on by v * tau in that time. In the frame of the rocket the gas is therefore at
            //     X(s) = u0_hat * s - v * tau(s)
            // along the axis first (the gas is fast), then turning over towards the wake (the rocket goes on, the gas stays): a turning rocket leaves a plume
            // that curls into its own track. The chord of this curve, as long as the jet is, sets the direction of the smoke's start.
            if (SmokeTuning.PhysicalJetBend && speed > 3f && airSpeed > 10f)
            {
                float r0 = Mathf.Clamp(sample.nozzleDiameter * 0.5f, 0.6f, 8f);
                Vector3 uHat = u / airSpeed;
                float k = Mathf.Max(SmokeTuning.SrbCrossflowBend, 0f) * 0.5f;       // the old slider: 2 = the physical curve, less = a straighter plume
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

        private static float RelativeAirDensity(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            double asl = v.mainBody.atmDensityASL;
            if (asl <= 0.0) return 0f;
            return Mathf.Clamp01((float)(v.atmDensity / asl));
        }

        private static float EmissionForAtmosphere(Vessel v)
        {
            if (v.mainBody == null || !v.mainBody.atmosphere) return 0f;
            float rel = RelativeAirDensity(v);
            if (rel < 0.05f) return Mathf.Lerp(0f, 0.8f, rel / 0.05f);
            return Mathf.Lerp(0.8f, 1f, (rel - 0.05f) / 0.95f);
        }

        private static float EmissionForThrust(float thrustFraction)
        {
            if (thrustFraction <= 0f) return 0f;
            if (thrustFraction < 0.01f) return Mathf.Lerp(0f, 0.2f, thrustFraction / 0.01f);
            return Mathf.Lerp(0.2f, 1f, (thrustFraction - 0.01f) / 0.99f);
        }
    }
}

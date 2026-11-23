using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Settings for the v1 capsule trail, engine plumes and shared lighting/compositing.
    public static class SmokeTuning
    {
        // 0 normal, 1 exhausted rays, 2 segment bounds off, 3 light, 4 shadow, 5 cost.
        public static int TrailDebugMode = 0;
        public static bool TrailAdaptiveSampling = false;
        public static bool ModEnabled = true;
        public static float Opacity = 1.0f;
        public static float Density = 1.21591f;
        public static float Absorption = 1.5f;
        public static float DetailStrength = 0.966851f;
        public static float EdgeErosionStrength = 0.85f; // frays the outer rim
        public static float SilhouetteWarpStrength = 20f;
        public static float SilhouetteNoiseScale = 0.129779f;
        public static float AmbientFloor = 0.432873f;        // lower = deeper shadows
        public static float ShadowExtinction = 0.025f;
        public static float LightReach = 320.028f;
        public static int ConfigVersion = SmokeTuningConfig.Layout;      // not a setting: the layout of tuning.cfg (see SmokeTuningConfig)
        public static bool GradientNoise = true;
        public static float AerialStrength = 0.6f;
        public static float AerialDistance = 14000f;
        public static bool PhysicalPlumes = false;        // off by default: Waterfall draws the flame; switch on to replace it with the physical plume
        public static float PlumeExposure = 2.5f;
        public static float PlumeStreak = 0.8f;           // the long smooth streak of a high-altitude launch (a long exposure): 0 = off, 1 = full (it grows in with altitude)
        public static float PlumeScale = 1.0f;
        public static int PlumeSteps = 56;
        public static int PlumeMaxEngines = 16;
        public static bool PlumeHideWaterfall = true;     // switch off Waterfall's flame while ours is drawn
        public static bool SmokeLast = false;              // composite the smoke at the very end (before the image effects): after EVE's volumetric clouds, which know nothing of the smoke and cover it when a cloud lies on the same ray
        public static bool SmokeBeforeClouds = false;      // composite the smoke before the transparent queue: cloud layers (no depth) are then drawn over it
        public static bool PlumeDeflect = true;           // the physical plume is turned by the surface it hits (flame deflector, trench floor)
        public static float PlumeDeflectSpread = 1f;      // how fast the turned flame thickens
        public static bool HeatHaze = true;
        public static float HeatHazeStrength = 0.3f;     // overall, also scaled by throttle and air density
        public static float HeatHazeAmount = 0.1f;        // displacement size
        public static float HeatHazeLengthDiameters = 14f; // length of the hot region, in nozzle diameters
        public static float HeatHazeScale = 1.6f;         // noise cell size (m)
        public static float HeatHazeSpeed = 30f;          // m/s, how fast it streams away from the nozzle
        public static float ShadowStrength = 0.50442f;       // 0 = flat, 1 = full self-shadowing
        public static float ShadowDarkness = 1.0f;
        public static float SpineBlend = 0.30f;
        public static float MacroNoiseScale = 0.02f;
        public static float MacroStrength = 0.154696f;
        public static float SkyTint = 0f;
        public static float GroundTint = 0f;
        public static bool PhysicsMode = true;
        public static float PhysExhaustTemp = 2000f;      // K at the nozzle exit (solid motor)
        public static float PhysRadiusScale = 1.0f;       // visual size of the puff relative to its physical radius
        public static float PhysMaxDilution = 60f;        // cap on the jet dilution at the release point
        public static float SceneAmbientBlend = 1.0f;
        public static float AnalyticAmbient = 0.75f;
        public static float ThinEdgeSoftness = 0.45f;
        public static float ThinDetailFade = 0.25f;
        public static float ShadowCastStrength = 0.7f;
        public static bool ShadowIntoSunMask = false;       // EXPERIMENTAL, deferred rendering: the shadow goes into the sun's screen-space mask. Unity's deferred path may not use that mask (the shadow did not show), so it is off: the shadow is a multiply over the lit picture
        public static bool ShadowMaskFlip = false;          // developer: flips the sun-mask shadow upside down (if it lands mirrored)
        public static float ShadowCastDensity = 0.5f;
        public static float ShadowCastFloor = 0.3f;
        public static float ShadowCastDistance = 2500f;
        public static float ShadowCastSteps = 16f;
        public static bool DebugLogging = false;
        public static bool SrbOnly = false;
        public static bool PhysicalJetBend = true;           // the plume bends the way the gas moves (the air frame u0 = v + ve e, slowing by entrainment, the rocket going on): it curls into the track of a turning rocket
        public static bool LiquidPlumes = true;
        public static float LiquidSizeScale = 0.5f;
        public static bool NozzleFromThrust = true;
        public static float NozzleSolidScale = 0.8f;
        public static bool WaterfallIntegration = true;
        public static float WaterfallHandoff = 0.7f;
        public static float WaterfallStartWidth = 0.5f;
        public static float HeatHazeMaxShift = 0.003f;
        public static float NozzleThrustScale = 1f;
        public static float LiquidDensityScale = 0.3f;
        public static float HydrogenPlumeVisibility = 0.9f;   // hydrogen exhaust is ~90 % water: it condenses to a dense white fog
        public static float MethanePlumeVisibility = 0.7f;
        public static float SrbCrossflowBend = 2.0f;
        public static int QualityPreset = 0;
        public static bool AutoQuality = false;
        public static float TargetFrameMs = 22f;
        public static bool TemporalUpscale = true;
        public static float TemporalFeedback = 0.88f;
        public static bool ScattererSunTint = true;          // light the smoke with the colour that Scatterer gives the sun (red at a low sun)
        public static float ScattererTintStrength = 1.0f;      // measured against the clouds of a game: the full Rayleigh tint (blue 0.86 of red) made a trail r/b 1.14 where a sunlit cloud next to it was 1.05
        public static bool NewTrail = true;
        public static bool NewTrailCloudShadow = true;      // EVE's volumetric clouds shade the trail (their light volume: the sun that gets through the clouds)
        public static float NewTrailCloudShadowStrength = 1f;
        public static int NewTrailEveMapVariant = 0;
        public static bool EveCalibOverlay = false;          // red over the ground where the trail thinks EVE's clouds cast their shadow (calibration)        // the axes of the mapping of EVE's light volume (calibration; 0..15)
        public static bool NewTrailPhysicalLight = true;    // sun scattering + sky light added (the model of cloud renderers and KSA) instead of the blend of the old palette
        public static float NewTrailSunBright = 4f;          // the physical light: brightness of the sun
        public static float NewTrailDistanceContrastReduction = 0.55f;
        public static float NewTrailSkyBright = 1f;          // the physical light: brightness of the sky light
        public static float NewTrailRoundLobes = 1.0f;      // 0 = the fbm noise (stringy), 1 = round perlin-worley lobes (like KSA); above 1 the lobes are fuller (less eroded)
        public static float NewTrailGlow = 1.0f;            // how strongly the hot exhaust next to the nozzle glows (the orange start of the trail)
        public static bool NewTrailPowder = false;          // the powdery fringe around the edge: off by default (a matter of taste)
        public static float NewTrailDust = 0.7f;            // the powdery fringe around the edge (fine grains of thin smoke), 0 = a clean edge
        public static bool NewTrailRoot = true;             // the new trail grows out of the flame (a tapering root from inside the flame) instead of starting as a round cap below it
        public static bool HideStockSmoke = true;           // with the new trail on: hides the particle smoke the engine parts bring themselves (the thread between the flame and our trail)
        public static float NewTrailMaxRadius = 30f;        // expansion cap (m); small emitters grow to 4x initial radius
        public static float NewTrailGrowTau = 12f;          // the time of growth (simulation seconds)
        public static float NewTrailLife = 300f;            // the life of the smoke (s), it fades over the last 45%
        public static float NewTrailDensityK = 8f;           // the column of smoke: density = K / radius (like KSA: soft, lit through; 30 was a solid lump)
        public static float NewTrailErosion = 0.8f;         // how deep the noise cuts into the shape (0..1)
        public static float NewTrailSharpness = 0.8f;       // the sharpness of the eroded edge (KSA 0.95; 0.45 with the thin smoke of K 8 read as fog)
        public static float NewTrailLightSoft = 0.4f;       // the edge sharpness the LIGHT march sees (the eye sees NewTrailSharpness): soft shadows inside the smoke (0 = same as the eye)
        public static float TrailDensSolid = 1f, TrailSizeSolid = 1f;
        public static float TrailDensKerolox = 0.5f, TrailSizeKerolox = 0.7f;
        public static float TrailDensHydrolox = 0f, TrailSizeHydrolox = 0.5f;
        public static float TrailDensMethalox = 0.25f, TrailSizeMethalox = 0.6f;
        public static float TrailDensHypergolic = 0.3f, TrailSizeHypergolic = 0.6f;
        public static float TrailDensNuclear = 0f, TrailSizeNuclear = 0.5f;
        public static float NewTrailEndAltitude = 25f;      // km: the trail does not exist above this altitude (0 = no limit); below it the smoke thins out over the fade length
        public static float NewTrailFadeLength = 10f;       // km: the length over which the trail thins out before it ends
        public static float NewTrailFill = 0f;              // the inner fill: from this profile inward the smoke is solid (the noise shapes only the surface; 0 = off)
        public static float NewTrailNoiseFreq = 0.004f;    // noise frequency (1/m) at the radius of 80 m: the lobes are about 1 / this wide
        public static float NewTrailShadowExt = 0.6f;       // how much of the extinction the light feels (self-shadow depth)
        public static float NewTrailLightReach = 1.2f;      // the length of the light march (in radii)
        public static float NewTrailSkyOcc = 0.8f;          // how much the smoke above blocks the sky light (dark undersides)
        public static float NewTrailEngineLight = 1.0f;     // the light of the engine's flame on the smoke next to it (0 = off)
        public static float NewTrailWobble = 0.3f;           // how much the width wanders and the column meanders (0 = a straight machined cone)
        public static bool NewTrailAerial = false;          // aerial perspective (far smoke veiled by the air): off for now, to be done properly later
        public static bool NewTrailShadow = true;           // the trail casts a shadow on the ground and the scene (strength and floor: the cast shadow settings)
        public static float NewTrailDetail = 0f;           // the second, smaller scale of lobes (cauliflower) on the big ones
        public static float NewTrailSilver = 6f;            // the light that shines through the thin parts of the smoke toward the sun (silver lining), 0 = off
        public static float NewTrailTint = 0.4f;           // how much of the colour of the light (low sun, sky, ground) the smoke keeps: 0 = neutral grey, 1 = all of it
        public static float NewTrailBillow = 0f;             // how far the big lobes move the edge (fraction of the radius)
        public static float NewTrailBillowFreq = 0.37f;     // the frequency of the big lobes relative to the erosion noise
        public static float NewTrailLightGain = 0.7f;      // how much of the sun the smoke takes (low = more contrast inside the plume)
        public static float NewTrailSun = 5f;               // the brightness of the sun-lit smoke
        public static float NewTrailAmbient = 0.35f;       // the least light (shadow floor)
        public static float NewTrailDarkEdge = 0.2f;        // the multiple scattering at the rim (1 = no dark edge)
        public static float ShadowCastDebug = 0f;
        public static float SunlitBrightness = 0.78f;
        public static bool NozzleScaleEnabled = true;
        public static float NozzleReferenceDiameter = 3.7f;
        public static float NozzleScalePower = 1f;
        public static float NozzleScaleMin = 0.1f;
        public static float SpawnInterval = 0.03f;
        public static float FlameLightRange = 7.58563f;
        public static bool EngineLights = true;
        public static float EngineLightIntensity = 1.5f;
        public static float EngineLightRange = 1f;
        public static float StartSize = 1.5f;
        public static float SpawnOffset = 32.2541f;
        public static bool AirlessBodyPlume = false;

        public static float TrailDensity(PlumeKind k)
        {
            switch (k)
            {
                case PlumeKind.Solid: return TrailDensSolid;
                case PlumeKind.Kerolox: return TrailDensKerolox;
                case PlumeKind.Hydrolox: return TrailDensHydrolox;
                case PlumeKind.Methalox: return TrailDensMethalox;
                case PlumeKind.Hypergolic: return TrailDensHypergolic;
                case PlumeKind.Nuclear: return TrailDensNuclear;
                default: return 0f;
            }
        }

        public static float TrailSize(PlumeKind k)
        {
            switch (k)
            {
                case PlumeKind.Solid: return TrailSizeSolid;
                case PlumeKind.Kerolox: return TrailSizeKerolox;
                case PlumeKind.Hydrolox: return TrailSizeHydrolox;
                case PlumeKind.Methalox: return TrailSizeMethalox;
                case PlumeKind.Hypergolic: return TrailSizeHypergolic;
                case PlumeKind.Nuclear: return TrailSizeNuclear;
                default: return 0.5f;
            }
        }

        public static void Apply(MaterialPropertyBlock block)
        {
            block.SetColor("_SunlitColor",
                new Color(SunlitBrightness, SunlitBrightness * 0.99f, SunlitBrightness * 0.97f));          // the colour of the sun at the smoke's place: ScattererSun.ApplyAt
            block.SetColor("_ShadowColor", Color.Lerp(
                new Color(0.95f, 0.96f, 0.99f), new Color(0.30f, 0.34f, 0.42f), ShadowDarkness));

            // The tint dials interpolate from a grey of the SAME luminance, so changing hue
            // doesn't also change brightness. Both then blend towards the scene's own light
            // probe: the fixed colours were picked under one lighting condition and stayed
            // that way at sunset and on other bodies. Blend, not replace - the probe can be
            // very dim, and ambient is all that keeps the shaded side off black.
            Color skyBase = Color.Lerp(
                new Color(0.72f, 0.72f, 0.72f), new Color(0.55f, 0.68f, 0.92f), SkyTint);
            Color groundBase = Color.Lerp(
                new Color(0.46f, 0.46f, 0.46f), new Color(0.46f, 0.47f, 0.44f), GroundTint);
            // Guarded. RenderSettings only carries a split sky/ground ambient when the
            // scene is in trilight mode; in flat-ambient mode those two read near black,
            // and following them at full blend would drain the shaded side to nothing.
            // Fall back to the hand-picked pair when the probe has nothing to say.
            Color probeSky = RenderSettings.ambientSkyColor;
            Color probeGround = RenderSettings.ambientGroundColor;
            float probeBlend = (probeSky.grayscale + probeGround.grayscale) > 0.02f
                ? SceneAmbientBlend : 0f;
            Color skyFinal = Color.Lerp(skyBase, probeSky, probeBlend);
            Color groundFinal = Color.Lerp(groundBase, probeGround, probeBlend);
            if (AnalyticAmbient > 0.001f)
            {
                Vector4 sv = Shader.GetGlobalVector("_SmokeSunDir");
                Vector4 uv = Shader.GetGlobalVector("_SmokeUpDir");
                Vector3 toSun = new Vector3(sv.x, sv.y, sv.z);
                Vector3 up = new Vector3(uv.x, uv.y, uv.z);
                if (toSun.sqrMagnitude > 0.0001f && up.sqrMagnitude > 0.0001f)
                {
                    float elev = Vector3.Dot(toSun.normalized, up.normalized);
                    float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.12f, 0.35f, elev));
                    float twi = Mathf.Exp(-Mathf.Pow((elev - 0.03f) / 0.16f, 2f));
                    Color zenith = Color.Lerp(new Color(0.03f, 0.04f, 0.09f), new Color(0.36f, 0.56f, 1.0f), day);
                    Color horizon = Color.Lerp(new Color(0.05f, 0.06f, 0.10f), new Color(0.80f, 0.86f, 0.97f), day);
                    Color skyA = Color.Lerp(horizon, zenith, 0.55f);
                    skyA = Color.Lerp(skyA, new Color(1.0f, 0.58f, 0.36f), 0.40f * twi);
                    float skyLevel = Mathf.Lerp(0.30f, 1.0f, day);
                    skyA = new Color(skyA.r * skyLevel, skyA.g * skyLevel, skyA.b * skyLevel);
                    float sunOnGround = Mathf.Clamp01(elev * 1.2f + 0.1f);
                    float bounce = 0.22f + 0.78f * sunOnGround;
                    Color groundA = new Color(0.40f, 0.44f, 0.30f);
                    groundA = Color.Lerp(groundA, new Color(0.55f, 0.36f, 0.26f), 0.5f * twi);
                    groundA = new Color(groundA.r * bounce, groundA.g * bounce, groundA.b * bounce);
                    skyFinal = Color.Lerp(skyFinal, skyA, AnalyticAmbient);
                    groundFinal = Color.Lerp(groundFinal, groundA, AnalyticAmbient);
                }
            }
            block.SetColor("_AmbientSkyColor", skyFinal);
            block.SetColor("_AmbientGroundColor", groundFinal);
        }
    }
}

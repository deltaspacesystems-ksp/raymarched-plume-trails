using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // Live-tunable parameters, driven from the in-flight slider window.
    //
    // Mostly shader values: these can't be pushed with Shader.SetGlobalFloat - a
    // material's own values win over globals - so SmokeVolumeGroup applies them to its
    // property block every frame. Defaults mirror the shader's Properties block; keep
    // them in sync. The simulation knobs at the bottom are read by LaunchSmokeController
    // instead, and live here so everything tunable is in one place.
    public static class SmokeTuning
    {
        public static float Density = 2.0f;
        public static float Absorption = 1.5f;

        // Surface bumpiness. Above ~0.9 it shreds the volume into flat scraps.
        public static float DetailStrength = 0.72f;
        // 0 = detail adds and subtracts density, 1 = add only. In the saturated core there
        // is no headroom to add, so the symmetric form only ever carved holes - patchy.
        public static float DetailBias = 1.0f;
        // 0 = noise frequency fixed in world space, 1 = it tracks puff size. Size-tracking
        // rescales the field as puffs grow, which makes the pattern swim.
        public static float SizeFreqTracking = 0.0f;
        public static float EdgeErosionStrength = 0.85f; // frays the outer rim
        // Fine grain, separate from DetailStrength: detail makes the lobes, grain textures
        // between them. Grain finer than a march step averages out and flickers.
        public static float GrainStrength = 0.7f;
        public static float GrainScale = 0.8f;

        // The warp is what makes LUMPS; DetailStrength only roughens the surface, because
        // coverage has headroom just in the thin shell around the capsule. Strength is how
        // deep, scale is how big (lower = bigger lobes).
        public static float SilhouetteWarpStrength = 18.0f;
        public static float SilhouetteWarpScale = 0.07f;
        // Swirl on top of the warp.
        public static float VortexScale = 0.2f;
        public static float VortexStrength = 0.8f;
        // Lower = bigger features.
        public static float SilhouetteNoiseScale = 0.09f;

        // Ambient, washout and shadow darkness all set the same thing - lit-to-shaded
        // contrast - so they have to be moved together. Pushing ambient and washout up at
        // once clips the whole volume to white, which reads as flat AND glaring.
        // Lowered from 0.20 alongside MultiScatterIntensity (27 IX, same "za plaski"
        // report): the two together were the flattening culprit, not shadow strength
        // itself (already at 1.0, the ceiling).
        public static float AmbientFloor = 0.15f;        // lower = deeper shadows
        // Fraction of the real extinction the shadow ray sees. Lower = softer gradient.
        // 0.12 was near the top of the range and that is the binary regime: exp() over a
        // multi-metre step is then effectively lit-or-black, so every density boundary
        // becomes a hard edge and the sunlit side breaks into dark blotches that read as
        // shadows which are not there. Halved after an offscreen A/B at matched settings.
        // Contrast lost here is better bought back with ShadowDarkness, which shapes the
        // final shading, than by making the exponential harsh again.
        public static float ShadowExtinction = 0.06f;

        // How far the shadow ray reaches. Must be comparable to the cloud's own thickness,
        // or a ray dies inside the lobe it started in and lobes never shadow each other.
        public static float LightReach = 65f;
        public static float ShadowStrength = 1.0f;       // 0 = flat, 1 = full self-shadowing
        // 0 = near-white shaded side, 1 = deep blue-grey.
        public static float ShadowDarkness = 0.62f;
        // 0 makes SmoothMax fall through to max(), whose gradient jumps at every capsule
        // joint - and each joint then renders as a transverse rib, i.e. pancakes.
        public static float SpineBlend = 0.30f;

        // Both animate the noise; both belong at zero. Smoke that has stopped moving must
        // not keep crawling. ScrollSpeed lives here because the material had a non-zero
        // value baked in, which beat the shader's own default.
        public static Vector3 ScrollSpeed = Vector3.zero;
        public static float NoiseAnimation = 0f;

        // Large-scale density variation - soft dark patches. Scale is 1/metres.
        public static float MacroNoiseScale = 0.02f;
        public static float MacroStrength = 0.0f;

        // Washout stands in for the multiple scattering a single-scattering march can't
        // produce; it's what keeps real launch smoke bright and low-contrast.
        public static float Washout = 0.12f;
        public static float WashoutDesaturate = 0.18f;
        // Tint dials rather than raw colours, so they stay usable from sliders. Both at 0
        // now: the ambient is meant to come from the SCENE, split into sky and ground, and
        // adding our own blue and green on top of that is what made the smoke look tinted
        // rather than lit. These are only the fallback for when the scene has no usable
        // ambient probe.
        public static float SkyTint = 0f;
        public static float GroundTint = 0f;

        // Scattering. This group decides whether the smoke reads as a thick volume or as a
        // lit surface. ForwardScatterG is the Henyey-Greenstein eccentricity: high gives a
        // bright rim towards the sun, 0 is uniform and flat.
        public static float ForwardScatterG = 0.75f;
        public static float ScatterIntensity = 1.6f;
        // Multiple scattering, faked with a second wide lobe. It's what makes a cloud glow
        // from within instead of looking like a shell.
        public static float MultiScatterG = 0.27f;
        // Cut from 4.0 (27 IX): this fakes multi-scatter as a second wide, nearly
        // omnidirectional lobe, and at 4x it was strong enough to wash the shadowed side
        // up close to the lit side regardless of ShadowStrength/ShadowDarkness - the
        // "za plaski" report. Lower value leaves it doing its job (glow from within
        // instead of a hard shell) without erasing the lit/shadowed contrast that reads
        // as a round volume rather than a flat card.
        public static float MultiScatterIntensity = 2.5f;
        // Darkens the sun-facing side of a billow, which stops it reading as a flat disc.
        public static float PowderStrength = 0.5f;
        public static float SkyTintStrength = 0.5f;
        // How far ambient follows the scene's light probe instead of the fixed colours.
        public static float SceneAmbientBlend = 1.0f;

        // Ambient occlusion towards the sky. Without it a crevice between two lobes gets
        // as much fill light as the lobe tops, which flattens the form.
        public static float SkyOcclusionDistance = 45f;
        public static float SkyOcclusionStrength = 0.75f;

        // Ambient kept on the night side of the terminator. Night smoke is dim, not
        // black - there is still skyglow and moonlight.
        public static float NightAmbient = 0.18f;
        // Metres over which the terminator softens. Also sets how gradually a climbing
        // plume comes back into sunlight after the ground below has gone dark.
        public static float TerminatorSoftness = 4000f;

        // Shadow the cloud casts onto the terrain and the vessel. 0 disables the pass
        // outright, which is also the first thing to try if it costs too much: it is a
        // screen-covering march, so it is paid per pixel rather than per volume.
        // How much softer a thinned section's edge becomes. 1 = the same hard falloff as
        // launch smoke, lower = the boundary spreads out. This is what separates mist from
        // a solid slab; see SolidityOf in the shader.
        public static float ThinEdgeSoftness = 0.45f;

        // How much interior detail survives thinning. 0 = the high bloom is smooth haze,
        // 1 = it keeps the same cauliflower as smoke at the pad.
        public static float ThinDetailFade = 0.25f;

        // OFF by default (2026-09-27). Two independent, confirmed problems on the same
        // day: (1) the shadow reads as a row of separate dark dots on the ground instead
        // of one continuous streak - the "row of ovals" bug from 7 IX evidently was not
        // fully fixed, or regressed since. (2) worse, at least once the WHOLE screen went
        // black - terrain, sky and ship all gone, only our own smoke and the UI left
        // visible - which is consistent with this pass (Blend DstColor Zero, multiplying
        // the framebuffer) writing near-black across a much larger area than it should,
        // rather than just under the plume. Confirmed independent of the quality governor
        // and of temporal upscaling (both ruled out by testing), so the march itself -
        // EmptyDistance/SpineDistance's group culling, most likely under a large MERGED
        // spine - is the remaining suspect. Not something to chase blind without a GPU
        // frame debugger on a release day. The pass is a total no-op below 0.001 (see the
        // early-out in HalfResSmokeRenderer.BuildShadowCast), so this fully disables it
        // rather than just fading it. Re-enable once the march bug is actually found.
        public static float ShadowCastStrength = 0f;
        public static float ShadowCastDistance = 2500f;
        public static float ShadowCastSteps = 16f;
        // 0 off, 1 = tint everything the pass reaches, 2 = tint only non-sky pixels.
        // Verbose per-second logging. Off by default - every line is a Debug.Log with a
        // stack trace and a disk write, landing exactly in the window where frame drops
        // get reported. Lives here rather than on the controller so the debug menu can
        // reach it without holding a reference to a per-vessel module.
        public static bool DebugLogging = false;

        // Emit only from solid rocket motors. ON by default: dense white smoke is what
        // aluminised solid propellant does, and liquid engines emitting the same column
        // was the single biggest source of wrong-looking trails. Liquid-engine smoke is
        // gated off, not deleted - clear this to bring it back.
        public static bool SrbOnly = true;

        // Exponent on how a spreading solid-motor trail thins. 2 is mass conservation along
        // a line (density ~ 1/r^2, optical depth ~ 1/r); 0 turns thinning off and the old
        // column stays as dense as the new. Eased to 1.5 (27 IX): full mass-conservation
        // thinning combined with the altitude bloom made the trail read as vanishing too
        // quickly right behind the vehicle instead of thinning gradually - real launch
        // smoke stays visibly opaque for longer than pure 1/r^2 predicts, because it is
        // not actually a 1-D line source once it billows this wide.
        public static float SrbThinningPower = 1.5f;

        // --- solid-motor plume shape ---------------------------------------------------

        // Seconds over which the spawn point follows a change in nozzle direction or in
        // flight path. Short = it whips with every attitude change; long = it lags a turn
        // and the base of the plume stops lining up with the nozzle.
        public static float SrbTurnSmoothing = 0.30f;

        // How hard the relative wind bends the plume when the rocket turns. 0 = the smoke
        // always leaves straight down the nozzle axis; 1 = physical-ish; higher exaggerates
        // it, so the plume leaves along the flame and is then swept sideways as if nothing
        // were pushing it out of the nozzle.
        public static float SrbCrossflowBend = 2.0f;

        // Draw the plume from the nozzle itself, on a curve through the flame, instead of
        // starting it a jet-length behind the engine.
        public static bool SrbRootEnabled = true;
        // Radius of the plume at the nozzle, as a fraction of the emission radius.
        public static float SrbRootRadius = 0.5f;

        // Multiplier on how fast the trail spreads, and the length of the slow near-field
        // phase before turbulent spreading takes over (seconds).
        public static float SrbSpreadRate = 1.0f;
        // Cut from 0.6 (27 IX): at high altitude/speed the jet phase still covered a
        // long enough physical stretch (velocity * t0) to look like the trail snaps from
        // thin to full width instead of tapering. Shortening the near-straight phase
        // hands more of the visible taper to the t^2/(t+t0) curve's own gradual middle
        // section instead of a short jet-phase segment followed by a sharp knee.
        public static float SrbJetPhaseTime = 0.35f;

        // How long a motor keeps shedding thin, decaying smoke after it goes quiet, in
        // seconds. 0 disables the tail and reproduces the old instant cutoff.
        public static float SrbTailTime = 4f;

        // --- quality / performance ------------------------------------------------------
        // 0 Low, 1 Medium, 2 High, 3 Ultra. High is what shipped before presets existed.
        public static int QualityPreset = 2;
        // Let the frame-time governor trade resolution and sample counts down on demand.
        public static bool AutoQuality = true;
        // Frame time the governor defends, in milliseconds (22 = ~45 fps).
        public static float TargetFrameMs = 22f;

        // Accumulate detail across frames when the smoke renders below screen resolution
        // (Low, Medium, or after the governor has stepped down). It never runs at full
        // resolution, so High and Ultra look exactly as they did without it.
        // OFF by default (2026-09-27). The governor can legitimately drop resolution
        // below 100% even on High under load (exactly what happened: a background OSMRoads
        // tile build stalled a frame, the governor stepped down for a few seconds), and
        // that is enough to arm this path - which then blanked the whole screen to black
        // for as long as it stayed armed, recovering on its own the moment the governor
        // stepped back up. Real, reproducible, and not something to debug blind without a
        // GPU frame debugger on a release day. Left in the code, opt-in only, until it can
        // be diagnosed properly.
        public static bool TemporalUpscale = false;
        // How much of the previous frame is kept, 0..1. Higher = smoother and sharper at
        // rest but slower to react; lower = more shimmer, less ghosting.
        public static float TemporalFeedback = 0.88f;

        public static float ShadowCastDebug = 0f;

        // Brightness of the lit side. The shader's default is already at the ceiling,
        // which leaves shading no range to work in.
        public static float SunlitBrightness = 0.88f;

        // ---- simulation, read by LaunchSmokeController (not shader values) ----

        // Retropropulsion umbrella. Firing into the airstream, the exhaust cannot simply
        // stream ahead: it meets the oncoming air, stagnates where its momentum balances
        // the freestream, and turns radially outward into a canopy that the airstream then
        // sweeps back around the vehicle.
        //
        // Mechanically this is the same thing as the ground cloud - an axial jet hitting a
        // barrier and converting to radial spread - except the barrier is the air rather
        // than the pad, which is why the numbers here look like the ground cloud's.
        // Slow wander in the column's thickness. See ColumnWander in SmokeVolumeGroup.
        public static float ColumnWander = 0f;

        // Puff size and spawn standoff, both in METRES - so both are tied to the scale of
        // the system being flown. The defaults were tuned on stock Kerbin; on a real-scale
        // system the rockets are several times larger and a trail sized for stock reads as
        // far too thin next to the vehicle.
        // Horizontal drift every puff converges to, in m/s. Coherent and cumulative, so
        // small numbers travel a long way over a 150s life - see the note in Tick.
        public static float WindSpeed = 0f;

        // Reference: Space Shuttle's twin SRBs - the plume billows out to many times the
        // booster's own diameter within the first couple of seconds off the nozzle, not
        // over a slow multi-second ramp. 18 was tuned for a slimmer trail; this is closer
        // to that reference. See also SrbJetPhaseTime and clusterStartSize below.
        public static float MaxPuffSize = 34f;
        public static float SpawnOffset = 15f;

        // OFF by default. Retropropulsion is a liquid-engine manoeuvre, and in SRB-only
        // mode the detector's only remaining effect was a false positive: a hard
        // pitch-over could swing the nozzle close enough to the flight path to trip it,
        // and the plume was then flung sideways and ahead as if the rocket were on fire.
        public static bool UmbrellaEnabled = false;
        // Radial speed given to a puff as it stagnates. Sets how wide the canopy opens.
        public static float UmbrellaSpread = 45f;
        // Speed of the jet leaving the nozzle into the airstream, before it stagnates.
        public static float UmbrellaJet = 90f;
        // How much of the vessel's OWN speed the canopy is swept back at. 1 means the
        // stagnated gas is fully at rest in the air, which is the physical case; lower
        // keeps it hanging with the vehicle for longer.
        public static float UmbrellaBackflowFraction = 0.9f;
        // Extra metres the spawn point is pushed AHEAD of the vehicle while retro-burning,
        // standing in for how far the plume penetrates before it stagnates. Without a
        // standoff the canopy forms on top of the vessel instead of ahead of it.
        public static float UmbrellaStandoff = 25f;

        // Altitude plume bloom - the "jellyfish". As ambient pressure falls the nozzle is
        // increasingly underexpanded, so the exhaust stops being a column and flares into a
        // huge translucent bell. It is the SAME plume as the launch column, not a second
        // effect: driven by one continuous pressure term, a real ascent passes smoothly
        // from one look to the other. Two discrete modes would visibly switch mid-flight.
        public static bool JellyfishEnabled = true;
        // How the plume thins as it expands, as an exponent on the size boost.
        //
        // Derived rather than dialled in by hand, because the two are not independent: the
        // same exhaust spread over a plume N times wider fills N^3 the volume, so density
        // falls as 1/N^3, while the path a ray takes through it only grows as N. Optical
        // depth therefore goes as 1/N^2 - which is why a real high-altitude plume is
        // enormous and see-through at the same time.
        //
        // 2 is that physical value. Lower keeps more of the milky look, 0 disables
        // thinning entirely and gives back a fat opaque column.
        public static float JellyfishThinningPower = 2.0f;
        // Radial speed at full bloom. This is what opens the bell.
        public static float JellyfishSpread = 130f;
        // Seconds for the bell to open. Deliberately short and separate from growthTime:
        // at orbital speed even a few seconds is tens of kilometres of trail, and the
        // plume is supposed to flare right behind the nozzle.
        public static float JellyfishExpandTime = 1.5f;
        // Puff size multiplier at full bloom. Note the sign: the plume gets BIGGER with
        // altitude, where the old altitude fade shrank it.
        // Calibrated against Avalanche's SolidSmoke rather than set by eye. Its particle
        // growth curve (logGrowScale) runs from 1x at sea level to 5x in vacuum; 150x was
        // a guess that overshot by a factor of thirty and produced the balloons and giant
        // spheres. A solid motor also burns out around 40-50km, well before the regime
        // where anything like a jellyfish bloom would form.
                // Multiplier on the EXCESS over the physical sqrt(pressure ratio) curve - see
        // PlumeBloomWidth in LaunchSmokeController. 1 = the honest physical relation with
        // no exaggeration; raise it if that still reads too subtle next to a reference
        // photo once actually seen in-game.
        public static float JellyfishSizeBoost = 1.0f;

        // Ambient pressure (kPa) below which the bloom stops growing further. Real physics
        // has no such floor - an underexpanded jet keeps widening all the way to vacuum -
        // this exists purely so the box, sample count and puff density stay in a sane
        // range once the air is functionally gone. 0.05 kPa is close to where a real-scale
        // SRB burns out anyway, so it is rarely the limiting factor in practice.
        public static float PlumeBloomPressureFloor = 0.05f;
        // Thin air means a thin plume, but not an absent one - the jellyfish is faint and
        // translucent, so alpha fades toward this floor rather than to zero.
        public static float JellyfishAlphaFloor = 0.22f;

        // Whether to emit at all on a body with no atmosphere. Off: this is a SMOKE
        // trail, and smoke needs air - the exhaust has nothing to entrain and nothing to
        // hold it in a column, so a dense plume on the Mun reads as a bug, which is
        // exactly how it was reported. On for anyone who wants the look anyway.
        //
        // Keyed on whether the BODY has an atmosphere, not on the local pressure: above
        // Kerbin's atmosphere the pressure is zero too, and gating on that would delete
        // the high-altitude bloom just where it is most worth seeing.
        public static bool AirlessBodyPlume = false;

        public static void Apply(MaterialPropertyBlock block)
        {
            block.SetFloat("_Density", Density);
            block.SetFloat("_Absorption", Absorption);
            block.SetFloat("_DetailStrength", DetailStrength);
            block.SetFloat("_DetailBias", DetailBias);
            block.SetFloat("_SizeFreqTracking", SizeFreqTracking);
            block.SetFloat("_EdgeErosionStrength", EdgeErosionStrength);
            block.SetFloat("_GrainStrength", GrainStrength);
            block.SetFloat("_GrainScale", GrainScale);
            block.SetFloat("_SilhouetteWarpStrength", SilhouetteWarpStrength);
            block.SetFloat("_SilhouetteWarpScale", SilhouetteWarpScale);
            block.SetFloat("_VortexScale", VortexScale);
            block.SetFloat("_VortexStrength", VortexStrength);
            block.SetFloat("_SilhouetteNoiseScale", SilhouetteNoiseScale);
            block.SetFloat("_AmbientFloor", AmbientFloor);
            block.SetFloat("_SpineBlend", SpineBlend);
            block.SetVector("_ScrollSpeed", new Vector4(ScrollSpeed.x, ScrollSpeed.y, ScrollSpeed.z, 0f));
            block.SetFloat("_NoiseAnimation", NoiseAnimation);
            block.SetFloat("_MacroNoiseScale", MacroNoiseScale);
            block.SetFloat("_MacroStrength", MacroStrength);
            block.SetFloat("_ShadowExtinction", ShadowExtinction);
            block.SetFloat("_SkyOcclusionDistance", SkyOcclusionDistance);
            block.SetFloat("_SkyOcclusionStrength", SkyOcclusionStrength);
            block.SetFloat("_LightMarchDistance", LightReach);
            block.SetFloat("_ShadowStrength", ShadowStrength);
            block.SetColor("_SunlitColor",
                new Color(SunlitBrightness, SunlitBrightness * 0.99f, SunlitBrightness * 0.97f));
            block.SetColor("_ShadowColor", Color.Lerp(
                new Color(0.95f, 0.96f, 0.99f), new Color(0.25f, 0.34f, 0.57f), ShadowDarkness));

            block.SetFloat("_ForwardScatterG", ForwardScatterG);
            block.SetFloat("_ScatterIntensity", ScatterIntensity);
            block.SetFloat("_MultiScatterG", MultiScatterG);
            block.SetFloat("_MultiScatterIntensity", MultiScatterIntensity);
            block.SetFloat("_PowderStrength", PowderStrength);
            block.SetFloat("_ThinEdgeSoftness", ThinEdgeSoftness);
            block.SetFloat("_ThinDetailFade", ThinDetailFade);
            block.SetFloat("_ShadowCastDebug", ShadowCastDebug);
            block.SetFloat("_NightAmbient", NightAmbient);
            block.SetFloat("_TerminatorSoftness", TerminatorSoftness);
            block.SetFloat("_SkyTintStrength", SkyTintStrength);
            block.SetFloat("_Washout", Washout);
            block.SetFloat("_WashoutDesaturate", WashoutDesaturate);

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
            block.SetColor("_AmbientSkyColor", Color.Lerp(skyBase, probeSky, probeBlend));
            block.SetColor("_AmbientGroundColor", Color.Lerp(groundBase, probeGround, probeBlend));
        }
    }
}

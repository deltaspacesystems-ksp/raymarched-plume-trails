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
        // Overall smoke opacity, applied at composite: 1 = as rendered, 0 = invisible.
        public static float Opacity = 1.0f;
        public static float Density = 3.0f;
        public static float Absorption = 1.5f;

        // Surface bumpiness. Above ~0.9 it shreds the volume into flat scraps.
        public static float DetailStrength = 0.72f;
        // 0 = detail adds and subtracts density, 1 = add only. In the saturated core there
        // is no headroom to add, so the symmetric form only ever carved holes - patchy.
        public static float DetailBias = 1.0f;
        // 0 = noise frequency fixed in world space, 1 = it tracks puff size. Size-tracking
        // rescales the field as puffs grow, which makes the pattern swim.
        // Now 0.5 (28 IX): at 0 the noise wavelength is fixed in metres, so a big grown
        // puff is many wavelengths across and the lumps average out to a smooth blob -
        // small puffs looked great, big ones had no structure. Half tracking gives big
        // puffs bigger lumps; growth past the limit is now slow (SrbPostLimitGrowth),
        // which keeps the swim this trades against small.
        public static float SizeFreqTracking = 0.0f;
        public static float EdgeErosionStrength = 0.85f; // frays the outer rim
        // Fine grain, separate from DetailStrength: detail makes the lobes, grain textures
        // between them. Grain finer than a march step averages out and flickers.
        public static float GrainStrength = 0.7f;
        public static float GrainScale = 0.8f;

        // The warp is what makes LUMPS; DetailStrength only roughens the surface, because
        // coverage has headroom just in the thin shell around the capsule. Strength is how
        // deep, scale is how big (lower = bigger lobes).
        // Raised 28 IX ("looks like a cone with holes, not a cloud"), then partly rolled
        // back the same evening: the full jump to 26/1.4 (below) combined with
        // NoiseAnimation produced a flat "blade" silhouette that flickered/popped in and
        // out - almost certainly the warp pushing the capsule union past where it stays
        // coherent, made worse by a moving warp field on top of it. Smaller, safer step
        // up from the original 18/0.8 instead of the full jump.
        public static float SilhouetteWarpStrength = 20f;
        public static float SilhouetteWarpScale = 0.07f;
        // Swirl on top of the warp. Eased back alongside the warp, same reason.
        public static float VortexScale = 0.2f;
        public static float VortexStrength = 1.0f;
        // Lower = bigger features.
        public static float SilhouetteNoiseScale = 0.09f;

        // Ambient, washout and shadow darkness all set the same thing - lit-to-shaded
        // contrast - so they have to be moved together. Pushing ambient and washout up at
        // once clips the whole volume to white, which reads as flat AND glaring.
        // Lowered from 0.20 alongside MultiScatterIntensity (27 IX, same "za plaski"
        // report): the two together were the flattening culprit, not shadow strength
        // itself (already at 1.0, the ceiling).
        // 29 IX: shading retuned against the NVIDIA Mie cloud figure using the offscreen
        // preview. Their shade is NEUTRAL grey, soft and bright (multiple scattering fills
        // shadows); ours was dark blue with hard-edged patches. AmbientFloor, ShadowExtinction,
        // LightReach, ShadowStrength, ShadowDarkness (and its dark-end colour in Apply), Washout,
        // LightSizeScaling and DetailShadeStrength were moved together - they all set the same
        // lit-to-shaded contrast, so change them together.
        public static float AmbientFloor = 0.35f;        // lower = deeper shadows
        // Fraction of the real extinction the shadow ray sees. Lower = softer gradient.
        // 0.12 was near the top of the range and that is the binary regime: exp() over a
        // multi-metre step is then effectively lit-or-black, so every density boundary
        // becomes a hard edge and the sunlit side breaks into dark blotches that read as
        // shadows which are not there. Halved after an offscreen A/B at matched settings.
        // Contrast lost here is better bought back with ShadowDarkness, which shapes the
        // final shading, than by making the exponential harsh again.
        public static float ShadowExtinction = 0.025f;

        // How far the shadow ray reaches. Must be comparable to the cloud's own thickness,
        // or a ray dies inside the lobe it started in and lobes never shadow each other.
        public static float LightReach = 45f;
        // Grows LightReach and SkyOcclusionDistance with the local puff size (exponent on
        // size relative to the 18 m reference, capped at 8x). 0 = fixed metres, which is
        // fine for small puffs and shades a big bloomed one flat, since the shadow ray
        // never leaves the lobe it started in.
        public static float LightSizeScaling = 0.2f;
        // Strength of the one-sample full-detail tap toward the sun that lets surface bumps
        // shade each other (the cheap light march has no noise, so texture was flat).
        public static float DetailShadeStrength = 0.25f;
        // Hierarchical billows whose lattice follows the local puff radius (see
        // ApplyDetailBuildup): 0 = old single fixed-size detail, 1 = fully billows. Cell is
        // the largest lobe size relative to the radius.
        public static float BillowMix = 1.0f;
        public static float BillowCell = 0.9f;
        // Rim erosion with the billow noise (breaks the edge into detached clumps and wisps) and
        // rim feathering. The cheap shadow density erodes the same way, so the shadow ray does
        // not see phantom mass where the rim was carved away.
        // 29 IX, third pass: erosion 0.5 -> 0.1 and warp 20 -> 30 with a size-relative amplitude.
        // Strong erosion made ragged, oddly shaped detached fragments in flight (they read as ugly,
        // not as cumulus). The shape now comes from the silhouette warp (lobes), with only a
        // little erosion left to feather the rim.
        public static float BillowAmp = 0.2f;
        public static float BillowErode = 0.0f;
        // Sphere lobes: a Worley field of spheres sized from the local radius, added around the
        // visible surface (see ApplyDetailBuildup). Kept moderate on purpose: stronger settings
        // put density where the surface is almost empty and produce flat detached flakes, because
        // the visible surface of a dense plume is a thin shell at very low coverage. Real round
        // silhouettes need the surface itself displaced (a union of spheres in the distance field),
        // not density added on top - see the todo.
        public static float LobeMix = 0.0f;
        // ---- KSA-inspired rendering (29 IX; ideas only, own implementation) ----
        // Threshold erosion (Horizon Zero Dawn): the capsule's LINEAR radial profile is compared
        // against multi-octave noise, so the visible surface is displaced by the noise itself -
        // real round lobes and detached clumps - while the core stays solid. Depth = how deep into
        // the radius the noise may cut, Sharp = edge softness, Boost = scales the profile (the linear
        // profile is smaller than the old smoothstep coverage, so 1.3 restores the body size).
        // Supersedes LobeMix (now 0) and BillowErode (now 0), which did the same job badly.
        public static float ErodeMix = 1.0f;
        public static float ErodeDepth = 0.3f;
        public static float ErodeSharp = 0.75f;
        public static float ErodeBoost = 1.3f;
        // Lobes standing out past the capsule (fraction of the radius, 0 = off) and a bright additive rim.
        public static float EdgeProtrusion = 0f;
        public static float RimLight = 0f;
        public static float EdgeWispStretch = 0.2f;
        public static float EdgeSwirl = 1.0f;
        // Liquid-engine exhaust is water vapour: this floor on its lit-ness and this pull toward white make it
        // brighter and whiter than SRB smoke. FineDetail adds a small-scale octave so big puffs are not smooth.
        // Bake the noise volume from periodic gradient (Perlin / simplex-like) noise instead of value noise: isotropic,
        // no blocky lattice-aligned streaks. Applied when the volume is baked, i.e. at the next game start.
        // Version of the saved tuning.cfg layout. When a build changes the DEFAULT of a value (see
        // SmokeTuningConfig.ResetBelow), an older saved file must not keep pinning the old one.
        public static int ConfigVersion = 3;
        public static bool GradientNoise = true;
        // Aerial perspective: how strongly far-away smoke is pulled toward the sky colour, and the distance (m, at sea
        // level) at which 63% of the contrast is gone. The haze thins with the air density at the camera.
        public static float AerialStrength = 0.6f;
        public static float AerialDistance = 14000f;
        // Heat haze over the nozzle: screen-space refraction of what is behind the hot exhaust.
        public static bool HeatHaze = true;
        public static float HeatHazeStrength = 0.5f;      // overall, also scaled by throttle and air density
        public static float HeatHazeAmount = 0.2f;        // displacement size
        public static float HeatHazeLengthDiameters = 14f; // length of the hot region, in nozzle diameters
        public static float HeatHazeScale = 1.6f;         // noise cell size (m)
        public static float HeatHazeSpeed = 30f;          // m/s, how fast it streams away from the nozzle
        public static float SteamBright = 0.9f;
        public static float SteamWhite = 0.7f;
        public static float FineDetail = 0.5f;
        // Protruding edge made of round lobes (flat-looking discs) instead of wisps; off by default.
        public static bool EdgeRoundLobes = false;
        // Multiple scattering (Wrenninge / Nubis): indirect light = BeerMS(density toward the sun)
        // times a dark-edge factor (small at the rim, 1 inside). 0 = old constant floor.
        public static float MSMix = 1.0f;
        public static float DarkEdge = 0.6f;
        // Sky ambient occlusion from the radial profile instead of a 3-sample march (cheaper).
        public static bool ProfileAO = true;
        public static float LobeCell = 1.0f;
        // Borrowed from EVE (LGhassen): normal-based diffuse (bumps facing the sun brighter, away
        // darker; the normal comes from the density falling toward the sun) and a glow on thin
        // translucent edges (silver lining), stronger when looking toward the light.
        public static float FacingShade = 0.1f;
        public static float EdgeGlow = 0.6f;
        public static float EdgeFeather = 1.0f;
        // Amplitude of the light-march and primary-march dithers (1 = old full strength).
        // Full strength put a fine stipple over the whole surface; offscreen A/B at 0.15-0.3
        // removed it without visible banding. Raise if terraces show up in game.
        public static float LightJitter = 0.4f;
        public static float MarchJitter = 1.0f;
        // Eased from 1.0 (27 IX): at full strength, litness comes ONLY from the
        // self-shadow march (shadedLit) - zero floor from the phase/ambient-driven
        // flatLit path. On a long, thin, self-occluding column that means a spot the
        // sun geometrically faces can still read as shadowed if the shadow ray has to
        // cross another part of the SAME trail on its way to the sun (confirmed
        // reported: "sunlit side looks like it's in shadow"). Blending in some flatLit
        // gives sunlit-facing regions a brighter floor without discarding self-shadow
        // contrast entirely. Phase math itself was checked and is not inverted.
        public static float ShadowStrength = 0.3f;       // 0 = flat, 1 = full self-shadowing
        // 0 = near-white shaded side, 1 = deep blue-grey.
        // Raised 28 IX alongside the warp strengths - deeper valleys between lobes were
        // one of the three things asked for ("more contrast between billows").
        public static float ShadowDarkness = 0.35f;
        // 0 makes SmoothMax fall through to max(), whose gradient jumps at every capsule
        // joint - and each joint then renders as a transverse rib, i.e. pancakes.
        public static float SpineBlend = 0.30f;

        // ScrollSpeed stays at zero: it translates the fine GRAIN noise field uniformly
        // through world space over time, which would make even long-frozen, fully-settled
        // sections of the trail visibly crawl. Lives here because the material had a
        // non-zero value baked in, which beat the shader's own default.
        public static Vector3 ScrollSpeed = Vector3.zero;
        // NoiseAnimation offsets the DOMAIN WARP sample point over time - it reshuffles
        // which lobes bulge where, not the trail's own silhouette shape in principle. Set
        // to 0.06 for 28 IX ("delikatny ruch/animacja"), rolled straight back to 0 the
        // same evening: right after this AND the warp strength increase went in, the
        // trail started flickering/popping (a lobe visibly there one frame, gone the
        // next). Warp strength is the prime suspect (see above) but this was reverted at
        // the same time and not independently re-tested, so it is not cleared either -
        // try it alone, later, before blaming warp strength exclusively.
        public static float NoiseAnimation = 0f;

        // Large-scale density variation - soft dark patches. Scale is 1/metres.
        public static float MacroNoiseScale = 0.02f;
        public static float MacroStrength = 0.0f;

        // Washout stands in for the multiple scattering a single-scattering march can't
        // produce; it's what keeps real launch smoke bright and low-contrast.
        public static float Washout = 0.15f;
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
        // Mie phase function (HG+Draine blend, Jendersie & d'Eon 2023): one droplet-size
        // dial replaces the hand-tuned two-lobe HG scheme below. Fitted over 5-50 um; the
        // shader clamps to 3-50. Smaller = broader, softer scattering; larger = sharper
        // forward peak. Off falls back to ForwardScatterG/MultiScatterG for A/B comparison.
        public static bool UseMiePhase = true;
        public static float MieParticleDiameter = 12f;
        // Isotropic floor added to the Mie phase as a multiple-scattering stand-in: pure Mie is
        // ~0.002 of its forward peak at 90 degrees, which lit side-on smoke dark grey.
        // Soft ceiling on the phase function (0 = off). Stops the forward lobe from turning the
        // sun-facing side into a mirror-like hotspot; the rest of the phase range is untouched.
        public static float PhaseMax = 0.9f;
        // Anti-tiling of the noise volume: layers sampled through different rotations (free) plus an optional
        // third decorrelated sample (0 = off, 1 = full weight). Costs about +50% noise fetches when on.
        public static float AntiTile = 0.5f;

        // --- REAL PHYSICS mode (experimental, 2 X 2026) --------------------------------------
        // Replaces the hand-tuned puff kinematics with a Lagrangian entrainment model: each puff is a
        // parcel of exhaust that entrains air (Morton-Taylor-Turner), is buoyant in the real atmosphere,
        // is carried by the wind through the entrained air and meets the ground as a wall jet. See
        // Docs/PHYSICS.md. Off by default; the old model stays available for comparison.
        public static bool PhysicsMode = false;
        public static float PhysAlphaJet = 0.055f;        // entrainment coefficient of a momentum jet (Fischer 1979)
        public static float PhysAlphaThermal = 0.25f;     // ... of a buoyant thermal (Turner 1973)
        public static float PhysAmbientTurb = 0.15f;      // m/s entrainment floor from ambient turbulence
        public static float PhysAddedMass = 0.5f;         // virtual-mass coefficient of a sphere
        public static float PhysRestitution = 0.1f;      // rebound off a surface (gas does not bounce like a ball)
        public static float PhysWallJet = 0.9f;           // fraction of the normal speed turned into the wall jet
        public static float PhysGroundFriction = 0.04f;   // wall friction on the wall jet
        public static float PhysExhaustTemp = 2000f;      // K at the nozzle exit (solid motor)
        public static float PhysRadiusScale = 1.0f;       // visual size of the puff relative to its physical radius
        public static float PhysDensityExp = 0.3f;        // how visible smoke thins as it dilutes (1 = linearly with concentration)
        public static float PhysWindRoughness = 0.1f;     // m, surface roughness for the logarithmic wind profile
        public static float PhysMaxDilution = 60f;        // cap on the jet dilution at the release point
        public static bool PhysCollide = true;
        // Parcels that hit the ground / pad / trench leave the main chain and form separate side-cloud chains per
        // outflow direction (set false to keep them in the single trail chain).
        public static bool PhysWallChains = true;
        // Side-cloud branches are broken into lumps where consecutive puffs are further apart than this x their radii (0 = one tube).
        public static float WallLumpGap = 1.1f;
        // Side-cloud puffs closer to the previous kept one than this x the sum of their radii are dropped, so a branch is a
        // string of distinct billows instead of a smooth tube (0 = keep all).
        public static float WallSpacing = 0.7f;
        // Condensation of the water vapour in liquid-engine plumes (see PuffPhysics.CondensedFraction): surface
        // relative humidity, the visibility an uncondensed (invisible-vapour) parcel keeps, and the liquid mass
        // fraction at which the cloud is fully dense.
        public static float PhysHumidity = 0.6f;
        public static float SteamMinVisibility = 0.7f;
        public static float SteamCondRef = 0.012f;            // collide puffs with the scene (pad, trench, buildings)
        public static float PhysCollideBudget = 300f;     // scene raycasts per tick
        // Debug overlay: purple = empty pixels inside the march box, red = rays that ran out of march budget.
        public static bool DebugBox = false;
        // Directional light/shade from the column's own geometry: the side facing the sun is lit, the
        // far side shaded, with a clear terminator line (0 = off). Independent of surface bumps, so it
        // holds at any erosion depth.
        public static float NormalShade = 0.6f;
        public static float MieFloor = 0.45f;
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
        public static float SkyTintStrength = 0.7f;
        // How far ambient follows the scene's light probe instead of the fixed colours.
        public static float SceneAmbientBlend = 1.0f;
        // Analytic sky ambient (30 IX, after the KSA comparison: their shaded side reads as
        // clean sky-blue fill, ours as dull grey). Built from the sun's elevation: blue zenith,
        // paler horizon, warm twilight glow, dim at night, and a ground bounce tinted like grass
        // and scaled by how much sun reaches the ground. 0 = only the scene probe / fixed pair.
        public static float AnalyticAmbient = 0.75f;

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

        // OFF again (28 IX), rolled back within the hour of re-enabling it. Dithering
        // fixed the dotted look (kept in the shader, see fragShadowCast), but right after
        // turning it back on the user reported a NEW bug: the trail size flickering
        // bigger/smaller and the vehicle appearing to "hop". Not confirmed, but the prime
        // suspect: this pass is a screen-covering per-pixel march, the single most
        // expensive thing added tonight, and StaleTipWatchdog in SmokeVolumeGroup.cs
        // force-freezes the live tip and starts a fresh one (back at spawn size) if
        // SetLiveTip goes unrefreshed for LiveTipStaleAfter=0.5s - exactly a
        // bigger-to-smaller snap. A frame hitch from this pass tripping that watchdog
        // fits both symptoms in one mechanism. Disabled again to isolate the variable
        // before chasing it further; re-enable only after retesting confirms or rules
        // this out.
        public static float ShadowCastStrength = 0.7f;
        // optical-depth scale of the cast shadow and the lowest it can get (sky light still reaches the ground)
        public static float ShadowCastDensity = 0.5f;
        public static float ShadowCastFloor = 0.3f;
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

        // Plumes for LIQUID engines too. They run the same solid-jet / physics model as the boosters, but a
        // kerolox / hydrolox / methalox exhaust carries far less condensate than aluminised SRB smoke, so it is
        // drawn smaller and much thinner (scaled by the share of thrust that is liquid, so a mixed cluster
        // blends). 0.35 / 0.3 keeps a clear but slim, translucent column.
        public static bool LiquidPlumes = true;
        public static float LiquidSizeScale = 0.5f;
        // Liquid engines: the nozzle diameter is estimated from the engine's thrust (D = 2.4 m * (F / 2280 kN)^0.4, which
        // fits RS-25, F-1, Merlin and RL10 within the expansion-ratio spread) instead of from the width of the PART, which
        // for a core stage or a cluster is the whole stage. Times this scale.
        public static bool NozzleFromThrust = true;
        // solid motors: nozzle exit relative to the same thrust formula (a real SRB nozzle is a bit smaller than a liquid
        // engine of the same thrust, which has a much larger expansion ratio)
        public static float NozzleSolidScale = 0.8f;
        // Waterfall (engine-flame mod): read the flame's real length and width. The smoke starts at WaterfallHandoff x the
        // flame length (so it continues from the flame instead of overlapping it); the haze wraps WaterfallHazeLength x it.
        public static bool WaterfallIntegration = true;
        public static float WaterfallHandoff = 0.7f;
        public static float WaterfallHazeLength = 1.0f;
        // Volumetric explosions replace the stock blast effect (fireball + smoke cloud, size from the energy)
        public static bool ExplosionsEnabled = true;
        public static float ExplosionScale = 1f;
        public static float ExplosionLife = 1f;
        public static float ExplosionFire = 1f;
        // Physics mode: the smoke's release radius is matched to the flame's width (x this) so it continues the flame
        public static bool WaterfallMatchStart = true;
        public static float WaterfallStartWidth = 0.5f;
        // Physical plumes: how far the exhaust may be diluted by entrained air and still count as visible smoke, as a
        // multiple of the engine kind's own value (SRB 90, kerolox 45, methalox 30, hydrolox 22). 0 = no limit.
        public static float PhysVisibleDilution = 1f;
        // Largest widening of a physical parcel's visible radius relative to its release radius (soft asymptote); keeps the
        // column from being a needle at the engine and fat far behind. 0 = off.
        public static float PhysMaxGrowth = 3f;
        // largest shift of the heat haze, as a fraction of the screen
        public static float HeatHazeMaxShift = 0.006f;
        public static float NozzleThrustScale = 1f;
        public static float LiquidDensityScale = 0.3f;
        // relative to kerolox: liquid hydrogen exhaust is almost pure water vapour, methane burns clean
        public static float HydrogenPlumeVisibility = 0.35f;
        public static float MethanePlumeVisibility = 0.7f;

        // Exponent on how a spreading solid-motor trail thins. 2 is mass conservation along
        // a line (density ~ 1/r^2, optical depth ~ 1/r); 0 turns thinning off and the old
        // column stays as dense as the new. Eased to 1.5 (27 IX): full mass-conservation
        // thinning combined with the altitude bloom made the trail read as vanishing too
        // quickly right behind the vehicle instead of thinning gradually - real launch
        // smoke stays visibly opaque for longer than pure 1/r^2 predicts, because it is
        // not actually a 1-D line source once it billows this wide.
        public static float SrbThinningPower = 1.5f;

        // Sideways/downward kick given to puffs spawned near the ground (see
        // groundImpingementRange/Speed in LaunchSmokeController), simulating exhaust
        // deflecting off the pad. OFF by default (28 IX) after a report of it
        // misbehaving hard - shooting smoke UPWARD - when the vessel sits at some exact
        // angle relative to the pad. Root cause not found yet (the random per-puff
        // lateral direction shouldn't depend on vessel orientation at all, so whatever
        // is happening needs its own investigation) - EXPERIMENTAL, opt-in only until
        // then.
        public static bool GroundImpingementEnabled = false;

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
        // How much the smoke FADES IN along the root from the nozzle to the first puff (0 = uniform, as before,
        // 1 = fully faded at the nozzle). With it, the visible smoke starts about where the flame ends and the
        // Spawn offset slider visibly moves that point.
        public static float SrbRootFade = 0.85f;

        // Multiplier on how fast the trail spreads, and the length of the slow near-field
        // phase before turbulent spreading takes over (seconds).
        public static float SrbSpreadRate = 1.0f;
        // Reversed 27 IX: an earlier cut to 0.35 was backwards. t0 sits in the
        // denominator of t^2/(t+t0) - a SMALLER t0 makes the curve rise FASTER at
        // small t, not slower (checked numerically: t0=0.35 reaches 61% of max width
        // by age 2s, t0=1.0 only 33%). Raised past the original 0.6 as well, paired
        // with the longer growthTime below, to actually flatten the early rise instead
        // of steepening it.
        public static float SrbJetPhaseTime = 4.0f;
        // Fraction of the diffusion rate a puff keeps once it passes its size limit
        // (growthTime). 1 = the old unbounded sqrt(t) spread, 0 = stops dead at the limit.
        public static float SrbPostLimitGrowth = 0.2f;

        // How long a motor keeps shedding thin, decaying smoke after it goes quiet, in
        // seconds. 0 disables the tail and reproduces the old instant cutoff.
        public static float SrbTailTime = 4f;

        // --- quality / performance ------------------------------------------------------
        // 0 Low, 1 Medium, 2 High, 3 Ultra. Low + temporal upscaling is the default (30 IX): it looks best.
        public static int QualityPreset = 0;
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
        public static bool TemporalUpscale = true;
        // How much of the previous frame is kept, 0..1. Higher = smoother and sharper at
        // rest but slower to react; lower = more shimmer, less ghosting.
        public static float TemporalFeedback = 0.88f;

        public static float ShadowCastDebug = 0f;

        // Brightness of the lit side. The shader's default is already at the ceiling,
        // which leaves shading no range to work in.
        public static float SunlitBrightness = 0.78f;

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
        // Nozzle-size scaling: plume size follows the engine's physical diameter (part width
        // across the thrust axis) instead of just the engine count, so a small motor no
        // longer smokes like a giant one. The reference is the diameter that gets exactly
        // MaxPuffSize - roughly a Shuttle-class booster, what the sizes were tuned on.
        // Power 1 = plume width proportional to nozzle diameter.
        public static bool NozzleScaleEnabled = true;
        public static float NozzleReferenceDiameter = 3.7f;
        public static float NozzleScalePower = 1f;
        public static float NozzleScaleMin = 0.1f;
        // Seconds over which the nozzle-size factor eases when the set of firing engines changes (a booster
        // burns out, an engine cuts off). Longer = the trail narrows more gradually instead of in a step.
        public static float NozzleFactorEase = 1.5f;
        // Spine detail falloff with age, in seconds (0 = one global stride as before). A puff older than
        // bandT keeps every 2nd spawn index, older than 3 bandT every 4th, and so on, so points leave the spine
        // one at a time instead of half of them at once.
        public static float SpineBandSeconds = 3f;
        public static float MaxPuffSize = 34f;
        // --- emission density (30 IX, live) --------------------------------------------------
        // Seconds between puffs while flying slowly; gaps between physics frames are filled by
        // interpolation anyway. Lower = denser chain, so single puffs no longer read as separate blobs.
        public static float SpawnInterval = 0.03f;
        // Scales both spacing limits (as a fraction of the puff radius). Lower = puffs packed closer.
        public static float SpawnSpacingScale = 0.6f;

        // --- engine-flame lighting of the smoke (30 IX) --------------------------------------
        // The exhaust is a light source: smoke near the nozzle picks up warm orange light, strongest at
        // the flame and fading with distance (1 / (1 + (d/range)^2)). 0 = off.
        public static float FlameLightStrength = 1.0f;
        // Distance (m) at which the flame light has fallen to half, before the nozzle-size factor.
        public static float FlameLightRange = 25f;
        // Real Unity point lights for the engine flames (lights the pad, ground, rocket and scenery)
        // Flame deflector / pad: water deluge steam and the glowing deflected flame
        public static bool PadWater = true;            // water-deluge steam in the flow that hits a deflector
        public static bool PadWaterAlways = false;      // ... on ANY ground impact, not only where a deflector / trench was detected
        public static float PadWaterFrac = 0.6f;        // mass fraction of water in the deflected flow
        public static float DeflectorFire = 1f;         // glow of the hot deflected flow (0 = off)
        public static bool EngineLights = true;
        public static float EngineLightIntensity = 1.5f;
        public static float EngineLightRange = 1f;
        public static float FlameColorR = 1.0f;
        public static float FlameColorG = 0.52f;
        public static float FlameColorB = 0.16f;
        // --- plume growth (30 IX, all live) --------------------------------------------------
        // Radius of the smoke at the nozzle, before the nozzle-size factor. Small = narrow start.
        public static float StartSize = 1.5f;
        // Seconds for a puff to reach MaxPuffSize. Longer = gentler, more gradual widening.
        public static float GrowthTime = 22f;
        // Shape of the non-SRB growth curve (1-(1-t)^n); only matters outside SRB mode.
        public static float GrowthSharpness = 2f;
        // Below this speed (m/s) puffs keep their full size; above SpeedThinTrailMax they shrink to
        // ThinTrailSizeMult of it (fast flight leaves a thinner trail).
        public static float SpeedBillowMin = 20f;
        public static float SpeedThinTrailMax = 500f;
        public static float ThinTrailSizeMult = 0.8f;
        public static float SpawnOffset = 15f;

        // --- landing cloud (28 IX) -------------------------------------------------------
        // Big, dramatic ground-effect cloud during a powered landing - the Starship/booster
        // "flame diverter" look. Deliberately an EXCEPTION to SrbOnly rather than a reason
        // to reopen it: it bypasses the solid-motor filter only while IsLanding is true
        // (see LaunchSmokeController.IsLanding), so a liquid engine still emits nothing at
        // any other point in flight. Reuses the existing trail/puff system rather than the
        // old raycast-based ground cloud (groundCloudEnabled), which flickered badly and
        // stayed off - a local size/density boost on ordinary puffs is far less code to
        // get wrong. Water/ground/deflector material distinction is a later pass.
        // OFF by default (29 IX). The ray-chain splash rendered as thin, banded domes off to
        // the side of the lander; the dust the user actually liked at the vessel's feet was
        // KerbalFX's. Stays in the code, opt-in, until it is rebuilt as a proper radial
        // dust ring instead of capsule chains.
        public static bool LandingCloudEnabled = false;
        // Altitude below which a descending, firing engine counts as "landing".
        public static float LandingCloudAltitude = 150f;
        // Vertical speed (m/s, negative = falling) below which the vessel counts as
        // descending. Small negative rather than 0, so a hover right at this altitude
        // does not flicker in and out of landing mode.
        public static float LandingCloudVerticalSpeed = -3f;
        // Size of each ground-splash puff (see LaunchSmokeController's isLanding block),
        // as a multiplier the same way groundCloudSizeScale works for the old pad-deflector
        // system. NOT applied to the live tip any more - see below.
        // 0.5, was 2.5 (29 IX): splash puffs grow to MaxPuffSize * engine size factor and
        // are THEN multiplied by this, so 2.5 gave a ~12 m radius cloud from a 0.8 m
        // engine - the "oversized second smoke" next to the correct small young puffs.
        public static float LandingCloudSizeBoost = 0.5f;
        // Reworked (28 IX, second pass): this used to BOOST the live tip's density/size in
        // place, which is exactly what produced the reported "mushroom"/"balloon" - the
        // tip barely moves while hovering to land, so growing it there just inflates a
        // sphere at one point instead of a cloud that spreads. Real ground effect is the
        // opposite of dense: exhaust that has just bounced off a hard surface and is
        // OUTRUNNING the smoke behind it reads as thin and fast, not thick and slow. Now
        // the density of each ground-splash puff instead (see LandingCloudSpreadSpeed
        // below), and defaults far below 1 rather than above it.
        public static float LandingCloudDensityBoost = 0.3f;
        // Outward speed of the splash, m/s at full throttle - this is what makes it a
        // skirt spreading out along the ground rather than a cloud growing in place.
        // Reference: real ground-effect gas is moving fast (it just left a nozzle and
        // bounced), well above the ~15-25 m/s the ordinary launch-pad ground impingement
        // kick uses, which is tuned for a much smaller, grazing deflection instead of a
        // near-vertical impact.
        public static float LandingCloudSpreadSpeed = 90f;
        // Number of FIXED, evenly-spaced rays the splash spreads along, once the
        // exhaust-to-ground raycast connects - not a per-tick random-direction count any
        // more. One puff is added to every ray each tick (see AddGroundPuff/
        // BuildActiveOrderedList), so each ray keeps extending the same streak instead of
        // scattering a new random heading every frame - the "jet stream" look asked for,
        // rather than a puffy cloud.
        public static int LandingCloudPuffsPerTick = 8;
        // How far below the engine the ground raycast looks before giving up. Generous
        // next to LandingCloudAltitude so the splash starts a little before the vessel
        // is technically "on the ground" - real ground effect builds up over the last
        // stretch of the descent, not instantaneously at touchdown.
        public static float LandingCloudRaycastReach = 200f;
        // Multiplier on the splash reach, which itself follows KerbalFX ImpactPuffs
        // (bimo1d, Unlicense): 10 m at low thrust to 28 m at full thrust, scaled up for
        // engines larger than the reference. Ground effect is a close-range phenomenon.
        public static float LandingSplashReachScale = 1f;

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
            block.SetFloat("_LightSizeScaling", LightSizeScaling);
            block.SetFloat("_DetailShadeStrength", DetailShadeStrength);
            block.SetFloat("_BillowMix", BillowMix);
            block.SetFloat("_BillowCell", BillowCell);
            block.SetFloat("_BillowErode", BillowErode);
            block.SetFloat("_BillowAmp", BillowAmp);
            block.SetFloat("_LobeMix", LobeMix);
            block.SetFloat("_ErodeMix", ErodeMix);
            block.SetFloat("_ErodeDepth", ErodeDepth);
            block.SetFloat("_ErodeSharp", ErodeSharp);
            block.SetFloat("_ErodeBoost", ErodeBoost);
            block.SetFloat("_Protrude", Mathf.Clamp01(EdgeProtrusion));
            block.SetFloat("_RimLight", RimLight);
            block.SetFloat("_WispStretch", EdgeWispStretch);
            block.SetFloat("_Swirl", EdgeSwirl);
            block.SetFloat("_SteamBright", SteamBright);
            block.SetFloat("_SteamWhite", SteamWhite);
            block.SetFloat("_FineDetail", FineDetail);
            block.SetFloat("_AerialStrength", AerialStrength);
            block.SetFloat("_RoundLobes", EdgeRoundLobes ? 1f : 0f);
            block.SetFloat("_MSMix", MSMix);
            block.SetFloat("_DarkEdge", DarkEdge);
            block.SetFloat("_ProfileAO", ProfileAO ? 1f : 0f);
            block.SetFloat("_LobeCell", LobeCell);
            block.SetFloat("_FacingShade", FacingShade);
            block.SetFloat("_EdgeGlow", EdgeGlow);
            block.SetFloat("_EdgeFeather", EdgeFeather);
            block.SetFloat("_LightJitter", LightJitter);
            block.SetFloat("_MarchJitter", MarchJitter);
            block.SetFloat("_ShadowStrength", ShadowStrength);
            block.SetColor("_SunlitColor",
                new Color(SunlitBrightness, SunlitBrightness * 0.99f, SunlitBrightness * 0.97f));
            block.SetColor("_ShadowColor", Color.Lerp(
                new Color(0.95f, 0.96f, 0.99f), new Color(0.30f, 0.34f, 0.42f), ShadowDarkness));

            block.SetFloat("_UseMiePhase", UseMiePhase ? 1f : 0f);
            block.SetFloat("_MieParticleDiameter", MieParticleDiameter);
            block.SetFloat("_MieFloor", MieFloor);
            block.SetFloat("_PhaseMax", PhaseMax);
            block.SetFloat("_AntiTile", AntiTile);
            block.SetFloat("_DebugBox", DebugBox ? 1f : 0f);
            block.SetFloat("_NormalShade", NormalShade);
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

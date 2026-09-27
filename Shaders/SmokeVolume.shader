Shader "RaymarchedPlumeTrails/SmokeVolume"
{
    // density texture is baked by SmokeVolumeSplat.compute, fragment shader just samples it
    Properties
    {
        _DepthBiasDistance ("Depth Bias Distance (m)", Float) = 6.0
        _DepthBiasFraction ("Depth Bias Fraction Of Camera Distance", Float) = 0.0
        // Per-material ZTest, separate from _GlobalZTestMode - that one only gates the
        // disabled occlusion-camera branch, whose texture is never bound.
        _ZTestMode ("ZTest Override", Float) = 4
        _NoiseScale ("Detail Noise Scale", Float) = 0.15
        // Zero: smoke that has stopped moving should not keep crawling.
        _ScrollSpeed ("Noise Scroll Speed", Vector) = (0, 0, 0, 0)
        // The second animated term. DomainWarp has its own time rates that _ScrollSpeed
        // does not touch, so zeroing that alone left the volume moving - and because the
        // warp displaces the sample position along the trail axis, animating it produces
        // travelling bulges, i.e. pancakes that scroll.
        _NoiseAnimation ("Noise Animation Rate", Range(0,2)) = 0
        // Strength makes the lumps deeper, scale sets how big they are.
        _DetailStrength ("Detail Noise Strength", Range(0,1)) = 0.72
        // 0 = the detail octave both adds and subtracts density (symmetric, as it always
        // was); 1 = it may only ADD. See the note at its use in ApplyDetailBuildup.
        _DetailBias ("Detail Additive Bias", Range(0,1)) = 1.0
        // How much noise FREQUENCY follows puff size. 0 = fixed in world space (still),
        // 1 = the old size-tracking behaviour (swims as puffs grow). See its uses.
        _SizeFreqTracking ("Size-Tracked Noise Frequency", Range(0,1)) = 0.0

        _VortexScale ("Vortex Warp Scale", Float) = 0.2
        _VortexStrength ("Vortex Warp Strength (m)", Float) = 0.8
        _SilhouetteWarpScale ("Silhouette Warp Scale", Float) = 0.15
        _SilhouetteWarpStrength ("Silhouette Warp Strength (m)", Float) = 7.0
        _SilhouetteNoiseScale ("Silhouette Erosion Noise Scale", Float) = 0.16
        // Source of spiky edges when pushed - at 0 the rim is smooth and rounded.
        _EdgeErosionStrength ("Edge Erosion Strength", Range(0,1)) = 0.85
        // Fine grain, a higher-frequency octave on top of the detail noise. Separate
        // knobs because the two fail in opposite directions: too much detail breaks the
        // volume into scraps, too much grain aliases into sparkle.
        _GrainStrength ("Grain Strength", Range(0,1)) = 0.35
        _GrainScale ("Grain Scale", Float) = 2.4
        _ReferenceRadius ("Reference Radius (m)", Float) = 18.0
        _TileRadiusRatio ("Tile Radius Ratio (per-instance)", Float) = 1.0

        _MarchSteps ("March Steps", Int) = 24
        // Density * absorption is the extinction coefficient. Too high and a ray goes
        // opaque within a metre of entering, so edges lose all translucency.
        _Density ("Density Multiplier", Float) = 2.0
        _Absorption ("Absorption", Float) = 1.5

        // Deliberately below the display ceiling. Clipped white leaves shading nowhere
        // to go, and the cloud reads as one flat blown-out mass. Headroom is what lets
        // form appear in the highlights. Driven live from SmokeTuning.
        _SunlitColor ("Sunlit Color", Color) = (1.0, 0.99, 0.97, 1)
        // The shaded side of a real cloud sits around 35-45% of its sunlit side. Near
        // white here caps the whole lighting system into a few percent of range, so
        // self-shadowing computes correctly and is then lerped into invisibility.
        _ShadowColor ("Shadow Color", Color) = (0.60, 0.65, 0.78, 1)
        // Literally the minimum litness, so a high value erases the shadow range.
        _AmbientFloor ("Ambient Floor", Range(0,1)) = 0.28
        _SkyTintStrength ("Sky Tint Strength", Range(0,1)) = 0.5
        // Hemisphere ambient: sky dome overhead, terrain bounce below. The ground colour
        // stays desaturated, or the underside goes sickly green.
        _AmbientSkyColor ("Ambient Sky Color", Color) = (0.55, 0.68, 0.92, 1)
        _AmbientGroundColor ("Ambient Ground Color", Color) = (0.46, 0.47, 0.44, 1)
        // Stands in for multiple scattering, which a single-scattering march cannot
        // produce: lift the shading toward ambient, and desaturate toward luminance.
        _Washout ("Washout (lift to ambient)", Range(0,1)) = 0.30
        _WashoutDesaturate ("Washout Desaturation", Range(0,1)) = 0.25
        _ForwardScatterG ("HG Forward Scatter (g)", Range(0,0.99)) = 0.75
        _ScatterIntensity ("Scatter Intensity", Float) = 1.6
        _MultiScatterG ("Multi-Scatter G", Range(0,0.6)) = 0.15
        _MultiScatterIntensity ("Multi-Scatter Intensity", Float) = 1.2
        // Beer's-law "powder" term - see its use in the march loop.
        _PowderStrength ("Powder (dark lit edges)", Range(0,1)) = 0.5
        // How much ambient the smoke keeps once the planet's own shadow has cut the sun.
        // Not zero: night smoke is dim, not black.
        _NightAmbient ("Night Ambient Floor", Range(0,1)) = 0.18
        // Metres over which the terminator softens.
        _TerminatorSoftness ("Terminator Softness (m)", Float) = 4000
        // Shadow the cloud casts onto the scene - see the second Pass.
        _ShadowCastStrength ("Cast Shadow Strength", Range(0,1)) = 0.85
        _ShadowCastDistance ("Cast Shadow Reach (m)", Float) = 2500
        _ShadowCastSteps ("Cast Shadow Steps", Int) = 16
        // NDC rectangle the cast-shadow quad is shrunk to (xy = min, zw = max).
        _CastRect ("Cast Rect", Vector) = (-1,-1,1,1)

        // Regime shaping. A thinned section is not just fainter - it is a different
        // material, and these two say how different.
        _ThinEdgeSoftness ("Thin Edge Softness", Range(0.15,1)) = 0.45
        _ThinDetailFade ("Thin Detail Fade", Range(0,1)) = 0.25
        _ShadowCastDebug ("Cast Shadow Debug", Float) = 0
        // Fraction of the real extinction the SHADOW ray sees. At full strength exp() is
        // effectively binary over a multi-metre step, so every boundary becomes a hard
        // edge. Reducing it is the standard cheap stand-in for multiple scattering, and
        // costs no extra samples.
        // Width of the smooth union between capsules. 0 falls back to hard max(), which
        // creases at every joint - the transverse ribs down the trail.
        // Large-scale density variation. Detail noise is texture, not form, on a 200m
        // cloud - and the shadow ray skips it entirely. A uniform solid can only shade as
        // a smooth gradient; the soft dark patches come from the light march passing
        // through genuinely thicker and thinner regions.
        _MacroNoiseScale ("Macro Density Scale", Range(0.002,0.08)) = 0.02
        _MacroStrength ("Macro Density Strength", Range(0,1)) = 0.0
        _SpineBlend ("Spine Blend Smoothness", Range(0,0.5)) = 0.0
        _ShadowExtinction ("Shadow Extinction Scale", Range(0.002,0.3)) = 0.3
        // Ambient occlusion towards the sky - what makes crevices read as deep and lobes
        // as rounded, instead of fill light flattening them.
        _SkyOcclusionDistance ("Sky Occlusion Distance (m)", Float) = 45
        _SkyOcclusionStrength ("Sky Occlusion Strength", Range(0,1)) = 0.75
        // 0 = flat, 1 = shading fully driven by the light march.
        _ShadowStrength ("Self-Shadow Strength", Range(0,1)) = 1.0
        _LightMarchSteps ("Light March Steps", Int) = 4
        // How far toward the sun the shadow ray reaches. Shorter than the cloud is thick
        // and every deep sample returns the same result, leaving a shadable rim around a
        // uniformly white body. Steps are geometric, so the same count covers more range
        // while keeping resolution close to the sample.
        _LightMarchDistance ("Light March Distance", Float) = 25.0

        // experimental scatterer atmospheric extinction integration, 0 = off
        _ScattererIntegrationStrength ("Scatterer Integration Strength", Range(0,1)) = 1.0
    }
    SubShader
    {
        // Most draws use the global default set in AssetLoader.Awake.
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        LOD 100
        Cull Front
        ZWrite Off
        ZTest [_ZTestMode]
        // Premultiplied. The smoke renders into a half-resolution buffer that starts
        // fully transparent and is composited afterwards; with straight alpha, blending
        // into a transparent target darkens toward black instead of accumulating colour.
        Blend 0 One OneMinusSrcAlpha
        // Second target (temporal upscaling): distance to the smoke, accumulated additively
        // so overlapping volumes weight each other by how much of the pixel they cover.
        Blend 1 One One

        // Shared body. It lives at SubShader scope so the shadow-casting pass
        // below can reuse the spine and density functions rather than carrying a
        // second copy of them that would silently drift out of step.
        CGINCLUDE
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            float _NoiseScale;
            float4 _ScrollSpeed;
            float _NoiseAnimation;
            float _DetailStrength;
            float _DetailBias;
            float _SizeFreqTracking;
            float _VortexScale;
            float _VortexStrength;
            float _SilhouetteWarpScale;
            float _SilhouetteWarpStrength;
            float _SilhouetteNoiseScale;
            float _EdgeErosionStrength;
            float _GrainStrength;
            float _GrainScale;
            // 1 for the analytic trail, lower for the settled cloud - see the note
            // above ApplyDetailBuildup. Exposed so both can be tuned separately.
            fixed4 _SunlitColor;
            fixed4 _ShadowColor;
            float _ReferenceRadius;
            float _TileRadiusRatio;
            int _MarchSteps;
            float _Density;
            float _Absorption;
            float _AmbientFloor;
            float _SkyTintStrength;
            fixed4 _AmbientSkyColor;
            fixed4 _AmbientGroundColor;
            float _Washout;
            float _WashoutDesaturate;
            float _ForwardScatterG;
            float _ScatterIntensity;
            float _MultiScatterG;
            float _MultiScatterIntensity;
            float _PowderStrength;
            float _NightAmbient;
            float _TerminatorSoftness;
            float _ThinEdgeSoftness;
            float _ThinDetailFade;
            float _ShadowCastStrength;
            float4 _CastRect;

            // Temporal upscaling. xy = sub-pixel jitter in target pixels, zw = a per-frame
            // offset into the dither pattern. Both are zero when it is off.
            float4 _PlumeJitter;
            // Written by the fragment body, read by the MRT wrapper. HLSL allows a static
            // global to be assigned from a function, which avoids threading an out-parameter
            // through every return path.
            static float4 gPlumeAux;
            float _ShadowCastDistance;
            int _ShadowCastSteps;
            float _ShadowCastDebug;
            // xyz = body centre in world space, w = body radius
            float4 _SmokeBodyCentre;
            // set from C# every frame - see HalfResSmokeRenderer.UpdateSunDirection
            float4 _SmokeSunDir;
            // planet-up at the smoke's position, published by HalfResSmokeRenderer
            // alongside _SmokeSunDir. Needed for the hemisphere ambient split; on a
            // globe "up" is body-relative, so a world-space constant would be wrong the
            // moment the vessel is not over KSC.
            float4 _SmokeUpDir;
            // Accumulated floating-origin shift, published by SmokeVolumeGroup.
            //
            // KSP moves the world origin as the vessel travels (Krakensbane), so the WORLD
            // coordinates of a body-fixed point drift over time. The noise field was being
            // sampled at raw world position, so it stayed pinned to the shifting world
            // while the smoke stayed pinned to the body - the texture appeared to sit
            // still and slide through the smoke, worst at high speed, which is exactly
            // when the origin shifts most. Subtracting the accumulated shift restores an
            // origin-invariant sampling space without inflating the coordinate magnitudes
            // the way going fully body-relative would (Kerbin-local coords are ~600km and
            // would eat float precision).
            float4 _SmokeNoiseOffset;

            float3 NoiseSpace(float3 p) { return p - _SmokeNoiseOffset.xyz; }
            float _MacroNoiseScale;
            float _MacroStrength;
            float _SpineBlend;
            float _ShadowExtinction;
            float _SkyOcclusionDistance;
            float _SkyOcclusionStrength;
            #define SKY_OCCLUSION_STEPS 3
            float _ShadowStrength;
            int _LightMarchSteps;
            float _LightMarchDistance;

            // experimental
            float _ScattererIntegrationStrength;
            float4 _Extinction_Tint;
            float extinctionMultiplier;
            float extinctionGroundFade;
            float extinctionThickness;

            float3 _BoxCenter;
            float3 _BoxExtents;

            // Unity's scene depth. Occlusion is resolved by clipping the raymarch
            // against this rather than by depth-testing the box, so it is always needed.
            sampler2D_float _CameraDepthTexture;
            float _GlobalZTestMode; // legacy, kept so existing SetGlobalInt calls still bind

#if defined(SMOKE_VOLUME_POLYLINE)
            #define MAX_SPINE_POINTS 200
            int _SpineCount;
            // xyz = world space, oldest to newest. w = that point's density multiplier,
            // which is how a bloomed high-altitude section gets to be thin while the same
            // trail is still solid down at the pad. It rides in w because the array was
            // already float4 and a second SetFloatArray per frame is not free.
            float4 _SpinePoints[MAX_SPINE_POINTS];
            float _SpineRadii[MAX_SPINE_POINTS];

            // Two-level culling. The spine is a chain, so a run of consecutive segments
            // shares one bounding sphere. Testing 13 group spheres and descending only
            // into those that pass turns ~199 segment tests into roughly 13 + 16, on
            // every march step including empty space - the shader's dominant cost.
            //
            // Bounds come from SmokeVolumeGroup and are strict over-estimates, so culling
            // can never remove real coverage. Getting that wrong punches holes.
            #define SPINE_GROUP_SIZE 16
            #define MAX_SPINE_GROUPS 13
            int _SpineGroupCount;
            float4 _SpineGroupBounds[MAX_SPINE_GROUPS]; // xyz = centre, w = radius
#else
            sampler3D _DensityTex;
#endif

            // pulls geometry toward camera for the depth test only, raymarch still uses true worldPos
            float _DepthBiasDistance;
            float _DepthBiasFraction;

            v2f vert (appdata v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;

                // Bias DEPTH only, not screen position: viewDir differs across a large
                // box, so moving the whole vertex warps the silhouette sideways at steep
                // angles. True clip pos for x/y/w, borrowed z from a biased one.
                float3 toCam = worldPos - _WorldSpaceCameraPos;
                float camDist = length(toCam);
                float3 viewDir = toCam / max(camDist, 0.0001);
                // A flat bias shrinks relative to view distance, so scale with it too.
                float effectiveBias = max(_DepthBiasDistance, camDist * _DepthBiasFraction);
                // Never let the bias exceed most of the camera distance, or a nearby
                // vertex ends up behind the camera, flipping w negative and corrupting
                // the z-divide - it looks like flat quads cutting through the cloud.
                effectiveBias = min(effectiveBias, camDist * 0.9);
                float3 biasedWorldPos = worldPos - viewDir * effectiveBias;

                float4 trueClip = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                float4 biasedClip = mul(UNITY_MATRIX_VP, float4(biasedWorldPos, 1.0));

                o.pos = trueClip;
                o.pos.z = (biasedClip.z / biasedClip.w) * trueClip.w;
                o.worldPos = worldPos;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // ---- noise ----

            // Baked at load by BakeNoise and bound globally by AssetLoader. RGB is the
            // vector field DomainWarp needs, A is perlin-worley for detail and erosion.
            // Sampling wraps, because the bake is periodic.
            sampler3D _RaymarchedPlumeTrailsNoise;
            float _NoiseTilePeriod;

            // p is in the same "lattice cell" space the old procedural fbm3D took, so
            // every existing scale constant keeps its meaning
            float4 SampleNoise(float3 p)
            {
                return tex3Dlod(_RaymarchedPlumeTrailsNoise, float4(p / _NoiseTilePeriod, 0.0));
            }

            // Interleaved gradient noise (Jimenez). Built as a dither hash: across any
            // small pixel neighbourhood its values are near-evenly spread over 0-1, so a
            // 3x3 block gets 9 well-spread offsets. A white-noise hash instead hands
            // neighbours near-identical offsets (banding) or wildly different ones (salt
            // and pepper), which is why it needed reduced amplitude to be tolerable.
            float InterleavedGradientNoise(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            // Shared by the full density path and the cheap light-march path, and they
            // MUST agree - otherwise shadows fall where nothing looks thicker. Sampled
            // unwarped, because the cheap path has no warped position to offer.
            float MacroVariation(float3 p)
            {
                float n = SampleNoise(NoiseSpace(p) * _MacroNoiseScale).r;
                // No saturate. A clipped plateau's boundary is an isosurface of the
                // noise, which on a trilinear texture is piecewise PLANAR - that is what
                // turned the cloud into stacked flat wafers. This remap only thins, and
                // never reaches its limits, so it has no plateau to go flat along.
                return lerp(1.0, 0.25 + n * 0.75, _MacroStrength);
            }


            // The procedural fbm / worley / perlin-worley functions now live in
            // SmokeVolumeSplat.compute, baked into the noise texture at load.

            // offsets the sample position before density lookup, gives the cauliflower look.
            // The three fbm evaluations this used to do (at exactly the offsets the bake
            // stores in RGB) are now a single filtered fetch.
            float3 DomainWarp(float3 p, float scale, float strength, float timeScale)
            {
                float3 warpBase = NoiseSpace(p) * scale + _Time.y * timeScale * _NoiseAnimation;
                float3 warp = SampleNoise(warpBase).rgb;
                return (warp - 0.5) * 2.0 * strength;
            }

            // ---- ray-box intersection ----

            bool IntersectBox(float3 ro, float3 rd, float3 boxCenter, float3 boxExtents, out float tNear, out float tFar)
            {
                float3 invRd = 1.0 / rd;
                float3 t0 = (boxCenter - boxExtents - ro) * invRd;
                float3 t1 = (boxCenter + boxExtents - ro) * invRd;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);

                tNear = max(max(tmin.x, tmin.y), tmin.z);
                tFar = min(min(tmax.x, tmax.y), tmax.z);

                tNear = max(tNear, 0.0);
                return tFar > tNear;
            }

            float EyeDepthOfWorldPos(float3 worldPos)
            {
                return -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z;
            }

            // perlin-worley noise builds density up rather than carving it away - adds
            // billowy bumps on top of the base coverage instead of eating the silhouette
            // How much fine detail the density field can carry. The polyline path is
            // analytic, so there is no ceiling. A baked grid has metre-wide voxels, and
            // noise finer than a voxel only exposes trilinear facets as flat sheets.

            // sizeRatio is not clamped to 1 and drives frequency; radiusRatio is clamped
            // and scales strengths.
            float ApplyDetailBuildup(float coverage, float3 p, float3 pWarped, float radiusRatio, float sizeRatio, out float bumpFactor)
            {
                bumpFactor = 0.0;
                if (coverage <= 0.0) return 0.0;

                // Blended towards a FIXED world-space frequency. Puffs grow continuously,
                // so scaling frequency by their size rescales the field underneath them
                // and the pattern swims - clumps drifting while the smoke stands still.
                // Amplitude may keep tracking size: changing it only makes features
                // deeper, while changing frequency moves every feature at once.
                // The clamp also matters on its own - unbounded, thin sections reached
                // 20x, putting features under a pixel, which reads as sparkle.
                float freqScale = lerp(1.0, clamp(1.0 / sizeRatio, 0.25, 4.0), _SizeFreqTracking);

                float detailNoise = SampleNoise(NoiseSpace(pWarped) * _SilhouetteNoiseScale * freqScale * 0.22).a;
                bumpFactor = detailNoise;

                // Ramps from a high floor on fresh small puffs up to full buildup on
                // grown ones. The floor matters: real exhaust is turbulent the moment it
                // leaves the nozzle, and a low floor left the young trail a smooth snake
                // that only cauliflowered after several seconds of growth.
                float buildAmount = lerp(0.80, 1.0, saturate(radiusRatio / 0.25));
                float buildStrength = _DetailStrength * buildAmount;
                // ADDITIVE BIAS. This term is symmetric - it adds density where the
                // noise is high and removes it where the noise is low - which sounds
                // balanced but is not, because of the saturate(). In the dense core
                // coverage is already ~1, so every ADDITION is clipped away and only the
                // SUBTRACTIONS survive. The net effect of "symmetric" detail on a
                // saturated volume is therefore purely destructive: it punches holes in
                // solid smoke. That is the patchiness, and it is why it looks worse from
                // a distance, where more of the column is optically thick.
                //
                // Biasing the term to add-only keeps the core solid and lets the noise do
                // its work in the shell instead, where SpineCoverage still has headroom
                // (it falls off smoothly out to 1.4x the capsule radius). Detail then
                // GLUES LUMPS ON to the silhouette rather than eating into it - which is
                // what cauliflower actually is.
                float signedDetail = (detailNoise - 0.5) * 2.0;
                // x2 on the add-only branch. Clamping to the positive half throws away
                // half the noise's range, so switching to add-only silently HALVED how
                // much shape the same _DetailStrength produced - the slider stopped being
                // able to reach as far as it used to. Doubling the one-sided branch puts
                // the usable range back where it was, so "1.0" means strong again.
                float detailTerm = lerp(signedDetail, max(signedDetail, 0.0) * 2.0, _DetailBias);
                float built = saturate(coverage + detailTerm * buildStrength);

                // Wispy boundary. The capsule chain gives a smooth analytic silhouette,
                // which reads as obviously geometric next to a real cloud's ragged edge.
                // Eroding hardest where coverage is already thin tears the outer shell
                // into wisps while leaving the dense core intact - and it makes the edge
                // genuinely translucent rather than just a fast alpha ramp.
                // (_EdgeErosionStrength was declared but never referenced before this.)
                // Scaled by radiusRatio: erosion is meant to tear up the SHELL around a
                // dense core, but a thin trail section is entirely "shell" - coverage is
                // low everywhere, so edgeMask was ~1 across the whole column and the full
                // 0.85 strength shredded it into speckle rather than fraying its edge.
                // Now only puffs with a real core get their boundary broken up.
                // x4, not x2: at x2 anything below 0.5 coverage counted as "edge", which
                // is a thick shell reaching well into the body, so erosion was carving the
                // interior rather than fraying the boundary. Combined with detail buildup
                // and the interior variation - three independent subtractions stacking -
                // it cut the volume into disconnected islands, which read as a second
                // layer of loose blobs floating around the plume. x4 confines it to the
                // genuinely thin outer rim.
                float edgeMask = 1.0 - saturate(coverage * 4.0);
                // partially restored: straight radiusRatio muted the wispiness on the
                // young trail almost entirely. A floor of 0.4 gives thin sections some
                // fraying back while still keeping them from being shredded outright.
                // MULTIPLICATIVE, not subtractive. `saturate(built - x)` clamps at zero,
                // and that clamp is a discontinuity: wherever the carve reaches zero the
                // density field gets a hard edge, and since eroded regions are optically
                // thinner they also shade brighter - which is exactly the bright flat
                // patches with sharp borders. Scaling instead can approach zero smoothly
                // and never produces an edge the falloff didn't already have.
                float erosion = edgeMask * _EdgeErosionStrength * radiusRatio * (1.0 - detailNoise) * 0.6;
                built *= saturate(1.0 - erosion);

                float time = _Time.y;
                float3 scrolled = NoiseSpace(p) * _NoiseScale * freqScale + time * _ScrollSpeed.xyz;
                float scroll = SampleNoise(scrolled).r;
                built *= lerp(0.85, 1.0, scroll);

                // Fine grain, sampled UNWARPED so it sits on the surface rather than
                // crawling across it. Multiplicative and one-sided downward, so it can
                // only thin - it cannot clip coverage into flat plateaus.
                float grainNoise = SampleNoise(NoiseSpace(p) * _SilhouetteNoiseScale * _GrainScale).a;
                built *= lerp(1.0, 0.35 + grainNoise * 0.65, _GrainStrength);

                return saturate(built);
            }

#if defined(SMOKE_VOLUME_POLYLINE)
            // Polynomial smooth maximum. Plain max() is only C0: the gradient jumps where
            // the two fields cross, and the eye reads that as a hard line. On a capsule
            // chain the crossing is a ring at every joint, so the trail comes out ribbed
            // along its whole length.
            float SmoothMax(float a, float b, float k)
            {
                if (k <= 0.0001) return max(a, b);
                float h = saturate(0.5 + 0.5 * (b - a) / k);
                return lerp(a, b, h) + k * h * (1.0 - h);
            }

            // Local puff radius, used to scale noise against feature size. Inverse-square
            // weighting rather than nearest-point: nearest is piecewise CONSTANT, so with
            // a strided scan it snapped between a few levels in slabs along the trail and
            // took the noise frequency with it.
            float EstimateLocalRadius(float3 p)
            {
                float weightedR = 0.0;
                float totalWeight = 0.0;
                int stride = max(1, _SpineCount >> 4);
                for (int i = 0; i < _SpineCount; i += stride)
                {
                    float r = _SpineRadii[i];
                    if (r <= 0.0) continue;
                    float3 d = p - _SpinePoints[i].xyz;
                    // the epsilon both avoids a divide by zero on top of a spine point and
                    // sets how quickly a nearer point takes over from its neighbours
                    float w = 1.0 / (dot(d, d) + 1.0);
                    weightedR += r * w;
                    totalWeight += w;
                }
                return totalWeight > 0.0 ? weightedR / totalWeight : _ReferenceRadius;
            }

            // raw capsule-chain coverage, no noise - shared by the full-quality path
            // (fed a domain-warped position) and the cheap light-march path (fed the
            // plain position).
            // stride > 1 spans one longer capsule instead of the intermediate points. On
            // a smoothed spine the chord barely differs from the polyline - invisible in a
            // shadow lookup, and this loop is O(_SpineCount) per density sample.
            // Per-point density, with a fallback.
            //
            // A plugin older than this shader leaves w at 0, and multiplying by that
            // renders the entire trail invisible while every number in the log still
            // looks healthy - which is exactly how it failed once, after a deploy put the
            // new bundle in place while the game still held the old DLL. Treating a
            // non-positive value as "no thinning" lets the two halves be deployed
            // independently instead of silently cancelling each other out.
            // Maps a point's density multiplier onto a 0..1 "solidity".
            //
            // The fourth root is deliberate. Optical depth falls as the square of the
            // bloom, so a plume only twice as wide already carries a sixteenth of the
            // density; on a linear scale everything above the first few kilometres would
            // read as identical wisp. A gentle curve keeps launch smoke, mist and the high
            // bloom visibly apart across the whole climb.
            float SolidityOf(float density)
            {
                return saturate(pow(saturate(density), 0.25));
            }

            // Guarded, because this value divides the blend radius and the cast-shadow
            // pass fills its own property block. A block that forgets it would leave 0
            // here, and an infinite blend radius makes every sample sit inside the plume.
            float ThinSoftnessSafe()
            {
                return max(_ThinEdgeSoftness, 0.05);
            }

            float SpinePointDensity(int i)
            {
                float d = _SpinePoints[i].w;
                return d > 0.0 ? d : 1.0;
            }

            float SpineCoverageStrided(float3 pSample, int stride, float blendK, out float densityMul)
            {
                float coverage = 0.0;
                // Density is taken from the segment that contributes most, not averaged
                // in. Coverage is a smooth union, so a blend would let a distant fat
                // section bleed its density into a thin one.
                float densityAcc = 0.0;
                float densityWeight = 0.0;
                densityMul = 1.0;
                int last = _SpineCount - 1;

                for (int i = 0; i < last; i += stride)
                {
                    int j = min(i + stride, last);
                    float3 a = _SpinePoints[i].xyz;
                    float3 b = _SpinePoints[j].xyz;
                    float ra = _SpineRadii[i];
                    float rb = _SpineRadii[j];
                    if (ra <= 0.0 || rb <= 0.0) continue;

                    float3 mid = (a + b) * 0.5;
                    float halfLen = length(b - a) * 0.5;
                    float maxR = max(ra, rb) * 1.4 / ThinSoftnessSafe();
                    float cullRadius = halfLen + maxR;
                    if (dot(pSample - mid, pSample - mid) > cullRadius * cullRadius) continue;

                    float3 ab = b - a;
                    float abLenSq = dot(ab, ab);
                    float t = abLenSq > 0.0001 ? saturate(dot(pSample - a, ab) / abLenSq) : 0.0;
                    float3 closest = a + ab * t;
                    float r = lerp(ra, rb, t);

                    float segDensity = lerp(SpinePointDensity(i), SpinePointDensity(j), t);
                    // A rarefied plume has no sharp boundary - it fades out over a distance
                    // comparable to its own size. Widening the falloff for thin sections is
                    // what turns the same capsule chain into mist instead of a solid slab
                    // with a cut edge.
                    float blendRadius = r * 1.4 / lerp(ThinSoftnessSafe(), 1.0, SolidityOf(segDensity));
                    float dSq = dot(pSample - closest, pSample - closest);
                    if (dSq >= blendRadius * blendRadius) continue;
                    float d = sqrt(dSq);

                    float tt = saturate(1.0 - d / blendRadius);
                    float shape = tt * tt * (3.0 - 2.0 * tt);
                    // Shape-WEIGHTED, not winner-takes-all. Taking the single strongest
                    // segment made density jump the moment the winner changed, drawing hard
                    // bright/grey bands wherever two segments contributed equally.
                    densityAcc += shape * segDensity;
                    densityWeight += shape;
                    coverage = SmoothMax(coverage, shape, blendK);
                }

                // SmoothMax can overshoot by up to k/4 where two capsules contribute
                // equally, which is the bulge that fills the joint in the first place
                if (densityWeight > 0.0) densityMul = densityAcc / densityWeight;
                return saturate(coverage);
            }

            // Group-accelerated stride-1 coverage, for the primary march. The strided
            // variant stays flat on purpose - its segments span group boundaries, so group
            // culling would not be a strict over-estimate there.
            // The blend width must scale with local thickness. SmoothMax adds up to k/4
            // of extra coverage where two capsules contribute equally - at every joint. On
            // a thick section coverage is already saturated, so that bulge is clipped and
            // invisible. On a thin one it survives as a bead. A single constant therefore
            // cannot serve both: raising it fixes creases on the column and creates beads
            // on the tip at the same time.
            float SpineBlendFor(float radiusRatio)
            {
                return _SpineBlend * saturate(radiusRatio);
            }

            float SpineCoverage(float3 pSample, float radiusRatio, out float densityMul)
            {
                float blendK = SpineBlendFor(radiusRatio);
                float coverage = 0.0;
                float densityAcc = 0.0;
                float densityWeight = 0.0;
                densityMul = 1.0;
                int last = _SpineCount - 1;

                [loop]
                for (int g = 0; g < _SpineGroupCount; g++)
                {
                    float4 gb = _SpineGroupBounds[g];
                    float3 gd = pSample - gb.xyz;
                    if (dot(gd, gd) > gb.w * gb.w) continue;

                    int start = g * SPINE_GROUP_SIZE;
                    int end = min(start + SPINE_GROUP_SIZE, last);

                    for (int i = start; i < end; i++)
                    {
                        float3 a = _SpinePoints[i].xyz;
                        float3 b = _SpinePoints[i + 1].xyz;
                        float ra = _SpineRadii[i];
                        float rb = _SpineRadii[i + 1];
                        if (ra <= 0.0 || rb <= 0.0) continue;

                        float3 ab = b - a;
                        float abLenSq = dot(ab, ab);
                        float t = abLenSq > 0.0001 ? saturate(dot(pSample - a, ab) / abLenSq) : 0.0;
                        float3 closest = a + ab * t;
                        float r = lerp(ra, rb, t);

                        float segDensity = lerp(SpinePointDensity(i), SpinePointDensity(i + 1), t);
                        float blendRadius = r * 1.4 / lerp(ThinSoftnessSafe(), 1.0, SolidityOf(segDensity));
                        float dSq = dot(pSample - closest, pSample - closest);
                        if (dSq >= blendRadius * blendRadius) continue;
                        float d = sqrt(dSq);

                        float tt = saturate(1.0 - d / blendRadius);
                        float shape = tt * tt * (3.0 - 2.0 * tt);
                        densityAcc += shape * segDensity;
                        densityWeight += shape;
                        coverage = SmoothMax(coverage, shape, blendK);
                    }
                }

                if (densityWeight > 0.0) densityMul = densityAcc / densityWeight;
                return saturate(coverage);
            }

            // Conservative "could this sample hit the chain at all?", with radii dilated
            // by the largest displacement DomainWarp could produce. Bails on the first
            // candidate rather than scanning every segment.
            // Signed distance to the nearest dilated capsule: <= 0 means density may be
            // present, positive is a guaranteed-empty radius the ray can cross in one jump.
            //
            // A boolean "is anything near?" says nothing about how far it is safe to
            // advance, so the loop had to guess with a multiple of the step - and on a long
            // trail those guesses reached hundreds of metres and jumped clean over puffs.
            // A distance turns that guess into sphere tracing.
            float SpineDistance(float3 pSample, float extra)
            {
                float best = 1e9;
                int last = _SpineCount - 1;

                [loop]
                for (int g = 0; g < _SpineGroupCount; g++)
                {
                    float4 gb = _SpineGroupBounds[g];
                    // The sphere already covers its segments' radii; `extra` is the
                    // caller's own dilation, so one set of bounds serves both callers.
                    float3 gd = pSample - gb.xyz;
                    float gr = gb.w + extra;
                    // Conservative distance to ANYTHING in this group. Because the sphere
                    // encloses the group's capsules, the true distance is always >= this.
                    float gDist = length(gd) - gr;

                    // cannot improve on what we already have
                    if (gDist >= best) continue;

                    // Outside the sphere: skip the per-segment work, but still fold the
                    // group's conservative distance into `best`. That fold is NOT optional
                    // - the caller advances by whatever this returns, so skipping a distant
                    // group outright leaves `best` at 1e9 and the ray jumps to infinity.
                    // Every ray starts on the box wall, far from the spine, so that killed
                    // all of them. Under-estimating is safe; over-estimating skips smoke.
                    if (gDist > 0.0)
                    {
                        best = min(best, gDist);
                        continue;
                    }

                    int start = g * SPINE_GROUP_SIZE;
                    int end = min(start + SPINE_GROUP_SIZE, last);

                    for (int i = start; i < end; i++)
                    {
                        float3 a = _SpinePoints[i].xyz;
                        float3 b = _SpinePoints[i + 1].xyz;
                        float ra = _SpineRadii[i];
                        float rb = _SpineRadii[i + 1];
                        if (ra <= 0.0 || rb <= 0.0) continue;

                        float3 ab = b - a;
                        float abLenSq = dot(ab, ab);
                        float t = abLenSq > 0.0001 ? saturate(dot(pSample - a, ab) / abLenSq) : 0.0;
                        float3 closest = a + ab * t;
                        float r = lerp(ra, rb, t) * 1.4 + extra;

                        best = min(best, distance(pSample, closest) - r);
                        if (best <= 0.0) return best; // already inside, no point refining
                    }
                }
                return best;
            }

            // The strength sum is the largest displacement DomainWarp can produce, so
            // dilating by it keeps this a strict over-estimate.
            float EmptyDistance(float3 p)
            {
                return SpineDistance(p, _VortexStrength + _SilhouetteWarpStrength);
            }

            float DensityAt(float3 p, out float bumpFactor, out float outRadiusRatio)
            {
                // Two ratios, deliberately different. sizeRatio may exceed 1 - a ground
                // puff can be 3x the reference, and clamping it hands that puff noise
                // sized for a small one, so it renders as a smooth ball with speckle.
                // radiusRatio stays clamped because it scales strengths, which must not
                // run away on a large puff.
                // Ceiling raised to 200. It used to sit at 4, and the log showed it
                // pinned there for the whole climb: the shader was told "you are 4x the
                // reference" while the plume had actually bloomed past 100x. Every scale
                // derived from it - warp amplitude, edge softness, feature size - was then
                // computed for an 18m puff on a plume hundreds of metres across, so the
                // detail vanished against the shape and only the bare capsule silhouette
                // was left. That is what read as a white slab with cut edges.
                float sizeRatio = clamp(EstimateLocalRadius(p) / _ReferenceRadius, 0.05, 200.0);
                float radiusRatio = min(sizeRatio, 1.0);
                outRadiusRatio = radiusRatio;

                // No empty-space test here: the march loop already ran EmptyDistance to
                // decide whether to take a fine step, and repeating it walks the spine
                // twice for every occupied sample.

                // Clamped. Unbounded, thin young smoke got a 20x frequency multiplier,
                // far past what the march step resolves - which is why flicker sat exactly
                // at the spawn point and vanished as puffs grew.
                // see the note on freqScale in ApplyDetailBuildup - same reasoning
                float freqRatio = lerp(1.0, clamp(sizeRatio, 0.4, 4.0), _SizeFreqTracking);
                float3 vortexOffset = DomainWarp(p, _VortexScale / freqRatio, _VortexStrength * radiusRatio, 0.035);
                // Amplitude scales with the puff, so displacement stays a constant
                // FRACTION of the body it deforms - a fixed metre value is far too small
                // against a large puff to fold anything, and it comes out a smooth pancake.
                // The floor holds thin young sections back: there, a displacement that is
                // small in metres is still a large part of the radius, and the whole
                // section visibly swings.
                // Was radiusRatio * radiusRatio * sizeRatio. The squaring was added to
                // stop thin young sections swinging as the warp field animated past them,
                // but it is brutal: at radiusRatio 0.1 (fresh smoke behind the nozzle) it
                // leaves ONE PERCENT of the warp, which is why the young trail is a smooth
                // cone while the mature column has billows. The wobble it was guarding
                // against came from the field ANIMATING, and those rates are now near zero
                // (0.012, with _NoiseAnimation off), so the guard can be much gentler.
                float silhouetteWarpScale = lerp(0.35, 1.0, radiusRatio) * sizeRatio;
                // Cap the displacement against the local radius. Coverage falls to zero
                // at 1.4x the capsule radius, so a warp that pushes a sample further moves
                // it clean out of the density field and the volume tears into flat slabs.
                // An absolute strength in metres is only safe while it stays small next to
                // the thinnest section being rendered.
                float localRadius = sizeRatio * _ReferenceRadius;
                // Two caps, and the second one is the important one.
                //
                // Amplitude is allowed to grow with the puff, but the warp's WAVELENGTH is
                // fixed in world space (that is what stops the pattern swimming as puffs
                // grow). Scaling only one half of that pair breaks the shape: a bloomed
                // puff gets tens of metres of displacement at a ~100m wavelength, and a
                // silhouette displaced by a third of its own wavelength does not read as
                // cauliflower - it reads as the whole column snaking from side to side.
                //
                // Bounding amplitude by a fraction of the wavelength keeps the surface
                // slope finite whatever the puff size, so big and small sections look like
                // the same material. The noise repeats every _NoisePeriod (8) lattice
                // cells, so the world wavelength is 8 / scale.
                float warpWavelength = 8.0 / max(_SilhouetteWarpScale / freqRatio, 1e-4);
                float warpAmplitude = min(_SilhouetteWarpStrength * silhouetteWarpScale,
                                          min(localRadius * 0.5, warpWavelength * 0.18));
                float3 silhouetteOffset = DomainWarp(p, _SilhouetteWarpScale / freqRatio, warpAmplitude, 0.012);
                float3 pWarped = p + vortexOffset + silhouetteOffset;

                float spineDensity;
                float coverage = SpineCoverage(pWarped, radiusRatio, spineDensity);
                coverage *= lerp(0.6, 1.15, saturate(radiusRatio * 1.5));
                coverage *= MacroVariation(p);

                float built = ApplyDetailBuildup(coverage, p, pWarped, radiusRatio, sizeRatio, bumpFactor);

                // Cauliflower belongs to launch smoke, not to the high bloom.
                //
                // Those lumps come from convection in dense, buoyant exhaust. Kilometres up
                // there is nothing left to drive them: the plume is a rarefied cloud that
                // spreads and blurs. Keeping full detail there gave a huge shape with a
                // hard curdled surface - the opposite of what thin air does. Fading the
                // detail back toward plain coverage lets the same chain read as launch
                // smoke low down and as soft haze high up, with no threshold between them.
                float detailKeep = lerp(_ThinDetailFade, 1.0, SolidityOf(spineDensity));
                built = lerp(coverage, built, detailKeep);

                // Density multiplies AFTER the buildup, never the coverage. Detail is
                // add-only, so thinning the coverage first would leave the added term
                // standing on its own and scatter loose noise where the plume is faint.
                return built * spineDensity;
            }

            // Cheap density for LightMarch - skips the warp and detail noise entirely.
            float DensityCheapAt(float3 p, float radiusRatio)
            {
                // cap the shadow lookup at ~32 capsules however long the trail gets
                int stride = max(1, _SpineCount >> 5);
                float spineDensity;
                float coverage = SpineCoverageStrided(p, stride, SpineBlendFor(radiusRatio), spineDensity);
                coverage *= lerp(0.6, 1.15, saturate(radiusRatio * 1.5));
                // detail buildup is mean-preserving but its scroll term averages ~0.92,
                // so match that here to keep shadow strength consistent with the full path.
                return coverage * 0.92 * MacroVariation(p) * spineDensity;
            }
#else
            // density from the texture baked by the compute shader
            float DensityAt(float3 p, out float bumpFactor, out float outRadiusRatio)
            {
                float sizeRatio = clamp(_TileRadiusRatio, 0.05, 4.0);
                float radiusRatio = min(sizeRatio, 1.0);
                outRadiusRatio = radiusRatio;

                // Clamped. Unbounded, thin young smoke got a 20x frequency multiplier,
                // far past what the march step resolves - which is why flicker sat exactly
                // at the spawn point and vanished as puffs grew.
                // see the note on freqScale in ApplyDetailBuildup - same reasoning
                float freqRatio = lerp(1.0, clamp(sizeRatio, 0.4, 4.0), _SizeFreqTracking);
                float3 vortexOffset = DomainWarp(p, _VortexScale / freqRatio, _VortexStrength * radiusRatio, 0.035);
                // The floor holds thin young sections back: there, a displacement that is
                // small in metres is still a large part of the radius and the section
                // visibly swings.
                // NOTE: the settled cloud reads a baked 64^3 field, so warping its sample
                // by several metres folds that field over itself and the folds show up as
                // thin flat sheets. If that returns, scale this down for this variant only.
                // Amplitude scales with the puff, so displacement stays a constant
                // FRACTION of the body it deforms - a fixed metre value is far too small
                // against a large puff to fold anything, and it comes out a smooth pancake.
                // see the note on the same line in the polyline branch above
                float silhouetteWarpScale = lerp(0.35, 1.0, radiusRatio) * sizeRatio;
                // Cap the displacement against the local radius. Coverage falls to zero
                // at 1.4x the capsule radius, so a warp that pushes a sample further moves
                // it clean out of the density field and the volume tears into flat slabs.
                // An absolute strength in metres is only safe while it stays small next to
                // the thinnest section being rendered.
                float localRadius = sizeRatio * _ReferenceRadius;
                // Two caps, and the second one is the important one.
                //
                // Amplitude is allowed to grow with the puff, but the warp's WAVELENGTH is
                // fixed in world space (that is what stops the pattern swimming as puffs
                // grow). Scaling only one half of that pair breaks the shape: a bloomed
                // puff gets tens of metres of displacement at a ~100m wavelength, and a
                // silhouette displaced by a third of its own wavelength does not read as
                // cauliflower - it reads as the whole column snaking from side to side.
                //
                // Bounding amplitude by a fraction of the wavelength keeps the surface
                // slope finite whatever the puff size, so big and small sections look like
                // the same material. The noise repeats every _NoisePeriod (8) lattice
                // cells, so the world wavelength is 8 / scale.
                float warpWavelength = 8.0 / max(_SilhouetteWarpScale / freqRatio, 1e-4);
                float warpAmplitude = min(_SilhouetteWarpStrength * silhouetteWarpScale,
                                          min(localRadius * 0.5, warpWavelength * 0.18));
                float3 silhouetteOffset = DomainWarp(p, _SilhouetteWarpScale / freqRatio, warpAmplitude, 0.012);
                float3 pWarped = p + vortexOffset + silhouetteOffset;

                float3 boxMin = _BoxCenter - _BoxExtents;
                float3 uv = (pWarped - boxMin) / (_BoxExtents * 2.0);
                if (any(uv < 0.0) || any(uv > 1.0)) { bumpFactor = 0.0; return 0.0; }

                float coverage = tex3Dlod(_DensityTex, float4(uv, 0.0)).r * MacroVariation(p);
                return ApplyDetailBuildup(coverage, p, pWarped, radiusRatio, sizeRatio, bumpFactor);
            }

            // Cheap density for LightMarch - skips the warp and detail noise entirely.
            float DensityCheapAt(float3 p, float radiusRatio)
            {
                float3 boxMin = _BoxCenter - _BoxExtents;
                float3 uv = (p - boxMin) / (_BoxExtents * 2.0);
                if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
                return tex3Dlod(_DensityTex, float4(uv, 0.0)).r * 0.92 * MacroVariation(p);
            }

            // No cheap probe here: the texture is sampled at the WARPED position, so
            // testing the unwarped one could skip real coverage and punch holes. This
            // path's box is compact and mostly full, so there was little to win.
            float EmptyDistance(float3 p) { return -1.0; }
#endif

            // Short march toward the sun for self-shadowing. Four of every five density
            // calls in the shader come from here, so it uses the cheap path: shadowing
            // needs coarse occlusion, and cauliflower detail in the shadow lookup is
            // invisible in the result.
            // Uniform spacing, extinction applied per step against the real step length.
            // Geometric spacing needs a correction factor to stop its long steps saturating
            // exp() to black, which stacks two approximations; uniform steps resolve
            // occlusion near the sample, where it shows, and need no fudge.
            //
            // _ShadowExtinction is the control over how hard the shading reads. At full
            // extinction exp() over a multi-metre step is effectively binary - lit or black
            // - and every density boundary becomes a hard edge, which is the harsh patching
            // on the sunlit side. A fraction of it is the standard cheap stand-in for
            // multiple scattering.
            // Per-pixel dither. Without it every pixel samples occlusion at the same
            // distances, so where the boundary crosses one of those fixed shells the shadow
            // steps across a visible seam - the terraced look in the shading. Only the
            // START is offset: full per-step jitter on a 4-sample march reads as noise.
            // Does the sun actually reach this point, given the planet?
            //
            // Nothing in the density field represents the planet, so the light march has
            // nothing to occlude against and happily reports every sample as fully lit -
            // at night the sun direction still points at the smoke, there is simply
            // nothing in the way. That is why night launches came out lit like noon.
            //
            // A cylinder shadow is enough: behind the body along the sun direction, and
            // within its radius of that axis. Softening the edge also gives the effect
            // for free that high smoke stays sunlit after the ground has gone dark, which
            // is the twilight plume.
            float PlanetSunlight(float3 p, float3 sunDir)
            {
                if (_SmokeBodyCentre.w <= 1.0) return 1.0;   // no body published

                float3 d = p - _SmokeBodyCentre.xyz;
                float along = dot(d, sunDir);
                if (along > 0.0) return 1.0;                 // sun-facing hemisphere

                float perp = length(d - sunDir * along);
                return smoothstep(_SmokeBodyCentre.w - _TerminatorSoftness,
                                  _SmokeBodyCentre.w + _TerminatorSoftness, perp);
            }

            float LightMarch(float3 p, float3 sunDir, float radiusRatio, float jitter)
            {
                float stepLen = (_LightMarchDistance * lerp(0.25, 1.0, radiusRatio)) / _LightMarchSteps;
                float transmittance = 1.0;

                [unroll(8)]
                for (int s = 0; s < _LightMarchSteps; s++)
                {
                    float3 samplePos = p + sunDir * stepLen * (s + jitter);
                    float d = DensityCheapAt(samplePos, radiusRatio) * _Density;
                    transmittance *= exp(-d * _Absorption * _ShadowExtinction * stepLen);
                }

                return transmittance;
            }

            // Sky visibility, i.e. ambient occlusion. Uniform fill light gives a crevice
            // between two lobes as much sky as the lobe tops, which flattens the very form
            // the sun shadow just built. Deliberately short and coarse - occlusion only
            // needs the immediate neighbourhood.
            float SkyVisibility(float3 p, float3 upDir, float radiusRatio, float jitter)
            {
                float stepLen = _SkyOcclusionDistance / SKY_OCCLUSION_STEPS;
                float transmittance = 1.0;

                [unroll]
                for (int s = 0; s < SKY_OCCLUSION_STEPS; s++)
                {
                    float3 samplePos = p + upDir * stepLen * (s + jitter);
                    float d = DensityCheapAt(samplePos, radiusRatio) * _Density;
                    transmittance *= exp(-d * _Absorption * _ShadowExtinction * stepLen);
                }
                return transmittance;
            }

            float HenyeyGreenstein(float cosAngle, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosAngle;
                return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(denom, 0.0001), 1.5));
            }

        ENDCG

        Pass
        {
            // Unity only binds _WorldSpaceLightPos0 for a pass tagged like this. Without
            // it the sun direction is whatever the last shader left in global state.
            // whole thing back to the old flat look if it still misbehaves.
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            // default variant samples _DensityTex (settled cloud), polyline variant
            // computes density analytically along a capsule chain (unused currently)
            #pragma multi_compile _ SMOKE_VOLUME_POLYLINE
            fixed4 fragBody (v2f i)
            {
                float3 ro = _WorldSpaceCameraPos;
                // Sub-pixel jitter: shift the pixel's world position by a fraction of its own
                // screen-space footprint. Successive frames then sample different points
                // inside each pixel, and the temporal resolve accumulates them into detail a
                // single low-resolution frame could not hold. Derivatives are taken before
                // any discard so the whole quad is still active.
                float3 jitteredWorld = i.worldPos
                    + ddx(i.worldPos) * _PlumeJitter.x
                    + ddy(i.worldPos) * _PlumeJitter.y;
                float3 rd = normalize(jitteredWorld - ro);

                float tNear, tFar;
                if (!IntersectBox(ro, rd, _BoxCenter, _BoxExtents, tNear, tFar)) discard;

                // manual depth test against the occlusion texture, only when active
                // Occlusion resolved per-RAY, not per-box. The hardware depth test can
                // only compare one depth per fragment - the box's back face - so a face
                // behind the pad threw away the whole fragment, including the stretch of
                // ray in front of it. Biasing the box works around that but cannot tell
                // "pad, should not occlude" from "hillside, should": there is no correct
                // setting, only a choice of artefact.
                //
                // Clipping the march at scene depth removes the trade. Eye depth is affine
                // in t, so the fraction maps straight onto the ray parameter.
                {
                    float2 screenUV = i.screenPos.xy / i.screenPos.w;
                    float rawSceneDepth = tex2D(_CameraDepthTexture, screenUV).r;
                    float sceneEyeDepth = LinearEyeDepth(rawSceneDepth);

                    float nearEyeDepth = EyeDepthOfWorldPos(ro + rd * tNear);
                    float farEyeDepth = EyeDepthOfWorldPos(ro + rd * tFar);

                    if (sceneEyeDepth <= nearEyeDepth) discard;
                    if (sceneEyeDepth < farEyeDepth)
                    {
                        float ratio = saturate((sceneEyeDepth - nearEyeDepth) / max(farEyeDepth - nearEyeDepth, 0.0001));
                        tFar = tNear + (tFar - tNear) * ratio;
                    }
                }

                float marchDist = tFar - tNear;
                if (marchDist <= 0.0) discard;

                // Fine step sized to the SMOKE, not to the box. The AABB spans kilometres
                // on a long trail, so dividing it by the step count gave ~50-100m steps
                // against a ~24m puff - and since the dither offset is half a step, that
                // scattered neighbouring pixels across different parts of the cloud.
                // Step size no longer depends on puff radius. Deriving it from the trail's
                // AVERAGE radius made cost swing with the state of the smoke: while engines
                // fire the trail fills with small puffs, the average collapses, and every
                // pixel runs to its iteration budget - the "lag only while spawning" case.
                // The feature-sized step existed to fight sparkle from procedural noise;
                // the baked texture is band-limited, so cost can be constant instead.
                // Capping the step by feature size is also what keeps march banding small
                // enough that the dither does not have to carry it alone.
                float uniformStep = marchDist / _MarchSteps;
                float localRadius = max(_ReferenceRadius * clamp(_TileRadiusRatio, 0.05, 1.0), 1.0);
                float stepSize = min(uniformStep, localRadius * 0.5);
                float transmittance = 1.0;
                float distAcc = 0.0;
                float distW = 0.0;
                float3 scatteredLight = 0;
                float bumpAccum = 0.0;
                // Self-shadowing without the phase gain. avgScatter cannot drive shading
                // alone: phase peaks near 4, so saturate() flattens nearly the whole volume
                // to fully lit.
                float shadowAccum = 0.0;
                float skyAccum = 0.0;
                float sunAccum = 0.0;

                // Dither in two parts. The start offset shifts where each pixel's ray
                // begins, which breaks up screen-space banding but not banding that repeats
                // ALONG a ray - one offset is carried for the ray's whole length, so every
                // sample stays locked to the same phase of the step lattice. The per-step
                // jitter inside the loop breaks the lattice everywhere instead.
                //
                // Both run at full amplitude, which IGN tolerates and a white-noise hash
                // did not.
                float ditherHash = InterleavedGradientNoise(i.pos.xy + _PlumeJitter.zw);
                float ditherOffset = (ditherHash - 0.5) * stepSize;

                // _WorldSpaceLightPos0 is not bound on this path: the smoke is submitted
                // through a CommandBuffer, which skips the forward renderer's light setup.
                // Fall back to it only if C# has not published a direction yet.
                float3 sunDir = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001
                    ? normalize(_SmokeSunDir.xyz)
                    : normalize(_WorldSpaceLightPos0.xyz);
                float cosAngle = dot(-rd, sunDir);
                // forward lobe (sharp glow toward sun) + multi lobe (soft internal glow)
                float phase = HenyeyGreenstein(cosAngle, _ForwardScatterG) * _ScatterIntensity
                    + HenyeyGreenstein(cosAngle, _MultiScatterG) * _MultiScatterIntensity;

                // Adaptive step size. The AABB is mostly empty space around a thin curved
                // chain, and a fixed march pays full density on every step of it - cheap at
                // distance, brutal up close where the box covers the screen. Empty
                // stretches are crossed in coarse jumps guarded by a conservative probe.
                // Coarse jumps stay at least as long as a uniform step, so a long empty
                // box is still crossed in roughly _MarchSteps iterations.
                float t = tNear + stepSize * 0.5 + ditherOffset;
                int budget = _MarchSteps * 4;

                // The step must never be so fine that the budget cannot cross the volume.
                // stepSize is sized to the smoke, but marchDist spans the whole box - and
                // looking ALONG the trail makes that enormous. A 3km traversal at a 9m step
                // needs ~330 iterations, so the march stops partway and everything beyond
                // is never accumulated: a chunk of trail missing, from some angles only.
                //
                // Dense smoke hides it, because transmittance terminates the ray first. It
                // is the thin high-altitude trail that shows it. Coarsening a long ray
                // costs detail; not finishing it costs the geometry.
                // Quantised to powers of two. marchDist tracks the bounding box, which
                // grows every frame as the trail lengthens, so an unquantised floor drifts
                // continuously and every sample position shifts frame to frame - constant
                // flicker against high-frequency grain. Snapping means the step changes
                // only when the box roughly doubles.
                // CAPPED against the feature size. The floor exists so a long ray cannot
                // run out of budget before crossing the box - but marchDist tracks the
                // BOX, and once the spawn ceiling went to 120km the box became tens of
                // kilometres while the trail stayed tens of metres thick. The floor then
                // worked out coarser than the trail is wide, so a ray got one sample
                // through it, the dither scattered that sample's depth per pixel, and the
                // result is a stipple mesh instead of smoke - which is exactly the
                // difference between our trail and the real cloud sitting next to it.
                //
                // Sphere tracing already crosses empty space in large jumps, so the budget
                // is mostly spent inside smoke and the floor no longer has to carry that
                // job alone. Capping it at roughly half a puff radius guarantees several
                // samples across the trail whatever the box grew to.
                // The ratio is CLAMPED here, and only here.
                //
                // _TileRadiusRatio is allowed to run to 200 so the noise, warp and edge
                // softness know how big the plume really is. This cap must not follow it.
                // Its job is to stop the step growing coarser than the detail, so it has to
                // track the SMALLEST feature the march must resolve, not the largest shape.
                // Letting it ride the bloom put the cap at ~1600m, which rounds up to a
                // 2048m step - one or two samples across a puff. That is what turned the
                // trail into separate spheres with concentric rings inside them.
                float stepRatio = min(max(_TileRadiusRatio, 0.05), 4.0);
                float featureCap = max(_ReferenceRadius * stepRatio * 0.6, 2.0);
                float rawFloor = clamp(marchDist / (float)budget, 0.5, featureCap);
                stepSize = max(stepSize, exp2(ceil(log2(rawFloor))));
                // Persists across iterations - deep samples reuse the last shadow.
                float lightTransmittance = 1.0;
                float skyVisibility = 1.0;

                [loop]
                for (int s = 0; s < budget; s++)
                {
                    if (t >= tFar) break;
                    float3 samplePos = ro + rd * t;

                    // Sphere tracing: advance by the guaranteed-empty radius. The floor is
                    // absolute, not a fraction of stepSize - that is derived from the box,
                    // so on a long trail a relative floor would be tens of metres and could
                    // still leap a small puff. 1m is below the smallest puff radius.
                    float empty = EmptyDistance(samplePos);
                    if (empty > 0.0)
                    {
                        t += max(empty, 1.0);
                        continue;
                    }

                    // Per-step jitter, on the density sample only. The probe above stays
                    // UNJITTERED - it is a conservative "definitely nothing within this
                    // radius" test, and moving its origin could let a jump skip a puff.
                    // The offset is zero-mean, so the march integrates the same optical
                    // depth on average. The step index enters the hash so consecutive
                    // samples differ, which is what breaks banding along the ray.
                    float stepJitter = InterleavedGradientNoise(i.pos.xy + _PlumeJitter.zw + float2(s * 5.588238, s * 3.141593)) - 0.5;
                    float3 densityPos = samplePos + rd * (stepJitter * stepSize);

                    float bump, sampleRadiusRatio;
                    float density = DensityAt(densityPos, bump, sampleRadiusRatio) * _Density;
                    if (density <= 0.001) { t += stepSize; continue; }

                    // The light march is the most expensive thing in the shader. Once the
                    // ray is deep inside the cloud its transmittance is tiny, so whatever
                    // the shadow term says gets multiplied by nothing - reuse the last
                    // value there. The samples that dominate the pixel are the early ones,
                    // and those still get exact shadowing.
                    // Also skip it where the sample is too thin to care. Edges are a large
                    // share of all samples and contribute little whatever their shadow says.
                    if (transmittance > 0.18 && density > 0.05)
                    {
                        // Reuse the primary dither, offset so the shadow ray is not phase
                        // locked to it - that would put both seams in the same place.
                        //
                        // TRIED AND REJECTED (2026-09-07): advancing this phase per primary
                        // step, frac(hash + 0.5 + s * 0.618), to let the light march average
                        // itself out and kill the salt-and-pepper. Offscreen A/B at equal
                        // settings showed it is worse: with sphere tracing, neighbouring
                        // pixels reach a given depth after a similar number of steps, so the
                        // phase stops depending on the pixel and starts depending on DEPTH.
                        // The quantisation error then becomes spatially coherent and the
                        // grain turns into flat grey terraces across the column. Grain from
                        // an incoherent error reads as texture; the same error made coherent
                        // reads as a defect.
                        float lightPhase = frac(ditherHash + 0.5);
                        float skyPhase = frac(ditherHash + 0.25);
                        lightTransmittance = LightMarch(densityPos, sunDir, sampleRadiusRatio, lightPhase);
                        skyVisibility = SkyVisibility(densityPos, _SmokeUpDir.xyz,
                            sampleRadiusRatio, skyPhase);
                    }

                    float stepTransmittance = exp(-density * _Absorption * stepSize);
                    float contribution = transmittance * (1.0 - stepTransmittance);
                    // Where along the ray the smoke actually is, weighted by how much of the
                    // pixel each step contributes. The temporal resolve needs a real position
                    // to reproject: reprojecting as if the smoke were at infinity ignores
                    // camera translation, which is most of the motion here.
                    distAcc += contribution * t;
                    distW += contribution;
                    // POWDER. Beer-Lambert makes the sun-facing side of a billow its
                    // brightest point, since that is where the shadow ray is shortest.
                    // Photographs show the opposite: those edges go slightly dark and the
                    // brightness picks up a little way in, because near a boundary there
                    // is not enough material to scatter light back toward the camera.
                    // This is what gives a lobe its rounded look instead of a flat disc.
                    float powder = 1.0 - exp(-density * 2.0);
                    float powderTerm = lerp(1.0, powder, _PowderStrength);
                    float sunlit = PlanetSunlight(densityPos, sunDir);
                    sunAccum += contribution * sunlit;
                    scatteredLight += contribution * phase * lightTransmittance * powderTerm * sunlit;
                    shadowAccum += contribution * lightTransmittance;
                    skyAccum += contribution * skyVisibility;
                    bumpAccum += contribution * bump;
                    transmittance *= stepTransmittance;
                    if (transmittance < 0.01) break;

                    t += stepSize;
                }

                float alpha = 1.0 - transmittance;
                if (alpha <= 0.001) discard;

                fixed3 shadowColor = _ShadowColor.rgb;
                fixed3 litColor = _SunlitColor.rgb;

                fixed4 col;
                // scatteredLight is a sum of per-step contributions, and those sum to
                // alpha - so it carries optical depth, not brightness. Feeding it straight
                // to lerp drifts the whole cloud toward the shadow colour as soon as
                // density drops. Dividing by alpha gives the contribution-weighted AVERAGE
                // lit-ness, so thin and thick regions share a hue and differ only in alpha.
                float avgScatter = alpha > 0.0001 ? scatteredLight / alpha : 0.0;

                // Hemisphere ambient. A cloud's shaded side is not the sunlit colour only
                // darker - it is lit by two different sources: the blue sky dome above and
                // dull warm bounce off the terrain below. Collapsing both into one shadow
                // colour is what makes shaded regions read as dirty grey.
                //
                // The mix follows how far the view ray looks up, which gives the column a
                // cool-topped, warm-bottomed gradient no shadow tuning can fake.
                float skyFacing = saturate(0.5 + 0.5 * dot(-rd, _SmokeUpDir.xyz));
                fixed3 ambient = lerp(_AmbientGroundColor.rgb, _AmbientSkyColor.rgb, skyFacing);
                float avgSky = alpha > 0.0001 ? saturate(skyAccum / alpha) : 1.0;
                // Ambient has to follow the terminator too. The sky above night-side smoke
                // is dark, so leaving ambient at its daylight value paints the cloud a
                // bright blue-white that no amount of shadow tuning can explain.
                float avgSunlit = alpha > 0.0001 ? saturate(sunAccum / alpha) : 1.0;
                float nightFade = lerp(_NightAmbient, 1.0, avgSunlit);
                ambient *= lerp(1.0, avgSky, _SkyOcclusionStrength);
                ambient *= nightFade;
                fixed3 shaded = lerp(shadowColor, ambient, _SkyTintStrength);

                // avgScatter drives the ramp. Driving it from the pure shadow term is
                // arguably more correct, but every attempt exposed how coarse the shadow
                // is and read worse.
                float flatLit = lerp(_AmbientFloor * 0.45, 1.0, saturate(avgScatter));
                float avgShadow = alpha > 0.0001 ? saturate(shadowAccum / alpha) : 0.0;
                float shadedLit = lerp(_AmbientFloor, 1.0, avgShadow);

                float litness = lerp(flatLit, shadedLit, _ShadowStrength);
                col.rgb = lerp(shaded, litColor, litness);

                // Phase acts as a brightness gain rather than the shading ramp - this is
                // what puts a glow on the sun-facing side.
                col.rgb *= lerp(1.0, 1.22, saturate(avgScatter * 0.4));

                // WASHOUT. Real launch smoke photographs far less contrasty than a
                // physically-lit volume renders: multiple scattering inside a dense white
                // medium bounces light into every shadow. A single-scattering march
                // produces a range that is too wide and too saturated.
                //
                // Two operations, both toward the ambient the cloud actually sits in:
                //  - desaturate toward the volume's own luminance (scattering in a white
                //    medium is achromatic; only the light sources carry colour)
                //  - lift the whole curve toward ambient, which compresses the range from
                //    the bottom without clipping the highlights the way a plain add would
                float luma = dot(col.rgb, fixed3(0.299, 0.587, 0.114));
                col.rgb = lerp(col.rgb, fixed3(luma, luma, luma), _WashoutDesaturate);
                col.rgb = lerp(col.rgb, max(col.rgb, ambient), _Washout);

                col.a = alpha;

                // Bump highlights get a brightness boost, no hue change. The low end must
                // stay near 1: knocking a third off wherever the basis is dark dims most
                // of the volume and reads as uniformly dingy.
                float avgBump = alpha > 0.0001 ? saturate(bumpAccum / alpha) : 0.0;
                col.rgb *= lerp(0.92, 1.08, avgBump);

                // experimental aerial perspective toward scatterer's extinction color
                if (_ScattererIntegrationStrength > 0.0001)
                {
                    float camDist = distance(i.worldPos, _WorldSpaceCameraPos);
                    float fogAmount = saturate(camDist * extinctionMultiplier * max(extinctionThickness, 0.0001))
                        * saturate(extinctionGroundFade);
                    col.rgb = lerp(col.rgb, _Extinction_Tint.rgb, fogAmount * _ScattererIntegrationStrength);
                }

                // premultiply on the way out - see the Blend note on the SubShader
                gPlumeAux = float4(distAcc, distW, 0.0, 0.0);
                col.rgb *= col.a;
                return col;
            }

            struct FragOut
            {
                fixed4 col : SV_Target0;
                float4 aux : SV_Target1;
            };

            FragOut frag (v2f i)
            {
                gPlumeAux = float4(0.0, 0.0, 0.0, 0.0);
                FragOut o;
                o.col = fragBody(i);
                o.aux = gPlumeAux;
                return o;
            }
            ENDCG
        }

        // ------------------------------------------------------------------
        // Shadow the cloud casts ONTO THE SCENE.
        //
        // The light march inside the volume can only find occluders that exist in the
        // density field, and the terrain and the rocket are not in it - so the smoke
        // shades itself perfectly and leaves the ground beneath it fully lit. Getting the
        // cloud into Unity's shadow map is not an option either: it has no geometry to
        // rasterise, and KSP's terrain shaders are not ours to modify.
        //
        // So this goes the other way round. It runs after the opaque scene is drawn,
        // reconstructs each pixel's world position from the depth buffer, and marches
        // from there toward the sun through the same spine the volume itself uses. The
        // result multiplies the framebuffer, so anything standing under the plume gets
        // darkened - terrain, buildings and the vessel alike, with no per-object work.
        //
        // Cost is kept down by sphere tracing: a pixel nowhere near the plume resolves in
        // one or two iterations, because EmptyDistance hands back a large guaranteed-empty
        // jump straight away.
        Pass
        {
            Name "SmokeShadowCast"
            Blend DstColor Zero
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertShadowCast
            #pragma fragment fragShadowCast
            #pragma multi_compile _ SMOKE_VOLUME_POLYLINE

            struct v2fCast
            {
                float4 pos : SV_POSITION;
                float4 scr : TEXCOORD0;
                float3 ray : TEXCOORD1;
            };

            v2fCast vertShadowCast (appdata v)
            {
                v2fCast o;
                // The quad arrives already in clip space; this pass covers the screen and
                // has no transform of its own.
                // The quad is shrunk to the rectangle the shadow can actually land in, so the
                // rest of the screen is never shaded. Everything below - screen position AND
                // view ray - is derived from the shrunk position, which is what keeps the
                // reconstruction correct on a sub-rectangle.
                float2 ndc = lerp(_CastRect.xy, _CastRect.zw, v.vertex.xy * 0.5 + 0.5);
                o.pos = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);

                // Screen coordinates for the depth lookup come from ComputeScreenPos, NOT
                // from the vertex position directly. Deriving them by hand reads the depth
                // texture upside down on D3D, and the give-away is not a blank screen but a
                // shadow that lands mirrored about the middle of the frame: every pixel
                // shades itself using the depth of the pixel opposite it, so the ground
                // stays lit and the plume's shadow appears floating in the sky above it.
                o.scr = ComputeScreenPos(o.pos);

                // View ray for this corner, carried from the vertex so it interpolates
                // linearly across the quad. It comes off the real clip position, which
                // keeps it in step with the flipped lookup above.
                float4 rayH = mul(unity_CameraInvProjection, float4(ndc, 1.0, 1.0));
                o.ray = rayH.xyz / rayH.w;
                return o;
            }

            fixed4 fragShadowCast (v2fCast i) : SV_Target
            {
                float2 uv = i.scr.xy / i.scr.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float depth01 = Linear01Depth(raw);

                // Diagnostic ladder. Each rung answers one question, in the order the pass
                // actually executes, so a single flight can walk it and stop at the first
                // rung that fails instead of costing a build per guess.
                //
                //   1 GREEN everywhere   - the pass runs at all
                //   2 CYAN on non-sky    - the depth texture is readable here
                //   3 BLUE               - the spine arrays reached this pass
                //                          (RED = _SpineCount is 0, the block is not
                //                           carrying the geometry and the march has
                //                           nothing to hit)
                //   4 YELLOW             - PlanetSunlight lets this pixel through
                //                          (RED = the planet-shadow test is rejecting the
                //                           ground before the march even starts)
                //   5 MAGENTA            - the march accumulated optical depth here,
                //                          i.e. the sun ray actually crossed the plume
                //                          (RED = it marched and found nothing)
                //
                // Rung 5 is the one that matters; 3 and 4 exist because both would fail
                // silently and look identical from outside.
                if (_ShadowCastDebug > 0.5 && _ShadowCastDebug < 1.5) return fixed4(0.3, 1, 0.3, 1);
                if (_ShadowCastDebug > 1.5 && _ShadowCastDebug < 2.5) return fixed4(depth01 > 0.999 ? 1.0 : 0.3, 1, 1, 1);
                if (_ShadowCastDebug > 2.5 && _ShadowCastDebug < 3.5)
                {
                    if (depth01 > 0.999) return fixed4(1, 1, 1, 1);
                    // _SpineCount only exists in the polyline variant; this pass compiles
                    // for both, so it has to be asked behind the keyword.
                    #ifdef SMOKE_VOLUME_POLYLINE
                        return _SpineCount > 1 ? fixed4(0.3, 0.3, 1, 1) : fixed4(1, 0.2, 0.2, 1);
                    #else
                        return fixed4(0.6, 0.6, 0.6, 1);
                    #endif
                }

                // Sky. There is no surface here to shadow, and reconstructing a position
                // from the far plane would put it effectively at infinity.
                if (depth01 > 0.999) return fixed4(1, 1, 1, 1);

                // Reconstruct the world position this pixel actually shows.
                //
                // Built from the INVERSE VIEW matrix rather than unity_CameraToWorld. The
                // latter is not simply the inverse of the view matrix in Unity - its third
                // row is negated - so using it here silently puts the reconstructed point
                // in the wrong place, and a march that starts in the wrong place finds
                // nothing while every other part of the pass looks healthy.
                float eye = LinearEyeDepth(raw);
                // normalise so z = -1, which makes scaling by eye depth land on the surface
                float3 viewDir = i.ray / max(-i.ray.z, 1e-6);
                float3 worldDir = mul((float3x3)UNITY_MATRIX_I_V, viewDir);
                float3 worldPos = _WorldSpaceCameraPos + worldDir * eye;

                float3 sunDir = dot(_SmokeSunDir.xyz, _SmokeSunDir.xyz) > 0.0001
                    ? normalize(_SmokeSunDir.xyz)
                    : normalize(_WorldSpaceLightPos0.xyz);

                // A surface already on the night side casts nothing - and marching there
                // would only find the plume lit from the wrong side.
                float groundSun = PlanetSunlight(worldPos, sunDir);
                if (_ShadowCastDebug > 3.5 && _ShadowCastDebug < 4.5)
                    return groundSun > 0.001 ? fixed4(1, 1, 0.3, 1) : fixed4(1, 0.2, 0.2, 1);
                if (groundSun <= 0.001) return fixed4(1, 1, 1, 1);

                float stepLen = _ShadowCastDistance / max((float)_ShadowCastSteps, 1.0);
                float opticalDepth = 0.0;
                // start clear of the surface so a pixel does not shadow itself
                float t = stepLen * 0.5;

                [loop]
                for (int s = 0; s < _ShadowCastSteps; s++)
                {
                    float3 sp = worldPos + sunDir * t;
                    float empty = EmptyDistance(sp);
                    if (empty > 0.0)
                    {
                        // Advance by the EMPTY distance, not by max(empty, stepLen).
                        //
                        // max() defeated the whole point of sphere tracing here. stepLen is
                        // the reach divided by the step budget - 156m at the defaults - so a
                        // ray that had correctly measured 5m of empty space in front of a
                        // 40m-wide plume was still pushed 156m and stepped clean over it.
                        // The shadow then only appeared where the geometry happened to line
                        // up with the fixed stride, which is why it came out as a row of
                        // separate dark ovals instead of one continuous streak.
                        //
                        // The small floor is only there to guarantee progress so the loop
                        // cannot stall against a surface it is sitting exactly on.
                        t += max(empty, 1.0);
                    }
                    else
                    {
                        opticalDepth += DensityCheapAt(sp, 1.0) * _Density * stepLen;
                        t += stepLen;
                    }
                    if (t > _ShadowCastDistance) break;
                }

                // 3 = did this ray meet ANY smoke? Separates "the march is wrong" from
                // "the march is right but the shading maths cancels out".
                if (_ShadowCastDebug > 2.5) return fixed4(opticalDepth > 0.0001 ? 0.2 : 1.0, 1, 1, 1);

                if (_ShadowCastDebug > 4.5)
                    return opticalDepth > 0.0001 ? fixed4(1, 0.3, 1, 1) : fixed4(1, 0.2, 0.2, 1);

                float transmittance = exp(-opticalDepth * _Absorption * _ShadowExtinction);
                float shade = lerp(1.0, transmittance, _ShadowCastStrength);
                return fixed4(shade, shade, shade, 1.0);
            }
            ENDCG
        }
    }
    FallBack Off
}

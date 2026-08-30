using UnityEngine;

namespace VolumetricContrails
{
    // Live-tunable shader parameters, driven from the in-flight slider window.
    //
    // These can't be pushed with Shader.SetGlobalFloat - a material's own values win over
    // globals - so SmokeVolumeGroup applies them to its property block every frame.
    // Defaults mirror the shader's Properties block; keep them in sync.
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
        public static float AmbientFloor = 0.20f;        // lower = deeper shadows
        // Fraction of the real extinction the shadow ray sees. Lower = softer gradient.
        public static float ShadowExtinction = 0.12f;
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
        // Tint dials rather than raw colours, so they stay usable from sliders.
        public static float SkyTint = 1.0f;
        public static float GroundTint = 1.0f;

        // Scattering. This group decides whether the smoke reads as a thick volume or as a
        // lit surface. ForwardScatterG is the Henyey-Greenstein eccentricity: high gives a
        // bright rim towards the sun, 0 is uniform and flat.
        public static float ForwardScatterG = 0.75f;
        public static float ScatterIntensity = 1.6f;
        // Multiple scattering, faked with a second wide lobe. It's what makes a cloud glow
        // from within instead of looking like a shell.
        public static float MultiScatterG = 0.27f;
        public static float MultiScatterIntensity = 4.0f;
        // Darkens the sun-facing side of a billow, which stops it reading as a flat disc.
        public static float PowderStrength = 0.5f;
        public static float SkyTintStrength = 0.5f;
        // How far ambient follows the scene's light probe instead of the fixed colours.
        public static float SceneAmbientBlend = 0.5f;

        // Ambient occlusion towards the sky. Without it a crevice between two lobes gets
        // as much fill light as the lobe tops, which flattens the form.
        public static float SkyOcclusionDistance = 45f;
        public static float SkyOcclusionStrength = 0.75f;

        // Brightness of the lit side. The shader's default is already at the ceiling,
        // which leaves shading no range to work in.
        public static float SunlitBrightness = 0.88f;

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
            block.SetColor("_AmbientSkyColor",
                Color.Lerp(skyBase, RenderSettings.ambientSkyColor, SceneAmbientBlend));
            block.SetColor("_AmbientGroundColor",
                Color.Lerp(groundBase, RenderSettings.ambientGroundColor, SceneAmbientBlend));
        }
    }
}

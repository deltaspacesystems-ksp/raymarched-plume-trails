using System.IO;
using UnityEngine;

namespace RaymarchedPlumeTrails
{
    // static shader cache, read by SmokeVolumeGroup since Shader.Find won't find a
    // custom shader until the asset bundle loads
    public static class ShaderCache
    {
        public static Shader SmokeVolumeShader;
        public static Shader SmokeCompositeShader;
        public static Shader HeatHazeShader;
        public static Shader PlumeCoreShader;
        public static Shader PadFluidShader;
        public static Shader PadHeightShader;
        public static Shader PadLayerShader;
        public static Shader ShockLensShader;
        public static Shader DebrisShader;
        public static ComputeShader PadFluidCompute;
        public static ComputeShader BlastFluidCompute;
        public static ComputeShader SmokeVolumeSplatCompute;
        public static RenderTexture NoiseTexture;
        public static RenderTexture WorleyTexture;          // the noise of the new trail (inverted worley fbm)
    }

    // runs once on the main menu, before any vessel loads
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class AssetLoader : MonoBehaviour
    {
        private const string BundleFileName = "raymarchedplumetrails_bundle";
        // Legacy fallback only. A hardcoded folder name silently breaks for anyone who
        // RENAMES the mod folder: KSP loads the assembly regardless, so the DLL runs and
        // the UI appears while the bundle lookup goes nowhere - "installed, UI works,
        // nothing renders". ResolveBundlePath derives the folder from the assembly.
        private const string BundleRelativePath = "RaymarchedPlumeTrails/Bundles/" + BundleFileName;
        private const string SmokeVolumeMaterialAssetName = "SmokeVolumeMat";
        private const string SmokeVolumeSplatComputeAssetName = "SmokeVolumeSplat";
        private const string SmokeCompositeShaderAssetName = "SmokeComposite";

        // Directory this DLL was loaded from, i.e. <GameData>/<whatever>/Plugins.
        internal static string AssemblyDirectory()
        {
            return Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        // Finds the bundle relative to the ASSEMBLY rather than a hardcoded folder name,
        // so renaming the mod folder cannot break it. Tries, in order:
        //   <pluginDir>/../Bundles/<file>   - the normal layout (Plugins and Bundles as
        //                                     siblings inside the mod folder)
        //   <pluginDir>/<file>              - bundle dropped in beside the DLL
        //   GameData/RaymarchedPlumeTrails/Bundles/<file> - legacy absolute path
        // Returns null if none exist.
        private static string ResolveBundlePath()
        {
            string pluginDir = AssemblyDirectory();

            if (!string.IsNullOrEmpty(pluginDir))
            {
                string modDir = Path.GetDirectoryName(pluginDir);
                if (!string.IsNullOrEmpty(modDir))
                {
                    string sibling = Path.Combine(Path.Combine(modDir, "Bundles"), BundleFileName);
                    if (File.Exists(sibling)) return sibling;
                }

                string beside = Path.Combine(pluginDir, BundleFileName);
                if (File.Exists(beside)) return beside;
            }

            string legacy = Path.Combine(
                Path.Combine(KSPUtil.ApplicationRootPath, "GameData"), BundleRelativePath);
            if (File.Exists(legacy)) return legacy;

            return null;
        }

        private static bool assetsLoaded;

        private void Awake()
        {
            SmokeTuningConfig.Load();
            if (!ModSettings.Enabled) { Debug.Log("[PlumeTrails] switched off: no assets are loaded, nothing hooks into the game (switch it on in the settings window: the assets are loaded then)"); return; }
            LoadAssets();
        }

        // called when the mod is switched on while the game runs and nothing was loaded at the start
        public static void EnsureLoaded()
        {
            if (assetsLoaded) return;
            LoadAssets();
        }

        private static void LoadAssets()
        {
            if (assetsLoaded) return;
            assetsLoaded = true;
            // Environment banner. "UI is there, bundle is there, nothing renders" is
            // otherwise undiagnosable from a log - every requirement below can fail
            // silently. Ask for these lines first in any blank-screen report.
            Debug.Log(string.Format(
                "[PlumeTrails] graphics: device={0} shaderLevel={1} computeShaders={2} " +
                "3dTex={3} randomWrite={4} ARGBHalf3D={5}",
                SystemInfo.graphicsDeviceType,
                SystemInfo.graphicsShaderLevel,
                SystemInfo.supportsComputeShaders,
                SystemInfo.supports3DTextures,
                SystemInfo.supportsComputeShaders ? "yes" : "n/a",
                SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)));

            // safe default ztest (LEqual), must be set before any smoke renders
            Shader.SetGlobalInt("_GlobalZTestMode", 4);
            Shader.SetGlobalInt("_ZTestMode", 4);

            string bundlePath = ResolveBundlePath();

            if (bundlePath == null)
            {
                Debug.LogError("[PlumeTrails] AssetBundle '" + BundleFileName + "' not found. Expected it "
                    + "in a 'Bundles' folder next to this plugin's own folder. Checked alongside the "
                    + "assembly at: " + AssemblyDirectory()
                    + " and at the legacy path GameData/" + BundleRelativePath);
                return;
            }

            Debug.Log("[PlumeTrails] Loading AssetBundle from: " + bundlePath);
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogError("[PlumeTrails] AssetBundle.LoadFromFile returned null. The Unity version " +
                    "used to build the bundle probably does not match KSP's.");
                return;
            }

            LoadShaderFromMaterial(bundle, SmokeVolumeMaterialAssetName, ref ShaderCache.SmokeVolumeShader);

            // the composite shader has no material of its own, so it is loaded directly
            ShaderCache.SmokeCompositeShader = bundle.LoadAsset<Shader>(SmokeCompositeShaderAssetName);
            if (ShaderCache.SmokeCompositeShader == null)
            {
                Debug.LogError("[PlumeTrails] Composite shader '" + SmokeCompositeShaderAssetName +
                    "' missing from the bundle - smoke will not be drawn. Rebuild the AssetBundle.");
            }

ShaderCache.PlumeCoreShader = bundle.LoadAsset<Shader>("PlumeCore");
            if (ShaderCache.PlumeCoreShader == null)
                Debug.LogWarning("[PlumeTrails] plume shader missing from the bundle (rebuild it) - no physical plumes");
            ShaderCache.PadHeightShader = bundle.LoadAsset<Shader>("PadHeight");
            ShaderCache.PadLayerShader = bundle.LoadAsset<Shader>("PadLayer");
            ShaderCache.ShockLensShader = bundle.LoadAsset<Shader>("ShockLens");
            ShaderCache.DebrisShader = bundle.LoadAsset<Shader>("BlastDebris");
            if (ShaderCache.ShockLensShader == null || ShaderCache.DebrisShader == null) Debug.LogWarning("[PlumeTrails] shock lens / debris shader missing from the bundle (rebuild it)");
            if (ShaderCache.PadLayerShader == null) Debug.LogWarning("[PlumeTrails] pad layer shader missing from the bundle (rebuild it) - the pad is a height map only");
            CapsuleTrail.TrailShader = bundle.LoadAsset<Shader>("TrailCapsules");
            if (CapsuleTrail.TrailShader == null) Debug.LogWarning("[PlumeTrails] capsule trail shader missing from the bundle (rebuild it)");
            ShaderCache.PadFluidShader = bundle.LoadAsset<Shader>("PadFluid");
            ShaderCache.PadFluidCompute = bundle.LoadAsset<ComputeShader>("PadFluid");
            ShaderCache.BlastFluidCompute = bundle.LoadAsset<ComputeShader>("BlastFluid");
            if (ShaderCache.BlastFluidCompute == null) Debug.LogWarning("[PlumeTrails] blast fluid compute missing from the bundle (rebuild it) - explosions use the blob model");
            if (ShaderCache.PadFluidShader == null || ShaderCache.PadFluidCompute == null)
                Debug.LogWarning("[PlumeTrails] pad fluid shader / compute missing from the bundle (rebuild it) - no GPU pad cloud");
            ShaderCache.HeatHazeShader = bundle.LoadAsset<Shader>("HeatHaze");
            if (ShaderCache.HeatHazeShader == null)
                Debug.LogWarning("[PlumeTrails] heat haze shader missing from the bundle (rebuild it) - no heat haze");

            ShaderCache.SmokeVolumeSplatCompute = bundle.LoadAsset<ComputeShader>(SmokeVolumeSplatComputeAssetName);
            if (ShaderCache.SmokeVolumeSplatCompute == null)
            {
                Debug.LogError("[PlumeTrails] Compute shader '" + SmokeVolumeSplatComputeAssetName + "' missing from the bundle.");
            }
            else
            {
                BakeNoiseTexture(ShaderCache.SmokeVolumeSplatCompute);
            }

            // keep assets loaded (false) for the whole game session
            bundle.Unload(false);
        }

        // Bakes the tileable noise volume once per session, replacing the procedural
        // fbm/perlin-worley the fragment shader used to evaluate at every density sample
        // (~500 hash calls each, up to ~160 samples per pixel) with a few 3D fetches.
        private const int NoiseResolution = 128;
        // Lattice cells spanned by the texture, i.e. how far the noise runs before it
        // repeats - tens of metres of world space, longer than the trail is wide. The
        // repeat lives in a WARP field rather than in the silhouette, so it reads as
        // variation. Raising it lengthens the period at the cost of texel density.
        private const float NoisePeriod = 8f;

        private static void BakeNoiseTexture(ComputeShader compute)
        {
            // HARD requirements. Every density sample reads the baked volume, so without
            // it the smoke vanishes or renders as a blob - and all three fail SILENTLY:
            // Dispatch on an unsupported device is a no-op and Create() just returns false.
            // KSP forced to OpenGL or DX9 is the usual cause; this needs SM5.0.
            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[PlumeTrails] GPU/graphics API reports NO compute shader support (device: "
                    + SystemInfo.graphicsDeviceType + "). The noise volume cannot be baked and the smoke "
                    + "will not render. If KSP was launched with -force-glcore, -force-opengl or "
                    + "-force-d3d9, remove that flag so it runs on DirectX 11.");
                return;
            }

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Debug.LogError("[PlumeTrails] ARGBHalf render textures are unsupported on this device ("
                    + SystemInfo.graphicsDeviceType + ") - cannot bake the noise volume.");
                return;
            }

            int kernel = compute.FindKernel("BakeNoise");
            if (kernel < 0)
            {
                Debug.LogError("[PlumeTrails] Compute shader has no 'BakeNoise' kernel - the bundle is " +
                    "older than the plugin. Rebuild the AssetBundle.");
                return;
            }

            RenderTexture tex = new RenderTexture(NoiseResolution, NoiseResolution, 0, RenderTextureFormat.ARGBHalf)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = NoiseResolution,
                enableRandomWrite = true,
                // Repeat is what makes the tiling work at all - the bake is periodic,
                // but only if sampling wraps instead of clamping at the faces
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                useMipMap = false
            };
            // Create() returning false is the last silent failure in this chain - a
            // writable 3D target can be refused even where compute itself is supported.
            if (!tex.Create())
            {
                Debug.LogError("[PlumeTrails] Failed to create the " + NoiseResolution + "^3 ARGBHalf 3D "
                    + "render texture (randomWrite). The smoke will not render. Device: "
                    + SystemInfo.graphicsDeviceType + ", shader level " + SystemInfo.graphicsShaderLevel + ".");
                return;
            }

            compute.SetTexture(kernel, "_NoiseOut", tex);
            compute.SetInts("_NoiseResolution", NoiseResolution, NoiseResolution, NoiseResolution);
            compute.SetFloat("_NoisePeriod", NoisePeriod);
            compute.SetFloat("_NoiseGradient", SmokeTuning.GradientNoise ? 1f : 0f);

            int groups = NoiseResolution / 4; // kernel is [numthreads(4,4,4)]
            compute.Dispatch(kernel, groups, groups, groups);

            ShaderCache.NoiseTexture = tex;
            Shader.SetGlobalTexture("_RaymarchedPlumeTrailsNoise", tex);
            BakeWorleyTexture(compute);
            Shader.SetGlobalFloat("_NoiseTilePeriod", NoisePeriod);

            Debug.Log(string.Format(
                "[PlumeTrails] Baked {0}^3 noise texture, period {1} cells.",
                NoiseResolution, NoisePeriod));
        }

        // THE NOISE OF THE NEW TRAIL: an inverted worley fbm (round, billowy lobes - no perlin, whose ridges read as a bony spine with bits stuck to it),
        // stretched to 0..1 by the min / max of the whole volume (found with atomics on the GPU). 4 cells per tile in the first octave, 5 octaves.
        private const int WorleyCells = 3, WorleyOctaves = 8;        // like KSA: big base puffs (~1 radius) with 8 octaves of smaller puffs on them; 3 octaves showed plates and terraces at a sharp edge
        private static void BakeWorleyTexture(ComputeShader compute)
        {
            int kb = compute.FindKernel("BakeWorley"), kn = compute.FindKernel("NormalizeWorley");
            if (kb < 0 || kn < 0) { Debug.LogError("[PlumeTrails] the bundle has no BakeWorley kernel - rebuild the AssetBundle"); return; }
            RenderTexture wt = new RenderTexture(NoiseResolution, NoiseResolution, 0, RenderTextureFormat.RHalf)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
                volumeDepth = NoiseResolution,
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                useMipMap = false
            };
            if (!wt.Create()) { Debug.LogError("[PlumeTrails] could not create the worley noise volume"); return; }
            ComputeBuffer stats = new ComputeBuffer(2, 4);
            stats.SetData(new uint[] { 0u, 0xffffffffu });
            compute.SetInts("_NoiseResolution", NoiseResolution, NoiseResolution, NoiseResolution);
            compute.SetInt("_WorleyCells", WorleyCells);
            compute.SetInt("_WorleyOctaves", WorleyOctaves);
            compute.SetFloat("_WorleySoftK", 4f);         // rounder cell edges (8 left creases that read as flat plates at the sharp edge)
            int groups = NoiseResolution / 4;
            compute.SetTexture(kb, "_WorleyOut", wt); compute.SetBuffer(kb, "_WorleyStats", stats);
            compute.Dispatch(kb, groups, groups, groups);
            compute.SetTexture(kn, "_WorleyOut", wt); compute.SetBuffer(kn, "_WorleyStats", stats);
            compute.Dispatch(kn, groups, groups, groups);
            stats.Release();
            ShaderCache.WorleyTexture = wt;
            Shader.SetGlobalTexture("_TrWorleyNoise", wt);
            Debug.Log("[PlumeTrails] Baked the " + NoiseResolution + "^3 worley noise of the trail (" + WorleyCells + " cells, " + WorleyOctaves + " octaves).");
        }

        private static void LoadShaderFromMaterial(AssetBundle bundle, string materialAssetName, ref Shader target)
        {
            Material mat = bundle.LoadAsset<Material>(materialAssetName);
            if (mat == null)
            {
                Debug.LogError("[PlumeTrails] Material '" + materialAssetName + "' missing from the bundle.");
                return;
            }

            target = mat.shader;

            // A shader can load fine and still be unusable - if it failed to compile for
            // this GPU, Unity silently swaps in the error shader instead of raising.
            if (!target.isSupported)
            {
                Debug.LogError("[PlumeTrails] Shader '" + target.name + "' is NOT supported on this GPU ("
                    + SystemInfo.graphicsDeviceType + ", shader level " + SystemInfo.graphicsShaderLevel
                    + "). It needs shader model 3.5 or better. The smoke will not render.");
                return;
            }

            Debug.Log("[PlumeTrails] Loaded shader from '" + materialAssetName + "': " + target.name
                + " (supported)");
        }
    }
}

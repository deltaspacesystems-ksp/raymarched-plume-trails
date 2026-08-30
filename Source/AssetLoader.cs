using System.IO;
using UnityEngine;

namespace VolumetricContrails
{
    // static shader cache, read by SmokeVolumeGroup since Shader.Find won't find a
    // custom shader until the asset bundle loads
    public static class ShaderCache
    {
        public static Shader SmokeVolumeShader;
        public static Shader SmokeCompositeShader;
        public static ComputeShader SmokeVolumeSplatCompute;
        public static RenderTexture NoiseTexture;
    }

    // runs once on the main menu, before any vessel loads
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class AssetLoader : MonoBehaviour
    {
        private const string BundleFileName = "volumetriccontrails_bundle";
        // Legacy fallback only. A hardcoded folder name silently breaks for anyone who
        // RENAMES the mod folder: KSP loads the assembly regardless, so the DLL runs and
        // the UI appears while the bundle lookup goes nowhere - "installed, UI works,
        // nothing renders". ResolveBundlePath derives the folder from the assembly.
        private const string BundleRelativePath = "VolumetricContrails/Bundles/" + BundleFileName;
        private const string SmokeVolumeMaterialAssetName = "SmokeVolumeMat";
        private const string SmokeVolumeSplatComputeAssetName = "SmokeVolumeSplat";
        private const string SmokeCompositeShaderAssetName = "SmokeComposite";

        // Directory this DLL was loaded from, i.e. <GameData>/<whatever>/Plugins.
        private static string AssemblyDirectory()
        {
            return Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
        }

        // Finds the bundle relative to the ASSEMBLY rather than a hardcoded folder name,
        // so renaming the mod folder cannot break it. Tries, in order:
        //   <pluginDir>/../Bundles/<file>   - the normal layout (Plugins and Bundles as
        //                                     siblings inside the mod folder)
        //   <pluginDir>/<file>              - bundle dropped in beside the DLL
        //   GameData/VolumetricContrails/Bundles/<file> - legacy absolute path
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

        private void Awake()
        {
            // Environment banner. "UI is there, bundle is there, nothing renders" is
            // otherwise undiagnosable from a log - every requirement below can fail
            // silently. Ask for these lines first in any blank-screen report.
            Debug.Log(string.Format(
                "[HairyBlob] graphics: device={0} shaderLevel={1} computeShaders={2} " +
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
                Debug.LogError("[HairyBlob] AssetBundle '" + BundleFileName + "' not found. Expected it "
                    + "in a 'Bundles' folder next to this plugin's own folder. Checked alongside the "
                    + "assembly at: " + AssemblyDirectory()
                    + " and at the legacy path GameData/" + BundleRelativePath);
                return;
            }

            Debug.Log("[HairyBlob] Loading AssetBundle from: " + bundlePath);
            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogError("[HairyBlob] AssetBundle.LoadFromFile returned null. The Unity version " +
                    "used to build the bundle probably does not match KSP's.");
                return;
            }

            LoadShaderFromMaterial(bundle, SmokeVolumeMaterialAssetName, ref ShaderCache.SmokeVolumeShader);

            // the composite shader has no material of its own, so it is loaded directly
            ShaderCache.SmokeCompositeShader = bundle.LoadAsset<Shader>(SmokeCompositeShaderAssetName);
            if (ShaderCache.SmokeCompositeShader == null)
            {
                Debug.LogError("[HairyBlob] Composite shader '" + SmokeCompositeShaderAssetName +
                    "' missing from the bundle - smoke will not be drawn. Rebuild the AssetBundle.");
            }

            ShaderCache.SmokeVolumeSplatCompute = bundle.LoadAsset<ComputeShader>(SmokeVolumeSplatComputeAssetName);
            if (ShaderCache.SmokeVolumeSplatCompute == null)
            {
                Debug.LogError("[HairyBlob] Compute shader '" + SmokeVolumeSplatComputeAssetName + "' missing from the bundle.");
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
                Debug.LogError("[HairyBlob] GPU/graphics API reports NO compute shader support (device: "
                    + SystemInfo.graphicsDeviceType + "). The noise volume cannot be baked and the smoke "
                    + "will not render. If KSP was launched with -force-glcore, -force-opengl or "
                    + "-force-d3d9, remove that flag so it runs on DirectX 11.");
                return;
            }

            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Debug.LogError("[HairyBlob] ARGBHalf render textures are unsupported on this device ("
                    + SystemInfo.graphicsDeviceType + ") - cannot bake the noise volume.");
                return;
            }

            int kernel = compute.FindKernel("BakeNoise");
            if (kernel < 0)
            {
                Debug.LogError("[HairyBlob] Compute shader has no 'BakeNoise' kernel - the bundle is " +
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
                Debug.LogError("[HairyBlob] Failed to create the " + NoiseResolution + "^3 ARGBHalf 3D "
                    + "render texture (randomWrite). The smoke will not render. Device: "
                    + SystemInfo.graphicsDeviceType + ", shader level " + SystemInfo.graphicsShaderLevel + ".");
                return;
            }

            compute.SetTexture(kernel, "_NoiseOut", tex);
            compute.SetInts("_NoiseResolution", NoiseResolution, NoiseResolution, NoiseResolution);
            compute.SetFloat("_NoisePeriod", NoisePeriod);

            int groups = NoiseResolution / 4; // kernel is [numthreads(4,4,4)]
            compute.Dispatch(kernel, groups, groups, groups);

            ShaderCache.NoiseTexture = tex;
            Shader.SetGlobalTexture("_VolumetricContrailsNoise", tex);
            Shader.SetGlobalFloat("_NoiseTilePeriod", NoisePeriod);

            Debug.Log(string.Format(
                "[HairyBlob] Baked {0}^3 noise texture, period {1} cells.",
                NoiseResolution, NoisePeriod));
        }

        private void LoadShaderFromMaterial(AssetBundle bundle, string materialAssetName, ref Shader target)
        {
            Material mat = bundle.LoadAsset<Material>(materialAssetName);
            if (mat == null)
            {
                Debug.LogError("[HairyBlob] Material '" + materialAssetName + "' missing from the bundle.");
                return;
            }

            target = mat.shader;

            // A shader can load fine and still be unusable - if it failed to compile for
            // this GPU, Unity silently swaps in the error shader instead of raising.
            if (!target.isSupported)
            {
                Debug.LogError("[HairyBlob] Shader '" + target.name + "' is NOT supported on this GPU ("
                    + SystemInfo.graphicsDeviceType + ", shader level " + SystemInfo.graphicsShaderLevel
                    + "). It needs shader model 3.5 or better. The smoke will not render.");
                return;
            }

            Debug.Log("[HairyBlob] Loaded shader from '" + materialAssetName + "': " + target.name
                + " (supported)");
        }
    }
}

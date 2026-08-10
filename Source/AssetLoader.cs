using System.IO;
using UnityEngine;

namespace VolumetricContrails
{
    /// <summary>
    /// Statyczny cache shaderów - PuffTrailMesh/SmokeVolumeGroup czytają stąd zamiast robić Shader.Find,
    /// bo Shader.Find nie znajdzie custom shadera dopóki AssetBundle się nie załaduje.
    /// </summary>
    public static class ShaderCache
    {
        public static Shader ContrailShader;
        public static Shader PuffShader;
        public static Shader SmokeVolumeShader;
        public static ComputeShader SmokeVolumeSplatCompute;
    }

    /// <summary>
    /// KSPAddon uruchamiany raz, na ekranie startowym gry (MainMenu), zanim jakikolwiek
    /// statek zostanie załadowany - więc ShaderCache.ContrailShader jest gotowy zanim
    /// ContrailVesselController w ogóle zacznie działać.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class AssetLoader : MonoBehaviour
    {
        // Ścieżka względna do GameData - dostosuj jeśli zmienisz nazwę folderu bundla.
        private const string BundleRelativePath = "VolumetricContrails/Bundles/volumetriccontrails_bundle";
        private const string ContrailMaterialAssetName = "ContrailMat";
        private const string PuffMaterialAssetName = "PuffMat";
        private const string SmokeVolumeMaterialAssetName = "SmokeVolumeMat";
        private const string SmokeVolumeSplatComputeAssetName = "SmokeVolumeSplat";

        private void Awake()
        {
            // Bezpieczny domyślny ZTest (LEqual=4, zwykłe zachowanie) - MUSI być
            // ustawiony zanim jakikolwiek SmokeVolumeGroup w ogóle mógłby się
            // wyrenderować, inaczej niezainicjalizowana globalna zmienna w shaderze
            // czytałaby się jako 0 (Disabled) = dym rysowałby się nad wszystkim,
            // dokładnie ten sam bug co przy wcześniejszym ZTest Always. Patrz
            // LaunchpadOcclusionExcluder.cs - to on przełącza na Always (8) w locie,
            // dopiero gdy jego kamera głębi z wykluczonym launchpadem jest gotowa.
            Shader.SetGlobalInt("_GlobalZTestMode", 4);

            string bundlePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", BundleRelativePath);

            if (!File.Exists(bundlePath))
            {
                Debug.LogError("[VolumetricContrails] Nie znaleziono AssetBundle pod: " + bundlePath);
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle == null)
            {
                Debug.LogError("[VolumetricContrails] AssetBundle.LoadFromFile zwrócił null - " +
                    "sprawdź czy wersja Unity użyta do zbudowania bundla zgadza się z wersją KSP.");
                return;
            }

            LoadShaderFromMaterial(bundle, ContrailMaterialAssetName, ref ShaderCache.ContrailShader);
            LoadShaderFromMaterial(bundle, PuffMaterialAssetName, ref ShaderCache.PuffShader);
            LoadShaderFromMaterial(bundle, SmokeVolumeMaterialAssetName, ref ShaderCache.SmokeVolumeShader);

            ShaderCache.SmokeVolumeSplatCompute = bundle.LoadAsset<ComputeShader>(SmokeVolumeSplatComputeAssetName);
            if (ShaderCache.SmokeVolumeSplatCompute == null)
            {
                Debug.LogError("[VolumetricContrails] Nie znaleziono ComputeShader '" + SmokeVolumeSplatComputeAssetName + "' w bundlu.");
            }

            // Nie zwalniamy assetów (false) - shadery muszą zostać w pamięci na cały czas gry.
            bundle.Unload(false);
        }

        private void LoadShaderFromMaterial(AssetBundle bundle, string materialAssetName, ref Shader target)
        {
            Material mat = bundle.LoadAsset<Material>(materialAssetName);
            if (mat == null)
            {
                Debug.LogError("[VolumetricContrails] Nie znaleziono materiału '" + materialAssetName +
                    "' w bundlu - sprawdź dokładną nazwę assetu.");
                return;
            }

            target = mat.shader;
            Debug.Log("[VolumetricContrails] Shader wczytany poprawnie z '" + materialAssetName + "': " + target.name);
        }
    }
}

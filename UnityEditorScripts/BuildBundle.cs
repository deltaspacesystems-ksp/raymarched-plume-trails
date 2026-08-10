using UnityEditor;
using System.IO;

// UWAGA: ten plik idzie do Assets/Editor/ w projekcie Unity (2019.4.18f1),
// NIE do naszego moda C#. To narzędzie edytorowe, nie kod runtime.

public class BuildBundle
{
    [MenuItem("Assets/Build VolumetricContrails Bundle")]
    static void Build()
    {
        string outputDir = "AssetBundles";
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        BuildPipeline.BuildAssetBundles(
            outputDir,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64); // zmień na StandaloneLinux64 jeśli budujesz pod Linuxa

        UnityEngine.Debug.Log("AssetBundle zbudowany w: " + Path.GetFullPath(outputDir));
    }
}

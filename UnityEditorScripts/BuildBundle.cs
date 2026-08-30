using UnityEditor;
using System.IO;

// Lives in Assets/Editor/ of the Unity project (2019.4.18f1).
// Editor tooling, not part of the shipped mod assembly.

public class BuildBundle
{
    [MenuItem("Assets/Build VolumetricContrails Bundle")]
    static void Build()
    {
        // Assign the bundle name programmatically instead of relying on each asset's
        // .meta. New assets otherwise import without one and get silently left out of the
        // bundle, which shows up much later as a null shader at runtime.
        const string BundleName = "volumetriccontrails_bundle";
        foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets/VolumetricContrails" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path)) continue;
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null || importer.assetBundleName == BundleName) continue;
            importer.assetBundleName = BundleName;
            importer.SaveAndReimport();
            UnityEngine.Debug.Log("[BuildBundle] assigned bundle to " + path);
        }

        string outputDir = "AssetBundles";
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        BuildPipeline.BuildAssetBundles(
            outputDir,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64); // StandaloneLinux64 for a Linux build

        UnityEngine.Debug.Log("[BuildBundle] built to " + Path.GetFullPath(outputDir));
    }
}

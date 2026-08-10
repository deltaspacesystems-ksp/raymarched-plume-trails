using UnityEngine;
using UnityEditor;
using System.IO;

// UWAGA: ten plik idzie do Assets/Editor/ w projekcie Unity, obok BuildBundle.cs.
// To narzędzie edytorowe (buduje asset raz), nie kod runtime moda.

public class BuildCloudTexture3D
{
    private const int SIZE = 32; // musi się zgadzać z rozmiarem wygenerowanym w Pythonie (CloudNoise3D.bytes)

    [MenuItem("Assets/Build Cloud Noise 3D Texture")]
    static void Build()
    {
        // Znajdź CloudNoise3D.bytes gdziekolwiek w Assets (żeby nie zależeć od konkretnej ścieżki)
        string[] guids = AssetDatabase.FindAssets("CloudNoise3D t:TextAsset");
        if (guids.Length == 0)
        {
            Debug.LogError("Nie znaleziono CloudNoise3D.bytes w Assets. Upewnij się że plik jest zaimportowany.");
            return;
        }

        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        TextAsset rawData = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        byte[] bytes = rawData.bytes;

        int expectedLength = SIZE * SIZE * SIZE * 4; // RGBA8
        if (bytes.Length != expectedLength)
        {
            Debug.LogError(string.Format(
                "Nieprawidłowy rozmiar danych: {0} bajtów, oczekiwano {1} (SIZE={2}). " +
                "Sprawdź czy SIZE w tym skrypcie zgadza się z Pythonem.",
                bytes.Length, expectedLength, SIZE));
            return;
        }

        Texture3D tex = new Texture3D(SIZE, SIZE, SIZE, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Repeat; // kluczowe dla bezszwowego kafelkowania
        tex.filterMode = FilterMode.Trilinear;

        Color[] colors = new Color[SIZE * SIZE * SIZE];
        for (int i = 0; i < colors.Length; i++)
        {
            int baseIdx = i * 4;
            colors[i] = new Color(
                bytes[baseIdx] / 255f,
                bytes[baseIdx + 1] / 255f,
                bytes[baseIdx + 2] / 255f,
                bytes[baseIdx + 3] / 255f);
        }

        tex.SetPixels(colors);
        tex.Apply();

        string outputPath = "Assets/VolumetricContrails/CloudNoise3D.asset";
        AssetDatabase.CreateAsset(tex, outputPath);
        AssetDatabase.SaveAssets();

        Debug.Log("Texture3D zbudowana: " + outputPath + " (" + SIZE + "^3, " + colors.Length + " wokseli)");
    }
}

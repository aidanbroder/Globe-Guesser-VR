using System.IO;
using UnityEngine;

public class StreetViewSkyboxCached : MonoBehaviour
{
    [Header("Cache Settings")]
    public string locationName = "germany_hesse";
    public Material skyboxMaterial;

    void Start()
    {
        LoadSkyboxFromDisk();
    }

    void LoadSkyboxFromDisk()
    {
        string path = Path.Combine(Application.dataPath, "StreetViewTextures", locationName, "equirectangular.png");

        if (!File.Exists(path))
        {
            Debug.LogError($"No cached equirectangular.png found at {path}.");
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        Texture2D tex = new Texture2D(2, 2);
        tex.LoadImage(bytes);
        tex.wrapMode = TextureWrapMode.Repeat;

        Material runtimeSkybox = new Material(skyboxMaterial);
        runtimeSkybox.SetTexture("_MainTex", tex);
        RenderSettings.skybox = runtimeSkybox;
        DynamicGI.UpdateEnvironment();

        Debug.Log($"[CACHED] Skybox loaded from disk: {locationName}");
    }
}
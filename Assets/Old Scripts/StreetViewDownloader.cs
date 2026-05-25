using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class StreetViewDownloader : MonoBehaviour
{
    [Header("Street View Settings")]
    public string apiKey = "YOUR_API_KEY_HERE";
    public string locationName = "germany_hesse";

    [Header("Metadata")]
    public float latitude = 50.4497107f;
    public float longitude = 8.9561217f;
    public string country = "Germany";
    public string region = "Hesse";
    public string timeZone = "Europe/Berlin";
    public string difficulty = "medium";

    // 6 cube faces x 4 quadrants = 24 images at 45 FOV
    // heading offset from face center: atan(0.5) = 26.565 degrees
    // pitch offset from face center:   asin(0.5 / sqrt(1.5)) = 24.094 degrees
    // top/bottom cap pitch:            asin(1 / sqrt(1.5)) = 54.735 degrees
    private const float SH = 26.565f; // side heading offset
    private const float SP = 24.094f; // side pitch offset
    private const float CP = 54.735f; // cap pitch

    private static readonly float[] faceHeadings = {
        // North face (heading 0): top-left, top-right, bottom-left, bottom-right
        360 - SH,     SH,  360 - SH,     SH,
        // East face (heading 90)
         90 - SH, 90 + SH,  90 - SH, 90 + SH,
        // South face (heading 180)
        180 - SH, 180 + SH, 180 - SH, 180 + SH,
        // West face (heading 270)
        270 - SH, 270 + SH, 270 - SH, 270 + SH,
        // Top cap: NE, NW, SE, SW quadrants
        45, 315, 135, 225,
        // Bottom cap: NE, NW, SE, SW quadrants
        45, 315, 135, 225
    };
    private static readonly float[] facePitches = {
        // North face
         SP,  SP, -SP, -SP,
        // East face
         SP,  SP, -SP, -SP,
        // South face
         SP,  SP, -SP, -SP,
        // West face
         SP,  SP, -SP, -SP,
        // Top cap
         CP,  CP,  CP,  CP,
        // Bottom cap
        -CP, -CP, -CP, -CP
    };
    private static readonly string[] faceNames = {
        "n_tl", "n_tr", "n_bl", "n_br",
        "e_tl", "e_tr", "e_bl", "e_br",
        "s_tl", "s_tr", "s_bl", "s_br",
        "w_tl", "w_tr", "w_bl", "w_br",
        "t_ne", "t_nw", "t_se", "t_sw",
        "b_ne", "b_nw", "b_se", "b_sw"
    };

    void Start()
    {
        StartCoroutine(DownloadAndStitch());
    }

    IEnumerator DownloadAndStitch()
    {
        int faceSize = 640;
        int faceCount = faceNames.Length; // 24
        string location = $"{latitude},{longitude}";
        string folder = Path.Combine(Application.dataPath, "StreetViewTextures", locationName);

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string equirectPath = Path.Combine(folder, "equirectangular.png");
        if (File.Exists(equirectPath))
        {
            Debug.Log("Equirectangular already exists, skipping. Delete it to re-download.");
            yield break;
        }

        Texture2D[] faceTextures = new Texture2D[faceCount];

        for (int i = 0; i < faceCount; i++)
        {
            string filePath = Path.Combine(folder, $"{faceNames[i]}.png");

            if (File.Exists(filePath))
            {
                byte[] existingBytes = File.ReadAllBytes(filePath);
                Texture2D existingTex = new Texture2D(2, 2);
                existingTex.LoadImage(existingBytes);
                faceTextures[i] = existingTex;
                Debug.Log($"Loaded cached face: {faceNames[i]}");
                continue;
            }

            string url = $"https://maps.googleapis.com/maps/api/streetview" +
                         $"?size={faceSize}x{faceSize}" +
                         $"&location={location}" +
                         $"&heading={faceHeadings[i]}" +
                         $"&pitch={facePitches[i]}" +
                         $"&fov=45" +
                         $"&key={apiKey}";

            UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to download face {faceNames[i]}: {request.error}");
                yield break;
            }

            Texture2D tex = DownloadHandlerTexture.GetContent(request);
            File.WriteAllBytes(filePath, tex.EncodeToPNG());
            faceTextures[i] = tex;
            Debug.Log($"Downloaded: {faceNames[i]} ({i + 1}/24)");
        }

        Debug.Log("All 24 faces ready. Stitching equirectangular (this may take ~30 seconds)...");
        Texture2D equirect = StitchEquirectangular(faceTextures, 4096, 2048);
        File.WriteAllBytes(equirectPath, equirect.EncodeToPNG());

        // write metadata.json
        string metadata =
            "{\n" +
            $"    \"locationName\": \"{locationName}\",\n" +
            $"    \"latitude\": {latitude},\n" +
            $"    \"longitude\": {longitude},\n" +
            $"    \"country\": \"{country}\",\n" +
            $"    \"region\": \"{region}\",\n" +
            $"    \"timeZone\": \"{timeZone}\",\n" +
            $"    \"difficulty\": \"{difficulty}\"\n" +
            "}";
        File.WriteAllText(Path.Combine(folder, "metadata.json"), metadata);

        Debug.Log("Saved equirectangular.png and metadata.json! Remove this GameObject before building.");

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    Texture2D StitchEquirectangular(Texture2D[] faces, int outWidth, int outHeight)
    {
        Texture2D result = new Texture2D(outWidth, outHeight, TextureFormat.RGB24, false);
        float tanHalfFov = Mathf.Tan(22.5f * Mathf.Deg2Rad); // tan(45 / 2)

        Vector3[] forwards = new Vector3[faces.Length];
        Vector3[] rights   = new Vector3[faces.Length];
        Vector3[] ups      = new Vector3[faces.Length];

        for (int i = 0; i < faces.Length; i++)
        {
            float h = faceHeadings[i] * Mathf.Deg2Rad;
            float p = facePitches[i] * Mathf.Deg2Rad;

            forwards[i] = new Vector3(
                Mathf.Cos(p) * Mathf.Sin(h),
                Mathf.Sin(p),
                Mathf.Cos(p) * Mathf.Cos(h)
            );

            if (Mathf.Abs(facePitches[i]) > 89f)
                rights[i] = new Vector3(1, 0, 0);
            else
                rights[i] = Vector3.Normalize(Vector3.Cross(Vector3.up, forwards[i]));

            ups[i] = Vector3.Normalize(Vector3.Cross(forwards[i], rights[i]));
        }

        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                float lon = ((float)x / outWidth) * 2f * Mathf.PI;
                float lat = (1f - (float)y / outHeight) * Mathf.PI - Mathf.PI / 2f;

                Vector3 dir = new Vector3(
                    Mathf.Cos(lat) * Mathf.Sin(lon),
                    Mathf.Sin(lat),
                    Mathf.Cos(lat) * Mathf.Cos(lon)
                );

                Color bestColor = Color.black;
                float bestDotForward = -1f;

                for (int i = 0; i < faces.Length; i++)
                {
                    float dotForward = Vector3.Dot(dir, forwards[i]);
                    if (dotForward <= 0f) continue;

                    float xNdc = Vector3.Dot(dir, rights[i]) / (dotForward * tanHalfFov);
                    float yNdc = Vector3.Dot(dir, ups[i])    / (dotForward * tanHalfFov);

                    if (Mathf.Abs(xNdc) > 1f || Mathf.Abs(yNdc) > 1f) continue;

                    if (dotForward > bestDotForward)
                    {
                        bestDotForward = dotForward;
                        float u = (xNdc + 1f) / 2f;
                        float v = (yNdc + 1f) / 2f;
                        bestColor = faces[i].GetPixelBilinear(u, v);
                    }
                }

                result.SetPixel(x, y, bestColor);
            }
        }

        result.Apply();
        return result;
    }
}

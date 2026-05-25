using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class StreetViewTilesDownloader : MonoBehaviour
{
    [Header("API Settings")]
    public string apiKey = "YOUR_API_KEY_HERE";

    [Header("Location")]
    public string locationName = "germany_hesse";
    public float latitude  = 50.4497107f;
    public float longitude = 8.9561217f;

    [Header("Metadata")]
    public string country    = "Germany";
    public string region     = "Hesse";
    public string timeZone   = "Europe/Berlin";
    public string difficulty = "medium";

    void Start()
    {
        StartCoroutine(DownloadAndSave());
    }

    IEnumerator DownloadAndSave()
    {
        string folder = Path.Combine(Application.dataPath, "StreetViewTextures", locationName);
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string equirectPath = Path.Combine(folder, "equirectangular.png");
        if (File.Exists(equirectPath))
        {
            Debug.Log("equirectangular.png already exists. Delete it to re-download.");
            yield break;
        }

        // Step 1: get session token
        string sessionToken = null;
        yield return StartCoroutine(FetchSessionToken(result => sessionToken = result));
        if (sessionToken == null)
        {
            Debug.LogError("Failed to get session token.");
            yield break;
        }

        // Step 2: get panoId
        string panoId = null;
        yield return StartCoroutine(FetchPanoId(sessionToken, result => panoId = result));
        if (panoId == null)
        {
            Debug.LogError("Failed to get panoId. No Street View coverage at this location?");
            yield break;
        }

        // Step 3: fetch 32 tiles at zoom 3 (8x4 grid) and stitch
        int zoom        = 3;
        int tilesX      = 8;
        int tilesY      = 4;
        int tileSize    = 512;
        int totalWidth  = tilesX * tileSize; // 4096
        int totalHeight = tilesY * tileSize; // 2048

        Texture2D panorama = new Texture2D(totalWidth, totalHeight, TextureFormat.RGB24, false);

        for (int y = 0; y < tilesY; y++)
        {
            for (int x = 0; x < tilesX; x++)
            {
                string url = $"https://tile.googleapis.com/v1/streetview/tiles/{zoom}/{x}/{y}" +
                             $"?session={sessionToken}&panoId={panoId}&key={apiKey}";

                UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Tile ({x},{y}) failed: {request.error}");
                    yield break;
                }

                Texture2D tile = DownloadHandlerTexture.GetContent(request);
                panorama.SetPixels(
                    x * tileSize,
                    (tilesY - 1 - y) * tileSize,
                    tileSize,
                    tileSize,
                    tile.GetPixels()
                );

                Debug.Log($"Tile ({x},{y}) downloaded — {y * tilesX + x + 1}/32");
            }
        }

        panorama.Apply();
        File.WriteAllBytes(equirectPath, panorama.EncodeToPNG());
        Debug.Log($"Saved equirectangular.png to {equirectPath}");

        // Step 4: write metadata.json
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
        Debug.Log("Saved metadata.json");

        Debug.Log("Done! Remove this GameObject before building.");

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    IEnumerator FetchSessionToken(System.Action<string> callback)
    {
        string url  = $"https://tile.googleapis.com/v1/createSession?key={apiKey}";
        string body = "{\"mapType\": \"streetview\", \"language\": \"en-US\", \"region\": \"US\"}";

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Session token failed: {request.error}");
            callback(null);
            yield break;
        }

        string token = ParseJsonField(request.downloadHandler.text, "session");
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogError("Could not parse session token.");
            callback(null);
            yield break;
        }

        Debug.Log("Session token obtained.");
        callback(token);
    }

    IEnumerator FetchPanoId(string sessionToken, System.Action<string> callback)
    {
        string url  = $"https://tile.googleapis.com/v1/streetview/panoIds?session={sessionToken}&key={apiKey}";
        string body = $"{{\"locations\": [{{\"lat\": {latitude}, \"lng\": {longitude}}}], \"radius\": 50}}";

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"PanoId request failed: {request.error}");
            callback(null);
            yield break;
        }

        string json     = request.downloadHandler.text;
        int arrayStart  = json.IndexOf('[');
        int firstQuote  = json.IndexOf('"', arrayStart + 1);
        int secondQuote = json.IndexOf('"', firstQuote + 1);

        if (firstQuote < 0 || secondQuote <= firstQuote)
        {
            Debug.LogError("No panoId found in response.");
            callback(null);
            yield break;
        }

        string panoId = json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        Debug.Log($"PanoId: {panoId}");
        callback(panoId);
    }

    string ParseJsonField(string json, string field)
    {
        string search = $"\"{field}\"";
        int idx = json.IndexOf(search);
        if (idx < 0) return null;

        int colon      = json.IndexOf(':', idx + search.Length);
        int firstQuote = json.IndexOf('"', colon + 1);
        int lastQuote  = json.IndexOf('"', firstQuote + 1);

        if (firstQuote < 0 || lastQuote < 0) return null;
        return json.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
    }
}
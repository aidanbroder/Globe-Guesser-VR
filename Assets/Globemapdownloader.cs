using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class GlobeMapDownloader : MonoBehaviour
{
    [Header("API Settings")]
    public string apiKey = "YOUR_API_KEY_HERE";

    [Header("Map Settings")]
    // zoom 3 = 8x8 grid = 64 tiles = 2048x2048 output
    // zoom 4 = 16x16 grid = 256 tiles = 4096x4096 output
    public int zoom = 3;

    void Start()
    {
        StartCoroutine(DownloadGlobeMap());
    }

    IEnumerator DownloadGlobeMap()
    {
        string folder = Path.Combine(Application.dataPath, "GlobeTextures");
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        string outputPath = Path.Combine(folder, $"world_map_zoom{zoom}.png");
        if (File.Exists(outputPath))
        {
            Debug.Log($"Globe map already exists at {outputPath}. Delete it to re-download.");
            yield break;
        }

        // Step 1: create roadmap session token
        string sessionToken = null;
        yield return StartCoroutine(FetchRoadmapSessionToken(result => sessionToken = result));
        if (sessionToken == null)
        {
            Debug.LogError("Failed to get roadmap session token.");
            yield break;
        }

        // Step 2: fetch all tiles and stitch
        int gridSize   = (int)Mathf.Pow(2, zoom); // 8 at zoom 3, 16 at zoom 4
        int tileSize   = 256;
        int totalSize  = gridSize * tileSize;
        int totalTiles = gridSize * gridSize;

        Debug.Log($"Downloading {totalTiles} tiles at zoom {zoom} ({totalSize}x{totalSize} output)...");

        Texture2D worldMap = new Texture2D(totalSize, totalSize, TextureFormat.RGB24, false);

        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                string url = $"https://tile.googleapis.com/v1/2dtiles/{zoom}/{x}/{y}" +
                             $"?session={sessionToken}&key={apiKey}";

                UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"Tile ({x},{y}) failed: {request.error}");
                    yield break;
                }

                Texture2D tile = DownloadHandlerTexture.GetContent(request);

                // map tiles are top-to-bottom, Unity textures are bottom-to-top
                worldMap.SetPixels(
                    x * tileSize,
                    (gridSize - 1 - y) * tileSize,
                    tileSize,
                    tileSize,
                    tile.GetPixels()
                );

                int count = y * gridSize + x + 1;
                Debug.Log($"Tile ({x},{y}) downloaded — {count}/{totalTiles}");
            }
        }

        worldMap.Apply();
        File.WriteAllBytes(outputPath, worldMap.EncodeToPNG());
        Debug.Log($"Globe map saved to {outputPath}. Remove this GameObject before building.");

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
    }

    IEnumerator FetchRoadmapSessionToken(System.Action<string> callback)
    {
        string url  = $"https://tile.googleapis.com/v1/createSession?key={apiKey}";
        string body = "{\"mapType\": \"roadmap\", \"language\": \"en-US\", \"region\": \"US\"}";

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        request.uploadHandler   = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"Roadmap session token failed: {request.error}");
            callback(null);
            yield break;
        }

        string token = ParseJsonField(request.downloadHandler.text, "session");
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogError("Could not parse roadmap session token.");
            callback(null);
            yield break;
        }

        Debug.Log("Roadmap session token obtained.");
        callback(token);
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
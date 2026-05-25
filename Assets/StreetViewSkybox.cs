using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class StreetViewSkybox : MonoBehaviour
{
    [Header("API Settings")]
    public string apiKey = "YOUR_API_KEY_HERE";

    [Header("Location (overridden by LocationManager at runtime)")]
    public float latitude  = 50.4497107f;
    public float longitude = 8.9561217f;

    [Header("Loading UI")]
    public GameObject loadingScreen;

    private const string TOKEN_KEY        = "StreetViewSessionToken";
    private const string TOKEN_EXPIRY_KEY = "StreetViewSessionExpiry";
    private const float  EXPIRY_DAYS      = 13f;

    [Header("Tile Grid")]
    [Tooltip("Street View tile zoom level.  Each zoom step doubles the resolution. " +
             "3 ≈ 4096×2048 final panorama (8×4 tiles for a standard pano), which is " +
             "the sweet spot for VR sharpness vs. download time / quota.")]
    public int zoom = 3;

    [Tooltip("Fallback tile grid width if the per-pano metadata request fails. " +
             "Standard Google-car panos at zoom 3 publish 8 columns; non-standard " +
             "panos override this via metadata at runtime.")]
    public int fallbackTilesX = 8;

    [Tooltip("Fallback tile grid height if the per-pano metadata request fails.")]
    public int fallbackTilesY = 4;

    [Header("Retry Behavior")]
    [Tooltip("How many times to fall back to a new random location if a panoId lookup " +
             "fails or the tile fetch comes back mostly empty.  Each retry costs one " +
             "panoId request + up to one full tile fetch.")]
    public int maxLocationRetries = 5;

    [Tooltip("Minimum fraction (0–1) of the expected tiles (per the pano's metadata) that " +
             "must download successfully for a panorama to be considered usable.  Below this " +
             "we discard the panorama and try a different location.  0.5 = need at least half.")]
    [Range(0f, 1f)]
    public float minTileSuccessRatio = 0.5f;

    [Tooltip("How many times to retry a single tile on TRANSIENT failures " +
             "(network error, 5xx, timeout).  Retries are skipped for 400/404 since " +
             "those are permanent — 400 means the tile coord is out of range for " +
             "this pano's actual image dimensions at the requested zoom (different " +
             "panos publish different resolutions; not all are 8×4 at zoom 3), and " +
             "404 means that specific tile doesn't exist (partial coverage pano).")]
    public int maxTileRetries = 3;

    [Tooltip("Seconds to wait between tile retries.  Short pause lets a transient CDN/network " +
             "blip clear before we re-request the same tile.")]
    public float tileRetryDelay = 0.25f;

    public Material skyboxMaterial;

    private Coroutine loadRoutine;

    void Start()
    {
        LoadNewLocation();
    }

    // Public entry point — GuessManager calls this on Next Round.
    // Stops any in-flight load so we don't clobber the skybox mid-fetch.
    public void LoadNewLocation()
    {
        if (loadRoutine != null) StopCoroutine(loadRoutine);
        loadRoutine = StartCoroutine(LoadSkybox());
    }

    IEnumerator LoadSkybox()
    {
        if (loadingScreen != null) loadingScreen.SetActive(true);

        // ── Step 1: get or refresh session token ────────────────────────────
        // Done once per LoadSkybox call (not per retry).  Token failures are
        // global — retrying with a different lat/lng won't help, so we bail.
        string sessionToken = GetCachedSessionToken();
        if (sessionToken == null)
        {
            yield return StartCoroutine(FetchSessionToken(result => sessionToken = result));
            if (sessionToken == null)
            {
                Debug.LogError("Failed to get session token.");
                if (loadingScreen != null) loadingScreen.SetActive(false);
                yield break;
            }
        }
        else
        {
            Debug.Log("Reusing cached session token.");
        }

        // ── Step 2 + 3: pick a location, resolve panoId, fetch tiles. ───────
        // Loop until one succeeds or we hit the retry cap.  Per-location
        // failures (no coverage, mostly-404 tiles) trigger a fresh pick.
        bool succeeded = false;
        for (int attempt = 1; attempt <= maxLocationRetries && !succeeded; attempt++)
        {
            StreetViewLocation loc = null;
            if (LocationManager.Instance != null)
            {
                loc = LocationManager.Instance.GetRandomLocation();
                if (loc == null)
                {
                    Debug.LogError("LocationManager returned null — no locations to try.");
                    break;
                }
                latitude  = loc.latitude;
                longitude = loc.longitude;
                Debug.Log($"[Attempt {attempt}/{maxLocationRetries}] Loading: {loc.locationName} " +
                          $"(lat={loc.latitude:F4}, lng={loc.longitude:F4})");
            }

            // PanoId — use the one provided in the location if available,
            // otherwise look it up from lat/lng.
            string panoId = null;
            if (loc != null && !string.IsNullOrEmpty(loc.panoId))
            {
                panoId = loc.panoId;
                Debug.Log($"Using provided panoId: {panoId}");
            }
            else
            {
                yield return StartCoroutine(FetchPanoId(sessionToken, result => panoId = result));
            }

            if (string.IsNullOrEmpty(panoId))
            {
                Debug.LogWarning($"[Attempt {attempt}] No panoId for {loc?.locationName} — " +
                                 "trying another location.");
                continue;
            }

            // Pano metadata — gives us the actual image dimensions for THIS specific
            // pano so we can compute the exact tile grid at the requested zoom.  Without
            // this we'd assume every pano is 8×4 at zoom 3, which 400s on panos that
            // publish narrower images (and leaves grey strips in the skybox).  If the
            // metadata request itself fails, we fall back to fallbackTilesX × fallbackTilesY.
            PanoMetadata meta = null;
            yield return StartCoroutine(FetchPanoMetadata(sessionToken, panoId, result => meta = result));

            // Tile fetch — returns false if too few tiles came back to be usable.
            bool tilesOk = false;
            yield return StartCoroutine(
                FetchTilesAndApplySkybox(sessionToken, panoId, meta, result => tilesOk = result));

            if (!tilesOk)
            {
                Debug.LogWarning($"[Attempt {attempt}] Tile fetch failed for {loc?.locationName} — " +
                                 "trying another location.");
                continue;
            }

            succeeded = true;
        }

        if (!succeeded)
            Debug.LogError($"All {maxLocationRetries} location attempts failed — " +
                           "skybox may be empty or stale.");

        if (loadingScreen != null) loadingScreen.SetActive(false);

        // Signal the controls overlay that the panorama is ready.  The overlay
        // only shows if RequestShowOnNextLoad() was queued (game start / Play Again);
        // between-round loads are ignored because the flag won't be set.
        if (succeeded && ControlsOverlay.Instance != null)
            ControlsOverlay.Instance.NotifyLoadComplete();
    }

    // -----------------------------------------------------------------------
    // Session Token
    // -----------------------------------------------------------------------

    string GetCachedSessionToken()
    {
        string token  = PlayerPrefs.GetString(TOKEN_KEY, "");
        string expiry = PlayerPrefs.GetString(TOKEN_EXPIRY_KEY, "");

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expiry))
            return null;

        if (System.DateTime.TryParse(expiry, out System.DateTime expiryDate))
            return System.DateTime.UtcNow < expiryDate ? token : null;

        return null;
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
            Debug.LogError($"Session token request failed: {request.error}");
            callback(null);
            yield break;
        }

        string token = ParseJsonField(request.downloadHandler.text, "session");
        if (string.IsNullOrEmpty(token))
        {
            Debug.LogError("Could not parse session token from response.");
            callback(null);
            yield break;
        }

        System.DateTime expiry = System.DateTime.UtcNow.AddDays(EXPIRY_DAYS);
        PlayerPrefs.SetString(TOKEN_KEY, token);
        PlayerPrefs.SetString(TOKEN_EXPIRY_KEY, expiry.ToString("o"));
        PlayerPrefs.Save();
        Debug.Log("New session token fetched and cached for 13 days.");

        callback(token);
    }

    // -----------------------------------------------------------------------
    // PanoId
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // Pano Metadata
    // -----------------------------------------------------------------------

    // Captures the per-pano fields we need to compute the correct tile grid.
    // Different panos publish different image resolutions — standard Google-car
    // panos are 16384×8192 (→ 8×4 tiles at zoom 3), but trekker / user / older
    // panos are often smaller (e.g. 13312×6656 → 7×4) or differently shaped.
    private class PanoMetadata
    {
        public int imageWidth, imageHeight;
        public int tileWidth, tileHeight;
        public int maxZoom;     // derived from imageWidth/tileWidth — see FetchPanoMetadata
    }

    IEnumerator FetchPanoMetadata(string sessionToken, string panoId, System.Action<PanoMetadata> callback)
    {
        string url = $"https://tile.googleapis.com/v1/streetview/metadata" +
                     $"?session={sessionToken}&key={apiKey}&panoId={panoId}";

        UnityWebRequest req = UnityWebRequest.Get(url);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"Pano metadata fetch failed (code={req.responseCode}, err={req.error}). " +
                             $"Falling back to {fallbackTilesX}×{fallbackTilesY} grid.");
            callback(null);
            yield break;
        }

        string json = req.downloadHandler.text;
        var meta = new PanoMetadata
        {
            imageWidth  = ParseJsonInt(json, "imageWidth",  0),
            imageHeight = ParseJsonInt(json, "imageHeight", 0),
            tileWidth   = ParseJsonInt(json, "tileWidth",   512),
            tileHeight  = ParseJsonInt(json, "tileHeight",  512),
        };

        if (meta.imageWidth <= 0 || meta.imageHeight <= 0)
        {
            Debug.LogWarning($"Pano metadata missing image dimensions. " +
                             $"Falling back to {fallbackTilesX}×{fallbackTilesY} grid. JSON: {json}");
            callback(null);
            yield break;
        }

        // maxZoom is the highest zoom level the pano supports — implied by the
        // ratio of image to tile size.  For 16384/512 = 32 tiles wide → maxZoom 5
        // (since 2^5 = 32).  For non-power-of-2 widths we round up so we never
        // request a zoom level the pano can't render.  At zoom z, the rendered
        // image is downscaled by 2^(maxZoom - z), and the tile grid follows.
        meta.maxZoom = Mathf.CeilToInt(Mathf.Log(meta.imageWidth / (float)meta.tileWidth, 2f));

        Debug.Log($"Pano metadata: {meta.imageWidth}×{meta.imageHeight}px, " +
                  $"tile {meta.tileWidth}×{meta.tileHeight}, derived maxZoom={meta.maxZoom}");
        callback(meta);
    }

    // Compute the exact tile grid (cols × rows) at the requested zoom level for a
    // given pano.  At zoom z, the rendered image dimension is
    //   imageDim * 2^(z - maxZoom) = imageDim / 2^(maxZoom - z)
    // and the number of tiles is the ceiling of that divided by tileDim.
    void ComputeTileGrid(PanoMetadata meta, int requestedZoom, out int tilesX, out int tilesY, out int effectiveZoom)
    {
        if (meta == null)
        {
            tilesX        = fallbackTilesX;
            tilesY        = fallbackTilesY;
            effectiveZoom = requestedZoom;
            return;
        }

        // Clamp zoom so we never ask for more detail than the pano publishes.
        effectiveZoom = Mathf.Clamp(requestedZoom, 0, meta.maxZoom);

        float scale  = Mathf.Pow(2f, meta.maxZoom - effectiveZoom);   // ≥ 1
        tilesX = Mathf.CeilToInt(meta.imageWidth  / (meta.tileWidth  * scale));
        tilesY = Mathf.CeilToInt(meta.imageHeight / (meta.tileHeight * scale));

        // Safety floor — never zero, never negative.
        tilesX = Mathf.Max(1, tilesX);
        tilesY = Mathf.Max(1, tilesY);
    }

    // -----------------------------------------------------------------------
    // Tile Fetching + Skybox
    // -----------------------------------------------------------------------

    IEnumerator FetchTilesAndApplySkybox(string sessionToken, string panoId, PanoMetadata meta, System.Action<bool> callback)
    {
        ComputeTileGrid(meta, zoom, out int tilesX, out int tilesY, out int effectiveZoom);

        int tileSize   = meta != null ? meta.tileWidth : 512;
        int totalTiles = tilesX * tilesY;

        // Panorama texture is sized to the pano's ACTUAL image dimensions at the
        // requested zoom — NOT to the full tile-grid extent.  For non-power-of-2
        // panos (e.g. a 13312×6656 pano renders to 3328×1664 at zoom 3, served
        // as a 7×4 tile grid), the rightmost tile and bottom row are partial:
        // only the top-left of the source tile contains real pixels and the rest
        // is empty.  If we sized the destination to 7*512 = 3584px wide and laid
        // tiles flush, the unused ~7% of the texture would alias into the skybox
        // because the equirect shader maps UV [0,1] → 360°, producing the visible
        // "duplicated tile" / wrap seam.  Cropping to the real content size makes
        // the seam land exactly at the meridian (UV.x = 0 == UV.x = 1).
        int contentWidth, contentHeight;
        if (meta != null)
        {
            float scale   = Mathf.Pow(2f, meta.maxZoom - effectiveZoom);
            contentWidth  = Mathf.CeilToInt(meta.imageWidth  / scale);
            contentHeight = Mathf.CeilToInt(meta.imageHeight / scale);
        }
        else
        {
            contentWidth  = tilesX * tileSize;
            contentHeight = tilesY * tileSize;
        }

        Debug.Log($"Fetching {tilesX}×{tilesY} = {totalTiles} tiles at zoom {effectiveZoom} " +
                  $"(panorama will be {contentWidth}×{contentHeight}px content; " +
                  $"tile-grid extent {tilesX * tileSize}×{tilesY * tileSize}px).");

        Texture2D panorama = new Texture2D(contentWidth, contentHeight, TextureFormat.RGBA32, false);
        int successCount = 0;

        for (int y = 0; y < tilesY; y++)
        {
            for (int x = 0; x < tilesX; x++)
            {
                string url = $"https://tile.googleapis.com/v1/streetview/tiles/{effectiveZoom}/{x}/{y}" +
                             $"?session={sessionToken}&panoId={panoId}&key={apiKey}";

                // Retry loop — for transient failures (network drops, CDN propagation gaps)
                // a quick re-request usually succeeds.  For "real" 404s (partial-coverage
                // pano or zoom unsupported) the retries also 404, but we cap at maxTileRetries
                // so we don't waste many seconds on each missing tile.  Worst-case extra cost
                // for a fully-missing tile: maxTileRetries * tileRetryDelay (~0.75s default).
                Texture2D tile = null;
                long      lastResponseCode = 0;
                string    lastError        = null;
                for (int attempt = 1; attempt <= maxTileRetries + 1; attempt++)
                {
                    UnityWebRequest request = UnityWebRequestTexture.GetTexture(url);
                    yield return request.SendWebRequest();

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        tile = DownloadHandlerTexture.GetContent(request);
                        if (attempt > 1)
                            Debug.Log($"Tile ({x},{y}) recovered on retry {attempt - 1}.");
                        break;
                    }

                    lastResponseCode = request.responseCode;
                    lastError        = request.error;

                    // 400 = tile coord out of range for this pano's actual dimensions
                    //       at this zoom (e.g. pano only has 7 columns instead of 8).
                    // 404 = tile genuinely missing (partial-coverage pano).
                    // Both are permanent for this exact URL — retrying just wastes time
                    // and spams the console.  Bail out of the retry loop immediately
                    // and let the pano-level threshold (minTileSuccessRatio) decide
                    // whether the overall panorama is still usable.
                    bool permanent = lastResponseCode == 400 || lastResponseCode == 404;
                    if (permanent || attempt > maxTileRetries) break;

                    Debug.LogWarning($"Tile ({x},{y}) attempt {attempt} failed " +
                                     $"(code={lastResponseCode}, err={lastError}) — retrying in {tileRetryDelay:F2}s.");
                    if (tileRetryDelay > 0f) yield return new WaitForSeconds(tileRetryDelay);
                }

                if (tile == null)
                {
                    // Quiet warning for the expected permanent cases; loud error for the rest.
                    if (lastResponseCode == 400 || lastResponseCode == 404)
                        Debug.LogWarning($"Tile ({x},{y}) skipped (code={lastResponseCode} — out of pano's tile grid at zoom {effectiveZoom}).");
                    else
                        Debug.LogError($"Tile ({x},{y}) gave up after {maxTileRetries + 1} attempts (code={lastResponseCode}, err={lastError}).");
                    continue;
                }

                // Crop the tile's source pixels to the actual content rectangle.
                // Partial tiles (rightmost column / bottom row) have their content
                // in the top-left of the source image; Google leaves the rest empty.
                //
                //   srcWidth   = how many real pixels of content this tile contributes horizontally
                //   srcHeight  = ditto, vertically
                //   srcY       = offset into the tile to skip the empty bottom strip on partial-height
                //                rows (Unity textures are bottom-up; Google's content sits in the TOP
                //                of the source image, which translates to y in [tileSize - srcHeight, tileSize])
                //   dstX, dstY = where this tile lands in the panorama (also bottom-up).  Tiles are
                //                fetched top-down (y=0 is north pole), so we flip onto the texture's
                //                bottom-up axis.
                int dstX      = x * tileSize;
                int srcWidth  = Mathf.Min(tileSize, contentWidth - dstX);
                int srcHeight = (y == tilesY - 1)
                    ? Mathf.Min(tileSize, contentHeight - y * tileSize)
                    : tileSize;
                int srcY      = tileSize - srcHeight;
                int dstY      = contentHeight - y * tileSize - srcHeight;

                if (srcWidth <= 0 || srcHeight <= 0)
                {
                    Debug.LogWarning($"Tile ({x},{y}) is fully outside content rect — skipping.");
                    continue;
                }

                panorama.SetPixels(
                    dstX, dstY, srcWidth, srcHeight,
                    tile.GetPixels(0, srcY, srcWidth, srcHeight)
                );

                successCount++;
                Debug.Log($"Tile ({x},{y}) loaded — {y * tilesX + x + 1}/{totalTiles}");
            }
        }

        // If too few tiles arrived, the panorama would be a Swiss-cheese mess.
        // Discard it and signal failure so LoadSkybox can pick a new location.
        // Threshold is configurable in the inspector via minTileSuccessRatio.
        int threshold = Mathf.CeilToInt(totalTiles * minTileSuccessRatio);
        if (successCount < threshold)
        {
            Debug.LogWarning($"Only {successCount}/{totalTiles} tiles loaded " +
                             $"(threshold {threshold}) — discarding panorama.");
            Destroy(panorama);
            callback(false);
            yield break;
        }

        panorama.Apply();
        panorama.wrapMode = TextureWrapMode.Repeat;

        Material runtimeSkybox = new Material(skyboxMaterial);
        runtimeSkybox.SetTexture("_MainTex", panorama);
        RenderSettings.skybox = runtimeSkybox;
        DynamicGI.UpdateEnvironment();

        Debug.Log($"Live Street View skybox applied! ({successCount}/{totalTiles} tiles)");
        callback(true);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

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

    // Numeric counterpart to ParseJsonField — pulls an unquoted integer value
    // (e.g. `"imageWidth": 13312`).  Returns `fallback` if the field is missing
    // or unparseable.  Avoids a JSON dep for what's just 4 fields per pano.
    int ParseJsonInt(string json, string field, int fallback)
    {
        string search = $"\"{field}\"";
        int idx = json.IndexOf(search);
        if (idx < 0) return fallback;

        int colon = json.IndexOf(':', idx + search.Length);
        if (colon < 0) return fallback;

        // Skip whitespace after the colon.
        int i = colon + 1;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;

        // Accept an optional leading minus, then digits.
        int start = i;
        if (i < json.Length && json[i] == '-') i++;
        while (i < json.Length && json[i] >= '0' && json[i] <= '9') i++;

        if (i == start) return fallback;
        return int.TryParse(json.Substring(start, i - start), out int v) ? v : fallback;
    }
}
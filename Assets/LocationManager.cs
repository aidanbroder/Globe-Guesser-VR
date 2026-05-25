using UnityEngine;

[System.Serializable]
public class StreetViewLocation
{
    public string locationName;
    public float  latitude;
    public float  longitude;
    public string country;        // ISO-2 country code from the JSON (e.g. "CL", "US")
    public string region;
    public string panoId;         // exact pano to load — bypasses the lat/lng lookup
    public float  heading;        // initial Street View facing direction, degrees — for a future compass / orientation HUD
    public float  pitch;          // initial Street View pitch, degrees — same use as heading
}

public class LocationManager : MonoBehaviour
{
    public static LocationManager Instance;

    [Header("Locations Source")]
    [Tooltip("Drag locations.json here.  Expected to be a top-level JSON array of " +
             "{lat, lng, panoId, heading, pitch, countryCode} objects.")]
    public TextAsset locationsJson;

    [Header("Debug")]
    [Tooltip("When enabled, GetRandomLocation always returns the single fixed " +
             "location below (Paris by default).  Used for tuning map projection " +
             "accuracy: the actual lat/lng is known, so guess error is meaningful.")]
    public bool debugFixedLocation = true;

    [Tooltip("Lat/lng/panoId to load when debugFixedLocation is on.  Default is " +
             "Paris near Notre-Dame: 48.8638082, 2.3215082, panoId 4-AlD-yRu5_98rp9fkMyJg.")]
    public float  debugLat     = 48.8638082f;
    public float  debugLng     = 2.3215082f;
    public string debugPanoId  = "4-AlD-yRu5_98rp9fkMyJg";
    public float  debugHeading = 21.078094f;
    public float  debugPitch   = 0f;
    public string debugCountry = "FR";

    [Tooltip("When enabled (and debugFixedLocation is OFF), GetRandomLocation only " +
             "picks from locations whose countryCode appears in debugCountryCodes — " +
             "used for tuning marker alignment or testing a single region.")]
    public bool debugMode = false;

    [Tooltip("ISO-2 country codes to restrict to in debug mode (e.g. US, FR, JP).  " +
             "Leave empty to disable the filter even when debugMode is on.")]
    public string[] debugCountryCodes = new string[] { "US", "FR", "JP" };

    // -----------------------------------------------------------------------
    // JSON shape — mirrors the file format exactly.  Kept private because
    // the rest of the game uses StreetViewLocation, not these raw rows.
    // -----------------------------------------------------------------------
    // JsonUtility silently ignores fields that aren't present in the source
    // JSON, so it's safe to keep `panoId` / `pitch` here even though the new
    // "customCoordinates" format omits them — they just stay at default
    // (null / 0).  StreetViewSkybox falls back to a lat/lng pano lookup when
    // panoId is empty, so missing panoIds are not a problem.
    [System.Serializable]
    private class JsonLocation
    {
        public float  lat;
        public float  lng;
        public string panoId;
        public float  heading;
        public float  pitch;
        public float  zoom;          // present in the new format, unused for now
        public string countryCode;
        public bool   needsPanoId;   // present in the new format, informational only
    }

    // Wrapper matches the new dataset's top-level shape:
    //   { "id": ..., "name": ..., "customCoordinates": [ {lat,lng,...}, ... ] }
    // The other top-level fields (id, name, description, avatar, etc.) are
    // ignored by JsonUtility since we don't declare them here.
    [System.Serializable]
    private class JsonLocationList
    {
        public JsonLocation[] customCoordinates;
    }

    private StreetViewLocation[] locations = new StreetViewLocation[0];
    private System.Collections.Generic.List<int> usedIndices = new System.Collections.Generic.List<int>();

    public StreetViewLocation CurrentLocation { get; private set; }

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }

        LoadLocationsFromJson();
    }

    // -----------------------------------------------------------------------
    // Parses the assigned TextAsset into the locations[] array.  Runs once
    // on Awake — 6k+ entries parse in milliseconds and total ~700KB of RAM.
    // -----------------------------------------------------------------------
    void LoadLocationsFromJson()
    {
        if (locationsJson == null)
        {
            Debug.LogError("LocationManager: no locations JSON assigned. " +
                           "Drag locations.json into the inspector field.");
            return;
        }

        // The new dataset's top-level is already an object containing
        // "customCoordinates", so we can pass the file text directly to
        // JsonUtility — no wrapping needed.  (The legacy format was a bare
        // top-level array that needed wrapping; if you ever need to support
        // both, detect a leading '[' and wrap to {"customCoordinates":...}.)
        JsonLocationList parsed;
        try { parsed = JsonUtility.FromJson<JsonLocationList>(locationsJson.text); }
        catch (System.Exception e)
        {
            Debug.LogError($"LocationManager: failed to parse locations JSON: {e.Message}");
            return;
        }

        if (parsed == null || parsed.customCoordinates == null || parsed.customCoordinates.Length == 0)
        {
            Debug.LogError("LocationManager: locations JSON parsed to empty list. " +
                           "Expected a top-level object with a 'customCoordinates' array.");
            return;
        }

        locations = new StreetViewLocation[parsed.customCoordinates.Length];
        for (int i = 0; i < parsed.customCoordinates.Length; i++)
        {
            JsonLocation src = parsed.customCoordinates[i];
            locations[i] = new StreetViewLocation
            {
                locationName = $"{src.countryCode} #{i}",  // generic — JSON has no name
                latitude     = src.lat,
                longitude    = src.lng,
                country      = src.countryCode,
                region       = "",
                panoId       = src.panoId,
                heading      = src.heading,
                pitch        = src.pitch,
            };
        }

        // METADATA ONLY — no panoramas fetched, no API calls.  These are just
        // ~700KB of {lat, lng, panoId} entries sitting in RAM.  Street View
        // imagery is only requested when StreetViewSkybox.LoadNewLocation runs.
        Debug.Log($"LocationManager: parsed {locations.Length} location entries from JSON " +
                  "(metadata only — no panoramas fetched).");
    }

    public StreetViewLocation GetRandomLocation()
    {
        // Debug override: always return the same hardcoded location so we can
        // verify globe-pin accuracy against a known lat/lng.  Bypasses the JSON
        // pool entirely so it works even if locations aren't loaded.
        if (debugFixedLocation)
        {
            CurrentLocation = new StreetViewLocation
            {
                locationName = "DEBUG (fixed)",
                latitude     = debugLat,
                longitude    = debugLng,
                country      = debugCountry,
                region       = "",
                panoId       = debugPanoId,
                heading      = debugHeading,
                pitch        = debugPitch,
            };
            Debug.Log($"LocationManager: debugFixedLocation ON — using ({debugLat:F4}, {debugLng:F4}) panoId={debugPanoId}");
            return CurrentLocation;
        }

        if (locations.Length == 0)
        {
            Debug.LogError("LocationManager: no locations loaded — cannot pick.");
            return null;
        }

        var candidates = new System.Collections.Generic.List<int>();
        for (int i = 0; i < locations.Length; i++)
        {
            if (usedIndices.Contains(i)) continue;
            if (debugMode && !IsDebugLocation(locations[i])) continue;
            candidates.Add(i);
        }

        // Exhausted the pool — wipe used-indices and try again without the
        // already-used filter.  Still respects the debug country filter.
        if (candidates.Count == 0)
        {
            usedIndices.Clear();
            for (int i = 0; i < locations.Length; i++)
            {
                if (debugMode && !IsDebugLocation(locations[i])) continue;
                candidates.Add(i);
            }
        }

        if (candidates.Count == 0)
        {
            Debug.LogError("LocationManager: no candidates after applying filters " +
                           "(debug country list may be too restrictive).");
            return null;
        }

        int chosen = candidates[Random.Range(0, candidates.Count)];
        usedIndices.Add(chosen);
        CurrentLocation = locations[chosen];

        Debug.Log($"Selected location: {CurrentLocation.locationName} " +
                  $"(lat={CurrentLocation.latitude:F4}, lng={CurrentLocation.longitude:F4}, " +
                  $"panoId={CurrentLocation.panoId}, heading={CurrentLocation.heading:F1}°, " +
                  $"pitch={CurrentLocation.pitch:F1}°)");
        return CurrentLocation;
    }

    public void ResetGame()
    {
        usedIndices.Clear();
        CurrentLocation = null;
    }

    // True when this location's country is in the debugCountryCodes list.
    bool IsDebugLocation(StreetViewLocation loc)
    {
        if (debugCountryCodes == null || debugCountryCodes.Length == 0) return false;
        for (int i = 0; i < debugCountryCodes.Length; i++)
            if (debugCountryCodes[i] == loc.country) return true;
        return false;
    }
}

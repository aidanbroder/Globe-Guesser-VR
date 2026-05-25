using System.IO;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class MercatorToEquirectangular : MonoBehaviour
{
    [Header("Settings")]
    public int sourceZoom = 4; // which zoom level to convert (3 or 4)

    void Start()
    {
        Convert();
    }

    void Convert()
    {
        // Load the Mercator source
        string srcPath = Path.Combine(Application.dataPath, "GlobeTextures", $"world_map_zoom{sourceZoom}.png");
        if (!File.Exists(srcPath))
        {
            Debug.LogError($"Source file not found: {srcPath}");
            return;
        }

        byte[] srcBytes = File.ReadAllBytes(srcPath);
        Texture2D mercator = new Texture2D(2, 2);
        mercator.LoadImage(srcBytes);

        int srcW = mercator.width;
        int srcH = mercator.height;
        Debug.Log($"Loaded Mercator source: {srcW}x{srcH}");

        // Output: equirectangular, 2:1 aspect ratio
        // Use same width as source, half height
        int outW = srcW;
        int outH = srcW / 2;

        Texture2D equirect = new Texture2D(outW, outH, TextureFormat.RGB24, false);

        // Mercator max latitude (source only has content within ±85.05°)
        float maxLatDeg = (Mathf.Atan(Mathf.Exp(Mathf.PI)) * 2f - Mathf.PI / 2f) * Mathf.Rad2Deg; // ~85.05°

        // KEY FIX: the output image's full V range [0, 1] now maps to the
        // sphere's full latitude range ±90°, not ±85.05°.  This means the
        // texture's equator sits at V=0.5, which matches the Unity sphere UV.
        //
        // For latitudes beyond ±85.05° (no Mercator data), we smear the edge
        // row of the Mercator source.  This fills the polar caps with the
        // existing ice / tundra color and avoids a hard black band.
        for (int py = 0; py < outH; py++)
        {
            // py=0 is bottom (south pole, -90°); py=outH-1 is top (north pole, +90°).
            float v      = (float)py / (outH - 1);       // 0 (south) → 1 (north)
            float latDeg = Mathf.Lerp(-90f, 90f, v);     // ±90° — true equirectangular

            int srcY;

            if (Mathf.Abs(latDeg) >= maxLatDeg)
            {
                // Beyond Mercator's valid range — clamp to the nearest edge row.
                // Mercator source row 0 = south, row srcH-1 = north (see downloader).
                srcY = latDeg > 0 ? srcH - 1 : 0;
            }
            else
            {
                // Inside Mercator range — compute the exact source row.
                float latRad = latDeg * Mathf.Deg2Rad;

                // Mercator Y, where 0 = north edge, 1 = south edge.
                float mercY = (1f - (Mathf.Log(Mathf.Tan(Mathf.PI / 4f + latRad / 2f)) / Mathf.PI)) / 2f;
                srcY = Mathf.Clamp(Mathf.RoundToInt(mercY * (srcH - 1)), 0, srcH - 1);

                // Downloader stored rows with Y-flip (tile y=0 at top of texture),
                // so srcY=srcH-1 is north and srcY=0 is south.  Flip mercY to match.
                srcY = srcH - 1 - srcY;
            }

            for (int px = 0; px < outW; px++)
            {
                // Longitude maps linearly — same in both projections.
                int srcX = Mathf.Clamp(Mathf.RoundToInt((float)px / (outW - 1) * (srcW - 1)), 0, srcW - 1);
                equirect.SetPixel(px, py, mercator.GetPixel(srcX, srcY));
            }
        }

        equirect.Apply();

        // Save the result
        string outPath = Path.Combine(Application.dataPath, "GlobeTextures", "world_map_equirectangular.png");
        File.WriteAllBytes(outPath, equirect.EncodeToPNG());
        Debug.Log($"Equirectangular map saved to {outPath} ({outW}x{outH})");

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif

        Debug.Log("Done! Remove this GameObject before building.");
    }
}

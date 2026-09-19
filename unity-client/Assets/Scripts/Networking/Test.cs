// =============================================================================
// TestTrigger.cs — Terrain builder for SampleScene
//
// When SampleScene loads, this script builds the terrain from the data
// cached in ImageSessionManager. If no cache exists, it re-uploads the
// user's image to the backend.
//
// ALWAYS destroys any pre-existing terrain and rebuilds fresh.
// =============================================================================

using System;
using UnityEngine;
using DepthWizard.Networking;
using DepthWizard.Terrain;
using DepthWizard.UI;

public class TestTrigger : MonoBehaviour
{
    void Start()
    {
        // ALWAYS destroy any existing terrain first — never skip
        DestroyExistingTerrain();

        var session = ImageSessionManager.Instance;
        if (session == null)
        {
            Debug.LogError("[TestTrigger] No ImageSessionManager found.");
            return;
        }

        // ── Path 1: Cached result from the processing pipeline ──
        if (session.HasResult)
        {
            Debug.Log("[TestTrigger] Building terrain from cached result.");
            BuildTerrainFromCache(session);
            return;
        }

        // ── Path 2: Re-upload the image (original working behavior) ──
        var client = GetComponent<BackendClient>();
        if (client == null)
            client = FindFirstObjectByType<BackendClient>();
        if (client == null)
        {
            Debug.LogError("[TestTrigger] No BackendClient found.");
            return;
        }

        if (session.HasImage)
        {
            Debug.Log($"[TestTrigger] Re-uploading image: {session.FilePath}");
            client.UploadAndGenerate(session.FilePath);
            return;
        }

        if (!string.IsNullOrEmpty(session.SampleId))
        {
            Debug.Log($"[TestTrigger] Processing sample: {session.SampleId}");
            client.ProcessSample(session.SampleId);
            return;
        }

        Debug.LogWarning("[TestTrigger] No image data available.");
    }

    private void DestroyExistingTerrain()
    {
        // Find and destroy ALL terrain objects IMMEDIATELY.
        // Must use DestroyImmediate — regular Destroy() is deferred to end
        // of frame, and RuntimeTerrainBuilder.Build() would still find the
        // doomed object via GameObject.Find(), update it, then Unity destroys
        // it at frame end — losing the new terrain data.
        var terrains = FindObjectsByType<UnityEngine.Terrain>(FindObjectsSortMode.None);
        foreach (var t in terrains)
        {
            Debug.Log($"[TestTrigger] Destroying existing terrain: {t.gameObject.name}");
            DestroyImmediate(t.gameObject);
        }
    }

    private void BuildTerrainFromCache(ImageSessionManager session)
    {
        var response = session.ResultResponse;

        float scale = 200f;
        float baseAltitude = 0f;

        if (response.is_calibrated &&
            response.max_elev_m > response.min_elev_m)
        {
            scale = response.max_elev_m - response.min_elev_m;
            baseAltitude = response.min_elev_m;
            Debug.Log($"[TestTrigger] Calibrated: {response.min_elev_m:F1}m – {response.max_elev_m:F1}m");
        }

        // Compute dynamic terrain extent from bbox or pixel dimensions
        ComputeTerrainExtent(response, out float geoWidth, out float geoLength,
                             out float relWidth, out float relLength);

        // Use geo extent for initial build (absolute mode if calibrated)
        float terrainWidth  = (response.is_georeferenced && geoWidth > 0f)  ? geoWidth  : relWidth;
        float terrainLength = (response.is_georeferenced && geoLength > 0f) ? geoLength : relLength;

        // -----------------------------------------------------------------
        // Adaptive vertical exaggeration.
        // Goal: always achieve a visually useful height:width ratio (~12%).
        // Flat urban terrain (Delhi: 67m height / 8km width = 0.8%) gets
        // boosted ~14×.  Hilly terrain (Mussoorie: 800m / 5km = 16%) stays
        // at 1× (no exaggeration). This adapts to every terrain type.
        // -----------------------------------------------------------------
        const float TARGET_HEIGHT_RATIO = 0.12f; // 12% of terrain width
        float maxExtent = Mathf.Max(terrainWidth, terrainLength);
        float currentRatio = (maxExtent > 0f) ? (scale / maxExtent) : 1f;
        float exaggeration = (currentRatio > 0f)
            ? Mathf.Clamp(TARGET_HEIGHT_RATIO / currentRatio, 1f, 20f)
            : 1f;
        float exaggeratedScale = scale * exaggeration;
        Debug.Log($"[TestTrigger] Terrain dims: {terrainWidth:F0}×{terrainLength:F0}m, " +
                  $"height={scale:F1}m, ratio={currentRatio*100:F1}%, " +
                  $"exaggeration={exaggeration:F1}× → {exaggeratedScale:F1}m");

        RuntimeTerrainBuilder.Build(
            heightmapPngBytes: session.ResultHeightmapBytes,
            texturePngBytes: session.ResultTextureBytes,
            expectedResolution: response.width,
            terrainWidth: terrainWidth,
            terrainLength: terrainLength,
            heightScale: exaggeratedScale,
            baseAltitude: baseAltitude,
            relativeHeightScale: 200f,
            metadata: response,
            relativeWidth: relWidth,
            relativeLength: relLength
        );

        // --- HUD overlays ---
        AccuracyMetricsHud.Show(response);
        CoordinateOverlay.Show(response);

        // --- Confidence overlay (C6: wire confidence_b64 to overlay) ---
        if (session.ResultConfidenceBytes != null && session.ResultConfidenceBytes.Length > 0)
        {
            var confOverlay = FindFirstObjectByType<ConfidenceOverlayController>();
            if (confOverlay == null)
            {
                // Auto-create on the main camera if not already present
                var cam = UnityEngine.Camera.main;
                if (cam != null)
                {
                    confOverlay = cam.gameObject.AddComponent<ConfidenceOverlayController>();
                }
            }
            if (confOverlay != null)
            {
                confOverlay.LoadConfidenceData(session.ResultConfidenceBytes);
                Debug.Log($"[TestTrigger] Confidence overlay loaded ({session.ResultConfidenceBytes.Length / 1024}KB).");
            }
        }

        // --- Visual environment polish ---
        EnvironmentPolish.Apply();

        Debug.Log($"[TestTrigger] Terrain built. Calibrated={response.is_calibrated}, Model={response.model_id}");
    }

    /// <summary>
    /// Compute terrain XZ extent from the backend response.
    /// - geoWidth/geoLength: real-world meters from bbox (0 if not georeferenced)
    /// - relWidth/relLength: pixel-proportional fallback (1 meter per pixel)
    /// Uses the same degrees-to-meters formula as GeoTiffHeightmapReader.cs.
    /// </summary>
    private static void ComputeTerrainExtent(
        ProcessResponse response,
        out float geoWidth, out float geoLength,
        out float relWidth, out float relLength)
    {
        const float METERS_PER_DEGREE_LAT = 111320f;

        // Relative mode: proportional to pixel dimensions (1 m/px)
        relWidth  = Mathf.Max(response.width,  64f);
        relLength = Mathf.Max(response.height, 64f);

        // Absolute mode: compute from bbox if available
        geoWidth  = 0f;
        geoLength = 0f;

        if (response.bbox != null && response.bbox.Length == 4 && response.is_georeferenced)
        {
            float west  = response.bbox[0];
            float south = response.bbox[1];
            float east  = response.bbox[2];
            float north = response.bbox[3];

            float centerLat = (south + north) * 0.5f;
            float latRad = centerLat * Mathf.Deg2Rad;
            float metersPerDegreeLon = METERS_PER_DEGREE_LAT * Mathf.Cos(latRad);

            geoWidth  = Mathf.Abs(east - west)  * metersPerDegreeLon;
            geoLength = Mathf.Abs(north - south) * METERS_PER_DEGREE_LAT;

            // Sanity check — guard against nonsensical values
            if (geoWidth <= 0f || geoLength <= 0f ||
                float.IsNaN(geoWidth) || float.IsNaN(geoLength) ||
                float.IsInfinity(geoWidth) || float.IsInfinity(geoLength))
            {
                Debug.LogWarning(
                    $"[TestTrigger] Computed geo extent ({geoWidth:F1}×{geoLength:F1}) " +
                    "is invalid. Falling back to pixel-proportional.");
                geoWidth  = 0f;
                geoLength = 0f;
            }
            else
            {
                Debug.Log(
                    $"[TestTrigger] Geo extent: {geoWidth:F1}×{geoLength:F1}m " +
                    $"(bbox: [{west:F6}, {south:F6}, {east:F6}, {north:F6}])");
            }
        }
    }
}


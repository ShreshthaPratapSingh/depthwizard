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

        RuntimeTerrainBuilder.Build(
            heightmapPngBytes: session.ResultHeightmapBytes,
            texturePngBytes: session.ResultTextureBytes,
            expectedResolution: response.width,
            terrainWidth: 500f,
            terrainLength: 500f,
            heightScale: scale,
            baseAltitude: baseAltitude,
            relativeHeightScale: 200f,
            metadata: response
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
}


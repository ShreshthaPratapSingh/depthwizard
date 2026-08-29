// =============================================================================
// BackendClient.cs
//
// Runtime HTTP client for the DepthWizard FastAPI backend.
// Sends an image file to POST /process (multipart form upload, field name
// "file") and parses the JSON response containing base64-encoded heightmap
// and texture PNGs plus metadata.
//
// This is a NEW runtime path — it does NOT replace or interfere with the
// existing editor-only GeoTIFF workflow (TerrainGeneratorEditorWindow.cs).
//
// Usage:
//   Attach to any GameObject. Call UploadAndGenerate(imagePath) from script
//   or wire it to a UI button.
// =============================================================================

using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace DepthWizard.Networking
{
    /// <summary>
    /// Parsed response from the backend's POST /process endpoint.
    /// Field names match the JSON keys returned by backend/app/main.py exactly.
    /// </summary>
    [Serializable]
    public class ProcessResponse
    {
        // --- base64-encoded file payloads ---
        public string heightmap_b64;
        public string texture_b64;

        // --- metadata (mirrored from depth_pipeline's process_image output) ---
        public string status;
        public int width;
        public int height;
        public bool is_georeferenced;
        public string crs;
        public bool is_calibrated;
        public bool srtm_aligned;
        public string srtm_tile_id;
        public float relative_min;
        public float relative_max;
        public string model_id;
        public float inference_ms;
        public string[] warnings;

        // --- error fields (only present on 500 responses) ---
        public string detail;
    }

    /// <summary>
    /// Runtime HTTP client that uploads an image to the backend and triggers
    /// terrain generation from the response via <see cref="RuntimeTerrainBuilder"/>.
    /// </summary>
    public class BackendClient : MonoBehaviour
    {
        [Header("Backend")]
        [Tooltip("Base URL of the DepthWizard FastAPI server.")]
        [SerializeField] private string backendUrl = "http://localhost:8000";

        [Header("Terrain")]
        [Tooltip("Terrain XZ size in meters (used when geo-extent is unavailable).")]
        [SerializeField] private float terrainSize = 500f;

        [Tooltip("Terrain height scale in meters (elevation range).")]
        [SerializeField] private float heightScale = 200f;

        /// <summary>True while a request is in flight.</summary>
        public bool IsBusy { get; private set; }

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Upload a local image file to the backend, run the depth pipeline,
        /// and generate a terrain from the result. This is the single entry
        /// point for the runtime pipeline.
        /// </summary>
        /// <param name="imagePath">Absolute path to a PNG, JPG, or GeoTIFF file.</param>
        public void UploadAndGenerate(string imagePath)
        {
            if (IsBusy)
            {
                Debug.LogWarning("[BackendClient] A request is already in progress.");
                return;
            }

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
            {
                Debug.LogError($"[BackendClient] File not found: {imagePath}");
                return;
            }

            StartCoroutine(UploadCoroutine(imagePath));
        }

        // -----------------------------------------------------------------
        // Coroutine
        // -----------------------------------------------------------------

        private IEnumerator UploadCoroutine(string imagePath)
        {
            IsBusy = true;
            Debug.Log($"[BackendClient] Uploading: {imagePath}");

            // --- Read file bytes ---
            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(imagePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BackendClient] Failed to read file: {ex.Message}");
                IsBusy = false;
                yield break;
            }

            // --- Build multipart form ---
            // The backend expects a multipart file upload with field name "file"
            // (see backend/app/main.py line 66: async def process(file: UploadFile = File(...))).
            string fileName = Path.GetFileName(imagePath);
            string mimeType = GetMimeType(fileName);

            WWWForm form = new WWWForm();
            form.AddBinaryData("file", fileBytes, fileName, mimeType);

            string url = backendUrl.TrimEnd('/') + "/process";
            Debug.Log($"[BackendClient] POST {url} ({fileBytes.Length / 1024}KB)");

            using (UnityWebRequest request = UnityWebRequest.Post(url, form))
            {
                // The pipeline can take 10-60+ seconds on GPU inference.
                request.timeout = 120;

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[BackendClient] Request failed: {request.error}\n" +
                        $"  HTTP {request.responseCode}\n" +
                        $"  Body: {request.downloadHandler?.text?.Substring(0, Mathf.Min(request.downloadHandler?.text?.Length ?? 0, 500))}");
                    IsBusy = false;
                    yield break;
                }

                // --- Parse response ---
                string json = request.downloadHandler.text;
                ProcessResponse response;
                try
                {
                    response = JsonUtility.FromJson<ProcessResponse>(json);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BackendClient] Failed to parse response JSON: {ex.Message}");
                    IsBusy = false;
                    yield break;
                }

                if (response.status == "error")
                {
                    Debug.LogError(
                        $"[BackendClient] Backend returned error: {response.detail}");
                    IsBusy = false;
                    yield break;
                }

                if (string.IsNullOrEmpty(response.heightmap_b64))
                {
                    Debug.LogError("[BackendClient] Response missing heightmap_b64 data.");
                    IsBusy = false;
                    yield break;
                }

                // --- Decode base64 payloads ---
                byte[] heightmapBytes;
                byte[] textureBytes = null;
                try
                {
                    heightmapBytes = Convert.FromBase64String(response.heightmap_b64);
                    if (!string.IsNullOrEmpty(response.texture_b64))
                    {
                        textureBytes = Convert.FromBase64String(response.texture_b64);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BackendClient] Failed to decode base64 data: {ex.Message}");
                    IsBusy = false;
                    yield break;
                }

                // --- Build terrain ---
                try
                {
                    Terrain.RuntimeTerrainBuilder.Build(
                        heightmapPngBytes: heightmapBytes,
                        texturePngBytes: textureBytes,
                        expectedResolution: response.width,
                        terrainWidth: terrainSize,
                        terrainLength: terrainSize,
                        heightScale: heightScale,
                        metadata: response
                    );

                    Debug.Log(
                        $"[BackendClient] ✓ Terrain generated successfully.\n" +
                        $"  Model: {response.model_id}\n" +
                        $"  Inference: {response.inference_ms:F0}ms\n" +
                        $"  Georeferenced: {response.is_georeferenced}\n" +
                        $"  Warnings: {(response.warnings != null ? string.Join("; ", response.warnings) : "none")}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BackendClient] Terrain generation failed: {ex.Message}\n{ex.StackTrace}");
                }
            }

            IsBusy = false;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static string GetMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            switch (ext)
            {
                case ".png":  return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".tif":
                case ".tiff": return "image/tiff";
                default:      return "application/octet-stream";
            }
        }
    }
}

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
using DepthWizard.UI;

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
        public float min_elev_m;
        public float max_elev_m;
        public float r_squared;
        public float rmse_m;
        public float mae_m;
        public int sample_count;
        public string model_id;
        public float inference_ms;
        public string[] warnings;

        // --- error fields (only present on 500 responses) ---
        public string detail;
    }

    /// <summary>
    /// Parsed response from GET /jobs/{job_id} while polling async progress.
    /// </summary>
    [Serializable]
    public class JobResponse
    {
        public string job_id;
        public string status;
        public string stage;
        public string progress_detail;
        public string error;

        // When status == "done", the result field contains the full payload.
        // JsonUtility cannot deserialize nested objects into ProcessResponse
        // directly, so we re-parse result from the raw JSON in the coroutine.
    }

    /// <summary>
    /// Wrapper for deserializing the job poll response when status == "done".
    /// The "result" field maps to a full ProcessResponse.
    /// </summary>
    [Serializable]
    public class JobResultWrapper
    {
        public ProcessResponse result;
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

        /// <summary>
        /// Fired each time PollJobCoroutine receives a progress update.
        /// The string argument is the current pipeline stage name.
        /// </summary>
        public event Action<string> OnProgressUpdate;

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Upload a local image file to the backend, run the depth pipeline,
        /// and generate a terrain from the result.
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

        /// <summary>
        /// Run the depth pipeline on a preloaded sample image (no file upload).
        /// Calls POST /process-sample/{sampleId} on the backend.
        /// </summary>
        public void ProcessSample(string sampleId)
        {
            if (IsBusy)
            {
                Debug.LogWarning("[BackendClient] A request is already in progress.");
                return;
            }

            if (string.IsNullOrEmpty(sampleId))
            {
                Debug.LogError("[BackendClient] sampleId is empty.");
                return;
            }

            IsBusy = true;
            StartCoroutine(ProcessSampleCoroutine(sampleId));
        }

        /// <summary>
        /// Upload an image and process it asynchronously. Returns immediately;
        /// subscribe to OnProgressUpdate for stage changes.
        /// </summary>
        public void UploadAndGenerateAsync(string imagePath)
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

            IsBusy = true;
            StartCoroutine(UploadAsyncCoroutine(imagePath));
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
                HandleResponse(request);
            }

            IsBusy = false;
        }

        private IEnumerator ProcessSampleCoroutine(string sampleId)
        {
            IsBusy = true;
            string url = backendUrl.TrimEnd('/') + $"/process-sample/{sampleId}";
            Debug.Log($"[BackendClient] POST {url}");

            using (UnityWebRequest request = UnityWebRequest.PostWwwForm(url, ""))
            {
                request.timeout = 120;
                yield return request.SendWebRequest();
                HandleResponse(request);
            }

            IsBusy = false;
        }

        private IEnumerator UploadAsyncCoroutine(string imagePath)
        {
            IsBusy = true;
            Debug.Log($"[BackendClient] Async upload: {imagePath}");

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

            string fileName = Path.GetFileName(imagePath);
            string mimeType = GetMimeType(fileName);

            WWWForm form = new WWWForm();
            form.AddBinaryData("file", fileBytes, fileName, mimeType);

            string url = backendUrl.TrimEnd('/') + "/process-async";
            Debug.Log($"[BackendClient] POST {url} ({fileBytes.Length / 1024}KB)");

            string jobId = null;
            using (UnityWebRequest request = UnityWebRequest.Post(url, form))
            {
                request.timeout = 30;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError(
                        $"[BackendClient] Async submit failed: {request.error}\n" +
                        $"  HTTP {request.responseCode}\n" +
                        $"  Body: {request.downloadHandler?.text?.Substring(0, Mathf.Min(request.downloadHandler?.text?.Length ?? 0, 500))}");
                    IsBusy = false;
                    yield break;
                }

                var submitResponse = JsonUtility.FromJson<JobResponse>(request.downloadHandler.text);
                jobId = submitResponse.job_id;
            }

            if (string.IsNullOrEmpty(jobId))
            {
                Debug.LogError("[BackendClient] No job_id in async response.");
                IsBusy = false;
                yield break;
            }

            Debug.Log($"[BackendClient] Job submitted: {jobId}");
            yield return StartCoroutine(PollJobCoroutine(jobId));

            IsBusy = false;
        }

        private IEnumerator PollJobCoroutine(string jobId)
        {
            string url = backendUrl.TrimEnd('/') + $"/jobs/{jobId}";
            var wait = new WaitForSeconds(0.5f);

            while (true)
            {
                using (UnityWebRequest request = UnityWebRequest.Get(url))
                {
                    request.timeout = 10;
                    yield return request.SendWebRequest();

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[BackendClient] Poll failed: {request.error}");
                        yield break;
                    }

                    string json = request.downloadHandler.text;
                    var job = JsonUtility.FromJson<JobResponse>(json);

                    if (job.status == "done")
                    {
                        Debug.Log($"[BackendClient] Job {jobId} complete.");
                        OnProgressUpdate?.Invoke("done");

                        // The result payload is nested in the JSON. Re-parse
                        // from the raw JSON to extract the "result" object.
                        // JsonUtility does not handle nested heterogeneous
                        // objects, so we use a wrapper approach.
                        var wrapper = JsonUtility.FromJson<JobResultWrapper>(json);
                        if (wrapper.result != null)
                        {
                            HandleResponseFromJob(wrapper.result);
                        }
                        yield break;
                    }

                    if (job.status == "error")
                    {
                        Debug.LogError($"[BackendClient] Job {jobId} failed: {job.error}");
                        OnProgressUpdate?.Invoke("error");
                        yield break;
                    }

                    OnProgressUpdate?.Invoke(job.stage);
                }

                yield return wait;
            }
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Shared response handling: parse JSON, decode base64, build terrain.
        /// Called from both UploadCoroutine and ProcessSampleCoroutine.
        /// </summary>
        private void HandleResponse(UnityWebRequest request)
        {
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError(
                    $"[BackendClient] Request failed: {request.error}\n" +
                    $"  HTTP {request.responseCode}\n" +
                    $"  Body: {request.downloadHandler?.text?.Substring(0, Mathf.Min(request.downloadHandler?.text?.Length ?? 0, 500))}");
                return;
            }

            string json = request.downloadHandler.text;
            ProcessResponse response;
            try
            {
                response = JsonUtility.FromJson<ProcessResponse>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BackendClient] Failed to parse response JSON: {ex.Message}");
                return;
            }

            if (response.status == "error")
            {
                Debug.LogError($"[BackendClient] Backend returned error: {response.detail}");
                return;
            }

            if (string.IsNullOrEmpty(response.heightmap_b64))
            {
                Debug.LogError("[BackendClient] Response missing heightmap_b64 data.");
                return;
            }

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
                return;
            }

            try
            {
                // When the backend successfully calibrated depth to meters,
                // use the real elevation range. Otherwise use the Inspector's
                // relative heightScale (default 200f).
                float scale = heightScale;
                float baseAltitude = 0f;
                if (response.is_calibrated &&
                    response.max_elev_m > response.min_elev_m)
                {
                    scale = response.max_elev_m - response.min_elev_m;
                    baseAltitude = response.min_elev_m;
                    Debug.Log(
                        $"[BackendClient] Calibrated mode: " +
                        $"elevation {response.min_elev_m:F1}m - {response.max_elev_m:F1}m " +
                        $"(range {scale:F1}m, R²={response.r_squared:F3})");
                }

                Terrain.RuntimeTerrainBuilder.Build(
                    heightmapPngBytes: heightmapBytes,
                    texturePngBytes: textureBytes,
                    expectedResolution: response.width,
                    terrainWidth: terrainSize,
                    terrainLength: terrainSize,
                    heightScale: scale,
                    baseAltitude: baseAltitude,
                    relativeHeightScale: heightScale,
                    metadata: response
                );

                AccuracyMetricsHud.Show(response);

                Debug.Log(
                    $"[BackendClient] Terrain generated successfully.\n" +
                    $"  Model: {response.model_id}\n" +
                    $"  Inference: {response.inference_ms:F0}ms\n" +
                    $"  Calibrated: {response.is_calibrated}\n" +
                    $"  Warnings: {(response.warnings != null ? string.Join("; ", response.warnings) : "none")}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BackendClient] Terrain generation failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Build terrain from an already-parsed ProcessResponse (used by
        /// PollJobCoroutine when the async job completes).
        /// </summary>
        private void HandleResponseFromJob(ProcessResponse response)
        {
            if (response.status == "error")
            {
                Debug.LogError($"[BackendClient] Backend returned error: {response.detail}");
                return;
            }

            if (string.IsNullOrEmpty(response.heightmap_b64))
            {
                Debug.LogError("[BackendClient] Response missing heightmap_b64 data.");
                return;
            }

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
                return;
            }

            try
            {
                float scale = heightScale;
                float baseAltitude = 0f;
                if (response.is_calibrated &&
                    response.max_elev_m > response.min_elev_m)
                {
                    scale = response.max_elev_m - response.min_elev_m;
                    baseAltitude = response.min_elev_m;
                }

                Terrain.RuntimeTerrainBuilder.Build(
                    heightmapPngBytes: heightmapBytes,
                    texturePngBytes: textureBytes,
                    expectedResolution: response.width,
                    terrainWidth: terrainSize,
                    terrainLength: terrainSize,
                    heightScale: scale,
                    baseAltitude: baseAltitude,
                    relativeHeightScale: heightScale,
                    metadata: response
                );

                AccuracyMetricsHud.Show(response);

                Debug.Log(
                    $"[BackendClient] Terrain generated (async).\n" +
                    $"  Model: {response.model_id}\n" +
                    $"  Calibrated: {response.is_calibrated}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BackendClient] Terrain generation failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

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

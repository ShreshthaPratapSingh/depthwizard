// =============================================================================
// ProcessingController.cs
//
// Pipeline orchestrator for the DepthWizard processing screen.
// Drives the backend upload, depth inference, and terrain generation,
// and fires progress events that ProcessingPanelUI subscribes to.
//
// This script is the BRIDGE between:
//   - ImageSessionManager (selected image data)
//   - BackendClient (HTTP upload + async polling)
//   - ProcessingPanelUI (visual feedback)
//   - SceneManager (transition to flythrough scene)
//
// Wiring:
//   - LandingPageUIController calls StartPipeline() on Generate click.
//   - This script reads the image from ImageSessionManager.
//   - It calls BackendClient.UploadAndGenerateAsync() and subscribes to
//     BackendClient.OnProgressUpdate for real backend stage updates.
//   - On completion, it transitions to the flythrough scene.
//
// For Backend/Pipeline Devs:
//   - Modify STAGE_BACKEND_MAP to match your actual backend stage strings.
//   - The pipeline stages and their weights are defined at the top.
//
// For Frontend/UI Devs:
//   - This script fires events; ProcessingPanelUI consumes them.
//   - You should not need to modify this file for visual changes.
//
// Namespace: DepthWizard.UI
// =============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using DepthWizard.Networking;

namespace DepthWizard.UI
{
    /// <summary>
    /// The 6 stages of the DepthWizard processing pipeline.
    /// Each stage maps to a row in the processing panel UI.
    /// </summary>
    public enum PipelineStage
    {
        UploadingImage       = 0,
        RunningDepthInference = 1,
        GeospatialCalibration = 2,
        GeneratingTerrainMesh = 3,
        ApplyingTextures     = 4,
        FinalizingScene      = 5
    }

    /// <summary>
    /// Orchestrates the depth pipeline: uploads image to backend,
    /// tracks progress, drives UI updates, and triggers scene transition.
    /// Attach to the same Canvas as <see cref="ProcessingPanelUI"/>.
    /// </summary>
    public class ProcessingController : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Configuration
        // ---------------------------------------------------------------------

        /// <summary>Scene to load after processing completes.</summary>
        private const string TARGET_SCENE = "SampleScene";

        /// <summary>Delay (seconds) after hitting 100% before scene transition.</summary>
        private const float COMPLETION_DELAY = 0.5f;

        /// <summary>
        /// Weight of each stage in overall progress (must sum to 1.0).
        /// Backend stages (0-2) are typically heavier than Unity-side (3-5).
        /// Adjust these based on real-world timings.
        /// </summary>
        private static readonly float[] STAGE_WEIGHTS = new float[]
        {
            0.10f,  // 0: Uploading image
            0.35f,  // 1: Running depth inference (heaviest — GPU work)
            0.15f,  // 2: Geospatial calibration
            0.20f,  // 3: Generating terrain mesh
            0.10f,  // 4: Applying textures
            0.10f   // 5: Finalizing scene
        };

        /// <summary>
        /// Human-readable labels for each stage, shown in the status text.
        /// </summary>
        private static readonly string[] STAGE_LABELS = new string[]
        {
            "Uploading image...",
            "Running depth inference...",
            "Calibrating geospatial data...",
            "Generating terrain mesh...",
            "Applying textures...",
            "Finalizing scene..."
        };

        /// <summary>
        /// Maps backend stage strings (from BackendClient.OnProgressUpdate)
        /// to PipelineStage enum values.
        ///
        /// ── BACKEND/PIPELINE DEVS: ──
        /// Update these keys to match whatever strings your FastAPI backend
        /// returns in the JobResponse.stage field. The values on the right
        /// are the UI stages they correspond to.
        /// </summary>
        private static readonly Dictionary<string, PipelineStage> STAGE_BACKEND_MAP =
            new Dictionary<string, PipelineStage>(StringComparer.OrdinalIgnoreCase)
        {
            { "uploading",            PipelineStage.UploadingImage },
            { "upload",               PipelineStage.UploadingImage },
            { "depth_inference",      PipelineStage.RunningDepthInference },
            { "inference",            PipelineStage.RunningDepthInference },
            { "inferring",            PipelineStage.RunningDepthInference },
            { "running_inference",    PipelineStage.RunningDepthInference },
            { "calibration",          PipelineStage.GeospatialCalibration },
            { "calibrating",          PipelineStage.GeospatialCalibration },
            { "geo_calibration",      PipelineStage.GeospatialCalibration },
            { "geospatial",           PipelineStage.GeospatialCalibration },
            { "generating_heightmap", PipelineStage.GeneratingTerrainMesh },
            { "heightmap",            PipelineStage.GeneratingTerrainMesh },
            { "exporting",            PipelineStage.ApplyingTextures },
            { "generating_texture",   PipelineStage.ApplyingTextures },
            { "texture",              PipelineStage.ApplyingTextures },
            { "finalizing",           PipelineStage.FinalizingScene },
            { "complete",             PipelineStage.FinalizingScene },
        };

        // ---------------------------------------------------------------------
        // Events (subscribed by ProcessingPanelUI)
        // ---------------------------------------------------------------------

        /// <summary>Overall progress changed (0..1).</summary>
        public event Action<float> OnOverallProgressChanged;

        /// <summary>Status message changed (human-readable text).</summary>
        public event Action<string> OnStatusMessageChanged;

        /// <summary>A stage's visual state changed (index, new state).</summary>
        public event Action<int, StageState> OnStageStateChanged;

        /// <summary>An error occurred (error message).</summary>
        public event Action<string> OnError;

        /// <summary>Pipeline completed successfully.</summary>
        public event Action OnComplete;

        // ---------------------------------------------------------------------
        // References
        // ---------------------------------------------------------------------

        private ProcessingPanelUI _panelUI;
        private BackendClient _backendClient;

        // Pipeline state
        private PipelineStage _currentStage;
        private bool _isRunning = false;
        private bool _hasError = false;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Awake()
        {
            _panelUI = GetComponent<ProcessingPanelUI>();
            if (_panelUI == null)
                _panelUI = FindFirstObjectByType<ProcessingPanelUI>();

            if (_panelUI != null)
            {
                // Wire UI events to controller handlers
                _panelUI.OnRetryClicked += HandleRetry;
                _panelUI.OnCancelClicked += HandleCancel;
            }

            // Wire controller events to UI methods
            SubscribeUIToEvents();
        }

        private void OnDestroy()
        {
            if (_panelUI != null)
            {
                _panelUI.OnRetryClicked -= HandleRetry;
                _panelUI.OnCancelClicked -= HandleCancel;
            }

            UnsubscribeFromBackend();
        }

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Begin the full processing pipeline. Called by LandingPageUIController
        /// when the user clicks "Generate Terrain".
        ///
        /// Reads the selected image from ImageSessionManager, uploads it to
        /// the backend, and tracks progress through all 6 stages.
        /// </summary>
        public void StartPipeline()
        {
            if (_isRunning)
            {
                Debug.LogWarning("[ProcessingController] Pipeline already running.");
                return;
            }

            var session = ImageSessionManager.Instance;
            if (session == null || !session.HasSource)
            {
                Debug.LogError("[ProcessingController] No image in session.");
                return;
            }

            _isRunning = true;
            _hasError = false;

            Debug.Log("[ProcessingController] Starting pipeline...");
            _panelUI?.Show();

            StartCoroutine(RunPipeline());
        }

        // ---------------------------------------------------------------------
        // Pipeline coroutine
        // ---------------------------------------------------------------------

        /// <summary>
        /// Main pipeline coroutine. Orchestrates all stages sequentially.
        /// </summary>
        private IEnumerator RunPipeline()
        {
            var session = ImageSessionManager.Instance;

            // ── Stage 0: Uploading Image ──
            SetStage(PipelineStage.UploadingImage);

            // Find or create a BackendClient
            _backendClient = FindFirstObjectByType<BackendClient>();
            if (_backendClient == null)
            {
                var clientGo = new GameObject("[BackendClient]");
                _backendClient = clientGo.AddComponent<BackendClient>();
                DontDestroyOnLoad(clientGo);
            }

            // Subscribe to backend progress updates
            _backendClient.OnProgressUpdate += HandleBackendProgress;

            if (!string.IsNullOrEmpty(session.SampleId))
            {
                _backendClient.ProcessSample(session.SampleId);
            }
            else
            {
                string imagePath = session.FilePath;
                if (string.IsNullOrEmpty(imagePath) || !System.IO.File.Exists(imagePath))
                {
                    if (session.ImageBytes == null || session.ImageBytes.Length == 0)
                    {
                        ReportError("Selected image data is missing. Please re-select the image and try again.");
                        yield break;
                    }

                    imagePath = System.IO.Path.Combine(
                        Application.temporaryCachePath, "depthwizard_upload.png");

                    try
                    {
                        System.IO.File.WriteAllBytes(imagePath, session.ImageBytes);
                    }
                    catch (Exception ex)
                    {
                        ReportError($"Failed to prepare image upload file: {ex.Message}");
                        yield break;
                    }
                }

                _backendClient.UploadAndGenerateAsync(imagePath);
            }

            if (!_backendClient.IsBusy)
            {
                ReportError("Failed to start backend processing. Please verify the backend is running and try again.");
                yield break;
            }
            // Wait for backend to finish (IsBusy becomes false)
            // The OnProgressUpdate events will drive stage transitions
            // for stages 0-2 (backend-side stages).
            float backendTimeout = 300f; // 5 minute timeout
            float waitElapsed = 0f;

            while (_backendClient.IsBusy && !_hasError)
            {
                waitElapsed += Time.deltaTime;
                if (waitElapsed > backendTimeout)
                {
                    ReportError("Backend processing timed out. Please check " +
                                "the server and try again.");
                    yield break;
                }
                yield return null;
            }

            if (_hasError) yield break;

            // ── Stages 3-5: Unity-side processing ──
            // The BackendClient.HandleResponseFromJob already called
            // RuntimeTerrainBuilder.Build() by this point. We mark the
            // remaining stages as done to show the user the full progress.

            // Stage 3: Generating terrain mesh (already done by BackendClient)
            SetStage(PipelineStage.GeneratingTerrainMesh);
            yield return new WaitForSeconds(0.3f);
            CompleteStage(PipelineStage.GeneratingTerrainMesh);

            // Stage 4: Applying textures
            SetStage(PipelineStage.ApplyingTextures);
            yield return new WaitForSeconds(0.3f);
            CompleteStage(PipelineStage.ApplyingTextures);

            // Stage 5: Finalizing scene
            SetStage(PipelineStage.FinalizingScene);
            yield return new WaitForSeconds(0.3f);
            CompleteStage(PipelineStage.FinalizingScene);

            // ── All done! ──
            ReportProgress(1f, "Complete!");
            OnComplete?.Invoke();

            Debug.Log("[ProcessingController] Pipeline complete. " +
                      $"Transitioning to {TARGET_SCENE} in {COMPLETION_DELAY}s...");

            // Brief pause so the user sees 100%
            yield return new WaitForSeconds(COMPLETION_DELAY);

            // Fade out and load the flythrough scene
            _panelUI?.Hide();
            yield return new WaitForSeconds(0.35f); // wait for fade

            // Async scene load so the UI doesn't stall
            var asyncOp = SceneManager.LoadSceneAsync(TARGET_SCENE);
            if (asyncOp != null)
            {
                asyncOp.allowSceneActivation = true;
                while (!asyncOp.isDone)
                    yield return null;
            }

            _isRunning = false;
            UnsubscribeFromBackend();
        }

        // ---------------------------------------------------------------------
        // Backend progress handler
        // ---------------------------------------------------------------------

        /// <summary>
        /// Called by BackendClient.OnProgressUpdate with the current stage
        /// string from the backend's job poll response.
        /// </summary>
        private void HandleBackendProgress(string backendStage)
        {
            if (string.IsNullOrEmpty(backendStage)) return;

            // Handle terminal states
            if (backendStage == "error")
            {
                ReportError("Backend processing failed. The server " +
                            "encountered an error during depth inference.");
                return;
            }

            if (backendStage == "done")
            {
                // Mark all backend stages as done
                CompleteStage(PipelineStage.UploadingImage);
                CompleteStage(PipelineStage.RunningDepthInference);
                CompleteStage(PipelineStage.GeospatialCalibration);
                return;
            }

            // Map the backend stage string to our PipelineStage enum
            if (STAGE_BACKEND_MAP.TryGetValue(backendStage, out var stage))
            {
                // Complete all stages before this one
                for (int i = 0; i < (int)stage; i++)
                    CompleteStage((PipelineStage)i);

                SetStage(stage);
            }
            else
            {
                Debug.LogWarning(
                    $"[ProcessingController] Unknown backend stage: '{backendStage}'. " +
                    "Add it to STAGE_BACKEND_MAP if this is a new pipeline stage.");
            }
        }

        // ---------------------------------------------------------------------
        // Stage management
        // ---------------------------------------------------------------------

        /// <summary>
        /// Transition to a new pipeline stage. Marks it as in-progress
        /// and updates overall progress.
        /// </summary>
        private void SetStage(PipelineStage stage)
        {
            _currentStage = stage;
            int idx = (int)stage;

            // Update stage state
            OnStageStateChanged?.Invoke(idx, StageState.InProgress);

            // Calculate overall progress (sum of completed stage weights)
            float progress = 0f;
            for (int i = 0; i < idx; i++)
                progress += STAGE_WEIGHTS[i];

            // Add half of current stage weight (we're in progress)
            progress += STAGE_WEIGHTS[idx] * 0.5f;

            ReportProgress(progress, STAGE_LABELS[idx]);

            Debug.Log($"[ProcessingController] Stage: {stage} ({STAGE_LABELS[idx]})");
        }

        /// <summary>
        /// Mark a stage as completed (green checkmark).
        /// </summary>
        private void CompleteStage(PipelineStage stage)
        {
            int idx = (int)stage;
            OnStageStateChanged?.Invoke(idx, StageState.Done);

            // Update progress to include this completed stage
            float progress = 0f;
            for (int i = 0; i <= idx; i++)
                progress += STAGE_WEIGHTS[i];

            ReportProgress(progress, STAGE_LABELS[Mathf.Min(idx + 1, STAGE_LABELS.Length - 1)]);
        }

        /// <summary>
        /// Update overall progress and status message, firing events.
        /// </summary>
        private void ReportProgress(float progress, string statusMessage)
        {
            OnOverallProgressChanged?.Invoke(progress);
            OnStatusMessageChanged?.Invoke(statusMessage);
        }

        /// <summary>
        /// Report an error. Stops the pipeline and shows error UI.
        /// </summary>
        private void ReportError(string message)
        {
            _hasError = true;
            _isRunning = false;

            // Mark current stage as errored
            int idx = (int)_currentStage;
            OnStageStateChanged?.Invoke(idx, StageState.Error);
            OnError?.Invoke(message);

            UnsubscribeFromBackend();

            Debug.LogError($"[ProcessingController] Error: {message}");
        }

        // ---------------------------------------------------------------------
        // Retry / Cancel handlers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Retry the pipeline from scratch.
        /// </summary>
        private void HandleRetry()
        {
            Debug.Log("[ProcessingController] Retrying pipeline...");
            _isRunning = false;
            _hasError = false;
            StopAllCoroutines();
            StartPipeline();
        }

        /// <summary>
        /// Cancel processing and return to the landing page idle state.
        /// </summary>
        private void HandleCancel()
        {
            Debug.Log("[ProcessingController] Pipeline cancelled by user.");
            _isRunning = false;
            _hasError = false;
            StopAllCoroutines();
            UnsubscribeFromBackend();
            _panelUI?.Hide();
        }

        // ---------------------------------------------------------------------
        // Event wiring helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Subscribe ProcessingPanelUI methods to this controller's events.
        /// This keeps the UI completely decoupled — it just reacts to events.
        /// </summary>
        private void SubscribeUIToEvents()
        {
            if (_panelUI == null) return;

            // Wire combined progress + status updates.
            // ReportProgress fires both events; the UI receives them
            // and updates the progress bar + status text independently.
            OnOverallProgressChanged += progress =>
                _panelUI.UpdateProgress(progress, null);

            OnStatusMessageChanged += message =>
                _panelUI.UpdateProgress(-1f, message);

            OnStageStateChanged += (index, state) =>
                _panelUI.SetStageStatus(index, state);

            OnError += message =>
                _panelUI.ShowError(message);
        }

        private void UnsubscribeFromBackend()
        {
            if (_backendClient != null)
                _backendClient.OnProgressUpdate -= HandleBackendProgress;
        }
    }
}

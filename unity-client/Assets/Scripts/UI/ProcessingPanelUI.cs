// =============================================================================
// ProcessingPanelUI.cs
//
// Pure UI controller for the DepthWizard processing overlay panel.
// This script manages all visual elements of the processing screen:
//   - Dimmed background overlay
//   - Modal panel with progress bar, stage list, status text
//   - Spinner animation
//   - Elapsed time counter
//   - Error state with Retry / Cancel buttons
//
// IMPORTANT: This script has ZERO knowledge of the actual pipeline.
// It only subscribes to events from ProcessingController and updates
// visuals accordingly. Pipeline Devs should never need to touch this file.
//
// UI element discovery follows the same pattern as LandingPageUIController:
// elements are found by name in the hierarchy (built by SetupLandingPageScene).
//
// Namespace: DepthWizard.UI
// =============================================================================

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DepthWizard.UI
{
    /// <summary>
    /// Possible visual states for a pipeline stage row in the UI.
    /// </summary>
    public enum StageState
    {
        Pending,     // Greyed out, waiting
        InProgress,  // Cyan accent, pulsing
        Done,        // Green checkmark
        Error        // Red X
    }

    /// <summary>
    /// Pure UI for the processing overlay. Subscribes to events, doesn't
    /// know pipeline internals. Attach to the Canvas alongside
    /// <see cref="ProcessingController"/>.
    /// </summary>
    public class ProcessingPanelUI : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // UI element names (must match hierarchy from SetupLandingPageScene)
        // ---------------------------------------------------------------------

        private const string NAME_OVERLAY          = "ProcessingOverlay";
        private const string NAME_DIM_BG           = "DimBackground";
        private const string NAME_PANEL            = "ProcessingPanel";
        private const string NAME_PANEL_TITLE      = "PanelTitle";
        private const string NAME_SPINNER          = "SpinnerIcon";
        private const string NAME_PROGRESS_BG      = "ProgressBarBg";
        private const string NAME_PROGRESS_FILL    = "ProgressBarFill";
        private const string NAME_PROGRESS_PERCENT = "ProgressPercentText";
        private const string NAME_STAGE_LIST       = "StageList";
        private const string NAME_STATUS_TEXT      = "ProcessingStatusText";
        private const string NAME_ELAPSED_TEXT     = "ElapsedTimeText";
        private const string NAME_RETRY_BTN        = "RetryButton";
        private const string NAME_CANCEL_BTN       = "CancelButton";
        private const string NAME_ERROR_ROW        = "ErrorButtonRow";

        // Stage row naming convention: "StageRow_0", "StageRow_1", etc.
        private const string NAME_STAGE_ROW_PREFIX = "StageRow_";
        private const string NAME_STAGE_ICON       = "StageIcon";
        private const string NAME_STAGE_LABEL      = "StageLabel";

        // Total number of pipeline stages (must match ProcessingController)
        private const int STAGE_COUNT = 6;

        // ---------------------------------------------------------------------
        // Colors (match landing page sci-fi theme)
        // ---------------------------------------------------------------------

        private static readonly Color COL_ACCENT       = new Color(0f, 0.898f, 1f, 1f);    // #00E5FF cyan
        private static readonly Color COL_SUCCESS      = new Color(0.412f, 0.941f, 0.682f, 1f); // #69F0AE green
        private static readonly Color COL_ERROR        = new Color(1f, 0.322f, 0.322f, 1f); // #FF5252 red
        private static readonly Color COL_PENDING      = new Color(1f, 1f, 1f, 0.25f);      // dim white
        private static readonly Color COL_IN_PROGRESS  = new Color(0f, 0.898f, 1f, 0.9f);   // bright cyan
        private static readonly Color COL_DONE         = new Color(0.412f, 0.941f, 0.682f, 0.9f);
        private static readonly Color COL_STATUS_TEXT  = new Color(1f, 1f, 1f, 0.7f);

        // Unicode icons for stage states
        private const string ICON_PENDING     = "\u25CB";   // hollow circle ○
        private const string ICON_IN_PROGRESS = "\u25CF";   // filled circle ●
        private const string ICON_DONE        = "\u2713";   // checkmark ✓
        private const string ICON_ERROR       = "\u2717";   // X mark ✗

        // ---------------------------------------------------------------------
        // Resolved UI references
        // ---------------------------------------------------------------------

        private GameObject  _overlay;
        private CanvasGroup _overlayCanvasGroup;
        private Image       _progressFill;
        private TMP_Text    _progressPercentText;
        private TMP_Text    _statusText;
        private TMP_Text    _elapsedTimeText;
        private TMP_Text    _panelTitle;
        private Transform   _spinner;
        private Button      _retryButton;
        private Button      _cancelButton;
        private GameObject  _errorButtonRow;

        // Stage row references
        private TMP_Text[] _stageIcons;
        private TMP_Text[] _stageLabelTexts;
        private StageState[] _stageStates;

        // Animation state
        private float _displayedProgress = 0f;
        private float _targetProgress = 0f;
        private float _elapsedTime = 0f;
        private bool  _isVisible = false;
        private bool  _isTimerRunning = false;
        private Coroutine _fadeCoroutine;
        private Coroutine _pulseCoroutine;

        // Spinner rotation speed (degrees per second)
        private const float SPINNER_SPEED = 180f;

        // Progress bar smooth lerp speed
        private const float PROGRESS_LERP_SPEED = 3f;

        // Events for Retry / Cancel (subscribed by ProcessingController)
        /// <summary>Fired when the user clicks "Retry" in the error state.</summary>
        public event Action OnRetryClicked;

        /// <summary>Fired when the user clicks "Cancel" in the error state.</summary>
        public event Action OnCancelClicked;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Awake()
        {
            ResolveUIElements();
            WireButtons();

            // Start hidden
            if (_overlay != null)
                _overlay.SetActive(false);
        }

        private void Update()
        {
            if (!_isVisible) return;

            // Rotate spinner
            if (_spinner != null)
                _spinner.Rotate(0f, 0f, -SPINNER_SPEED * Time.deltaTime);

            // Smooth progress bar
            if (Mathf.Abs(_displayedProgress - _targetProgress) > 0.001f)
            {
                _displayedProgress = Mathf.Lerp(
                    _displayedProgress, _targetProgress,
                    Time.deltaTime * PROGRESS_LERP_SPEED);
                ApplyProgressVisuals(_displayedProgress);
            }

            // Update elapsed time
            if (_isTimerRunning)
            {
                _elapsedTime += Time.deltaTime;
                UpdateElapsedTimeDisplay();
            }
        }

        private void OnDestroy()
        {
            UnwireButtons();
        }

        // ---------------------------------------------------------------------
        // Public API (called by ProcessingController via events)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fade in the processing overlay, blocking the landing page.
        /// Resets all state to fresh.
        /// </summary>
        public void Show()
        {
            ResetState();

            if (_overlay == null) return;
            _overlay.SetActive(true);
            _isVisible = true;
            _isTimerRunning = true;

            // Hide error buttons
            if (_errorButtonRow != null)
                _errorButtonRow.SetActive(false);

            // Fade in
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(FadeOverlay(0f, 1f, 0.3f));

            // Start pulse coroutine for in-progress stages
            if (_pulseCoroutine != null) StopCoroutine(_pulseCoroutine);
            _pulseCoroutine = StartCoroutine(PulseInProgressStages());

            Debug.Log("[ProcessingPanelUI] Panel shown.");
        }

        /// <summary>
        /// Fade out and hide the processing overlay.
        /// </summary>
        public void Hide()
        {
            if (!_isVisible) return;
            _isTimerRunning = false;

            if (_pulseCoroutine != null)
            {
                StopCoroutine(_pulseCoroutine);
                _pulseCoroutine = null;
            }

            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(FadeOverlay(1f, 0f, 0.3f, () =>
            {
                _isVisible = false;
                if (_overlay != null)
                    _overlay.SetActive(false);
            }));

            Debug.Log("[ProcessingPanelUI] Panel hidden.");
        }

        /// <summary>
        /// Update the overall progress bar and/or status message.
        /// Pass a negative overallPercent to update only the message.
        /// </summary>
        /// <param name="overallPercent">0 to 1 progress value, or negative to skip bar update.</param>
        /// <param name="statusMessage">Human-readable status string, or null to skip.</param>
        public void UpdateProgress(float overallPercent, string statusMessage)
        {
            if (overallPercent >= 0f)
                _targetProgress = Mathf.Clamp01(overallPercent);

            if (_statusText != null && statusMessage != null)
            {
                _statusText.text = statusMessage;
                _statusText.color = COL_STATUS_TEXT;
            }
        }

        /// <summary>
        /// Update the visual state of a specific pipeline stage row.
        /// </summary>
        /// <param name="stageIndex">0-based stage index (0..5).</param>
        /// <param name="state">New visual state for this stage.</param>
        public void SetStageStatus(int stageIndex, StageState state)
        {
            if (stageIndex < 0 || stageIndex >= STAGE_COUNT) return;
            _stageStates[stageIndex] = state;

            if (_stageIcons == null || _stageIcons[stageIndex] == null) return;

            switch (state)
            {
                case StageState.Pending:
                    _stageIcons[stageIndex].text = ICON_PENDING;
                    _stageIcons[stageIndex].color = COL_PENDING;
                    if (_stageLabelTexts[stageIndex] != null)
                        _stageLabelTexts[stageIndex].color = COL_PENDING;
                    break;

                case StageState.InProgress:
                    _stageIcons[stageIndex].text = ICON_IN_PROGRESS;
                    _stageIcons[stageIndex].color = COL_IN_PROGRESS;
                    if (_stageLabelTexts[stageIndex] != null)
                        _stageLabelTexts[stageIndex].color = COL_IN_PROGRESS;
                    break;

                case StageState.Done:
                    _stageIcons[stageIndex].text = ICON_DONE;
                    _stageIcons[stageIndex].color = COL_DONE;
                    if (_stageLabelTexts[stageIndex] != null)
                        _stageLabelTexts[stageIndex].color = COL_DONE;
                    break;

                case StageState.Error:
                    _stageIcons[stageIndex].text = ICON_ERROR;
                    _stageIcons[stageIndex].color = COL_ERROR;
                    if (_stageLabelTexts[stageIndex] != null)
                        _stageLabelTexts[stageIndex].color = COL_ERROR;
                    break;
            }
        }

        /// <summary>
        /// Switch to the error state: show red status, stop spinner,
        /// display Retry and Cancel buttons.
        /// </summary>
        /// <param name="errorMessage">User-friendly error description.</param>
        public void ShowError(string errorMessage)
        {
            _isTimerRunning = false;

            // Update status text to red error
            if (_statusText != null)
            {
                _statusText.text = errorMessage;
                _statusText.color = COL_ERROR;
            }

            // Update title
            if (_panelTitle != null)
                _panelTitle.text = "Processing Failed";

            // Hide spinner
            if (_spinner != null)
                _spinner.gameObject.SetActive(false);

            // Show error buttons
            if (_errorButtonRow != null)
                _errorButtonRow.SetActive(true);

            Debug.Log($"[ProcessingPanelUI] Error shown: {errorMessage}");
        }

        // ---------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------

        private void ResolveUIElements()
        {
            _overlay = FindChild(NAME_OVERLAY);

            if (_overlay != null)
            {
                _overlayCanvasGroup = _overlay.GetComponent<CanvasGroup>();
                if (_overlayCanvasGroup == null)
                    _overlayCanvasGroup = _overlay.AddComponent<CanvasGroup>();
                _overlayCanvasGroup.blocksRaycasts = true;
            }

            _panelTitle = FindTMP(NAME_PANEL_TITLE);

            var spinnerGo = FindChild(NAME_SPINNER);
            if (spinnerGo != null)
                _spinner = spinnerGo.transform;

            var fillGo = FindChild(NAME_PROGRESS_FILL);
            if (fillGo != null)
                _progressFill = fillGo.GetComponent<Image>();

            _progressPercentText = FindTMP(NAME_PROGRESS_PERCENT);
            _statusText = FindTMP(NAME_STATUS_TEXT);
            _elapsedTimeText = FindTMP(NAME_ELAPSED_TEXT);

            var retryGo = FindChild(NAME_RETRY_BTN);
            if (retryGo != null)
                _retryButton = retryGo.GetComponent<Button>();

            var cancelGo = FindChild(NAME_CANCEL_BTN);
            if (cancelGo != null)
                _cancelButton = cancelGo.GetComponent<Button>();

            _errorButtonRow = FindChild(NAME_ERROR_ROW);

            // Resolve stage rows
            _stageIcons = new TMP_Text[STAGE_COUNT];
            _stageLabelTexts = new TMP_Text[STAGE_COUNT];
            _stageStates = new StageState[STAGE_COUNT];

            for (int i = 0; i < STAGE_COUNT; i++)
            {
                var rowGo = FindChild(NAME_STAGE_ROW_PREFIX + i);
                if (rowGo == null) continue;

                var iconTransform = FindDeep(rowGo.transform, NAME_STAGE_ICON);
                if (iconTransform != null)
                    _stageIcons[i] = iconTransform.GetComponent<TMP_Text>();

                var labelTransform = FindDeep(rowGo.transform, NAME_STAGE_LABEL);
                if (labelTransform != null)
                    _stageLabelTexts[i] = labelTransform.GetComponent<TMP_Text>();
            }
        }

        private void WireButtons()
        {
            _retryButton?.onClick.AddListener(HandleRetryClicked);
            _cancelButton?.onClick.AddListener(HandleCancelClicked);
        }

        private void UnwireButtons()
        {
            _retryButton?.onClick.RemoveListener(HandleRetryClicked);
            _cancelButton?.onClick.RemoveListener(HandleCancelClicked);
        }

        // ---------------------------------------------------------------------
        // Button handlers
        // ---------------------------------------------------------------------

        private void HandleRetryClicked()
        {
            Debug.Log("[ProcessingPanelUI] Retry clicked.");
            OnRetryClicked?.Invoke();
        }

        private void HandleCancelClicked()
        {
            Debug.Log("[ProcessingPanelUI] Cancel clicked.");
            OnCancelClicked?.Invoke();
        }

        // ---------------------------------------------------------------------
        // Internal helpers
        // ---------------------------------------------------------------------

        private void ResetState()
        {
            _displayedProgress = 0f;
            _targetProgress = 0f;
            _elapsedTime = 0f;

            ApplyProgressVisuals(0f);

            if (_statusText != null)
            {
                _statusText.text = "Preparing...";
                _statusText.color = COL_STATUS_TEXT;
            }

            if (_panelTitle != null)
                _panelTitle.text = "Processing";

            if (_spinner != null)
            {
                _spinner.gameObject.SetActive(true);
                _spinner.localRotation = Quaternion.identity;
            }

            UpdateElapsedTimeDisplay();

            // Reset all stages to pending
            for (int i = 0; i < STAGE_COUNT; i++)
                SetStageStatus(i, StageState.Pending);
        }

        private void ApplyProgressVisuals(float progress)
        {
            // Update fill bar (scale X from 0 to 1)
            if (_progressFill != null)
                _progressFill.fillAmount = progress;

            // Update percentage text
            if (_progressPercentText != null)
                _progressPercentText.text = $"{Mathf.RoundToInt(progress * 100)}%";
        }

        private void UpdateElapsedTimeDisplay()
        {
            if (_elapsedTimeText == null) return;
            int minutes = Mathf.FloorToInt(_elapsedTime / 60f);
            int seconds = Mathf.FloorToInt(_elapsedTime % 60f);
            _elapsedTimeText.text = $"{minutes:00}:{seconds:00}";
        }

        // ---------------------------------------------------------------------
        // Coroutines
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fade the overlay CanvasGroup alpha from → to over duration.
        /// Optional callback on completion.
        /// </summary>
        private IEnumerator FadeOverlay(float from, float to, float duration,
            Action onComplete = null)
        {
            if (_overlayCanvasGroup == null) yield break;

            _overlayCanvasGroup.alpha = from;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Ease out cubic for smooth feel
                float eased = 1f - Mathf.Pow(1f - t, 3f);
                _overlayCanvasGroup.alpha = Mathf.Lerp(from, to, eased);
                yield return null;
            }

            _overlayCanvasGroup.alpha = to;
            onComplete?.Invoke();
        }

        /// <summary>
        /// Continuously pulse the alpha of in-progress stage icons for a
        /// breathing effect. Runs while the panel is visible.
        /// </summary>
        private IEnumerator PulseInProgressStages()
        {
            float pulseSpeed = 2.5f; // full cycles per second

            while (_isVisible)
            {
                float pulse = (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) / 2f;
                float alpha = Mathf.Lerp(0.4f, 1.0f, pulse);

                for (int i = 0; i < STAGE_COUNT; i++)
                {
                    if (_stageStates[i] != StageState.InProgress) continue;
                    if (_stageIcons[i] == null) continue;

                    var c = COL_IN_PROGRESS;
                    c.a = alpha;
                    _stageIcons[i].color = c;
                }

                yield return null;
            }
        }

        // ---------------------------------------------------------------------
        // Hierarchy search helpers (same pattern as LandingPageUIController)
        // ---------------------------------------------------------------------

        private GameObject FindChild(string name)
        {
            var result = FindDeep(transform, name);
            if (result == null)
                Debug.LogWarning(
                    $"[ProcessingPanelUI] UI element '{name}' not found. " +
                    "Run Tools \u2192 DepthWizard \u2192 Setup Landing Page to rebuild.");
            return result?.gameObject;
        }

        private TMP_Text FindTMP(string name)
        {
            var go = FindChild(name);
            if (go == null) return null;
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp == null)
                Debug.LogWarning(
                    $"[ProcessingPanelUI] '{name}' found but has no TMP_Text.");
            return tmp;
        }

        /// <summary>
        /// Recursive depth-first search for a child with the given name.
        /// </summary>
        private static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                    return child;
                var result = FindDeep(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }
    }
}

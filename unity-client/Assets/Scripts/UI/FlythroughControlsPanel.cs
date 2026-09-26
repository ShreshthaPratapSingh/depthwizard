// =============================================================================
// FlythroughControlsPanel.cs
//
// On-screen clickable buttons for flythrough-scene actions that were previously
// keyboard-only (PRD v2.0 FR24). Positioned bottom-right to avoid colliding
// with the control hints (bottom-left) and export button (top-right).
//
// Buttons provided:
//   - Elevation Mode (M key equivalent): toggles relative/absolute
//   - Camera Mode (Tab key equivalent): toggles FreeFly/Orbit
//   - Cinematic (C key equivalent): toggles cinematic flythrough
//
// Export is NOT duplicated here — ExportButtonHandler already provides a
// visible on-screen button in the top-right corner.
//
// Self-contained: creates its own Canvas at runtime, does not require any
// pre-existing UI hierarchy or Inspector wiring. Reads state from existing
// controllers via their public APIs.
//
// Attach to: the Main Camera GameObject in SampleScene (same as
// DroneController, CinematicFlythrough, ExportButtonHandler).
//
// Namespace: DepthWizard.UI
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DepthWizard.Camera;
using DepthWizard.Terrain;

namespace DepthWizard.UI
{
    /// <summary>
    /// Builds clickable control buttons at runtime for the flythrough scene.
    /// Updates button labels to reflect current state (e.g. "Absolute ✓").
    /// </summary>
    public class FlythroughControlsPanel : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Theme (matching ControlHintsOverlay / ExportButtonHandler palette)
        // ---------------------------------------------------------------------

        // Colors from centralized HudTheme
        private static Color COL_BG         => HudTheme.COL_BG_PANEL;
        private static Color COL_ACCENT     => HudTheme.COL_ACCENT;
        private static Color COL_BTN_NORMAL => HudTheme.COL_BTN_NORMAL;
        private static Color COL_BTN_HOVER  => HudTheme.COL_BTN_HOVER;
        private static Color COL_BTN_ACTIVE => HudTheme.COL_BTN_PRESS;
        private static Color COL_LABEL      => HudTheme.COL_TEXT;
        private static Color COL_KEY_HINT   => HudTheme.COL_TEXT_DIM;

        // ---------------------------------------------------------------------
        // State / references
        // ---------------------------------------------------------------------

        private DroneController _drone;
        private CinematicFlythrough _cinematic;
        private TerrainElevationController _elevation;

        private TMP_Text _elevationLabel;
        private TMP_Text _cameraModeLabel;
        private TMP_Text _cinematicLabel;
        private TMP_Text _confidenceLabel;

        // Track state for label updates
        private bool _lastAbsolute;
        private CameraMode _lastCameraMode;
        private bool _lastCinematicActive;
        private bool _lastConfidenceVisible;

        private ConfidenceOverlayController _confidence;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Start()
        {
            _drone = FindFirstObjectByType<DroneController>();
            _cinematic = FindFirstObjectByType<CinematicFlythrough>();

            BuildUI();

            // Start checking for TerrainElevationController (added after terrain builds)
            StartCoroutine(WatchForElevationController());
        }

        private void Update()
        {
            UpdateLabels();
        }

        // ---------------------------------------------------------------------
        // UI construction
        // ---------------------------------------------------------------------

        private void BuildUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("ControlsButtonCanvas");
            canvasGo.transform.SetParent(transform);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 92; // between hints (90) and export (100)

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Panel (bottom-right) ---
            var panelGo = new GameObject("ControlsPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);

            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 0f);
            panelRect.pivot = new Vector2(1f, 0f);
            panelRect.anchoredPosition = new Vector2(-16f, 16f);

            var bgImage = panelGo.AddComponent<Image>();
            bgImage.color = COL_BG;

            var outline = panelGo.AddComponent<Outline>();
            outline.effectColor = HudTheme.COL_BORDER;
            outline.effectDistance = new Vector2(1f, 1f);

            // --- Vertical layout ---
            var vlg = panelGo.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 8, 8);
            vlg.spacing = 4f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var fitter = panelGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // --- Buttons ---
            _elevationLabel = CreateButton(panelGo.transform, "Relative", "M",
                () => OnElevationToggle());

            _cameraModeLabel = CreateButton(panelGo.transform, "FreeFly", "Tab",
                () => OnCameraModeToggle());

            _cinematicLabel = CreateButton(panelGo.transform, "Cinematic", "C",
                () => OnCinematicToggle());

            _confidenceLabel = CreateButton(panelGo.transform, "Confidence", "V",
                () => OnConfidenceToggle());
        }

        // ---------------------------------------------------------------------
        // Button factory
        // ---------------------------------------------------------------------

        private TMP_Text CreateButton(Transform parent, string label, string keyHint,
            UnityEngine.Events.UnityAction onClick)
        {
            var btnGo = new GameObject("Btn_" + label);
            btnGo.transform.SetParent(parent, false);

            // Layout
            var le = btnGo.AddComponent<LayoutElement>();
            le.preferredWidth = 170f;
            le.preferredHeight = 32f;

            // Background
            var btnImage = btnGo.AddComponent<Image>();
            btnImage.color = COL_BTN_NORMAL;

            // Button component
            var btn = btnGo.AddComponent<Button>();
            btn.targetGraphic = btnImage;
            btn.onClick.AddListener(onClick);

            var colors = btn.colors;
            colors.normalColor = COL_BTN_NORMAL;
            colors.highlightedColor = COL_BTN_HOVER;
            colors.pressedColor = COL_BTN_ACTIVE;
            colors.disabledColor = HudTheme.COL_BTN_DISABLED;
            btn.colors = colors;

            // Outline for a subtle border
            var btnOutline = btnGo.AddComponent<Outline>();
            btnOutline.effectColor = HudTheme.COL_BORDER;
            btnOutline.effectDistance = new Vector2(1f, 1f);

            // --- Horizontal layout inside button ---
            var hlg = btnGo.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 8, 2, 2);
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            // Main label (left-aligned, takes most space)
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(btnGo.transform, false);
            var labelText = labelGo.AddComponent<TextMeshProUGUI>();
            labelText.text = label;
            labelText.fontSize = HudTheme.FONT_BODY;
            labelText.color = COL_LABEL;
            labelText.fontStyle = FontStyles.Bold;
            labelText.alignment = TextAlignmentOptions.Left;

            var labelLE = labelGo.AddComponent<LayoutElement>();
            labelLE.flexibleWidth = 1f;

            // Key hint (right-aligned, dim)
            var hintGo = new GameObject("KeyHint");
            hintGo.transform.SetParent(btnGo.transform, false);
            var hintText = hintGo.AddComponent<TextMeshProUGUI>();
            hintText.text = keyHint;
            hintText.fontSize = HudTheme.FONT_SMALL;
            hintText.color = COL_KEY_HINT;
            hintText.fontStyle = FontStyles.Italic;
            hintText.alignment = TextAlignmentOptions.Right;

            var hintLE = hintGo.AddComponent<LayoutElement>();
            hintLE.preferredWidth = 30f;

            return labelText;
        }

        // ---------------------------------------------------------------------
        // Button callbacks
        // ---------------------------------------------------------------------

        private void OnElevationToggle()
        {
            if (_elevation == null)
            {
                // Try to find it now (terrain may have been built)
                _elevation = FindFirstObjectByType<TerrainElevationController>();
            }

            if (_elevation != null)
            {
                _elevation.SetElevationMode(!_elevation.IsAbsolute);
            }
            else
            {
                Debug.LogWarning("[FlythroughControlsPanel] No TerrainElevationController found.");
            }
        }

        private void OnCameraModeToggle()
        {
            if (_drone != null)
            {
                _drone.ToggleCameraMode();
            }
        }

        private void OnCinematicToggle()
        {
            if (_cinematic != null)
            {
                _cinematic.ToggleFlythrough();
            }
        }

        private void OnConfidenceToggle()
        {
            if (_confidence == null)
            {
                _confidence = FindFirstObjectByType<ConfidenceOverlayController>();
            }
            if (_confidence != null && _confidence.HasData)
            {
                _confidence.Toggle();
            }
            else
            {
                Debug.LogWarning("[FlythroughControlsPanel] No confidence data loaded.");
            }
        }

        // ---------------------------------------------------------------------
        // Label updates (reflect current state)
        // ---------------------------------------------------------------------

        private void UpdateLabels()
        {
            // Elevation mode
            if (_elevation != null)
            {
                bool abs = _elevation.IsAbsolute;
                if (abs != _lastAbsolute)
                {
                    _lastAbsolute = abs;
                    _elevationLabel.text = abs ? "Absolute ✓" : "Relative";
                    _elevationLabel.color = abs ? COL_ACCENT : COL_LABEL;
                }
            }

            // Camera mode
            if (_drone != null)
            {
                var mode = _drone.CurrentMode;
                if (mode != _lastCameraMode)
                {
                    _lastCameraMode = mode;
                    _cameraModeLabel.text = mode == CameraMode.Orbit ? "Orbit ✓" : "FreeFly";
                    _cameraModeLabel.color = mode == CameraMode.Orbit ? COL_ACCENT : COL_LABEL;
                }
            }

            // Cinematic
            if (_cinematic != null)
            {
                bool active = _cinematic.IsActive;
                if (active != _lastCinematicActive)
                {
                    _lastCinematicActive = active;
                    _cinematicLabel.text = active ? "Cinematic ■" : "Cinematic";
                    _cinematicLabel.color = active ? COL_ACCENT : COL_LABEL;
                }
            }

            // Confidence overlay
            if (_confidence == null)
                _confidence = FindFirstObjectByType<ConfidenceOverlayController>();
            if (_confidence != null)
            {
                bool visible = _confidence.IsVisible;
                if (visible != _lastConfidenceVisible)
                {
                    _lastConfidenceVisible = visible;
                    _confidenceLabel.text = visible ? "Confidence ✓" : "Confidence";
                    _confidenceLabel.color = visible ? COL_ACCENT : COL_LABEL;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Terrain detection
        // ---------------------------------------------------------------------

        private IEnumerator WatchForElevationController()
        {
            while (_elevation == null)
            {
                _elevation = FindFirstObjectByType<TerrainElevationController>();
                yield return new WaitForSeconds(0.5f);
            }
            // Sync initial state
            _lastAbsolute = _elevation.IsAbsolute;
            _elevationLabel.text = _lastAbsolute ? "Absolute ✓" : "Relative";
            _elevationLabel.color = _lastAbsolute ? COL_ACCENT : COL_LABEL;
        }
    }
}

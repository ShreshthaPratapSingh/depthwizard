// =============================================================================
// ExportButtonHandler.cs
//
// Creates a minimal HUD overlay in the terrain flythrough scene (SampleScene)
// with an "Export Terrain" button. On click, calls TerrainExporter.ExportAll()
// which writes OBJ + MTL + texture PNG + heightmap PNG to
// Application.persistentDataPath/Exports/.
//
// The button is disabled until Terrain.activeTerrain is available (i.e. after
// RuntimeTerrainBuilder has finished generating the terrain).
//
// Self-contained: creates its own Canvas at runtime, does not require any
// pre-existing UI hierarchy or Inspector wiring.
//
// Attach to: any GameObject in SampleScene (e.g. the same one that has
// BackendClient / TestTrigger). Or let SetupSampleSceneUI add it.
//
// Namespace: DepthWizard.UI
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DepthWizard.UI
{
    /// <summary>
    /// Builds a small HUD overlay with an Export button in the top-right corner.
    /// Monitors for terrain availability and enables the button once a terrain
    /// exists. On click, exports OBJ + heightmap via TerrainExporter.
    /// </summary>
    public class ExportButtonHandler : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Theme colors (matching landing page sci-fi palette)
        // ---------------------------------------------------------------------

        private static readonly Color COL_BG        = new Color(0.06f, 0.09f, 0.14f, 0.85f);
        private static readonly Color COL_ACCENT    = new Color(0f, 0.898f, 1f, 1f);       // #00E5FF
        private static readonly Color COL_SUCCESS   = new Color(0.412f, 0.941f, 0.682f, 1f); // #69F0AE
        private static readonly Color COL_ERROR     = new Color(1f, 0.322f, 0.322f, 1f);     // #FF5252
        private static readonly Color COL_DISABLED  = new Color(1f, 1f, 1f, 0.25f);
        private static readonly Color COL_BTN_NORMAL = new Color(0f, 0.898f, 1f, 0.15f);
        private static readonly Color COL_BTN_HOVER  = new Color(0f, 0.898f, 1f, 0.3f);

        private const string DEFAULT_LABEL = "Export Terrain";
        private const float FEEDBACK_DURATION = 2.5f;

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private Button _exportButton;
        private TMP_Text _buttonLabel;
        private TMP_Text _pathLabel;
        private Coroutine _feedbackCoroutine;
        private bool _terrainReady;

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Start()
        {
            BuildUI();
            // Start checking for terrain availability
            StartCoroutine(WatchForTerrain());
        }

        // ---------------------------------------------------------------------
        // UI construction
        // ---------------------------------------------------------------------

        private void BuildUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("ExportHUDCanvas");
            canvasGo.transform.SetParent(transform);
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100; // on top of everything

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGo.AddComponent<GraphicRaycaster>();

            // Ensure an EventSystem exists (needed for button clicks and
            // for EventSystem.current.IsPointerOverGameObject() in DroneController)
            if (UnityEngine.EventSystems.EventSystem.current == null)
            {
                var esGo = new GameObject("EventSystem");
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            }

            // --- Button container (top-right) ---
            var panelGo = new GameObject("ExportPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);

            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(-20f, -20f);
            panelRect.sizeDelta = new Vector2(220f, 80f);

            // --- Button ---
            var btnGo = new GameObject("ExportButton");
            btnGo.transform.SetParent(panelGo.transform, false);

            var btnRect = btnGo.AddComponent<RectTransform>();
            btnRect.anchorMin = Vector2.zero;
            btnRect.anchorMax = new Vector2(1f, 0.65f);
            btnRect.offsetMin = Vector2.zero;
            btnRect.offsetMax = Vector2.zero;

            var btnImage = btnGo.AddComponent<Image>();
            btnImage.color = COL_BTN_NORMAL;

            _exportButton = btnGo.AddComponent<Button>();
            _exportButton.targetGraphic = btnImage;
            _exportButton.onClick.AddListener(OnExportClicked);

            // Button colors
            var colors = _exportButton.colors;
            colors.normalColor = COL_BTN_NORMAL;
            colors.highlightedColor = COL_BTN_HOVER;
            colors.pressedColor = COL_ACCENT;
            colors.disabledColor = new Color(0.1f, 0.1f, 0.15f, 0.5f);
            _exportButton.colors = colors;

            // Rounded look via outline
            var outline = btnGo.AddComponent<Outline>();
            outline.effectColor = COL_ACCENT;
            outline.effectDistance = new Vector2(1f, 1f);

            // --- Button label ---
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(btnGo.transform, false);

            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(8f, 2f);
            labelRect.offsetMax = new Vector2(-8f, -2f);

            _buttonLabel = labelGo.AddComponent<TextMeshProUGUI>();
            _buttonLabel.text = DEFAULT_LABEL;
            _buttonLabel.fontSize = 18f;
            _buttonLabel.color = COL_ACCENT;
            _buttonLabel.alignment = TextAlignmentOptions.Center;
            _buttonLabel.fontStyle = FontStyles.Bold;

            // --- Path info label (below button, small text) ---
            var pathGo = new GameObject("PathLabel");
            pathGo.transform.SetParent(panelGo.transform, false);

            var pathRect = pathGo.AddComponent<RectTransform>();
            pathRect.anchorMin = new Vector2(0f, 0.65f);
            pathRect.anchorMax = new Vector2(1f, 1f);
            pathRect.offsetMin = Vector2.zero;
            pathRect.offsetMax = Vector2.zero;

            _pathLabel = pathGo.AddComponent<TextMeshProUGUI>();
            _pathLabel.text = "Press E or click to export";
            _pathLabel.fontSize = 11f;
            _pathLabel.color = COL_DISABLED;
            _pathLabel.alignment = TextAlignmentOptions.Center;
            _pathLabel.enableWordWrapping = true;

            // Start disabled
            SetButtonEnabled(false);
        }

        // ---------------------------------------------------------------------
        // Terrain detection
        // ---------------------------------------------------------------------

        private IEnumerator WatchForTerrain()
        {
            while (true)
            {
                bool hasTerrain = UnityEngine.Terrain.activeTerrain != null;
                if (hasTerrain != _terrainReady)
                {
                    _terrainReady = hasTerrain;
                    SetButtonEnabled(hasTerrain);
                    if (hasTerrain)
                    {
                        _pathLabel.text = "Press E or click to export";
                        _pathLabel.color = COL_DISABLED;
                    }
                }
                yield return new WaitForSeconds(0.5f);
            }
        }

        // ---------------------------------------------------------------------
        // Export action
        // ---------------------------------------------------------------------

        private void OnExportClicked()
        {
            if (!_terrainReady)
            {
                ShowFeedback("No terrain to export!", COL_ERROR);
                return;
            }

            // Use project-relative Exports/ folder (same as ExportTest.cs T-key)
            string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string exportDir = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", "Exports"));

            try
            {
                System.IO.Directory.CreateDirectory(exportDir);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ExportButtonHandler] Failed to create export dir: {ex.Message}");
                ShowFeedback("Export failed!", COL_ERROR);
                return;
            }

            string objPath = System.IO.Path.Combine(exportDir, $"terrain_{timestamp}.obj");
            string pngPath = System.IO.Path.Combine(exportDir, $"heightmap_{timestamp}.png");

            bool objOk = DepthWizard.Export.TerrainExporter.ExportCurrentTerrainAsObj(objPath);
            bool pngOk = DepthWizard.Export.TerrainExporter.ExportCurrentTerrainAsHeightmapPng(pngPath);

            if (objOk || pngOk)
            {
                Debug.Log($"[ExportButtonHandler] Export complete → {exportDir}");
                ShowFeedback("Exported ✓", COL_SUCCESS);
                _pathLabel.text = exportDir.Replace("\\", "/");
                _pathLabel.color = new Color(1f, 1f, 1f, 0.5f);
            }
            else
            {
                ShowFeedback("Export failed!", COL_ERROR);
            }
        }

        // ---------------------------------------------------------------------
        // Feedback
        // ---------------------------------------------------------------------

        private void ShowFeedback(string message, Color color)
        {
            if (_feedbackCoroutine != null)
                StopCoroutine(_feedbackCoroutine);
            _feedbackCoroutine = StartCoroutine(FeedbackRoutine(message, color));
        }

        private IEnumerator FeedbackRoutine(string message, Color color)
        {
            _buttonLabel.text = message;
            _buttonLabel.color = color;
            SetButtonEnabled(false);

            yield return new WaitForSeconds(FEEDBACK_DURATION);

            _buttonLabel.text = DEFAULT_LABEL;
            _buttonLabel.color = COL_ACCENT;
            if (_terrainReady)
                SetButtonEnabled(true);

            _feedbackCoroutine = null;
        }

        private void SetButtonEnabled(bool enabled)
        {
            if (_exportButton != null)
            {
                _exportButton.interactable = enabled;
                _buttonLabel.color = enabled ? COL_ACCENT : COL_DISABLED;
            }
        }
    }
}

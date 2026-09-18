// =============================================================================
// LoadNewImageButton.cs
//
// Adds a "Load New Image" button to the flythrough scene (SampleScene) that
// returns to the landing page for another image upload (PRD v2.0 A3).
//
// On click:
//   1. Clears ImageSessionManager state (preview, result data).
//   2. Destroys DontDestroyOnLoad HUD objects (AccuracyMetricsHud).
//   3. Loads the LandingPage scene.
//
// Positioned top-left to avoid colliding with:
//   - Export button (top-right)
//   - Control hints (bottom-left)
//   - Flythrough controls (bottom-right)
//   - Accuracy HUD (top-left, below this button — offset down)
//
// Self-contained: creates its own Canvas at runtime.
//
// Attach to: the Main Camera GameObject in SampleScene.
//
// Namespace: DepthWizard.UI
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

namespace DepthWizard.UI
{
    /// <summary>
    /// Flythrough-scene button that returns to the landing page for a new image.
    /// </summary>
    public class LoadNewImageButton : MonoBehaviour
    {
        // Theme (matching existing UI palette)
        private static readonly Color COL_BG         = new Color(0.04f, 0.06f, 0.10f, 0.72f);
        private static readonly Color COL_ACCENT     = new Color(0f, 0.898f, 1f, 1f);       // #00E5FF
        private static readonly Color COL_BTN_NORMAL = new Color(0f, 0.898f, 1f, 0.12f);
        private static readonly Color COL_BTN_HOVER  = new Color(0f, 0.898f, 1f, 0.25f);
        private static readonly Color COL_BTN_PRESS  = new Color(0f, 0.898f, 1f, 0.35f);
        private static readonly Color COL_LABEL      = new Color(0.88f, 0.90f, 0.93f, 1f);

        private const string LANDING_SCENE = "LandingPage";

        private void Start()
        {
            BuildUI();
        }

        private void BuildUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("LoadNewImageCanvas");
            canvasGo.transform.SetParent(transform);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 98; // above hints (90), below export (100)

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Button panel (top-left, below AccuracyMetricsHud) ---
            var panelGo = new GameObject("LoadNewPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);

            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            // Position below the AccuracyMetricsHud (which is at y=-16, height ~168)
            panelRect.anchoredPosition = new Vector2(16f, -200f);
            panelRect.sizeDelta = new Vector2(200f, 36f);

            var bgImage = panelGo.AddComponent<Image>();
            bgImage.color = COL_BTN_NORMAL;

            var btn = panelGo.AddComponent<Button>();
            btn.targetGraphic = bgImage;
            btn.onClick.AddListener(OnLoadNewClicked);

            var colors = btn.colors;
            colors.normalColor = COL_BTN_NORMAL;
            colors.highlightedColor = COL_BTN_HOVER;
            colors.pressedColor = COL_BTN_PRESS;
            btn.colors = colors;

            // Outline
            var outline = panelGo.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0.898f, 1f, 0.15f);
            outline.effectDistance = new Vector2(1f, 1f);

            // Label
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(panelGo.transform, false);

            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(10f, 2f);
            labelRect.offsetMax = new Vector2(-10f, -2f);

            var label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = "← Load New Image";
            label.fontSize = 14f;
            label.color = COL_LABEL;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
        }

        private void OnLoadNewClicked()
        {
            Debug.Log("[LoadNewImageButton] Returning to landing page...");

            // 1. Clear session data
            var session = ImageSessionManager.Instance;
            if (session != null)
            {
                session.Clear();
            }

            // 2. Destroy DontDestroyOnLoad HUD objects that survived scene load
            var accuracyHud = FindFirstObjectByType<AccuracyMetricsHud>();
            if (accuracyHud != null)
            {
                Destroy(accuracyHud.gameObject);
            }

            // 3. Load the landing page scene
            SceneManager.LoadScene(LANDING_SCENE);
        }
    }
}

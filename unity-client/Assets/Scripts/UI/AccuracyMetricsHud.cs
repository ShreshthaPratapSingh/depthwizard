// =============================================================================
// AccuracyMetricsHud.cs
//
// Player-facing readout of calibration quality (FR13). Backend already logs
// RMSE/MAE/R²; this surfaces the same numbers in the flythrough scene.
// Built at runtime so it does not depend on a scene hierarchy.
//
// Namespace: DepthWizard.UI
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DepthWizard.Networking;

namespace DepthWizard.UI
{
    /// <summary>
    /// Top-right overlay showing calibration metrics when SRTM fit succeeded.
    /// </summary>
    public class AccuracyMetricsHud : MonoBehaviour
    {
        private static readonly Color COL_BG = new Color(0.04f, 0.06f, 0.10f, 0.72f);
        private static readonly Color COL_TITLE = new Color(0f, 0.898f, 1f, 1f);
        private static readonly Color COL_BODY = new Color(0.88f, 0.90f, 0.93f, 1f);
        private static readonly Color COL_WARN = new Color(1f, 0.75f, 0.35f, 1f);

        private TMP_Text _body;

        /// <summary>
        /// Create or reuse the HUD and fill it from a process response.
        /// Survives the landing → SampleScene load.
        /// </summary>
        public static void Show(ProcessResponse response)
        {
            if (response == null) return;

            var hud = FindFirstObjectByType<AccuracyMetricsHud>();
            if (hud == null)
            {
                var go = new GameObject("[AccuracyMetricsHud]");
                DontDestroyOnLoad(go);
                hud = go.AddComponent<AccuracyMetricsHud>();
                hud.BuildUI();
            }

            hud.Fill(response);
        }

        private void BuildUI()
        {
            var canvasGo = new GameObject("AccuracyMetricsCanvas");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 95;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("MetricsPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(16f, -16f);
            panelRect.sizeDelta = new Vector2(320f, 168f);

            var bg = panelGo.AddComponent<Image>();
            bg.color = COL_BG;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -10f);
            titleRect.sizeDelta = new Vector2(-24f, 28f);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "Calibration";
            title.fontSize = 18;
            title.color = COL_TITLE;
            title.alignment = TextAlignmentOptions.Left;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(panelGo.transform, false);
            var bodyRect = bodyGo.AddComponent<RectTransform>();
            bodyRect.anchorMin = new Vector2(0f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.offsetMin = new Vector2(12f, 12f);
            bodyRect.offsetMax = new Vector2(-12f, -40f);
            _body = bodyGo.AddComponent<TextMeshProUGUI>();
            _body.fontSize = 16;
            _body.color = COL_BODY;
            _body.alignment = TextAlignmentOptions.TopLeft;
        }

        private void Fill(ProcessResponse response)
        {
            if (_body == null) return;

            if (!response.is_calibrated)
            {
                _body.color = COL_WARN;
                string warn = (response.warnings != null && response.warnings.Length > 0)
                    ? response.warnings[0]
                    : "relative elevation only";
                _body.text = "Not calibrated to SRTM.\n" + warn;
                return;
            }

            _body.color = COL_BODY;
            string tile = string.IsNullOrEmpty(response.srtm_tile_id)
                ? "—"
                : response.srtm_tile_id;
            _body.text =
                $"Tile  {tile}\n" +
                $"R²    {response.r_squared:F3}\n" +
                $"RMSE  {response.rmse_m:F1} m\n" +
                $"MAE   {response.mae_m:F1} m\n" +
                $"Elev  {response.min_elev_m:F0}–{response.max_elev_m:F0} m";
        }
    }
}

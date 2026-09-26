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
        // Colors from centralized HudTheme
        private static Color COL_BG    => HudTheme.COL_BG_PANEL;
        private static Color COL_TITLE => HudTheme.COL_ACCENT;
        private static Color COL_BODY  => HudTheme.COL_TEXT;
        private static Color COL_WARN  => HudTheme.COL_WARN;

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
            panelRect.sizeDelta = new Vector2(HudTheme.PANEL_WIDTH, 148f);

            var bg = panelGo.AddComponent<Image>();
            bg.color = COL_BG;

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -10f);
            titleRect.sizeDelta = new Vector2(-24f, 22f);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text = "CALIBRATION";
            title.fontSize = HudTheme.FONT_TITLE;
            title.color = COL_TITLE;
            title.fontStyle = FontStyles.Bold;
            title.alignment = TextAlignmentOptions.Left;

            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(panelGo.transform, false);
            var bodyRect = bodyGo.AddComponent<RectTransform>();
            bodyRect.anchorMin = new Vector2(0f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.offsetMin = new Vector2(12f, 12f);
            bodyRect.offsetMax = new Vector2(-12f, -40f);
            _body = bodyGo.AddComponent<TextMeshProUGUI>();
            _body.fontSize = HudTheme.FONT_BODY;
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

            // C7: Classify terrain type from response metadata
            string terrainType = ClassifyTerrain(response);

            _body.text =
                $"Tile  {tile}\n" +
                $"Type  {terrainType}\n" +
                $"Fit R²  {response.r_squared:F3}\n" +
                $"Fit RMSE  {response.rmse_m:F1} m\n" +
                $"Elev  {response.min_elev_m:F0}–{response.max_elev_m:F0} m\n" +
                "<size=10><color=#FFFFFF66>(calibration fit stats)</color></size>";
        }

        /// <summary>
        /// Classify terrain type from the elevation range and warnings.
        /// </summary>
        private static string ClassifyTerrain(ProcessResponse r)
        {
            float range = r.max_elev_m - r.min_elev_m;

            // Check warnings for terrain hints
            if (r.warnings != null)
            {
                foreach (var w in r.warnings)
                {
                    string wl = w.ToLowerInvariant();
                    if (wl.Contains("coastal") || wl.Contains("water"))
                        return "Coastal";
                    if (wl.Contains("forest") || wl.Contains("vegetation"))
                        return "Forested";
                }
            }

            // Heuristic from elevation range
            if (range > 500f) return "Mountainous";
            if (range > 100f) return "Hilly";
            if (range > 30f)  return "Urban/Mixed";
            if (range > 5f)   return "Flat Urban";
            return "Low Relief";
        }
    }
}

// =============================================================================
// TerrainProbe.cs
//
// Shows a floating HUD element at the bottom-center of the screen displaying
// the elevation (meters) and terrain type at the point the camera is looking at.
// Raycasts from the camera center to the terrain surface each frame.
//
// Also includes a static elevation legend panel showing the colour ramp
// and min/max elevation values (PRD v2.0 FR23/B9/B10).
//
// Attach to: Main Camera in SampleScene.
//
// Namespace: DepthWizard.UI
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DepthWizard.Terrain;

namespace DepthWizard.UI
{
    /// <summary>
    /// Real-time terrain probe and elevation legend UI.
    /// </summary>
    public class TerrainProbe : MonoBehaviour
    {
        // --- Theme (matches FlythroughControlsPanel) ---
        private static readonly Color COL_BG      = new Color(0.04f, 0.06f, 0.10f, 0.72f);
        private static readonly Color COL_ACCENT   = new Color(0f, 0.898f, 1f, 1f);
        private static readonly Color COL_LABEL    = new Color(0.88f, 0.90f, 0.93f, 1f);
        private static readonly Color COL_DIM      = new Color(1f, 1f, 1f, 0.35f);

        // Legend ramp colours (matches terrain material gradient)
        private static readonly Color RAMP_LO = new Color(0.05f, 0.15f, 0.50f);
        private static readonly Color RAMP_HI = new Color(1.00f, 0.95f, 0.75f);

        private TMP_Text _probeText;
        private TMP_Text _legendMinText;
        private TMP_Text _legendMaxText;
        private TMP_Text _legendTitle;
        private RawImage _rampImage;

        private TerrainElevationController _elevation;
        private UnityEngine.Camera _cam;

        private float _minElev;
        private float _maxElev;
        private bool _hasElevRange;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Start()
        {
            _cam = GetComponent<UnityEngine.Camera>() ?? UnityEngine.Camera.main;
            BuildUI();
            StartCoroutine(WatchForElevation());
        }

        private void Update()
        {
            UpdateProbe();
        }

        // -----------------------------------------------------------------
        // UI construction
        // -----------------------------------------------------------------

        private void BuildUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("TerrainProbeCanvas");
            canvasGo.transform.SetParent(transform);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 91;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGo.AddComponent<GraphicRaycaster>();

            // =============== Probe readout (bottom-center) ===============

            var probeGo = new GameObject("ProbePanel");
            probeGo.transform.SetParent(canvasGo.transform, false);

            var probeRect = probeGo.AddComponent<RectTransform>();
            probeRect.anchorMin = new Vector2(0.5f, 0f);
            probeRect.anchorMax = new Vector2(0.5f, 0f);
            probeRect.pivot = new Vector2(0.5f, 0f);
            probeRect.anchoredPosition = new Vector2(0f, 16f);
            probeRect.sizeDelta = new Vector2(300f, 36f);

            var probeBg = probeGo.AddComponent<Image>();
            probeBg.color = COL_BG;

            var probeOutline = probeGo.AddComponent<Outline>();
            probeOutline.effectColor = new Color(1f, 1f, 1f, 0.04f);
            probeOutline.effectDistance = new Vector2(1f, 1f);

            var probeTxtGo = new GameObject("ProbeText");
            probeTxtGo.transform.SetParent(probeGo.transform, false);
            var probeTxtRect = probeTxtGo.AddComponent<RectTransform>();
            probeTxtRect.anchorMin = Vector2.zero;
            probeTxtRect.anchorMax = Vector2.one;
            probeTxtRect.offsetMin = new Vector2(12f, 4f);
            probeTxtRect.offsetMax = new Vector2(-12f, -4f);

            _probeText = probeTxtGo.AddComponent<TextMeshProUGUI>();
            _probeText.text = "Elevation: --";
            _probeText.fontSize = 13f;
            _probeText.color = COL_LABEL;
            _probeText.alignment = TextAlignmentOptions.Center;

            // =============== Legend (left edge, vertical) ===============

            var legendGo = new GameObject("LegendPanel");
            legendGo.transform.SetParent(canvasGo.transform, false);

            var legendRect = legendGo.AddComponent<RectTransform>();
            legendRect.anchorMin = new Vector2(0f, 0.3f);
            legendRect.anchorMax = new Vector2(0f, 0.7f);
            legendRect.pivot = new Vector2(0f, 0.5f);
            legendRect.anchoredPosition = new Vector2(16f, 0f);
            legendRect.sizeDelta = new Vector2(56f, 0f); // height from anchors

            var legendBg = legendGo.AddComponent<Image>();
            legendBg.color = COL_BG;

            var legendOutline = legendGo.AddComponent<Outline>();
            legendOutline.effectColor = new Color(1f, 1f, 1f, 0.04f);
            legendOutline.effectDistance = new Vector2(1f, 1f);

            // Title "m"
            var titleGo = new GameObject("LegendTitle");
            titleGo.transform.SetParent(legendGo.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -4f);
            titleRect.sizeDelta = new Vector2(0f, 16f);

            _legendTitle = titleGo.AddComponent<TextMeshProUGUI>();
            _legendTitle.text = "m";
            _legendTitle.fontSize = 11f;
            _legendTitle.color = COL_DIM;
            _legendTitle.alignment = TextAlignmentOptions.Center;

            // Max label (top)
            var maxGo = new GameObject("MaxLabel");
            maxGo.transform.SetParent(legendGo.transform, false);
            var maxRect = maxGo.AddComponent<RectTransform>();
            maxRect.anchorMin = new Vector2(0f, 1f);
            maxRect.anchorMax = new Vector2(1f, 1f);
            maxRect.pivot = new Vector2(0.5f, 1f);
            maxRect.anchoredPosition = new Vector2(0f, -22f);
            maxRect.sizeDelta = new Vector2(0f, 14f);

            _legendMaxText = maxGo.AddComponent<TextMeshProUGUI>();
            _legendMaxText.text = "--";
            _legendMaxText.fontSize = 10f;
            _legendMaxText.color = COL_LABEL;
            _legendMaxText.alignment = TextAlignmentOptions.Center;

            // Color ramp (gradient image)
            var rampGo = new GameObject("ColorRamp");
            rampGo.transform.SetParent(legendGo.transform, false);
            var rampRect = rampGo.AddComponent<RectTransform>();
            rampRect.anchorMin = new Vector2(0.15f, 0.15f);
            rampRect.anchorMax = new Vector2(0.85f, 0.85f);
            rampRect.offsetMin = Vector2.zero;
            rampRect.offsetMax = Vector2.zero;

            _rampImage = rampGo.AddComponent<RawImage>();
            _rampImage.texture = CreateRampTexture(64);

            // Min label (bottom)
            var minGo = new GameObject("MinLabel");
            minGo.transform.SetParent(legendGo.transform, false);
            var minRect = minGo.AddComponent<RectTransform>();
            minRect.anchorMin = new Vector2(0f, 0f);
            minRect.anchorMax = new Vector2(1f, 0f);
            minRect.pivot = new Vector2(0.5f, 0f);
            minRect.anchoredPosition = new Vector2(0f, 4f);
            minRect.sizeDelta = new Vector2(0f, 14f);

            _legendMinText = minGo.AddComponent<TextMeshProUGUI>();
            _legendMinText.text = "--";
            _legendMinText.fontSize = 10f;
            _legendMinText.color = COL_LABEL;
            _legendMinText.alignment = TextAlignmentOptions.Center;
        }

        // -----------------------------------------------------------------
        // Ramp texture
        // -----------------------------------------------------------------

        private Texture2D CreateRampTexture(int height)
        {
            var tex = new Texture2D(1, height, TextureFormat.RGB24, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var pixels = new Color[height];
            for (int y = 0; y < height; y++)
            {
                float t = (float)y / (height - 1);
                pixels[y] = Color.Lerp(RAMP_LO, RAMP_HI, t);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        // -----------------------------------------------------------------
        // Probe raycasting
        // -----------------------------------------------------------------

        private void UpdateProbe()
        {
            if (_cam == null) return;

            // Raycast from screen center
            var ray = _cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                float worldY = hit.point.y;

                if (_elevation != null && _elevation.IsAbsolute && _hasElevRange)
                {
                    // Convert terrain-space Y to real elevation
                    var terrain = UnityEngine.Terrain.activeTerrain;
                    if (terrain != null)
                    {
                        float terrainH = terrain.terrainData.size.y;
                        float t = worldY / terrainH;
                        float realElev = Mathf.Lerp(_minElev, _maxElev, t);
                        _probeText.text = $"Elevation: {realElev:F1} m  (Absolute)";
                    }
                    else
                    {
                        _probeText.text = $"Y: {worldY:F1}  (no terrain)";
                    }
                }
                else
                {
                    _probeText.text = $"Height: {worldY:F1}  (Relative)";
                }
            }
            else
            {
                _probeText.text = "Elevation: --";
            }
        }

        // -----------------------------------------------------------------
        // Watch for elevation controller
        // -----------------------------------------------------------------

        private System.Collections.IEnumerator WatchForElevation()
        {
            while (_elevation == null)
            {
                _elevation = FindFirstObjectByType<TerrainElevationController>();
                if (_elevation != null)
                {
                    UpdateLegendRange();
                }
                yield return new WaitForSeconds(1f);
            }

            // Keep syncing range in case mode switches
            while (true)
            {
                UpdateLegendRange();
                yield return new WaitForSeconds(2f);
            }
        }

        private void UpdateLegendRange()
        {
            if (_elevation == null) return;

            _minElev = _elevation.MinElevM;
            _maxElev = _elevation.MaxElevM;

            if (_maxElev > _minElev && _elevation.IsAbsolute)
            {
                _hasElevRange = true;
                _legendMinText.text = $"{_minElev:F0}";
                _legendMaxText.text = $"{_maxElev:F0}";
                _legendTitle.text = "m ASL";
            }
            else
            {
                _hasElevRange = false;
                _legendMinText.text = "0";
                _legendMaxText.text = "1";
                _legendTitle.text = "rel";
            }
        }
    }
}

// =============================================================================
// CoordinateOverlay.cs
//
// Live GPS-style coordinate readout that maps the camera's world position
// to geographic lat/lon using the bounding box from the backend response.
// Updates every frame as the user flies around the terrain.
//
// Hidden entirely for non-georeferenced uploads (no bbox data).
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
    /// Live coordinate overlay that updates as the camera moves over the terrain.
    /// Maps camera world XZ position to geographic lat/lon via the response bbox.
    /// </summary>
    public class CoordinateOverlay : MonoBehaviour
    {
        private static readonly Color COL_BG    = new Color(0.04f, 0.06f, 0.10f, 0.72f);
        private static readonly Color COL_TITLE = new Color(0f, 0.898f, 1f, 1f);
        private static readonly Color COL_BODY  = new Color(0.88f, 0.90f, 0.93f, 1f);

        private TMP_Text _body;
        private GameObject _root;

        // Cached mapping parameters
        private bool _hasMapping;
        private float _bboxWest;
        private float _bboxSouth;
        private float _bboxEast;
        private float _bboxNorth;
        private float _terrainX;      // terrain world origin X
        private float _terrainZ;      // terrain world origin Z
        private float _terrainWidth;  // terrain XZ extent
        private float _terrainLength;
        private string _crs;

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Initialize the overlay from a process response. If the image was
        /// not georeferenced or has no bbox, the overlay is hidden entirely.
        /// Call once after terrain generation — the overlay then self-updates
        /// every frame via Update().
        /// </summary>
        public static void Show(ProcessResponse response)
        {
            if (response == null) return;

            // Non-georeferenced or no bbox → hide
            if (!response.is_georeferenced ||
                response.bbox == null || response.bbox.Length < 4)
            {
                var existing = FindFirstObjectByType<CoordinateOverlay>();
                if (existing != null && existing._root != null)
                    existing._root.SetActive(false);
                return;
            }

            var overlay = FindFirstObjectByType<CoordinateOverlay>();
            if (overlay == null)
            {
                var go = new GameObject("[CoordinateOverlay]");
                DontDestroyOnLoad(go);
                overlay = go.AddComponent<CoordinateOverlay>();
                overlay.BuildUI();
            }

            overlay.SetupMapping(response);
            overlay._root.SetActive(true);
        }

        // ---------------------------------------------------------------------
        // Frame update — live coordinate tracking
        // ---------------------------------------------------------------------

        private void Update()
        {
            if (!_hasMapping || _body == null) return;

            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null) return;

            Vector3 pos = cam.transform.position;

            // Map world XZ → normalized [0,1] on terrain footprint
            float nx = Mathf.Clamp01((pos.x - _terrainX) / _terrainWidth);
            float nz = Mathf.Clamp01((pos.z - _terrainZ) / _terrainLength);

            // Interpolate to geographic coordinates
            float lon = Mathf.Lerp(_bboxWest, _bboxEast, nx);
            float lat = Mathf.Lerp(_bboxSouth, _bboxNorth, nz);

            // Format with hemisphere indicators
            string latDir = lat >= 0 ? "N" : "S";
            string lonDir = lon >= 0 ? "E" : "W";

            string altitude = $"Alt  {pos.y:F0} m";

            _body.text =
                $"{Mathf.Abs(lat):F4}\u00b0{latDir},  {Mathf.Abs(lon):F4}\u00b0{lonDir}\n" +
                $"{altitude}\n" +
                $"CRS  {_crs}";
        }

        // ---------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------

        private void SetupMapping(ProcessResponse response)
        {
            _bboxWest  = response.bbox[0];
            _bboxSouth = response.bbox[1];
            _bboxEast  = response.bbox[2];
            _bboxNorth = response.bbox[3];
            _crs = string.IsNullOrEmpty(response.crs) ? "WGS84" : response.crs;

            // Find the terrain to get its world position and size
            var terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain != null)
            {
                _terrainX      = terrain.transform.position.x;
                _terrainZ      = terrain.transform.position.z;
                _terrainWidth  = terrain.terrainData.size.x;
                _terrainLength = terrain.terrainData.size.z;
            }
            else
            {
                // Fallback: assume terrain at origin, 500m square
                _terrainX      = 0f;
                _terrainZ      = 0f;
                _terrainWidth  = 500f;
                _terrainLength = 500f;
            }

            _hasMapping = true;

            Debug.Log(
                $"[CoordinateOverlay] Mapping active: " +
                $"bbox=[{_bboxWest:F4}, {_bboxSouth:F4}, {_bboxEast:F4}, {_bboxNorth:F4}], " +
                $"terrain at ({_terrainX}, {_terrainZ}) size ({_terrainWidth}, {_terrainLength}), " +
                $"CRS={_crs}");
        }

        // ---------------------------------------------------------------------
        // UI construction
        // ---------------------------------------------------------------------

        private void BuildUI()
        {
            var canvasGo = new GameObject("CoordinateCanvas");
            canvasGo.transform.SetParent(transform, false);
            _root = canvasGo;

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 94;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            canvasGo.AddComponent<GraphicRaycaster>();

            // Panel — below AccuracyMetricsHud (168px at y=-16)
            var panelGo = new GameObject("CoordPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);
            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot     = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(16f, -192f);
            panelRect.sizeDelta = new Vector2(320f, 105f);

            var bg = panelGo.AddComponent<Image>();
            bg.color = COL_BG;

            // Title
            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(panelGo.transform, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot     = new Vector2(0.5f, 1f);
            titleRect.anchoredPosition = new Vector2(0f, -8f);
            titleRect.sizeDelta = new Vector2(-24f, 22f);
            var title = titleGo.AddComponent<TextMeshProUGUI>();
            title.text      = "Location";
            title.fontSize  = 16;
            title.color     = COL_TITLE;
            title.alignment = TextAlignmentOptions.Left;

            // Body (live-updating coordinates)
            var bodyGo = new GameObject("Body");
            bodyGo.transform.SetParent(panelGo.transform, false);
            var bodyRect = bodyGo.AddComponent<RectTransform>();
            bodyRect.anchorMin = new Vector2(0f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 1f);
            bodyRect.offsetMin = new Vector2(12f, 8f);
            bodyRect.offsetMax = new Vector2(-12f, -32f);
            _body = bodyGo.AddComponent<TextMeshProUGUI>();
            _body.fontSize  = 15;
            _body.color     = COL_BODY;
            _body.alignment = TextAlignmentOptions.TopLeft;
            _body.text      = "Acquiring...";
        }
    }
}

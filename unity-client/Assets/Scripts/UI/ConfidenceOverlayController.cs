// =============================================================================
// ConfidenceOverlayController.cs
//
// Toggleable confidence mask overlay on the terrain. Decodes the confidence PNG
// from the backend response and renders it as a semi-transparent projector or
// secondary terrain texture layer.
//
// Color mapping:
//   - High confidence (255): transparent (no overlay)
//   - Water (128): blue tint
//   - Vegetation (100): green tint
//   - Other low values: red tint (unexpected low-confidence)
//
// The overlay is toggled via a button in FlythroughControlsPanel.
//
// Namespace: DepthWizard.UI
// =============================================================================

using System;
using UnityEngine;

namespace DepthWizard.UI
{
    /// <summary>
    /// Manages a confidence mask overlay on the terrain. Receives the confidence
    /// PNG bytes, creates a colored overlay texture, and renders it via a Projector.
    /// </summary>
    public class ConfidenceOverlayController : MonoBehaviour
    {
        private Projector _projector;
        private Texture2D _overlayTex;
        private bool _isVisible;
        private bool _hasData;

        // Confidence values from config.py
        private const byte FULL_CONFIDENCE = 255;
        private const byte WATER_CONFIDENCE = 128;
        private const byte VEG_CONFIDENCE = 100;

        // Overlay colors (semi-transparent)
        private static readonly Color COL_WATER = new Color(0.2f, 0.4f, 1.0f, 0.45f);
        private static readonly Color COL_VEG   = new Color(0.2f, 0.8f, 0.3f, 0.45f);
        private static readonly Color COL_LOW   = new Color(1.0f, 0.3f, 0.2f, 0.45f);
        private static readonly Color COL_CLEAR = new Color(0f, 0f, 0f, 0f);

        /// <summary>True when confidence data has been loaded.</summary>
        public bool HasData => _hasData;

        /// <summary>True when the overlay is currently visible.</summary>
        public bool IsVisible => _isVisible;

        /// <summary>
        /// Load confidence mask from raw PNG bytes (base64-decoded by caller).
        /// Creates the colored overlay texture.
        /// </summary>
        public void LoadConfidenceData(byte[] pngBytes)
        {
            if (pngBytes == null || pngBytes.Length == 0)
            {
                _hasData = false;
                return;
            }

            // Decode the PNG
            var srcTex = new Texture2D(2, 2, TextureFormat.R8, false);
            if (!srcTex.LoadImage(pngBytes))
            {
                Debug.LogWarning("[ConfidenceOverlay] Failed to decode confidence PNG.");
                Destroy(srcTex);
                _hasData = false;
                return;
            }

            int w = srcTex.width;
            int h = srcTex.height;
            var srcPixels = srcTex.GetPixels32();
            Destroy(srcTex);

            // Create colored overlay texture
            _overlayTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            _overlayTex.filterMode = FilterMode.Bilinear;
            _overlayTex.wrapMode = TextureWrapMode.Clamp;

            var colors = new Color32[srcPixels.Length];
            for (int i = 0; i < srcPixels.Length; i++)
            {
                byte conf = srcPixels[i].r; // Grayscale confidence mask

                Color c;
                if (conf >= FULL_CONFIDENCE)
                    c = COL_CLEAR;
                else if (conf >= WATER_CONFIDENCE - 5 && conf <= WATER_CONFIDENCE + 5)
                    c = COL_WATER;
                else if (conf >= VEG_CONFIDENCE - 5 && conf <= VEG_CONFIDENCE + 5)
                    c = COL_VEG;
                else
                    c = COL_LOW;

                colors[i] = c;
            }

            _overlayTex.SetPixels32(colors);
            _overlayTex.Apply();

            _hasData = true;
            Debug.Log($"[ConfidenceOverlay] Loaded {w}x{h} confidence overlay.");
        }

        /// <summary>Toggle overlay visibility.</summary>
        public void SetVisible(bool visible)
        {
            _isVisible = visible && _hasData;

            if (_projector != null)
            {
                _projector.enabled = _isVisible;
            }
            else if (_isVisible)
            {
                CreateProjector();
            }
        }

        /// <summary>Toggle on/off.</summary>
        public void Toggle()
        {
            SetVisible(!_isVisible);
        }

        private void CreateProjector()
        {
            if (_overlayTex == null) return;

            // Find terrain to position the projector
            var terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain == null)
            {
                Debug.LogWarning("[ConfidenceOverlay] No active terrain found.");
                return;
            }

            var terrainData = terrain.terrainData;
            var terrainPos = terrain.transform.position;
            var terrainSize = terrainData.size;

            // Create projector above terrain center, pointing down
            var projGo = new GameObject("ConfidenceProjector");
            projGo.transform.SetParent(transform);
            projGo.transform.position = new Vector3(
                terrainPos.x + terrainSize.x / 2f,
                terrainPos.y + terrainSize.y + 50f,
                terrainPos.z + terrainSize.z / 2f
            );
            projGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            _projector = projGo.AddComponent<Projector>();
            _projector.orthographic = true;
            _projector.orthographicSize = Mathf.Max(terrainSize.x, terrainSize.z) / 2f;
            _projector.nearClipPlane = 1f;
            _projector.farClipPlane = terrainSize.y + 100f;
            _projector.ignoreLayers = ~(1 << terrain.gameObject.layer);

            // Create a simple unlit transparent material for the projector
            var mat = new Material(Shader.Find("Projector/Light"));
            if (mat.shader == null || mat.shader.name == "Hidden/InternalErrorShader")
            {
                // Fallback: use a standard unlit shader
                mat = new Material(Shader.Find("Unlit/Transparent"));
            }
            mat.mainTexture = _overlayTex;
            _projector.material = mat;

            _projector.enabled = _isVisible;
        }

        private void OnDestroy()
        {
            if (_overlayTex != null) Destroy(_overlayTex);
        }
    }
}

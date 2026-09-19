// =============================================================================
// TerrainElevationController.cs
//
// Attached to the DepthWizard_Terrain GameObject by RuntimeTerrainBuilder
// after terrain generation. Stores both the relative visualization scale and
// the calibrated metric scale so the user can toggle between them at runtime.
//
// Press M to toggle between relative and absolute (metric) elevation modes.
// When metric calibration data is unavailable, the toggle is a no-op.
// =============================================================================

using UnityEngine;

namespace DepthWizard.Terrain
{
    /// <summary>
    /// Runtime controller that switches terrain between relative and metric
    /// elevation display. Rescales TerrainData.size (XZ and Y) and repositions
    /// the terrain vertically when switching modes.
    /// </summary>
    public class TerrainElevationController : MonoBehaviour
    {
        // Absolute (geo) XZ dimensions — real-world meters from bbox
        private float absoluteWidth;
        private float absoluteLength;

        // Relative XZ dimensions — pixel-proportional (1 m/px)
        private float relativeWidth;
        private float relativeLength;

        private float relativeScale;
        private float calibratedScale;
        private float calibratedBaseAltitude;
        private bool hasCalibration;
        private bool useAbsolute;
        private float minElevM;
        private float maxElevM;

        /// <summary>True when the terrain is showing metric (absolute) elevation.</summary>
        public bool IsAbsolute => useAbsolute;

        /// <summary>True when calibrated elevation data is available.</summary>
        public bool HasCalibration => hasCalibration;

        /// <summary>Min calibrated elevation in meters (0 if uncalibrated).</summary>
        public float MinElevM => minElevM;

        /// <summary>Max calibrated elevation in meters (1 if uncalibrated).</summary>
        public float MaxElevM => maxElevM;

        /// <summary>
        /// Called by RuntimeTerrainBuilder.Build() after terrain creation.
        /// </summary>
        public void Init(
            float absWidth, float absLength,
            float relWidth, float relLength,
            float relScale, float calScale, float calBase,
            bool calibrated)
        {
            absoluteWidth  = absWidth;
            absoluteLength = absLength;
            relativeWidth  = relWidth;
            relativeLength = relLength;
            relativeScale = Mathf.Max(relScale, 1f);
            calibratedScale = Mathf.Max(calScale, 1f);
            calibratedBaseAltitude = calBase;
            hasCalibration = calibrated;

            if (calibrated)
            {
                minElevM = calBase;
                maxElevM = calBase + calScale;
            }
            else
            {
                minElevM = 0f;
                maxElevM = 1f;
            }

            // Start in calibrated mode when available.
            useAbsolute = calibrated;
        }

        /// <summary>
        /// Switch between absolute (metric) and relative elevation display.
        /// No-op if calibration data is not available and useAbsolute is true.
        /// </summary>
        public void SetElevationMode(bool absolute)
        {
            if (absolute && !hasCalibration)
            {
                Debug.LogWarning(
                    "[TerrainElevationController] No calibration data available. " +
                    "Staying in relative mode.");
                return;
            }

            useAbsolute = absolute;
            ApplyMode();
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.M))
            {
                SetElevationMode(!useAbsolute);
            }
        }

        private void ApplyMode()
        {
            var terrain = GetComponent<UnityEngine.Terrain>();
            if (terrain == null || terrain.terrainData == null)
                return;

            float scale;
            float yPos;
            float width;
            float length;

            if (useAbsolute)
            {
                scale  = calibratedScale;
                yPos   = calibratedBaseAltitude;
                width  = absoluteWidth;
                length = absoluteLength;
            }
            else
            {
                scale  = relativeScale;
                yPos   = 0f;
                width  = relativeWidth;
                length = relativeLength;
            }

            terrain.terrainData.size = new Vector3(width, scale, length);
            transform.position = new Vector3(transform.position.x, yPos, transform.position.z);

            // Update texture tiling to match new terrain size
            if (terrain.terrainData.terrainLayers != null &&
                terrain.terrainData.terrainLayers.Length > 0)
            {
                var layer = terrain.terrainData.terrainLayers[0];
                layer.tileSize = new Vector2(width, length);
            }

            // Force collider rebuild after resizing
            var collider = GetComponent<TerrainCollider>();
            if (collider != null)
            {
                collider.enabled = false;
                collider.enabled = true;
            }

            Debug.Log(
                $"[TerrainElevationController] Mode: {(useAbsolute ? "absolute" : "relative")}, " +
                $"size={width:F1}x{scale:F1}x{length:F1}m, baseY={yPos:F1}m");
        }
    }
}

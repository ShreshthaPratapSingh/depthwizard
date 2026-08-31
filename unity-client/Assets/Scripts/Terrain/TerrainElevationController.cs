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
    /// elevation display. Rescales TerrainData.size.y and repositions the
    /// terrain vertically when switching modes.
    /// </summary>
    public class TerrainElevationController : MonoBehaviour
    {
        private float terrainWidth;
        private float terrainLength;
        private float relativeScale;
        private float calibratedScale;
        private float calibratedBaseAltitude;
        private bool hasCalibration;
        private bool useAbsolute;

        /// <summary>True when the terrain is showing metric (absolute) elevation.</summary>
        public bool IsAbsolute => useAbsolute;

        /// <summary>True when calibrated elevation data is available.</summary>
        public bool HasCalibration => hasCalibration;

        /// <summary>
        /// Called by RuntimeTerrainBuilder.Build() after terrain creation.
        /// </summary>
        public void Init(
            float width, float length,
            float relScale, float calScale, float calBase,
            bool calibrated)
        {
            terrainWidth = width;
            terrainLength = length;
            relativeScale = Mathf.Max(relScale, 1f);
            calibratedScale = Mathf.Max(calScale, 1f);
            calibratedBaseAltitude = calBase;
            hasCalibration = calibrated;

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
            if (useAbsolute)
            {
                scale = calibratedScale;
                yPos = calibratedBaseAltitude;
            }
            else
            {
                scale = relativeScale;
                yPos = 0f;
            }

            terrain.terrainData.size = new Vector3(
                terrainWidth, scale, terrainLength);
            transform.position = new Vector3(0f, yPos, 0f);

            // Force collider rebuild after resizing
            var collider = GetComponent<TerrainCollider>();
            if (collider != null)
            {
                collider.enabled = false;
                collider.enabled = true;
            }

            Debug.Log(
                $"[TerrainElevationController] Mode: {(useAbsolute ? "absolute" : "relative")}, " +
                $"heightScale={scale:F1}m, baseY={yPos:F1}m");
        }
    }
}

// =============================================================================
// CameraSpawnPositioner.cs
//
// Automatically positions the drone/camera above the center of the generated
// terrain once it appears, so the user immediately sees the terrain instead of
// staring at empty sky from a fixed spawn point.
//
// Uses a lightweight poll to detect when Terrain.activeTerrain becomes
// available, then sets the camera transform once and stops polling.
//
// Does NOT modify DroneController's movement/look/clamping logic. It only sets
// transform.position and syncs DroneController's internal _pitch/_yaw fields
// via reflection so the rotation sticks.
//
// Attach to: the Main Camera GameObject in SampleScene (same as DroneController).
//
// Namespace: DepthWizard.Camera
// =============================================================================

using System.Collections;
using System.Reflection;
using UnityEngine;

namespace DepthWizard.Camera
{
    /// <summary>
    /// One-shot repositioner: waits for terrain generation, then places the
    /// camera at a sensible overview position above the terrain center.
    /// </summary>
    public class CameraSpawnPositioner : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Configuration
        // ---------------------------------------------------------------------

        [Header("Spawn Settings")]

        [Tooltip("Height multiplier relative to terrain vertical range (size.y). " +
                 "E.g. 0.3 means spawn height = center terrain height + terrainHeightRange * 0.3.")]
        [SerializeField] private float heightOffsetFactor = 0.3f;

        [Tooltip("Initial downward pitch angle (degrees). " +
                 "30-40 gives a good overview of the terrain.")]
        [SerializeField, Range(10f, 60f)] private float initialPitch = 35f;

        [Tooltip("How often to check for terrain availability (seconds).")]
        [SerializeField] private float pollInterval = 0.25f;

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private bool _positioned;

        // Cached reflection fields for DroneController._pitch and _yaw
        private static readonly FieldInfo _pitchField =
            typeof(DroneController).GetField("_pitch", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _yawField =
            typeof(DroneController).GetField("_yaw", BindingFlags.NonPublic | BindingFlags.Instance);

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Start()
        {
            StartCoroutine(WaitForTerrainAndPosition());
        }

        // ---------------------------------------------------------------------
        // Core logic
        // ---------------------------------------------------------------------

        private IEnumerator WaitForTerrainAndPosition()
        {
            var wait = new WaitForSeconds(pollInterval);

            // Wait for TestTrigger to finish destroying old terrain and
            // rebuilding from cache before we start polling.
            yield return new WaitForSeconds(1.0f);

            // Poll until terrain is created by TestTrigger/RuntimeTerrainBuilder
            while (!_positioned)
            {
                UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
                if (terrain != null && terrain.terrainData != null)
                {
                    PositionAboveTerrain(terrain);
                    _positioned = true;
                    yield break;
                }
                yield return wait;
            }
        }

        private void PositionAboveTerrain(UnityEngine.Terrain terrain)
        {
            TerrainData td = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = td.size; // (width, height, length)

            // --- Compute center of terrain (X, Z) ---
            float centerX = terrainPos.x + terrainSize.x * 0.5f;
            float centerZ = terrainPos.z + terrainSize.z * 0.5f;

            // --- Compute spawn height ---
            // Sample terrain height at the actual center point rather than
            // using the global maximum, so we spawn relative to where we're
            // looking, not the highest peak that could be far away.
            Vector3 centerWorld = new Vector3(centerX, 0f, centerZ);
            float centerHeight = terrain.SampleHeight(centerWorld) + terrainPos.y;

            // Offset above the center by a fraction of the terrain's VERTICAL
            // range (size.y), not XZ width. size.y is the heightScale passed
            // to TerrainData — typically 200m relative or the calibrated
            // elevation range. This keeps the spawn proportional to the actual
            // terrain relief regardless of how large the XZ footprint is.
            float spawnY = centerHeight + terrainSize.y * heightOffsetFactor;

            // Pull back slightly from center so forward camera direction shows terrain
            float pullBack = terrainSize.z * 0.15f;
            float spawnZ = centerZ - pullBack;

            Vector3 spawnPosition = new Vector3(centerX, spawnY, spawnZ);

            // --- Set camera transform ---
            transform.position = spawnPosition;
            transform.rotation = Quaternion.Euler(initialPitch, 0f, 0f);

            // --- Sync DroneController's internal _pitch/_yaw ---
            // DroneController sets rotation = Quaternion.Euler(_pitch, _yaw, 0)
            // every frame in HandleLook(). If we don't update its private fields,
            // it will snap back to the old rotation on the next frame.
            var drone = GetComponent<DroneController>();
            if (drone != null)
            {
                if (_pitchField != null && _yawField != null)
                {
                    _pitchField.SetValue(drone, initialPitch);
                    _yawField.SetValue(drone, 0f);
                    Debug.Log("[CameraSpawnPositioner] Synced DroneController pitch/yaw via reflection.");
                }
                else
                {
                    Debug.LogWarning(
                        "[CameraSpawnPositioner] Could not find DroneController._pitch/_yaw fields. " +
                        "Camera rotation may snap on next frame.");
                }
            }

            Debug.Log(
                $"[CameraSpawnPositioner] Positioned camera at ({spawnPosition.x:F0}, " +
                $"{spawnPosition.y:F0}, {spawnPosition.z:F0}), " +
                $"pitch {initialPitch}° over terrain center " +
                $"({centerX:F0}, {centerZ:F0}), terrain size {terrainSize}, " +
                $"center terrain height {centerHeight:F1}");
        }

        /// <summary>
        /// Find the actual maximum world-space elevation of the terrain by
        /// sampling the heightmap at a coarse grid.
        /// </summary>
        private static float GetMaxTerrainHeight(TerrainData td, Vector3 terrainPos)
        {
            int res = td.heightmapResolution;
            float[,] heights = td.GetHeights(0, 0, res, res);

            float maxNorm = 0f;
            // Sample every Nth point for speed (exact max not critical for spawn)
            int step = Mathf.Max(1, res / 128);
            for (int y = 0; y < res; y += step)
            {
                for (int x = 0; x < res; x += step)
                {
                    if (heights[y, x] > maxNorm)
                        maxNorm = heights[y, x];
                }
            }

            // World height = terrainPos.y + normalizedHeight * terrainData.size.y
            return terrainPos.y + maxNorm * td.size.y;
        }
    }
}

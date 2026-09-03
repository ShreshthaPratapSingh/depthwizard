// =============================================================================
// CinematicFlythrough.cs
//
// Scripted camera flythrough (PRD "should-have"): generates a smooth,
// automated camera path around the terrain for demo/judge presentation.
//
// The path is a Catmull-Rom spline through auto-generated waypoints that
// circle the terrain at varying heights and look-angles. Press C to start,
// press C again (or Escape) to cancel and return to manual controls.
//
// While active, DroneController input is suppressed (the drone GO is not
// disabled; only this script drives transform each frame). On exit, the
// camera stays at its current position and DroneController resumes.
//
// Attach to: the Main Camera GameObject in SampleScene (same as
// DroneController and CameraSpawnPositioner).
//
// Namespace: DepthWizard.Camera
// =============================================================================

using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DepthWizard.Camera
{
    /// <summary>
    /// Automated cinematic flythrough around the generated terrain.
    /// </summary>
    public class CinematicFlythrough : MonoBehaviour
    {
        // -----------------------------------------------------------------
        // Settings
        // -----------------------------------------------------------------

        [Header("Flythrough")]

        [Tooltip("Total duration of the full flythrough loop (seconds).")]
        [SerializeField] private float duration = 30f;

        [Tooltip("Number of waypoints generated around the terrain.")]
        [SerializeField] private int waypointCount = 8;

        [Tooltip("Height offset above terrain center (fraction of terrain height range).")]
        [SerializeField] private float heightFactor = 0.5f;

        [Tooltip("Orbit radius as a fraction of terrain diagonal.")]
        [SerializeField] private float radiusFactor = 0.6f;

        [Tooltip("Height variation between waypoints (fraction of terrain height range). " +
                 "Creates gentle altitude undulation along the path.")]
        [SerializeField] private float heightVariation = 0.15f;

        // -----------------------------------------------------------------
        // State
        // -----------------------------------------------------------------

        private bool _active;
        private Vector3[] _waypoints;
        private float _elapsed;
        private DroneController _drone;

        // Reflection fields for syncing DroneController pitch/yaw on exit
        private static readonly FieldInfo _pitchField =
            typeof(DroneController).GetField("_pitch", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo _yawField =
            typeof(DroneController).GetField("_yaw", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>True while the cinematic flythrough is running.</summary>
        public bool IsActive => _active;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            _drone = GetComponent<DroneController>();
        }

        private void Update()
        {
            // C key toggles the flythrough
            if (Keyboard.current != null && Keyboard.current.cKey.wasPressedThisFrame)
            {
                if (_active)
                    StopFlythrough();
                else
                    TryStartFlythrough();
            }

            // Escape also cancels an active flythrough
            if (_active && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                StopFlythrough();
                return;
            }

            if (!_active) return;

            _elapsed += Time.deltaTime;
            if (_elapsed >= duration)
            {
                StopFlythrough();
                return;
            }

            float t = _elapsed / duration;
            UpdateCameraAlongSpline(t);
        }

        // -----------------------------------------------------------------
        // Start / Stop
        // -----------------------------------------------------------------

        private void TryStartFlythrough()
        {
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogWarning("[CinematicFlythrough] No terrain available.");
                return;
            }

            GenerateWaypoints(terrain);

            _elapsed = 0f;
            _active = true;

            // Suppress DroneController while the flythrough is running
            if (_drone != null)
                _drone.enabled = false;

            Debug.Log($"[CinematicFlythrough] Started ({duration}s, {waypointCount} waypoints). Press C to cancel.");
        }

        private void StopFlythrough()
        {
            _active = false;

            // Re-enable DroneController and sync its internal rotation state
            // to the camera's current orientation so there is no visual snap.
            if (_drone != null)
            {
                _drone.enabled = true;
                SyncDroneRotation();
            }

            Debug.Log("[CinematicFlythrough] Stopped. Manual controls restored.");
        }

        // -----------------------------------------------------------------
        // Waypoint generation
        // -----------------------------------------------------------------

        /// <summary>
        /// Generate waypoints in an elliptical path around the terrain center
        /// at varying heights for visual interest.
        /// </summary>
        private void GenerateWaypoints(UnityEngine.Terrain terrain)
        {
            TerrainData td = terrain.terrainData;
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = td.size;

            float centerX = terrainPos.x + terrainSize.x * 0.5f;
            float centerZ = terrainPos.z + terrainSize.z * 0.5f;

            Vector3 centerWorld = new Vector3(centerX, 0f, centerZ);
            float centerHeight = terrain.SampleHeight(centerWorld) + terrainPos.y;

            float diagonal = Mathf.Sqrt(terrainSize.x * terrainSize.x + terrainSize.z * terrainSize.z);
            float radius = diagonal * radiusFactor;
            float baseHeight = centerHeight + terrainSize.y * heightFactor;

            _waypoints = new Vector3[waypointCount];

            for (int i = 0; i < waypointCount; i++)
            {
                float angle = (float)i / waypointCount * Mathf.PI * 2f;

                // Slight ellipse (wider on X than Z) for variety
                float rx = radius * 1.1f;
                float rz = radius * 0.9f;

                float x = centerX + Mathf.Cos(angle) * rx;
                float z = centerZ + Mathf.Sin(angle) * rz;

                // Alternate height for gentle undulation
                float heightOffset = Mathf.Sin(angle * 2f) * terrainSize.y * heightVariation;
                float y = baseHeight + heightOffset;

                // Clamp above terrain surface at this XZ
                float surfaceY = terrain.SampleHeight(new Vector3(x, 0f, z)) + terrainPos.y;
                y = Mathf.Max(y, surfaceY + 10f);

                _waypoints[i] = new Vector3(x, y, z);
            }
        }

        // -----------------------------------------------------------------
        // Spline evaluation (Catmull-Rom)
        // -----------------------------------------------------------------

        /// <summary>
        /// Evaluate a closed Catmull-Rom spline through the waypoints and
        /// look at the terrain center. t is in [0, 1] over the full duration.
        /// </summary>
        private void UpdateCameraAlongSpline(float t)
        {
            if (_waypoints == null || _waypoints.Length < 2) return;

            int n = _waypoints.Length;
            float scaledT = t * n;
            int i1 = Mathf.FloorToInt(scaledT) % n;
            float localT = scaledT - Mathf.FloorToInt(scaledT);

            // Four control points for Catmull-Rom (closed loop)
            int i0 = (i1 - 1 + n) % n;
            int i2 = (i1 + 1) % n;
            int i3 = (i1 + 2) % n;

            Vector3 pos = CatmullRom(_waypoints[i0], _waypoints[i1], _waypoints[i2], _waypoints[i3], localT);
            transform.position = pos;

            // Look at terrain center (with slight upward offset for natural framing)
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain != null)
            {
                Vector3 terrainPos = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;
                Vector3 lookTarget = new Vector3(
                    terrainPos.x + terrainSize.x * 0.5f,
                    terrainPos.y + terrainSize.y * 0.2f,
                    terrainPos.z + terrainSize.z * 0.5f
                );

                Quaternion targetRot = Quaternion.LookRotation(lookTarget - pos);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * 3f);
            }
        }

        /// <summary>
        /// Standard Catmull-Rom interpolation between p1 and p2.
        /// </summary>
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;

            return 0.5f * (
                (2f * p1) +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3
            );
        }

        // -----------------------------------------------------------------
        // DroneController sync
        // -----------------------------------------------------------------

        /// <summary>
        /// Sync DroneController's _pitch/_yaw to the camera's current
        /// Euler angles so the handoff back to manual mode is smooth.
        /// </summary>
        private void SyncDroneRotation()
        {
            if (_drone == null) return;

            Vector3 euler = transform.eulerAngles;
            float pitch = euler.x;
            if (pitch > 180f) pitch -= 360f;
            float yaw = euler.y;

            if (_pitchField != null && _yawField != null)
            {
                _pitchField.SetValue(_drone, pitch);
                _yawField.SetValue(_drone, yaw);
            }
        }
    }
}

// =============================================================================
// DroneController.cs
//
// Free-fly drone / spectator camera controller for inspecting generated terrain.
// Uses Unity's new Input System (com.unity.inputsystem) with strongly-typed
// DroneControls input actions for WASD + mouse look flight.
//
// Attach this to a GameObject with a Camera component (or as a parent of one)
// and press Play to fly around the scene.
//
// FUTURE UPGRADE: Replace this simple Transform-based flycam with a full
// physics-based drone model (Rigidbody + aerodynamic forces) if the demo needs
// realistic flight dynamics, prop wash effects, or collision response.
// =============================================================================

using UnityEngine;
using UnityEngine.InputSystem;
using DepthWizard.Input;

namespace DepthWizard.Camera
{
    /// <summary>
    /// Transform-based free-fly drone controller with mouse look.
    /// Movement is relative to the drone's local orientation (forward/right/up),
    /// giving a spectator-cam / noclip feel suitable for terrain inspection.
    /// </summary>
    [RequireComponent(typeof(UnityEngine.Camera))]
    public class DroneController : MonoBehaviour
    {
        // -----------------------------------------------------------------
        // Serialized Settings
        // -----------------------------------------------------------------

        [Header("Movement")]
        [Tooltip("Base flight speed in m/s.")]
        [SerializeField] private float moveSpeed = 20f;

        [Tooltip("Speed multiplier when Boost (Shift) is held.")]
        [SerializeField] private float boostMultiplier = 3f;

        [Header("Mouse Look")]
        [Tooltip("Mouse sensitivity for pitch/yaw (degrees per pixel of mouse delta).")]
        [SerializeField] private float lookSensitivity = 0.15f;

        [Tooltip("Maximum pitch angle (degrees) to prevent flipping.")]
        [SerializeField] private float maxPitch = 89f;

        [Header("Cursor")]
        [Tooltip("Lock and hide cursor on Play. Press Escape to release.")]
        [SerializeField] private bool lockCursorOnPlay = true;

        [Header("Terrain Awareness")]
        [Tooltip("Minimum height above the terrain surface (meters). " +
                 "Set to 0 to disable terrain clamping.")]
        [SerializeField] private float minHeightAboveTerrain = 2f;

        [Tooltip("Enable XZ boundary clamping to keep the drone within " +
                 "the terrain's horizontal extent.")]
        [SerializeField] private bool enableBoundaryClamping = true;

        [Tooltip("Inward margin (meters) from the terrain edge for " +
                 "boundary clamping, giving visual breathing room.")]
        [SerializeField] private float boundaryMargin = 10f;

        // -----------------------------------------------------------------
        // Input
        // -----------------------------------------------------------------

        private DroneControls _controls;
        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _verticalAction;
        private InputAction _boostAction;

        // -----------------------------------------------------------------
        // State
        // -----------------------------------------------------------------

        private float _pitch;
        private float _yaw;
        private bool _cursorLocked;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            _controls = new DroneControls();

            _moveAction = _controls.Drone.Move;
            _lookAction = _controls.Drone.Look;
            _verticalAction = _controls.Drone.Vertical;
            _boostAction = _controls.Drone.Boost;

            // Initialize rotation from current transform so the camera
            // doesn't snap to (0,0,0) on Play.
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x;

            // Normalize pitch to -180..180 range
            if (_pitch > 180f) _pitch -= 360f;
        }

        private void OnEnable()
        {
            _controls.Enable();

            if (lockCursorOnPlay)
            {
                SetCursorLocked(true);
            }
        }

        private void OnDisable()
        {
            _controls.Disable();
            SetCursorLocked(false);
        }

        private void OnDestroy()
        {
            _controls?.Dispose();
        }

        // -----------------------------------------------------------------
        // Update
        // -----------------------------------------------------------------

        private void Update()
        {
            HandleCursorToggle();

            if (_cursorLocked)
            {
                HandleLook();
            }

            HandleMovement();
            HandleTerrainClamping();
            HandleBoundaryClamping();
        }

        // -----------------------------------------------------------------
        // Look (Pitch / Yaw)
        // -----------------------------------------------------------------

        private void HandleLook()
        {
            Vector2 lookDelta = _lookAction.ReadValue<Vector2>();

            _yaw += lookDelta.x * lookSensitivity;
            _pitch -= lookDelta.y * lookSensitivity;
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        // -----------------------------------------------------------------
        // Movement (local-space WASD + vertical)
        // -----------------------------------------------------------------

        private void HandleMovement()
        {
            Vector2 moveInput = _moveAction.ReadValue<Vector2>();
            float verticalInput = _verticalAction.ReadValue<float>();

            bool boosting = _boostAction.IsPressed();
            float speed = moveSpeed * (boosting ? boostMultiplier : 1f);

            // Build movement vector in local space
            Vector3 move = Vector3.zero;
            move += transform.forward * moveInput.y;  // W/S
            move += transform.right * moveInput.x;    // A/D
            move += transform.up * verticalInput;     // Space/Ctrl (local up)

            // Apply movement
            transform.position += move * (speed * Time.deltaTime);
        }

        // -----------------------------------------------------------------
        // Terrain Clamping (optional)
        // -----------------------------------------------------------------

        /// <summary>
        /// Prevents the drone from flying below the terrain surface.
        /// Uses Terrain.activeTerrain if available; silently skips otherwise.
        /// </summary>
        private void HandleTerrainClamping()
        {
            if (minHeightAboveTerrain <= 0f) return;

            // TODO: When multiple terrains are supported, iterate
            // Terrain.activeTerrains and find the one under the drone's XZ.
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain == null) return;

            float terrainHeight = terrain.SampleHeight(transform.position)
                                + terrain.transform.position.y;
            float minY = terrainHeight + minHeightAboveTerrain;

            if (transform.position.y < minY)
            {
                Vector3 pos = transform.position;
                pos.y = minY;
                transform.position = pos;
            }
        }

        // -----------------------------------------------------------------
        // Boundary Clamping (XZ)
        // -----------------------------------------------------------------

        /// <summary>
        /// Prevents the drone from flying outside the terrain's horizontal
        /// (XZ) extent. Reads bounds from the active terrain's size at
        /// runtime, so it adapts to any dynamically generated terrain.
        /// </summary>
        private void HandleBoundaryClamping()
        {
            if (!enableBoundaryClamping) return;

            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain == null) return;

            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;

            float minX = terrainPos.x + boundaryMargin;
            float maxX = terrainPos.x + terrainSize.x - boundaryMargin;
            float minZ = terrainPos.z + boundaryMargin;
            float maxZ = terrainPos.z + terrainSize.z - boundaryMargin;

            // Guard against margin being larger than half the terrain
            // (would invert min/max and cause snapping to center)
            if (minX >= maxX)
            {
                float midX = terrainPos.x + terrainSize.x * 0.5f;
                minX = midX;
                maxX = midX;
            }
            if (minZ >= maxZ)
            {
                float midZ = terrainPos.z + terrainSize.z * 0.5f;
                minZ = midZ;
                maxZ = midZ;
            }

            Vector3 pos = transform.position;
            float clampedX = Mathf.Clamp(pos.x, minX, maxX);
            float clampedZ = Mathf.Clamp(pos.z, minZ, maxZ);

            if (clampedX != pos.x || clampedZ != pos.z)
            {
                // TODO: Hook for future UI feedback (toast, vignette flash,
                // edge glow) when the drone hits a boundary. For now, just
                // clamp silently to avoid log spam every frame.
                pos.x = clampedX;
                pos.z = clampedZ;
                transform.position = pos;
            }
        }

        // -----------------------------------------------------------------
        // Cursor Lock / Unlock
        // -----------------------------------------------------------------

        private void HandleCursorToggle()
        {
            // Escape releases cursor; left-click re-locks it
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                SetCursorLocked(false);
            }
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && !_cursorLocked)
            {
                SetCursorLocked(true);
            }
        }

        private void SetCursorLocked(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}

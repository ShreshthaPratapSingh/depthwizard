// =============================================================================
// DroneController.cs
//
// Dual-mode camera controller for inspecting generated terrain:
//
//   FREE-FLY (default):
//     WASD + mouse look flight, Shift to boost, Space/Ctrl for vertical,
//     terrain height clamping, boundary clamping. This is the original
//     behavior, preserved exactly as it was.
//
//   ORBIT:
//     Camera orbits around a target point (defaults to terrain center).
//     Mouse look rotates around the target (yaw/pitch on a sphere).
//     Scroll wheel zooms in/out (clamped to min/max distance).
//     WASD/Vertical/Boost inputs are ignored — position is fully
//     determined by orbit angle + distance from target.
//
// Press TAB to toggle between modes at runtime. The transition is smooth:
//   - FreeFly → Orbit: computes initial orbit angles/distance from current
//     camera position relative to the target, so no visual snap.
//   - Orbit → FreeFly: keeps current position/rotation as the new free-fly
//     starting point.
//
// Uses Unity's new Input System (com.unity.inputsystem) with the DroneControls
// input actions asset. New actions added to Drone.inputactions:
//   - ToggleCameraMode (Tab) — switches between FreeFly and Orbit
//   - OrbitZoom (Mouse Scroll Y) — zoom in/out in orbit mode
//
// Attach this to a GameObject with a Camera component and press Play.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using DepthWizard.Input;

namespace DepthWizard.Camera
{
    /// <summary>
    /// The two available camera modes.
    /// </summary>
    public enum CameraMode
    {
        FreeFly,
        Orbit
    }

    /// <summary>
    /// Transform-based dual-mode camera controller: free-fly drone + orbit cam.
    /// In FreeFly mode, movement is relative to the drone's local orientation
    /// (forward/right/up), giving a spectator-cam / noclip feel.
    /// In Orbit mode, the camera orbits a target point with mouse-driven
    /// rotation and scroll-wheel zoom.
    /// </summary>
    [RequireComponent(typeof(UnityEngine.Camera))]
    public class DroneController : MonoBehaviour
    {
        // -----------------------------------------------------------------
        // Serialized Settings — Free-Fly (unchanged from original)
        // -----------------------------------------------------------------

        [Header("Movement (Free-Fly)")]
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

        [Header("Terrain Awareness (Free-Fly only)")]
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
        // Serialized Settings — Orbit
        // -----------------------------------------------------------------

        [Header("Orbit Mode")]
        [Tooltip("The point the camera orbits around. If unassigned, " +
                 "defaults to the center of the active terrain's bounds.")]
        [SerializeField] private Transform orbitTarget;

        [Tooltip("Minimum distance from the orbit target (meters).")]
        [SerializeField] private float minOrbitDistance = 20f;

        [Tooltip("Maximum distance from the orbit target (meters).")]
        [SerializeField] private float maxOrbitDistance = 500f;

        [Tooltip("Mouse sensitivity for orbit rotation (degrees per pixel).")]
        [SerializeField] private float orbitSensitivity = 0.25f;

        [Tooltip("Zoom speed multiplier for scroll wheel in orbit mode.")]
        [SerializeField] private float zoomSpeed = 15f;

        // -----------------------------------------------------------------
        // Input
        // -----------------------------------------------------------------

        private DroneControls _controls;
        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _verticalAction;
        private InputAction _boostAction;
        private InputAction _toggleModeAction;
        private InputAction _orbitZoomAction;

        // -----------------------------------------------------------------
        // State
        // -----------------------------------------------------------------

        private float _pitch;
        private float _yaw;
        private bool _cursorLocked;

        /// <summary>Current camera mode (FreeFly or Orbit).</summary>
        public CameraMode CurrentMode { get; private set; } = CameraMode.FreeFly;

        // Orbit state
        private Vector3 _orbitTargetPosition;
        private float _orbitDistance;
        private float _orbitYaw;
        private float _orbitPitch;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Awake()
        {
            _controls = new DroneControls();

            // Original actions (unchanged)
            _moveAction = _controls.Drone.Move;
            _lookAction = _controls.Drone.Look;
            _verticalAction = _controls.Drone.Vertical;
            _boostAction = _controls.Drone.Boost;

            // New actions — accessed via FindAction() so this works both
            // before and after Unity regenerates DroneControls.cs from the
            // updated .inputactions asset.
            _toggleModeAction = _controls.asset.FindAction("Drone/ToggleCameraMode");
            _orbitZoomAction = _controls.asset.FindAction("Drone/OrbitZoom");

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

            if (_toggleModeAction != null)
            {
                _toggleModeAction.performed += OnToggleCameraMode;
            }

            if (lockCursorOnPlay)
            {
                SetCursorLocked(true);
            }
        }

        private void OnDisable()
        {
            if (_toggleModeAction != null)
            {
                _toggleModeAction.performed -= OnToggleCameraMode;
            }

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

            if (CurrentMode == CameraMode.FreeFly)
            {
                if (_cursorLocked)
                {
                    HandleLook();
                }

                HandleMovement();
                HandleTerrainClamping();
                HandleBoundaryClamping();
            }
            else // Orbit
            {
                if (_cursorLocked)
                {
                    HandleOrbitRotation();
                }

                HandleOrbitZoom();
                ApplyOrbitPosition();
            }
        }

        // -----------------------------------------------------------------
        // Mode Toggle (Tab key)
        // -----------------------------------------------------------------

        private void OnToggleCameraMode(InputAction.CallbackContext ctx)
        {
            if (CurrentMode == CameraMode.FreeFly)
            {
                SwitchToOrbit();
            }
            else
            {
                SwitchToFreeFly();
            }
        }

        /// <summary>
        /// Public API for toggling camera mode from UI buttons.
        /// Keyboard shortcut (Tab) continues to work via OnToggleCameraMode.
        /// </summary>
        public void ToggleCameraMode()
        {
            if (CurrentMode == CameraMode.FreeFly)
                SwitchToOrbit();
            else
                SwitchToFreeFly();
        }

        private void SwitchToOrbit()
        {
            // Compute orbit target position
            _orbitTargetPosition = GetOrbitTargetPosition();

            // Compute initial orbit parameters from current camera position
            // so the transition doesn't visually snap/jump.
            Vector3 offset = transform.position - _orbitTargetPosition;
            _orbitDistance = offset.magnitude;
            _orbitDistance = Mathf.Clamp(_orbitDistance, minOrbitDistance, maxOrbitDistance);

            // Compute spherical angles from the offset vector
            // Yaw = angle around Y axis, Pitch = angle above/below horizontal
            _orbitYaw = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Asin(Mathf.Clamp(offset.y / _orbitDistance, -1f, 1f)) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Clamp(_orbitPitch, -maxPitch, maxPitch);

            CurrentMode = CameraMode.Orbit;
            Debug.Log($"[DroneController] Camera mode: Orbit (target: {_orbitTargetPosition})");
        }

        private void SwitchToFreeFly()
        {
            // Keep camera at its current orbit position/rotation as the new
            // free-fly starting point — no jarring snap.
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x;
            if (_pitch > 180f) _pitch -= 360f;

            CurrentMode = CameraMode.FreeFly;
            Debug.Log("[DroneController] Camera mode: FreeFly");
        }

        // -----------------------------------------------------------------
        // Orbit Target Resolution
        // -----------------------------------------------------------------

        /// <summary>
        /// Returns the world-space position to orbit around. Uses the
        /// assigned orbitTarget Transform if set, otherwise computes the
        /// center of the active terrain's bounds.
        /// </summary>
        private Vector3 GetOrbitTargetPosition()
        {
            if (orbitTarget != null)
            {
                return orbitTarget.position;
            }

            // Default: center of active terrain
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (terrain != null)
            {
                Vector3 terrainPos = terrain.transform.position;
                Vector3 terrainSize = terrain.terrainData.size;

                float centerX = terrainPos.x + terrainSize.x * 0.5f;
                float centerZ = terrainPos.z + terrainSize.z * 0.5f;

                // Sample terrain height at center for a reasonable Y
                Vector3 centerWorldPos = new Vector3(centerX, 0f, centerZ);
                float centerY = terrain.SampleHeight(centerWorldPos) + terrainPos.y;

                return new Vector3(centerX, centerY, centerZ);
            }

            // No terrain, no target — orbit around origin
            Debug.LogWarning("[DroneController] No orbit target assigned and no active terrain. Orbiting around origin.");
            return Vector3.zero;
        }

        // -----------------------------------------------------------------
        // Orbit Rotation (mouse Look action, reused)
        // -----------------------------------------------------------------

        private void HandleOrbitRotation()
        {
            Vector2 lookDelta = _lookAction.ReadValue<Vector2>();

            _orbitYaw += lookDelta.x * orbitSensitivity;
            _orbitPitch -= lookDelta.y * orbitSensitivity;
            _orbitPitch = Mathf.Clamp(_orbitPitch, -maxPitch, maxPitch);
        }

        // -----------------------------------------------------------------
        // Orbit Zoom (scroll wheel)
        // -----------------------------------------------------------------

        private void HandleOrbitZoom()
        {
            if (_orbitZoomAction == null) return;

            float scrollDelta = _orbitZoomAction.ReadValue<float>();
            if (Mathf.Abs(scrollDelta) > 0.01f)
            {
                // Scroll values are typically ±120; normalize to a usable range
                _orbitDistance -= (scrollDelta / 120f) * zoomSpeed;
                _orbitDistance = Mathf.Clamp(_orbitDistance, minOrbitDistance, maxOrbitDistance);
            }
        }

        // -----------------------------------------------------------------
        // Apply Orbit Position & Rotation
        // -----------------------------------------------------------------

        private void ApplyOrbitPosition()
        {
            // Update target position each frame in case the target Transform moves
            if (orbitTarget != null)
            {
                _orbitTargetPosition = orbitTarget.position;
            }

            // Convert spherical coordinates to Cartesian offset
            float pitchRad = _orbitPitch * Mathf.Deg2Rad;
            float yawRad = _orbitYaw * Mathf.Deg2Rad;

            float cosP = Mathf.Cos(pitchRad);
            Vector3 offset = new Vector3(
                Mathf.Sin(yawRad) * cosP,
                Mathf.Sin(pitchRad),
                Mathf.Cos(yawRad) * cosP
            ) * _orbitDistance;

            transform.position = _orbitTargetPosition + offset;
            transform.LookAt(_orbitTargetPosition);
        }

        // =================================================================
        // EXISTING FREE-FLY METHODS (unchanged from original)
        // =================================================================

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
            // (but not when clicking on UI elements — lets buttons work)
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                SetCursorLocked(false);
            }
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && !_cursorLocked)
            {
                // Don't re-lock if the click landed on a UI element
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                    return;
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


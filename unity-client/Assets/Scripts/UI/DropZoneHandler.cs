// =============================================================================
// DropZoneHandler.cs
//
// Handles drag-and-drop of image files onto the landing page drop zone.
// Provides visual feedback (highlight border) during drag-over and forwards
// dropped file paths to ImagePicker for validation and loading.
//
// Unity's built-in drag-and-drop (IDropHandler) works in the Editor but NOT
// in standalone builds. For standalone Windows builds, we use a simple
// approach: poll for file drops using Application.dataPath checks or an
// external plugin. As a pragmatic fallback for the hackathon, this script
// primarily handles Editor drag-and-drop and provides the visual highlight
// on hover that works in both contexts.
//
// In a standalone build, the "Select Image" button (native file dialog)
// is the primary upload method.
//
// Namespace: DepthWizard.UI
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace DepthWizard.UI
{
    /// <summary>
    /// Drop zone visual handler. Highlights the zone on pointer hover
    /// and handles drag-and-drop in the Unity Editor.
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class DropZoneHandler : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler,
        IPointerClickHandler, IDropHandler
    {
        // ---------------------------------------------------------------------
        // Configuration
        // ---------------------------------------------------------------------

        [Header("Visual Feedback")]
        [Tooltip("Border color when idle.")]
        [SerializeField] private Color normalBorderColor = new Color(0.165f, 0.227f, 0.306f, 1f); // #2A3A4E

        [Tooltip("Border color when hovering / dragging over.")]
        [SerializeField] private Color highlightBorderColor = new Color(0f, 0.898f, 1f, 0.6f); // #00E5FF at 60%

        [Tooltip("Border color when an image is loaded.")]
        [SerializeField] private Color loadedBorderColor = new Color(0.412f, 0.941f, 0.682f, 0.6f); // #69F0AE at 60%

        // ---------------------------------------------------------------------
        // References (set by LandingPageUIController or found at runtime)
        // ---------------------------------------------------------------------

        private Image _borderImage;
        private ImagePicker _picker;
        private bool _hasImage;

        // ---------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------

        /// <summary>
        /// Initialize references. Called by LandingPageUIController during setup.
        /// </summary>
        public void Init(ImagePicker picker)
        {
            _picker = picker;
            _borderImage = GetComponent<Image>();
            SetBorderColor(normalBorderColor);

            // Subscribe to session changes
            var session = ImageSessionManager.Instance;
            if (session != null)
            {
                session.OnImageLoaded += _ => {
                    _hasImage = true;
                    SetBorderColor(loadedBorderColor);
                };
                session.OnImageCleared += () => {
                    _hasImage = false;
                    SetBorderColor(normalBorderColor);
                };
            }
        }

        // ---------------------------------------------------------------------
        // Pointer events (hover highlight + click-to-browse)
        // ---------------------------------------------------------------------

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_hasImage)
                SetBorderColor(highlightBorderColor);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            SetBorderColor(_hasImage ? loadedBorderColor : normalBorderColor);
        }

        /// <summary>
        /// Clicking the drop zone also opens the file browser.
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (_picker != null)
                _picker.OpenFileBrowser();
        }

        // ---------------------------------------------------------------------
        // Drag-and-drop (works in Unity Editor, limited in standalone)
        // ---------------------------------------------------------------------

        public void OnDrop(PointerEventData eventData)
        {
            Debug.Log("[DropZoneHandler] OnDrop event received.");

            // Unity Editor drag-and-drop provides paths via DragAndDrop class
            // In standalone builds, this event may not fire for OS file drops.
            // The file browser button is the reliable fallback.
#if UNITY_EDITOR
            if (UnityEditor.DragAndDrop.paths != null &&
                UnityEditor.DragAndDrop.paths.Length > 0)
            {
                _picker?.TryLoadFromDrop(UnityEditor.DragAndDrop.paths);
            }
#endif
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private void SetBorderColor(Color color)
        {
            if (_borderImage != null)
                _borderImage.color = color;
        }
    }
}

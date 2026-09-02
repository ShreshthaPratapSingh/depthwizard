// =============================================================================
// LandingPageUIController.cs
//
// Main controller for the DepthWizard landing page scene. Manages all UI
// state: button enables, preview display, status text, and scene transition.
//
// Finds UI elements by GameObject name in the hierarchy (set by the Editor
// setup script SetupLandingPageScene.cs), so it does not rely on serialized
// Inspector references — resilient to scene re-creation.
//
// Wiring:
//   - Subscribes to ImagePicker.OnSuccess / OnError for status text
//   - Subscribes to ImageSessionManager.OnImageLoaded for preview display
//   - "Select Image" button → ImagePicker.OpenFileBrowser()
//   - "Generate" button → SceneManager.LoadScene("SampleScene")
//
// Attach to: Canvas root in LandingPage.unity (done by SetupLandingPageScene)
//
// Namespace: DepthWizard.UI
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

namespace DepthWizard.UI
{
    /// <summary>
    /// Landing page UI orchestrator. Manages the upload flow from image
    /// selection through to scene transition.
    /// </summary>
    public class LandingPageUIController : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // UI element names (must match the hierarchy built by the Editor script)
        // ---------------------------------------------------------------------

        private const string NAME_TITLE       = "TitleText";
        private const string NAME_SUBTITLE    = "SubtitleText";
        private const string NAME_DROP_ZONE   = "DropZone";
        private const string NAME_PLACEHOLDER = "PlaceholderGroup";
        private const string NAME_PREVIEW     = "PreviewImage";
        private const string NAME_SELECT_BTN  = "SelectButton";
        private const string NAME_GENERATE_BTN = "GenerateButton";
        private const string NAME_STATUS_TEXT = "StatusText";

        // Scene to load when the user clicks "Generate Terrain"
        private const string TARGET_SCENE = "SampleScene";

        // ---------------------------------------------------------------------
        // Resolved references
        // ---------------------------------------------------------------------

        private TMP_Text    _titleText;
        private TMP_Text    _subtitleText;
        private GameObject  _placeholderGroup;
        private RawImage    _previewImage;
        private Button      _selectButton;
        private Button      _generateButton;
        private TMP_Text    _generateButtonText;
        private TMP_Text    _statusText;
        private CanvasGroup _previewCanvasGroup;

        // Components
        private ImagePicker     _picker;
        private DropZoneHandler _dropZone;

        // Animation state
        private Coroutine _fadeCoroutine;
        private Coroutine _statusClearCoroutine;

        // Status text colors
        private static readonly Color COLOR_STATUS_DEFAULT =
            new Color(1f, 1f, 1f, 0.5f);
        private static readonly Color COLOR_STATUS_ERROR =
            new Color(1f, 0.322f, 0.322f, 1f); // #FF5252
        private static readonly Color COLOR_STATUS_SUCCESS =
            new Color(0.412f, 0.941f, 0.682f, 1f); // #69F0AE

        // Button colors
        private static readonly Color COLOR_BTN_ACTIVE =
            new Color(0f, 0.898f, 1f, 1f); // #00E5FF
        private static readonly Color COLOR_BTN_DISABLED =
            new Color(0.3f, 0.3f, 0.4f, 0.5f);
        private static readonly Color COLOR_GENERATE_ACTIVE =
            new Color(0f, 0.784f, 0.325f, 1f); // #00C853
        private static readonly Color COLOR_GENERATE_DISABLED =
            new Color(0.2f, 0.3f, 0.25f, 0.5f);

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Awake()
        {
            ResolveUIElements();
            SetupComponents();
            WireEvents();
            SetInitialState();
        }

        private void OnDestroy()
        {
            UnwireEvents();
        }

        // ---------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------

        /// <summary>
        /// Find all UI elements by name in the hierarchy. Logs warnings
        /// for any missing elements so teammates can diagnose scene issues.
        /// </summary>
        private void ResolveUIElements()
        {
            _titleText      = FindTMP(NAME_TITLE);
            _subtitleText   = FindTMP(NAME_SUBTITLE);
            _statusText     = FindTMP(NAME_STATUS_TEXT);

            var dropZoneGo  = FindChild(NAME_DROP_ZONE);
            _placeholderGroup = FindChild(NAME_PLACEHOLDER);

            var previewGo   = FindChild(NAME_PREVIEW);
            if (previewGo != null)
            {
                _previewImage = previewGo.GetComponent<RawImage>();
                _previewCanvasGroup = previewGo.GetComponent<CanvasGroup>();
                if (_previewCanvasGroup == null)
                    _previewCanvasGroup = previewGo.AddComponent<CanvasGroup>();
            }

            var selectGo    = FindChild(NAME_SELECT_BTN);
            if (selectGo != null) _selectButton = selectGo.GetComponent<Button>();

            var generateGo  = FindChild(NAME_GENERATE_BTN);
            if (generateGo != null)
            {
                _generateButton = generateGo.GetComponent<Button>();
                _generateButtonText = generateGo.GetComponentInChildren<TMP_Text>();
            }

            // Get or add DropZoneHandler
            if (dropZoneGo != null)
            {
                _dropZone = dropZoneGo.GetComponent<DropZoneHandler>();
                if (_dropZone == null)
                    _dropZone = dropZoneGo.AddComponent<DropZoneHandler>();
            }
        }

        /// <summary>
        /// Ensure ImagePicker and DropZoneHandler are set up.
        /// </summary>
        private void SetupComponents()
        {
            // ImagePicker lives on this same GameObject
            _picker = GetComponent<ImagePicker>();
            if (_picker == null)
                _picker = gameObject.AddComponent<ImagePicker>();

            // Initialize drop zone with picker reference
            _dropZone?.Init(_picker);
        }

        /// <summary>
        /// Wire button clicks and image events.
        /// </summary>
        private void WireEvents()
        {
            _selectButton?.onClick.AddListener(OnSelectButtonClicked);
            _generateButton?.onClick.AddListener(OnGenerateButtonClicked);

            _picker.OnSuccess += OnImageLoadSuccess;
            _picker.OnError += OnImageLoadError;

            // Also listen to the session manager for external changes
            var session = ImageSessionManager.Instance;
            if (session != null)
            {
                session.OnImageLoaded += OnSessionImageChanged;
                session.OnImageCleared += OnSessionCleared;
            }
        }

        private void UnwireEvents()
        {
            _selectButton?.onClick.RemoveListener(OnSelectButtonClicked);
            _generateButton?.onClick.RemoveListener(OnGenerateButtonClicked);

            if (_picker != null)
            {
                _picker.OnSuccess -= OnImageLoadSuccess;
                _picker.OnError -= OnImageLoadError;
            }

            var session = ImageSessionManager.Instance;
            if (session != null)
            {
                session.OnImageLoaded -= OnSessionImageChanged;
                session.OnImageCleared -= OnSessionCleared;
            }
        }

        /// <summary>
        /// Set the UI to its initial "no image selected" state.
        /// If the session already has an image (e.g. user returned from
        /// terrain scene), show it immediately.
        /// </summary>
        private void SetInitialState()
        {
            var session = ImageSessionManager.Instance;
            if (session != null && session.HasImage)
            {
                // Restore previous selection
                ShowPreview(session.PreviewTexture);
                SetStatus(
                    $"Image loaded: {session.Width}×{session.Height}",
                    COLOR_STATUS_SUCCESS);
                SetGenerateButtonEnabled(true);
            }
            else
            {
                // Fresh state — no image
                ShowPlaceholder();
                SetStatus("", COLOR_STATUS_DEFAULT);
                SetGenerateButtonEnabled(false);
            }
        }

        // ---------------------------------------------------------------------
        // Event handlers
        // ---------------------------------------------------------------------

        private void OnSelectButtonClicked()
        {
            _picker.OpenFileBrowser();
        }

        private void OnGenerateButtonClicked()
        {
            if (!ImageSessionManager.Instance.HasImage)
            {
                SetStatus("No image selected.", COLOR_STATUS_ERROR);
                return;
            }

            Debug.Log("[LandingPageUI] Starting processing pipeline...");
            SetStatus("Starting processing...", COLOR_STATUS_DEFAULT);

            // Disable the button to prevent double-clicks
            SetGenerateButtonEnabled(false);

            // Find ProcessingController on this Canvas (attached by the
            // editor setup script). If missing, create one dynamically.
            var controller = GetComponent<ProcessingController>();
            if (controller == null)
                controller = gameObject.AddComponent<ProcessingController>();

            // The processing overlay will appear on top of the landing page.
            // Scene transition happens inside ProcessingController after
            // the full pipeline completes.
            controller.StartPipeline();
        }

        private void OnImageLoadSuccess(string message)
        {
            SetStatus(message, COLOR_STATUS_SUCCESS);
            // Preview display is handled by OnSessionImageChanged
        }

        private void OnImageLoadError(string message)
        {
            SetStatus(message, COLOR_STATUS_ERROR);
            // Auto-clear error after 5 seconds
            ClearStatusAfterDelay(5f);
        }

        private void OnSessionImageChanged(Texture2D texture)
        {
            ShowPreview(texture);
            SetGenerateButtonEnabled(true);
        }

        private void OnSessionCleared()
        {
            ShowPlaceholder();
            SetGenerateButtonEnabled(false);
            SetStatus("", COLOR_STATUS_DEFAULT);
        }

        // ---------------------------------------------------------------------
        // UI state management
        // ---------------------------------------------------------------------

        /// <summary>
        /// Show the image preview and hide the placeholder.
        /// Fades the preview in smoothly.
        /// </summary>
        private void ShowPreview(Texture2D texture)
        {
            if (_previewImage != null && texture != null)
            {
                _previewImage.texture = texture;
                _previewImage.gameObject.SetActive(true);

                // Update AspectRatioFitter if present
                var fitter = _previewImage.GetComponent<AspectRatioFitter>();
                if (fitter != null && texture.height > 0)
                {
                    fitter.aspectRatio = (float)texture.width / texture.height;
                }

                // Fade in
                if (_fadeCoroutine != null)
                    StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(FadeIn(_previewCanvasGroup, 0.3f));
            }

            if (_placeholderGroup != null)
                _placeholderGroup.SetActive(false);
        }

        /// <summary>
        /// Show the placeholder and hide the preview.
        /// </summary>
        private void ShowPlaceholder()
        {
            if (_placeholderGroup != null)
                _placeholderGroup.SetActive(true);

            if (_previewImage != null)
            {
                _previewImage.gameObject.SetActive(false);
                _previewImage.texture = null;
            }
        }

        /// <summary>
        /// Update the status text with a message and color.
        /// </summary>
        private void SetStatus(string message, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.color = color;

            // Cancel any pending clear
            if (_statusClearCoroutine != null)
            {
                StopCoroutine(_statusClearCoroutine);
                _statusClearCoroutine = null;
            }
        }

        private void ClearStatusAfterDelay(float delay)
        {
            if (_statusClearCoroutine != null)
                StopCoroutine(_statusClearCoroutine);
            _statusClearCoroutine = StartCoroutine(ClearStatusAfter(delay));
        }

        /// <summary>
        /// Enable or disable the Generate button with visual feedback.
        /// </summary>
        private void SetGenerateButtonEnabled(bool enabled)
        {
            if (_generateButton == null) return;
            _generateButton.interactable = enabled;

            // Update button color
            var btnImage = _generateButton.GetComponent<Image>();
            if (btnImage != null)
                btnImage.color = enabled ? COLOR_GENERATE_ACTIVE : COLOR_GENERATE_DISABLED;

            if (_generateButtonText != null)
                _generateButtonText.color = enabled
                    ? Color.white
                    : new Color(1f, 1f, 1f, 0.3f);
        }

        // ---------------------------------------------------------------------
        // Helpers: find UI elements by name
        // ---------------------------------------------------------------------

        private GameObject FindChild(string name)
        {
            var result = FindDeep(transform, name);
            if (result == null)
                Debug.LogWarning(
                    $"[LandingPageUI] UI element '{name}' not found in hierarchy. " +
                    "Run Tools → DepthWizard → Setup Landing Page to rebuild.");
            return result?.gameObject;
        }

        private TMP_Text FindTMP(string name)
        {
            var go = FindChild(name);
            if (go == null) return null;
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp == null)
                Debug.LogWarning(
                    $"[LandingPageUI] '{name}' found but has no TMP_Text component.");
            return tmp;
        }

        /// <summary>
        /// Recursive depth-first search for a child with the given name.
        /// </summary>
        private static Transform FindDeep(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                    return child;
                var result = FindDeep(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // Coroutines
        // ---------------------------------------------------------------------

        private IEnumerator FadeIn(CanvasGroup group, float duration)
        {
            if (group == null) yield break;
            group.alpha = 0f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                group.alpha = Mathf.Clamp01(elapsed / duration);
                yield return null;
            }
            group.alpha = 1f;
        }

        private IEnumerator ClearStatusAfter(float delay)
        {
            yield return new WaitForSeconds(delay);
            SetStatus("", COLOR_STATUS_DEFAULT);
        }
    }
}

// =============================================================================
// ControlHintsOverlay.cs
//
// Minimal, semi-transparent on-screen overlay showing camera control hints
// in the 3D flythrough result screen (SampleScene). Positioned bottom-left
// to avoid colliding with the Export button (top-right).
//
// Context-aware: reads DroneController.CurrentMode and swaps between FreeFly
// and Orbit hint sets when the user presses Tab.
//
// Auto-fades to low opacity after a configurable delay so it doesn't obstruct
// the 3D view once the user has learned the controls.
//
// Self-contained: reads camera mode via public property only. Does not modify
// DroneController, CameraModeController, or any other existing script.
//
// Namespace: DepthWizard.UI
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DepthWizard.Camera;

namespace DepthWizard.UI
{
    /// <summary>
    /// Builds a small control-hints panel at runtime and keeps it in sync
    /// with the current camera mode (FreeFly vs Orbit).
    /// </summary>
    public class ControlHintsOverlay : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Configuration
        // ---------------------------------------------------------------------

        [Header("Auto-Fade")]
        [Tooltip("Seconds after scene load before the overlay fades to resting opacity.")]
        [SerializeField] private float autoFadeDelay = 6f;

        [Tooltip("Resting opacity after the initial fade (0 = invisible, 1 = fully visible).")]
        [SerializeField, Range(0f, 1f)] private float restingAlpha = 0.18f;

        [Tooltip("Duration of the fade animation in seconds.")]
        [SerializeField] private float fadeDuration = 1f;

        // ---------------------------------------------------------------------
        // Theme
        // ---------------------------------------------------------------------

        private static readonly Color COL_BG       = new Color(0.04f, 0.06f, 0.10f, 0.65f);
        private static readonly Color COL_KEY      = new Color(0f, 0.898f, 1f, 1f);       // #00E5FF
        private static readonly Color COL_DESC     = new Color(0.85f, 0.87f, 0.90f, 1f);  // light grey
        private static readonly Color COL_DIVIDER  = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color COL_MODE_TAG = new Color(0f, 0.898f, 1f, 0.35f);

        // ---------------------------------------------------------------------
        // Hint data
        // ---------------------------------------------------------------------

        private static readonly string[][] HINTS_FREEFLY = new[]
        {
            new[] { "WASD",           "Move" },
            new[] { "Mouse",          "Look" },
            new[] { "Space / Ctrl",   "Up / Down" },
            new[] { "Shift",          "Boost" },
            new[] { "Scroll",         "Speed" },
            new[] { "Esc",            "Unlock Cursor" },
        };

        private static readonly string[][] HINTS_ORBIT = new[]
        {
            new[] { "Mouse",          "Rotate" },
            new[] { "Scroll",         "Zoom" },
            new[] { "Esc",            "Unlock Cursor" },
        };

        // Always shown at the bottom, regardless of mode
        private static readonly string[][] HINTS_SHARED = new[]
        {
            new[] { "Tab",            "Toggle Orbit" },
            new[] { "E",              "Export" },
        };

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        private CanvasGroup _canvasGroup;
        private TMP_Text _modeLabel;
        private Transform _hintContainer;
        private DroneController _droneController;
        private CameraMode _lastMode = CameraMode.FreeFly;
        private Coroutine _fadeCoroutine;

        // Prefab-style hint row pool
        private readonly System.Collections.Generic.List<GameObject> _hintRows =
            new System.Collections.Generic.List<GameObject>();

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------

        private void Start()
        {
            _droneController = FindFirstObjectByType<DroneController>();
            BuildUI();
            RefreshHints();

            // Start the auto-fade timer
            _fadeCoroutine = StartCoroutine(AutoFadeSequence());
        }

        private void Update()
        {
            if (_droneController == null) return;

            // Detect mode change and refresh hints
            if (_droneController.CurrentMode != _lastMode)
            {
                _lastMode = _droneController.CurrentMode;
                RefreshHints();

                // Briefly restore full opacity on mode switch
                RestoreAndFade(2.5f);
            }
        }

        // ---------------------------------------------------------------------
        // UI construction
        // ---------------------------------------------------------------------

        private void BuildUI()
        {
            // --- Canvas ---
            var canvasGo = new GameObject("ControlHintsCanvas");
            canvasGo.transform.SetParent(transform);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90; // below Export HUD (100)

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            canvasGo.AddComponent<GraphicRaycaster>();

            // --- Panel (bottom-left) ---
            var panelGo = new GameObject("HintsPanel");
            panelGo.transform.SetParent(canvasGo.transform, false);

            var panelRect = panelGo.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 0f);
            panelRect.anchorMax = new Vector2(0f, 0f);
            panelRect.pivot = new Vector2(0f, 0f);
            panelRect.anchoredPosition = new Vector2(16f, 16f);

            // Size is set by ContentSizeFitter
            var bgImage = panelGo.AddComponent<Image>();
            bgImage.color = COL_BG;

            // Rounded corners effect via a slight outline
            var outline = panelGo.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 1f, 1f, 0.04f);
            outline.effectDistance = new Vector2(1f, 1f);

            // CanvasGroup for alpha fading
            _canvasGroup = panelGo.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 1f;

            // --- Vertical layout ---
            var vlg = panelGo.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(14, 14, 10, 10);
            vlg.spacing = 3f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var fitter = panelGo.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // --- Mode label (top of panel) ---
            var modeLabelGo = new GameObject("ModeLabel");
            modeLabelGo.transform.SetParent(panelGo.transform, false);
            _modeLabel = modeLabelGo.AddComponent<TextMeshProUGUI>();
            _modeLabel.fontSize = 11f;
            _modeLabel.color = COL_MODE_TAG;
            _modeLabel.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            _modeLabel.text = "FREE-FLY MODE";
            _modeLabel.alignment = TextAlignmentOptions.Left;
            _modeLabel.margin = new Vector4(0, 0, 0, 4f);

            var modeLE = modeLabelGo.AddComponent<LayoutElement>();
            modeLE.preferredHeight = 16f;

            // Store container reference
            _hintContainer = panelGo.transform;
        }

        // ---------------------------------------------------------------------
        // Hint population
        // ---------------------------------------------------------------------

        private void RefreshHints()
        {
            // Clear old hint rows (keep mode label at index 0)
            foreach (var row in _hintRows)
            {
                if (row != null) Destroy(row);
            }
            _hintRows.Clear();

            // Pick the right hint set
            bool isOrbit = _lastMode == CameraMode.Orbit;
            _modeLabel.text = isOrbit ? "ORBIT MODE" : "FREE-FLY MODE";

            var hints = isOrbit ? HINTS_ORBIT : HINTS_FREEFLY;

            foreach (var hint in hints)
            {
                _hintRows.Add(CreateHintRow(hint[0], hint[1]));
            }

            // Divider
            _hintRows.Add(CreateDivider());

            // Shared hints (Tab, E)
            foreach (var hint in HINTS_SHARED)
            {
                _hintRows.Add(CreateHintRow(hint[0], hint[1]));
            }
        }

        private GameObject CreateHintRow(string key, string description)
        {
            var rowGo = new GameObject("Hint_" + key.Replace(" ", ""));
            rowGo.transform.SetParent(_hintContainer, false);

            var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            var rowLE = rowGo.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 18f;

            // Key label
            var keyGo = new GameObject("Key");
            keyGo.transform.SetParent(rowGo.transform, false);
            var keyText = keyGo.AddComponent<TextMeshProUGUI>();
            keyText.text = key;
            keyText.fontSize = 12f;
            keyText.color = COL_KEY;
            keyText.fontStyle = FontStyles.Bold;
            keyText.alignment = TextAlignmentOptions.Left;

            var keyLE = keyGo.AddComponent<LayoutElement>();
            keyLE.preferredWidth = 100f;

            // Description label
            var descGo = new GameObject("Desc");
            descGo.transform.SetParent(rowGo.transform, false);
            var descText = descGo.AddComponent<TextMeshProUGUI>();
            descText.text = description;
            descText.fontSize = 12f;
            descText.color = COL_DESC;
            descText.alignment = TextAlignmentOptions.Left;

            var descLE = descGo.AddComponent<LayoutElement>();
            descLE.preferredWidth = 80f;

            return rowGo;
        }

        private GameObject CreateDivider()
        {
            var divGo = new GameObject("Divider");
            divGo.transform.SetParent(_hintContainer, false);

            var divImage = divGo.AddComponent<Image>();
            divImage.color = COL_DIVIDER;

            var divLE = divGo.AddComponent<LayoutElement>();
            divLE.preferredHeight = 1f;

            return divGo;
        }

        // ---------------------------------------------------------------------
        // Fade logic
        // ---------------------------------------------------------------------

        private IEnumerator AutoFadeSequence()
        {
            yield return new WaitForSeconds(autoFadeDelay);
            yield return FadeTo(restingAlpha, fadeDuration);
            _fadeCoroutine = null;
        }

        private void RestoreAndFade(float holdSeconds)
        {
            if (_fadeCoroutine != null)
                StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(RestoreAndFadeSequence(holdSeconds));
        }

        private IEnumerator RestoreAndFadeSequence(float holdSeconds)
        {
            // Snap to full opacity
            _canvasGroup.alpha = 1f;
            yield return new WaitForSeconds(holdSeconds);
            yield return FadeTo(restingAlpha, fadeDuration);
            _fadeCoroutine = null;
        }

        private IEnumerator FadeTo(float targetAlpha, float duration)
        {
            float startAlpha = _canvasGroup.alpha;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
                yield return null;
            }
            _canvasGroup.alpha = targetAlpha;
        }
    }
}

// =============================================================================
// SetupLandingPageScene.cs  (Editor-only)
//
// Menu: Tools → DepthWizard → Setup Landing Page
//
// Builds the full Canvas UI hierarchy for the DepthWizard landing page
// inside the LandingPage.unity scene. Run this once; then tweak visuals in
// the Inspector as needed.
//
// What it creates:
//   Canvas (Screen Space Overlay, 1920×1080 CanvasScaler)
//   ├── Background (dark navy full-screen Image)
//   ├── CenterPanel (VerticalLayoutGroup, centered)
//   │   ├── TitleText ("DepthWizard", cyan, 52pt)
//   │   ├── SubtitleText ("Single-Image Elevation Reconstruction", 18pt)
//   │   ├── DropZone (charcoal panel with highlight border)
//   │   │   ├── PlaceholderGroup (icon + text)
//   │   │   └── PreviewImage (RawImage + AspectRatioFitter)
//   │   ├── ButtonRow (Select Image + Generate Terrain)
//   │   └── StatusText (feedback/error messages)
//   └── VersionText (bottom-right)
//   EventSystem (InputSystemUIInputModule)
//
// Attaches: LandingPageUIController, ImagePicker on Canvas;
//           DropZoneHandler on DropZone.
//
// All UI elements are named so LandingPageUIController can find them at
// runtime via transform search — no serialized references needed.
// =============================================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using DepthWizard.UI;

namespace DepthWizard.Editor
{
    public static class SetupLandingPageScene
    {
        // =====================================================================
        // Colors
        // =====================================================================

        static readonly Color COL_BG           = HexColor("#0F1729");
        static readonly Color COL_PANEL        = HexColor("#1A2332");
        static readonly Color COL_BORDER       = HexColor("#2A3A4E");
        static readonly Color COL_ACCENT       = HexColor("#00E5FF");
        static readonly Color COL_GENERATE     = HexColor("#00C853");
        static readonly Color COL_GEN_DISABLED = new Color(0.2f, 0.3f, 0.25f, 0.5f);
        static readonly Color COL_TEXT_PRIMARY  = new Color(1, 1, 1, 0.9f);
        static readonly Color COL_TEXT_SECONDARY= new Color(1, 1, 1, 0.5f);
        static readonly Color COL_TEXT_DIM      = new Color(1, 1, 1, 0.3f);
        static readonly Color COL_PLACEHOLDER   = new Color(1, 1, 1, 0.35f);
        static readonly Color COL_BTN_HOVER    = HexColor("#33EEFF");
        static readonly Color COL_BTN_PRESSED  = HexColor("#00B8D4");

        // =====================================================================
        // Menu item
        // =====================================================================

        [MenuItem("Tools/DepthWizard/Setup Landing Page", false, 100)]
        public static void Execute()
        {
            // Open the LandingPage scene
            string scenePath = "Assets/Scenes/LandingPage.unity";
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);

            // Clear ALL root objects (we'll rebuild everything)
            foreach (var root in scene.GetRootGameObjects())
                Object.DestroyImmediate(root);

            // ----- Build hierarchy -----
            var canvas    = CreateCanvas();
            var bg        = CreateBackground(canvas.transform);
            var center    = CreateCenterPanel(canvas.transform);
            var title     = CreateTitleText(center.transform);
            var subtitle  = CreateSubtitleText(center.transform);
            CreateSpacer(center.transform, 24);
            var dropZone  = CreateDropZone(center.transform);
            CreateSpacer(center.transform, 20);
            var btnRow    = CreateButtonRow(center.transform);
            CreateSpacer(center.transform, 12);
            var status    = CreateStatusText(center.transform);
            CreateVersionText(canvas.transform);

            // Processing overlay (starts inactive — shown by ProcessingController)
            CreateProcessingOverlay(canvas.transform);

            var eventSys  = CreateEventSystem();

            // ----- Create ImageSessionManager in scene -----
            var sessionGo = new GameObject("[ImageSessionManager]");
            sessionGo.AddComponent<ImageSessionManager>();
            SceneManager.MoveGameObjectToScene(sessionGo, scene);

            // ----- Attach controllers -----
            canvas.AddComponent<ImagePicker>();
            canvas.AddComponent<LandingPageUIController>();
            canvas.AddComponent<ProcessingPanelUI>();
            canvas.AddComponent<ProcessingController>();

            // Drop zone handler
            dropZone.AddComponent<DropZoneHandler>();

            // ----- Camera -----
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<UnityEngine.Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = COL_BG;
            cam.orthographic = true;
            camGo.AddComponent<AudioListener>();
            SceneManager.MoveGameObjectToScene(camGo, scene);

            // ----- Directional Light -----
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.957f, 0.839f, 1f);
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
            SceneManager.MoveGameObjectToScene(lightGo, scene);

            // ----- Save -----
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(
                "[SetupLandingPageScene] ✓ Landing page rebuilt successfully.\n" +
                "  Hierarchy: Canvas → Background, CenterPanel, ProcessingOverlay\n" +
                "  Controllers: LandingPageUIController, ImagePicker, DropZoneHandler,\n" +
                "               ProcessingPanelUI, ProcessingController\n" +
                "  Enter Play mode to test.");
        }

        // =====================================================================
        // Canvas + core layout
        // =====================================================================

        static GameObject CreateCanvas()
        {
            var go = new GameObject("Canvas");
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 0;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            go.AddComponent<GraphicRaycaster>();

            return go;
        }

        static GameObject CreateBackground(Transform parent)
        {
            var go = CreateUIObject("Background", parent);
            StretchFull(go);

            var img = go.AddComponent<Image>();
            img.color = COL_BG;
            img.raycastTarget = false;

            return go;
        }

        static GameObject CreateCenterPanel(Transform parent)
        {
            var go = CreateUIObject("CenterPanel", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(720, 0); // width fixed, height auto

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.spacing = 8;
            layout.padding = new RectOffset(20, 20, 40, 40);
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            return go;
        }

        // =====================================================================
        // Title + subtitle
        // =====================================================================

        static GameObject CreateTitleText(Transform parent)
        {
            var go = CreateUIObject("TitleText", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(680, 70);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "DepthWizard";
            tmp.fontSize = 52;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = COL_ACCENT;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;

            // Character spacing for a techy look
            tmp.characterSpacing = 4f;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 70;

            return go;
        }

        static GameObject CreateSubtitleText(Transform parent)
        {
            var go = CreateUIObject("SubtitleText", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(680, 52);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "Turn a single satellite image into a 3D terrain you can fly through.\n" +
                       "<size=13><color=#FFFFFF55>Upload or drop an image below — no LiDAR, no stereo pairs, just one RGB photo.</color></size>";
            tmp.fontSize = 17;
            tmp.color = COL_TEXT_SECONDARY;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 52;

            return go;
        }

        // =====================================================================
        // Drop zone
        // =====================================================================

        static GameObject CreateDropZone(Transform parent)
        {
            // Outer container (the border / clickable area)
            var go = CreateUIObject("DropZone", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(660, 380);

            var borderImg = go.AddComponent<Image>();
            borderImg.color = COL_BORDER;
            borderImg.sprite = CreateRoundedRectSprite(16);
            borderImg.type = Image.Type.Sliced;
            borderImg.pixelsPerUnitMultiplier = 1;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 380;
            le.preferredWidth = 660;

            // Inner fill (slightly smaller to reveal border)
            var inner = CreateUIObject("DropZoneFill", go.transform);
            StretchFull(inner, 3); // 3px inset = border width
            var fillImg = inner.AddComponent<Image>();
            fillImg.color = COL_PANEL;
            fillImg.sprite = CreateRoundedRectSprite(14);
            fillImg.type = Image.Type.Sliced;
            fillImg.raycastTarget = false;

            // Placeholder group (icon + text)
            var placeholder = CreateUIObject("PlaceholderGroup", go.transform);
            StretchFull(placeholder);
            var phLayout = placeholder.AddComponent<VerticalLayoutGroup>();
            phLayout.childAlignment = TextAnchor.MiddleCenter;
            phLayout.spacing = 16;
            phLayout.childControlWidth = false;
            phLayout.childControlHeight = false;
            phLayout.childForceExpandWidth = false;
            phLayout.childForceExpandHeight = false;

            // Upload icon (arrow ↑ character as placeholder, or a simple
            // procedural texture)
            var iconGo = CreateUIObject("UploadIcon", placeholder.transform);
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.sizeDelta = new Vector2(72, 72);
            var iconImg = iconGo.AddComponent<Image>();
            iconImg.sprite = CreateUploadIconSprite();
            iconImg.color = COL_PLACEHOLDER;
            iconImg.raycastTarget = false;
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.preferredWidth = 72;
            iconLe.preferredHeight = 72;

            // Placeholder text
            var phTextGo = CreateUIObject("PlaceholderText", placeholder.transform);
            var phTextRt = phTextGo.GetComponent<RectTransform>();
            phTextRt.sizeDelta = new Vector2(400, 50);
            var phTmp = phTextGo.AddComponent<TextMeshProUGUI>();
            phTmp.text = "Click or drag an image here\n<size=13>PNG, JPG — max 20 MB</size>";
            phTmp.fontSize = 18;
            phTmp.color = COL_PLACEHOLDER;
            phTmp.alignment = TextAlignmentOptions.Center;
            phTmp.enableWordWrapping = true;
            phTmp.raycastTarget = false;
            var phTextLe = phTextGo.AddComponent<LayoutElement>();
            phTextLe.preferredWidth = 400;
            phTextLe.preferredHeight = 50;

            // Preview image (hidden initially)
            var preview = CreateUIObject("PreviewImage", go.transform);
            StretchFull(preview, 8); // 8px padding from border
            var rawImage = preview.AddComponent<RawImage>();
            rawImage.color = Color.white;
            rawImage.raycastTarget = false;

            var arFitter = preview.AddComponent<AspectRatioFitter>();
            arFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            arFitter.aspectRatio = 16f / 9f;

            var cg = preview.AddComponent<CanvasGroup>();
            cg.alpha = 0f;

            preview.SetActive(false);

            return go;
        }

        // =====================================================================
        // Buttons
        // =====================================================================

        static GameObject CreateButtonRow(Transform parent)
        {
            var go = CreateUIObject("ButtonRow", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(660, 54);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleCenter;
            layout.spacing = 20;
            layout.padding = new RectOffset(60, 60, 0, 0);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 54;

            // Select Image button
            CreateButton(
                layout.transform, "SelectButton", "Select Image",
                COL_ACCENT, COL_BTN_HOVER, COL_BTN_PRESSED, Color.white);

            // Generate Terrain button (starts disabled)
            var genBtn = CreateButton(
                layout.transform, "GenerateButton", "Generate Terrain",
                COL_GEN_DISABLED, COL_GENERATE, COL_GENERATE, new Color(1,1,1,0.3f));
            genBtn.GetComponent<Button>().interactable = false;

            return go;
        }

        static GameObject CreateButton(
            Transform parent, string name, string label,
            Color bgColor, Color hoverColor, Color pressedColor, Color textColor)
        {
            var go = CreateUIObject(name, parent);

            var img = go.AddComponent<Image>();
            img.color = bgColor;
            img.sprite = CreateRoundedRectSprite(12);
            img.type = Image.Type.Sliced;

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(
                hoverColor.r / Mathf.Max(bgColor.r, 0.01f),
                hoverColor.g / Mathf.Max(bgColor.g, 0.01f),
                hoverColor.b / Mathf.Max(bgColor.b, 0.01f),
                1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            // Button label
            var textGo = CreateUIObject("Label", go.transform);
            StretchFull(textGo);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 18;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = textColor;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;

            return go;
        }

        // =====================================================================
        // Status text
        // =====================================================================

        static GameObject CreateStatusText(Transform parent)
        {
            var go = CreateUIObject("StatusText", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(660, 26);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = 14;
            tmp.color = COL_TEXT_SECONDARY;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 26;

            return go;
        }

        static GameObject CreateVersionText(Transform parent)
        {
            var go = CreateUIObject("VersionText", parent);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1, 0);
            rt.anchorMax = new Vector2(1, 0);
            rt.pivot = new Vector2(1, 0);
            rt.anchoredPosition = new Vector2(-20, 12);
            rt.sizeDelta = new Vector2(200, 20);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "v0.1 — SIH 2026";
            tmp.fontSize = 12;
            tmp.color = COL_TEXT_DIM;
            tmp.alignment = TextAlignmentOptions.Right;
            tmp.raycastTarget = false;

            return go;
        }

        // =====================================================================
        // Processing overlay
        // =====================================================================

        /// <summary>
        /// Build the full processing overlay hierarchy.
        /// This is shown by ProcessingController when the user clicks Generate.
        ///
        /// Hierarchy:
        ///   ProcessingOverlay (CanvasGroup, starts inactive)
        ///   ├── DimBackground (full-screen semi-transparent black)
        ///   └── ProcessingPanel (centered 600×560 rounded rect)
        ///       ├── PanelTitle ("Processing")
        ///       ├── SpinnerIcon (rotating ring)
        ///       ├── ProgressBarBg + ProgressBarFill
        ///       ├── ProgressPercentText
        ///       ├── StageList (6 rows: StageRow_0..5)
        ///       │   └── each: StageIcon + StageLabel
        ///       ├── ProcessingStatusText
        ///       ├── ElapsedTimeText
        ///       └── ErrorButtonRow (RetryButton + CancelButton, hidden)
        /// </summary>
        static GameObject CreateProcessingOverlay(Transform parent)
        {
            // --- Root overlay (full-screen, blocks raycasts) ---
            var overlay = CreateUIObject("ProcessingOverlay", parent);
            StretchFull(overlay);
            var overlayCg = overlay.AddComponent<CanvasGroup>();
            overlayCg.alpha = 0f;
            overlayCg.blocksRaycasts = true;
            overlayCg.interactable = true;

            // --- Dim background ---
            var dimBg = CreateUIObject("DimBackground", overlay.transform);
            StretchFull(dimBg);
            var dimImg = dimBg.AddComponent<Image>();
            dimImg.color = new Color(0f, 0f, 0f, 0.6f);
            dimImg.raycastTarget = true; // blocks clicks to landing page

            // --- Modal panel ---
            var panel = CreateUIObject("ProcessingPanel", overlay.transform);
            var panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(600, 560);

            var panelImg = panel.AddComponent<Image>();
            panelImg.color = HexColor("#141E30");
            panelImg.sprite = CreateRoundedRectSprite(20);
            panelImg.type = Image.Type.Sliced;
            panelImg.raycastTarget = true;

            // Panel border (slightly larger, behind content)
            var panelBorder = CreateUIObject("PanelBorder", panel.transform);
            StretchFull(panelBorder, -2); // 2px outset for border glow
            var borderImg = panelBorder.AddComponent<Image>();
            borderImg.color = new Color(0f, 0.898f, 1f, 0.15f); // faint cyan border
            borderImg.sprite = CreateRoundedRectSprite(22);
            borderImg.type = Image.Type.Sliced;
            borderImg.raycastTarget = false;
            panelBorder.transform.SetAsFirstSibling(); // behind content

            // Panel layout
            var panelLayout = panel.AddComponent<VerticalLayoutGroup>();
            panelLayout.childAlignment = TextAnchor.UpperCenter;
            panelLayout.spacing = 10;
            panelLayout.padding = new RectOffset(30, 30, 28, 24);
            panelLayout.childControlWidth = true;
            panelLayout.childControlHeight = false;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            // --- Panel title ---
            var titleGo = CreateUIObject("PanelTitle", panel.transform);
            var titleRt = titleGo.GetComponent<RectTransform>();
            titleRt.sizeDelta = new Vector2(540, 40);
            var titleTmp = titleGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = "Processing";
            titleTmp.fontSize = 28;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = COL_ACCENT;
            titleTmp.alignment = TextAlignmentOptions.Center;
            titleTmp.enableWordWrapping = false;
            titleTmp.raycastTarget = false;
            titleTmp.characterSpacing = 3f;
            var titleLe = titleGo.AddComponent<LayoutElement>();
            titleLe.preferredHeight = 40;

            // --- Spinner icon ---
            var spinnerGo = CreateUIObject("SpinnerIcon", panel.transform);
            var spinnerRt = spinnerGo.GetComponent<RectTransform>();
            spinnerRt.sizeDelta = new Vector2(48, 48);
            var spinnerImg = spinnerGo.AddComponent<Image>();
            spinnerImg.sprite = CreateSpinnerSprite();
            spinnerImg.color = COL_ACCENT;
            spinnerImg.raycastTarget = false;
            var spinnerLe = spinnerGo.AddComponent<LayoutElement>();
            spinnerLe.preferredWidth = 48;
            spinnerLe.preferredHeight = 48;

            // --- Progress bar ---
            var barBg = CreateUIObject("ProgressBarBg", panel.transform);
            var barBgRt = barBg.GetComponent<RectTransform>();
            barBgRt.sizeDelta = new Vector2(540, 12);
            var barBgImg = barBg.AddComponent<Image>();
            barBgImg.color = HexColor("#1A2744");
            barBgImg.sprite = CreateRoundedRectSprite(6);
            barBgImg.type = Image.Type.Sliced;
            barBgImg.raycastTarget = false;
            var barBgLe = barBg.AddComponent<LayoutElement>();
            barBgLe.preferredHeight = 12;

            // Fill bar (child of bg, uses Image.fillAmount for smooth fill)
            var barFill = CreateUIObject("ProgressBarFill", barBg.transform);
            StretchFull(barFill);
            var barFillImg = barFill.AddComponent<Image>();
            barFillImg.color = COL_ACCENT;
            barFillImg.sprite = CreateRoundedRectSprite(6);
            barFillImg.type = Image.Type.Filled;
            barFillImg.fillMethod = Image.FillMethod.Horizontal;
            barFillImg.fillOrigin = 0; // left to right
            barFillImg.fillAmount = 0f;
            barFillImg.raycastTarget = false;

            // --- Percentage text ---
            var percentGo = CreateUIObject("ProgressPercentText", panel.transform);
            var percentRt = percentGo.GetComponent<RectTransform>();
            percentRt.sizeDelta = new Vector2(540, 24);
            var percentTmp = percentGo.AddComponent<TextMeshProUGUI>();
            percentTmp.text = "0%";
            percentTmp.fontSize = 16;
            percentTmp.fontStyle = FontStyles.Bold;
            percentTmp.color = COL_ACCENT;
            percentTmp.alignment = TextAlignmentOptions.Center;
            percentTmp.raycastTarget = false;
            var percentLe = percentGo.AddComponent<LayoutElement>();
            percentLe.preferredHeight = 24;

            // --- Stage list ---
            var stageList = CreateUIObject("StageList", panel.transform);
            var stageListRt = stageList.GetComponent<RectTransform>();
            stageListRt.sizeDelta = new Vector2(540, 180);
            var stageListLayout = stageList.AddComponent<VerticalLayoutGroup>();
            stageListLayout.childAlignment = TextAnchor.UpperLeft;
            stageListLayout.spacing = 4;
            stageListLayout.padding = new RectOffset(40, 20, 4, 4);
            stageListLayout.childControlWidth = true;
            stageListLayout.childControlHeight = false;
            stageListLayout.childForceExpandWidth = true;
            stageListLayout.childForceExpandHeight = false;
            var stageListLe = stageList.AddComponent<LayoutElement>();
            stageListLe.preferredHeight = 180;

            // Stage labels for the 6 pipeline stages
            string[] stageLabels = new string[]
            {
                "Uploading image",
                "Running depth inference",
                "Geospatial calibration",
                "Generating terrain mesh",
                "Applying textures",
                "Finalizing scene"
            };

            for (int i = 0; i < stageLabels.Length; i++)
            {
                CreateStageRow(stageList.transform, i, stageLabels[i]);
            }

            // --- Status text ---
            var statusGo = CreateUIObject("ProcessingStatusText", panel.transform);
            var statusRt = statusGo.GetComponent<RectTransform>();
            statusRt.sizeDelta = new Vector2(540, 24);
            var statusTmp = statusGo.AddComponent<TextMeshProUGUI>();
            statusTmp.text = "Preparing...";
            statusTmp.fontSize = 15;
            statusTmp.fontStyle = FontStyles.Italic;
            statusTmp.color = new Color(1f, 1f, 1f, 0.7f);
            statusTmp.alignment = TextAlignmentOptions.Center;
            statusTmp.enableWordWrapping = true;
            statusTmp.raycastTarget = false;
            var statusLe = statusGo.AddComponent<LayoutElement>();
            statusLe.preferredHeight = 24;

            // --- Elapsed time ---
            var elapsedGo = CreateUIObject("ElapsedTimeText", panel.transform);
            var elapsedRt = elapsedGo.GetComponent<RectTransform>();
            elapsedRt.sizeDelta = new Vector2(540, 20);
            var elapsedTmp = elapsedGo.AddComponent<TextMeshProUGUI>();
            elapsedTmp.text = "00:00";
            elapsedTmp.fontSize = 13;
            elapsedTmp.color = new Color(1f, 1f, 1f, 0.35f);
            elapsedTmp.alignment = TextAlignmentOptions.Center;
            elapsedTmp.raycastTarget = false;
            var elapsedLe = elapsedGo.AddComponent<LayoutElement>();
            elapsedLe.preferredHeight = 20;

            // --- Error buttons (hidden by default) ---
            var errorRow = CreateUIObject("ErrorButtonRow", panel.transform);
            var errorRowRt = errorRow.GetComponent<RectTransform>();
            errorRowRt.sizeDelta = new Vector2(540, 44);
            var errorRowLayout = errorRow.AddComponent<HorizontalLayoutGroup>();
            errorRowLayout.childAlignment = TextAnchor.MiddleCenter;
            errorRowLayout.spacing = 16;
            errorRowLayout.padding = new RectOffset(100, 100, 0, 0);
            errorRowLayout.childControlWidth = true;
            errorRowLayout.childControlHeight = true;
            errorRowLayout.childForceExpandWidth = true;
            errorRowLayout.childForceExpandHeight = true;
            var errorRowLe = errorRow.AddComponent<LayoutElement>();
            errorRowLe.preferredHeight = 44;

            // Retry button
            CreateProcessingButton(
                errorRow.transform, "RetryButton", "Retry",
                COL_ACCENT, Color.white);

            // Cancel button
            CreateProcessingButton(
                errorRow.transform, "CancelButton", "Cancel",
                HexColor("#3A3A4A"), new Color(1f, 1f, 1f, 0.7f));

            errorRow.SetActive(false);

            // Start the overlay inactive
            overlay.SetActive(false);

            return overlay;
        }

        /// <summary>
        /// Create a single stage row: icon (TMP unicode) + label text.
        /// Named "StageRow_0", "StageRow_1", etc. for runtime lookup.
        /// </summary>
        static GameObject CreateStageRow(Transform parent, int index, string label)
        {
            var row = CreateUIObject($"StageRow_{index}", parent);
            var rowRt = row.GetComponent<RectTransform>();
            rowRt.sizeDelta = new Vector2(460, 26);

            var rowLayout = row.AddComponent<HorizontalLayoutGroup>();
            rowLayout.childAlignment = TextAnchor.MiddleLeft;
            rowLayout.spacing = 10;
            rowLayout.childControlWidth = false;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;

            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.preferredHeight = 26;

            // Stage icon (unicode circle)
            var iconGo = CreateUIObject("StageIcon", row.transform);
            var iconRt = iconGo.GetComponent<RectTransform>();
            iconRt.sizeDelta = new Vector2(20, 26);
            var iconTmp = iconGo.AddComponent<TextMeshProUGUI>();
            iconTmp.text = "\u25CB"; // hollow circle (pending)
            iconTmp.fontSize = 16;
            iconTmp.color = new Color(1f, 1f, 1f, 0.25f); // dim
            iconTmp.alignment = TextAlignmentOptions.Center;
            iconTmp.raycastTarget = false;
            var iconLe = iconGo.AddComponent<LayoutElement>();
            iconLe.preferredWidth = 20;

            // Stage label
            var labelGo = CreateUIObject("StageLabel", row.transform);
            var labelRt = labelGo.GetComponent<RectTransform>();
            labelRt.sizeDelta = new Vector2(400, 26);
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = label;
            labelTmp.fontSize = 15;
            labelTmp.color = new Color(1f, 1f, 1f, 0.25f); // dim (pending)
            labelTmp.alignment = TextAlignmentOptions.Left;
            labelTmp.enableWordWrapping = false;
            labelTmp.raycastTarget = false;
            var labelLe = labelGo.AddComponent<LayoutElement>();
            labelLe.preferredWidth = 400;

            return row;
        }

        /// <summary>
        /// Create a button for the error state (Retry / Cancel).
        /// </summary>
        static GameObject CreateProcessingButton(
            Transform parent, string name, string label,
            Color bgColor, Color textColor)
        {
            var go = CreateUIObject(name, parent);

            var img = go.AddComponent<Image>();
            img.color = bgColor;
            img.sprite = CreateRoundedRectSprite(10);
            img.type = Image.Type.Sliced;

            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.fadeDuration = 0.1f;
            btn.colors = colors;

            var textGo = CreateUIObject("Label", go.transform);
            StretchFull(textGo);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 16;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = textColor;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;

            return go;
        }

        /// <summary>
        /// Create a simple procedural spinner sprite: a ring with a gap,
        /// giving a topographic/radar feel when rotated.
        /// </summary>
        static Sprite CreateSpinnerSprite()
        {
            int size = 64;
            int center = size / 2;
            float outerR = 28f;
            float innerR = 22f;
            float gapAngleDeg = 60f; // 60-degree gap in the ring

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    // Inside the ring?
                    if (dist < innerR - 0.5f || dist > outerR + 0.5f)
                        continue;

                    // Check angle for gap (top of ring)
                    float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                    if (angle < 0) angle += 360f;
                    // Gap centered at top (90 degrees)
                    float gapStart = 90f - gapAngleDeg / 2f;
                    float gapEnd = 90f + gapAngleDeg / 2f;
                    if (angle >= gapStart && angle <= gapEnd)
                        continue;

                    // Anti-aliased edges
                    float outerAlpha = Mathf.Clamp01(outerR - dist + 0.5f);
                    float innerAlpha = Mathf.Clamp01(dist - innerR + 0.5f);
                    float alpha = Mathf.Min(outerAlpha, innerAlpha);

                    pixels[y * size + x] = new Color(1, 1, 1, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            return Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f);
        }

        // =====================================================================
        // Event System
        // =====================================================================

        static GameObject CreateEventSystem()
        {
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
            return go;
        }

        // =====================================================================
        // Spacer utility
        // =====================================================================

        static GameObject CreateSpacer(Transform parent, float height)
        {
            var go = CreateUIObject("Spacer", parent);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.flexibleWidth = 1;
            return go;
        }

        // =====================================================================
        // Procedural sprites
        // =====================================================================

        /// <summary>
        /// Create a simple rounded rectangle sprite for 9-slice use.
        /// </summary>
        static Sprite CreateRoundedRectSprite(int radius)
        {
            int size = radius * 3; // minimum for 9-slice
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance to nearest corner
                    float dx = 0, dy = 0;
                    if (x < radius) dx = radius - x;
                    else if (x >= size - radius) dx = x - (size - radius - 1);
                    if (y < radius) dy = radius - y;
                    else if (y >= size - radius) dy = y - (size - radius - 1);

                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(radius - dist + 0.5f); // smooth edge

                    pixels[y * size + x] = new Color(1, 1, 1, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();

            // 9-slice border = radius on each side
            var border = new Vector4(radius, radius, radius, radius);
            return Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                border);
        }

        /// <summary>
        /// Create a simple upload arrow icon (↑ with base line) as a sprite.
        /// </summary>
        static Sprite CreateUploadIconSprite()
        {
            int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            var pixels = new Color[size * size];
            // Initialize transparent
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = Color.clear;

            // Draw a simple upload icon:
            // - Vertical line (stem) from y=8 to y=44, at x=30-33
            // - Arrow head: triangle from y=44 to y=56
            // - Horizontal base line at y=6-8, from x=16 to x=48

            // Stem
            FillRect(pixels, size, 29, 12, 35, 44, Color.white);

            // Arrow head (triangle pointing up)
            for (int y = 44; y <= 56; y++)
            {
                int half = (y - 44);
                int left = 32 - half;
                int right = 32 + half;
                FillRect(pixels, size,
                    Mathf.Max(left, 0), y,
                    Mathf.Min(right, size - 1), y,
                    Color.white);
            }

            // Base line (tray)
            FillRect(pixels, size, 14, 6, 50, 9, Color.white);
            // Tray sides
            FillRect(pixels, size, 14, 6, 17, 18, Color.white);
            FillRect(pixels, size, 47, 6, 50, 18, Color.white);

            tex.SetPixels(pixels);
            tex.Apply();

            return Sprite.Create(
                tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f),
                100f);
        }

        static void FillRect(Color[] pixels, int texSize,
            int x0, int y0, int x1, int y1, Color color)
        {
            x0 = Mathf.Clamp(x0, 0, texSize - 1);
            x1 = Mathf.Clamp(x1, 0, texSize - 1);
            y0 = Mathf.Clamp(y0, 0, texSize - 1);
            y1 = Mathf.Clamp(y1, 0, texSize - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    pixels[y * texSize + x] = color;
        }

        // =====================================================================
        // Utility
        // =====================================================================

        static GameObject CreateUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.layer = LayerMask.NameToLayer("UI");
            return go;
        }

        static void StretchFull(GameObject go, int inset = 0)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        static Color HexColor(string hex)
        {
            if (ColorUtility.TryParseHtmlString(hex, out var c))
                return c;
            return Color.magenta; // obvious error color
        }
    }
}
#endif

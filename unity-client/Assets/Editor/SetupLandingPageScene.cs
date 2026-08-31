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

            var eventSys  = CreateEventSystem();

            // ----- Create ImageSessionManager in scene -----
            var sessionGo = new GameObject("[ImageSessionManager]");
            sessionGo.AddComponent<ImageSessionManager>();
            SceneManager.MoveGameObjectToScene(sessionGo, scene);

            // ----- Attach controllers -----
            canvas.AddComponent<ImagePicker>();
            canvas.AddComponent<LandingPageUIController>();

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
                "  Hierarchy: Canvas → Background, CenterPanel → Title, DropZone, Buttons, Status\n" +
                "  Controllers: LandingPageUIController, ImagePicker, DropZoneHandler\n" +
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
            rt.sizeDelta = new Vector2(680, 30);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "Single-Image Elevation Reconstruction & 3D Flythrough";
            tmp.fontSize = 17;
            tmp.color = COL_TEXT_SECONDARY;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 30;

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

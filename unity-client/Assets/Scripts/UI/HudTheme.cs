// =============================================================================
// HudTheme.cs
//
// Centralized design system constants for all DepthWizard HUD overlays.
// Provides shared colors, typography sizes, and panel-building helpers
// so every overlay uses the same visual language.
//
// Usage: Replace per-file COL_* constants with HudTheme.COL_* references.
//
// Namespace: DepthWizard.UI
// =============================================================================

using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DepthWizard.UI
{
    /// <summary>
    /// Shared design tokens and UI factory helpers for all HUD overlays.
    /// </summary>
    public static class HudTheme
    {
        // -----------------------------------------------------------------
        // Color palette
        // -----------------------------------------------------------------

        /// <summary>Panel background: deep navy with high opacity.</summary>
        public static readonly Color COL_BG_PANEL = new Color(0.04f, 0.055f, 0.10f, 0.82f);

        /// <summary>Primary accent: cyan #00E5FF.</summary>
        public static readonly Color COL_ACCENT = new Color(0f, 0.898f, 1f, 1f);

        /// <summary>Accent at reduced opacity for mode tags, dividers.</summary>
        public static readonly Color COL_ACCENT_DIM = new Color(0f, 0.898f, 1f, 0.35f);

        /// <summary>Primary body text: light warm grey.</summary>
        public static readonly Color COL_TEXT = new Color(0.878f, 0.890f, 0.910f, 1f);

        /// <summary>Dimmed text for hints, secondary info.</summary>
        public static readonly Color COL_TEXT_DIM = new Color(1f, 1f, 1f, 0.30f);

        /// <summary>Warning/uncalibrated text.</summary>
        public static readonly Color COL_WARN = new Color(1f, 0.75f, 0.35f, 1f);

        /// <summary>Success feedback.</summary>
        public static readonly Color COL_SUCCESS = new Color(0.412f, 0.941f, 0.682f, 1f);

        /// <summary>Error feedback.</summary>
        public static readonly Color COL_ERROR = new Color(1f, 0.322f, 0.322f, 1f);

        /// <summary>Disabled / placeholder text.</summary>
        public static readonly Color COL_DISABLED = new Color(1f, 1f, 1f, 0.25f);

        /// <summary>Panel border (subtle white outline).</summary>
        public static readonly Color COL_BORDER = new Color(1f, 1f, 1f, 0.06f);

        /// <summary>Divider line inside panels.</summary>
        public static readonly Color COL_DIVIDER = new Color(1f, 1f, 1f, 0.08f);

        // Button states
        public static readonly Color COL_BTN_NORMAL = new Color(0f, 0.898f, 1f, 0.12f);
        public static readonly Color COL_BTN_HOVER  = new Color(0f, 0.898f, 1f, 0.25f);
        public static readonly Color COL_BTN_PRESS  = new Color(0f, 0.898f, 1f, 0.35f);
        public static readonly Color COL_BTN_DISABLED = new Color(0.1f, 0.1f, 0.15f, 0.4f);

        // -----------------------------------------------------------------
        // Typography
        // -----------------------------------------------------------------

        /// <summary>Panel title size (e.g. "Calibration", "Location").</summary>
        public const float FONT_TITLE = 14f;

        /// <summary>Body text size.</summary>
        public const float FONT_BODY = 13f;

        /// <summary>Small label / dim annotation size.</summary>
        public const float FONT_SMALL = 11f;

        /// <summary>Key hint label size (e.g. "WASD", "Tab").</summary>
        public const float FONT_KEY = 12f;

        /// <summary>Button label size.</summary>
        public const float FONT_BUTTON = 14f;

        // -----------------------------------------------------------------
        // Layout constants
        // -----------------------------------------------------------------

        /// <summary>Standard panel width for info panels (left column).</summary>
        public const float PANEL_WIDTH = 300f;

        /// <summary>Horizontal padding inside panels.</summary>
        public const float PAD_H = 14f;

        /// <summary>Vertical padding inside panels.</summary>
        public const float PAD_V = 10f;

        /// <summary>Gap between stacked panels.</summary>
        public const float PANEL_GAP = 8f;

        // -----------------------------------------------------------------
        // Factory helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Create a styled panel with background + outline, anchored to the
        /// given corner. Returns the panel GameObject with RectTransform,
        /// Image, and Outline already configured.
        /// </summary>
        public static GameObject CreatePanel(
            Transform parent,
            string name,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var bg = go.AddComponent<Image>();
            bg.color = COL_BG_PANEL;

            var outline = go.AddComponent<Outline>();
            outline.effectColor = COL_BORDER;
            outline.effectDistance = new Vector2(1f, 1f);

            return go;
        }

        /// <summary>
        /// Create a title TMP_Text inside a parent with standard title styling.
        /// </summary>
        public static TextMeshProUGUI CreateTitle(Transform parent, string text)
        {
            var go = new GameObject("Title");
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -PAD_V);
            rect.sizeDelta = new Vector2(-(PAD_H * 2), 20f);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = FONT_TITLE;
            tmp.color = COL_ACCENT;
            tmp.fontStyle = FontStyles.Bold | FontStyles.UpperCase;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;

            return tmp;
        }

        /// <summary>
        /// Create a body TMP_Text that fills most of the parent panel,
        /// below the title area.
        /// </summary>
        public static TextMeshProUGUI CreateBody(Transform parent)
        {
            var go = new GameObject("Body");
            go.transform.SetParent(parent, false);

            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(PAD_H, PAD_V);
            rect.offsetMax = new Vector2(-PAD_H, -32f); // leave room for title

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.fontSize = FONT_BODY;
            tmp.color = COL_TEXT;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.enableWordWrapping = true;
            tmp.raycastTarget = false;

            return tmp;
        }
    }
}

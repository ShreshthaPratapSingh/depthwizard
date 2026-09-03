// =============================================================================
// ImagePicker.cs
//
// Handles runtime file selection and image loading for the DepthWizard
// landing page. Uses StandaloneFileBrowser for a native OS file dialog
// (Windows), validates the selected file, and stores it in
// ImageSessionManager.
//
// Validation rules:
//   - Extension must be .png, .jpg, .jpeg, .tif, or .tiff
//   - File size must be ≤ 20MB
//   - Texture2D.LoadImage() must succeed (valid image data)
//
// Usage:
//   var picker = GetComponent<ImagePicker>();
//   picker.OpenFileBrowser();                    // opens native dialog
//   picker.LoadImageFromPath(@"C:\img.png");     // direct load
//
// Namespace: DepthWizard.UI
// =============================================================================

using System;
using System.IO;
using UnityEngine;
using SFB; // StandaloneFileBrowser

namespace DepthWizard.UI
{
    /// <summary>
    /// Runtime file picker and image loader. Opens a native file dialog,
    /// validates the selection, loads it as a Texture2D, and pushes it to
    /// <see cref="ImageSessionManager"/>.
    /// </summary>
    public class ImagePicker : MonoBehaviour
    {
        // Maximum file size in bytes (20 MB)
        private const long MAX_FILE_SIZE_BYTES = 20L * 1024 * 1024;

        // Supported extensions (lowercase, with dot)
        private static readonly string[] ALLOWED_EXTENSIONS =
            { ".png", ".jpg", ".jpeg", ".tif", ".tiff" };

        // File browser filter
        private static readonly ExtensionFilter[] FILE_FILTERS = new[]
        {
            new ExtensionFilter("Image Files", "png", "jpg", "jpeg", "tif", "tiff"),
            new ExtensionFilter("All Files", "*"),
        };

        // ---------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fired when an image is successfully loaded.
        /// string = status message (e.g. "Image loaded: 1920×1080").
        /// </summary>
        public event Action<string> OnSuccess;

        /// <summary>
        /// Fired when loading fails or validation rejects the file.
        /// string = error message for the UI.
        /// </summary>
        public event Action<string> OnError;

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Open a native file dialog filtered to PNG/JPG. If the user
        /// selects a file, it is validated and loaded automatically.
        /// </summary>
        public void OpenFileBrowser()
        {
            string[] paths;
            try
            {
                paths = StandaloneFileBrowser.OpenFilePanel(
                    title: "Select an Image",
                    directory: "",
                    extensions: FILE_FILTERS,
                    multiselect: false
                );
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ImagePicker] Native file dialog failed: {ex.Message}");
                OnError?.Invoke("File dialog unavailable on this OS. Use a demo sample button instead.");
                return;
            }

            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
            {
                Debug.Log("[ImagePicker] File dialog cancelled.");
                return;
            }

            LoadImageFromPath(paths[0]);
        }

        /// <summary>
        /// Load an image from an absolute file path. Validates type and size,
        /// creates a Texture2D, and stores it in <see cref="ImageSessionManager"/>.
        /// </summary>
        /// <param name="path">Absolute path to a PNG, JPG, or GeoTIFF file.</param>
        public void LoadImageFromPath(string path)
        {
            Debug.Log($"[ImagePicker] Loading: {path}");

            // --- Validate file exists ---
            if (!File.Exists(path))
            {
                ReportError($"File not found: {Path.GetFileName(path)}");
                return;
            }

            // --- Validate extension ---
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!IsAllowedExtension(ext))
            {
                ReportError(
                    $"Unsupported file type: {ext}\n" +
                    "Accepted formats: PNG, JPG, GeoTIFF");
                return;
            }

            // --- Validate file size ---
            long fileSize = new FileInfo(path).Length;
            if (fileSize > MAX_FILE_SIZE_BYTES)
            {
                float sizeMB = fileSize / (1024f * 1024f);
                ReportError(
                    $"File too large: {sizeMB:F1} MB\n" +
                    $"Maximum: {MAX_FILE_SIZE_BYTES / (1024 * 1024)} MB");
                return;
            }

            if (fileSize == 0)
            {
                ReportError("File is empty (0 bytes).");
                return;
            }

            // --- Read bytes ---
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                ReportError($"Failed to read file: {ex.Message}");
                return;
            }

            // --- Load texture (preview) ---
            // GeoTIFF (.tif/.tiff) cannot be decoded by Unity's
            // Texture2D.LoadImage() — skip the preview for those files.
            // The backend handles all real format processing.
            Texture2D texture = null;
            bool isGeoTiff = IsGeoTiff(ext);

            if (!isGeoTiff)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.filterMode = FilterMode.Bilinear;

                if (!texture.LoadImage(bytes))
                {
                    DestroyImmediate(texture);
                    ReportError("Failed to decode image data.\nThe file may be corrupted.");
                    return;
                }
            }

            // --- Success: store in session manager ---
            // For GeoTIFF, texture is null — UI should show filename instead of preview.
            ImageSessionManager.Instance.SetImage(bytes, path, texture);

            string msg;
            if (isGeoTiff)
            {
                msg = $"GeoTIFF loaded: {Path.GetFileName(path)}  " +
                      $"({fileSize / 1024}KB)";
            }
            else
            {
                msg = $"Image loaded: {texture.width}×{texture.height}  " +
                      $"({fileSize / 1024}KB)";
            }
            Debug.Log($"[ImagePicker] {msg}");
            OnSuccess?.Invoke(msg);
        }

        /// <summary>
        /// Called by DropZoneHandler when files are dropped.
        /// Tries to load the first valid image from the dropped paths.
        /// </summary>
        /// <param name="paths">Array of dropped file paths.</param>
        public void TryLoadFromDrop(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;

            // Try the first file; if there are multiple, just use the first
            LoadImageFromPath(paths[0]);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private bool IsAllowedExtension(string ext)
        {
            foreach (var allowed in ALLOWED_EXTENSIONS)
            {
                if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsGeoTiff(string ext)
        {
            return string.Equals(ext, ".tif", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private void ReportError(string message)
        {
            Debug.LogWarning($"[ImagePicker] {message}");
            OnError?.Invoke(message);
        }
    }
}

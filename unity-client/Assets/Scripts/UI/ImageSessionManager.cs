// =============================================================================
// ImageSessionManager.cs
//
// Singleton that persists selected image data across Unity scenes.
// The landing page stores the user's chosen image here, and the terrain
// generation scene (SampleScene) reads it to send to the backend.
//
// Lives as a DontDestroyOnLoad GameObject, auto-created on first access.
//
// Usage:
//   // Store (called by ImagePicker when a file is loaded):
//   ImageSessionManager.Instance.SetImage(bytes, path, texture);
//
//   // Read (called by BackendClient in the next scene):
//   byte[] data = ImageSessionManager.Instance.ImageBytes;
//   string path = ImageSessionManager.Instance.FilePath;
//
// Namespace: DepthWizard.UI
// =============================================================================

using System;
using UnityEngine;

namespace DepthWizard.UI
{
    /// <summary>
    /// Persistent singleton holding the user-selected image across scenes.
    /// Auto-creates itself if no instance exists when first accessed.
    /// </summary>
    public class ImageSessionManager : MonoBehaviour
    {
        // ---------------------------------------------------------------------
        // Singleton
        // ---------------------------------------------------------------------

        private static ImageSessionManager _instance;
        private static readonly object _lock = new object();
        private static bool _quitting;

        /// <summary>
        /// Reset static state when entering Play mode. Required because
        /// Unity may not perform a domain reload between Play sessions
        /// (Enter Play Mode Options), which leaves _quitting = true.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
            _quitting = false;
        }

        /// <summary>
        /// Global singleton instance. Creates itself on first access.
        /// </summary>
        public static ImageSessionManager Instance
        {
            get
            {
                if (_quitting)
                    return null;

                lock (_lock)
                {
                    if (_instance == null)
                    {
                        // Try to find an existing instance in the scene
                        _instance = FindFirstObjectByType<ImageSessionManager>();

                        if (_instance == null)
                        {
                            // Auto-create a new persistent GameObject
                            var go = new GameObject("[ImageSessionManager]");
                            _instance = go.AddComponent<ImageSessionManager>();
                            DontDestroyOnLoad(go);
                            Debug.Log("[ImageSessionManager] Auto-created singleton.");
                        }
                    }
                    return _instance;
                }
            }
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------

        /// <summary>Raw file bytes — ready to upload to the backend.</summary>
        public byte[] ImageBytes { get; private set; }

        /// <summary>Original absolute file path on disk.</summary>
        public string FilePath { get; private set; }

        /// <summary>Loaded Texture2D for UI preview (may be null if cleared).</summary>
        public Texture2D PreviewTexture { get; private set; }

        /// <summary>Image width in pixels.</summary>
        public int Width { get; private set; }

        /// <summary>Image height in pixels.</summary>
        public int Height { get; private set; }

        /// <summary>True when a valid image is currently loaded.</summary>
        public bool HasImage => ImageBytes != null && ImageBytes.Length > 0;

        // ---------------------------------------------------------------------
        // Events
        // ---------------------------------------------------------------------

        /// <summary>
        /// Fired when a new image is successfully loaded.
        /// Subscribers receive the preview texture.
        /// </summary>
        public event Action<Texture2D> OnImageLoaded;

        /// <summary>Fired when the session is cleared.</summary>
        public event Action OnImageCleared;

        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Store a newly selected image. Called by ImagePicker after
        /// successful validation and texture creation.
        /// </summary>
        /// <param name="bytes">Raw file bytes (PNG/JPG).</param>
        /// <param name="path">Absolute file path.</param>
        /// <param name="texture">Loaded Texture2D preview.</param>
        public void SetImage(byte[] bytes, string path, Texture2D texture)
        {
            // Clean up previous texture if we own it
            if (PreviewTexture != null && PreviewTexture != texture)
            {
                Destroy(PreviewTexture);
            }

            ImageBytes = bytes;
            FilePath = path;
            PreviewTexture = texture;
            Width = texture != null ? texture.width : 0;
            Height = texture != null ? texture.height : 0;

            Debug.Log(
                $"[ImageSessionManager] Image set: {path} " +
                $"({Width}×{Height}, {bytes.Length / 1024}KB)");

            OnImageLoaded?.Invoke(texture);
        }

        /// <summary>
        /// Reset all state. Call when returning to the landing page or
        /// starting a new session.
        /// </summary>
        public void Clear()
        {
            if (PreviewTexture != null)
            {
                Destroy(PreviewTexture);
            }

            ImageBytes = null;
            FilePath = null;
            PreviewTexture = null;
            Width = 0;
            Height = 0;

            Debug.Log("[ImageSessionManager] Session cleared.");
            OnImageCleared?.Invoke();
        }

        // ---------------------------------------------------------------------
        // MonoBehaviour lifecycle
        // ---------------------------------------------------------------------

        private void Awake()
        {
            // Enforce singleton: if another instance exists, destroy this one
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning(
                    "[ImageSessionManager] Duplicate instance destroyed.");
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnApplicationQuit()
        {
            _quitting = true;
        }
    }
}

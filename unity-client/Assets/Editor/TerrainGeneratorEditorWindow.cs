// =============================================================================
// TerrainGeneratorEditorWindow.cs
//
// Unity Editor window for DepthWizard terrain generation.
// Accessible via: Window > DepthWizard > Terrain Generator
//
// Provides a file picker to select a GeoTIFF DEM, preview its metadata,
// and generate a Unity Terrain from it with a single button click.
//
// REPLACEMENT POINT: When the FastAPI backend pipeline is wired in, this
// window can be updated to:
//   1. Call the backend endpoint instead of reading a local GeoTIFF.
//   2. Receive a pre-processed 16-bit PNG heightmap.
//   3. Feed that PNG to a simplified loader that skips GeoTIFF parsing,
//      reusing the same normalization + terrain-creation logic below.
// =============================================================================

#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;
using DepthWizard.Terrain;

namespace DepthWizard.Editor
{
    public class TerrainGeneratorEditorWindow : EditorWindow
    {
        // --- Configuration ---
        private string _geoTiffPath = "";
        private float _terrainWidth = 500f;
        private float _terrainLength = 500f;
        private bool _autoHeightScale = true;
        private float _manualHeightScale = 200f;

        // --- State ---
        private HeightmapData _lastResult;
        private string _errorMessage;
        private bool _isGenerating;
        private Vector2 _scrollPos;

        // --- Styles (lazy-init) ---
        private GUIStyle _headerStyle;
        private GUIStyle _boxStyle;
        private GUIStyle _pathLabelStyle;
        private bool _stylesInitialized;

        [MenuItem("Window/DepthWizard/Terrain Generator")]
        public static void ShowWindow()
        {
            var window = GetWindow<TerrainGeneratorEditorWindow>("DepthWizard Terrain");
            window.minSize = new Vector2(420, 520);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleCenter,
                margin = new RectOffset(0, 0, 8, 8)
            };

            _boxStyle = new GUIStyle("HelpBox")
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(4, 4, 4, 4)
            };

            _pathLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                wordWrap = true,
                richText = true
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            // Entire OnGUI body is wrapped in try/finally to guarantee
            // EndScrollView is always called, even if an exception is thrown
            // mid-layout by any GUI control or callback.
            _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);
            try
            {
                DrawWindowContents();
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }

        /// <summary>
        /// All window content drawn here, inside the scroll view's try/finally.
        /// No layout-affecting calls (BeginVertical/BeginHorizontal) straddle
        /// any code path that could throw or return early.
        /// </summary>
        private void DrawWindowContents()
        {
            // ── Header ──────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("🛰  DepthWizard Terrain Generator", _headerStyle);
            DrawSeparator();

            // ── File Selection ──────────────────────────────────────────
            EditorGUILayout.BeginVertical(_boxStyle);
            try
            {
                EditorGUILayout.LabelField("GeoTIFF Source", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);

                EditorGUILayout.BeginHorizontal();
                try
                {
                    EditorGUILayout.LabelField("File:", GUILayout.Width(30));

                    string displayPath = string.IsNullOrEmpty(_geoTiffPath)
                        ? "<i>No file selected</i>"
                        : _geoTiffPath;
                    EditorGUILayout.LabelField(displayPath, _pathLabelStyle);

                    if (GUILayout.Button("Browse…", GUILayout.Width(80)))
                    {
                        string path = EditorUtility.OpenFilePanel(
                            "Select GeoTIFF DEM",
                            Application.dataPath,
                            "tif,tiff,geotiff");

                        if (!string.IsNullOrEmpty(path))
                        {
                            _geoTiffPath = path;
                            _lastResult = null;
                            _errorMessage = null;
                        }
                    }
                }
                finally
                {
                    EditorGUILayout.EndHorizontal();
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(4);

            // ── Terrain Settings ────────────────────────────────────────
            EditorGUILayout.BeginVertical(_boxStyle);
            try
            {
                EditorGUILayout.LabelField("Terrain Settings", EditorStyles.boldLabel);
                EditorGUILayout.Space(2);

                _terrainWidth = EditorGUILayout.FloatField("Width (m)", _terrainWidth);
                _terrainLength = EditorGUILayout.FloatField("Length (m)", _terrainLength);

                EditorGUILayout.Space(2);
                _autoHeightScale = EditorGUILayout.Toggle("Auto Height Scale", _autoHeightScale);

                if (!_autoHeightScale)
                {
                    _manualHeightScale = EditorGUILayout.FloatField("Height Scale (m)",
                        _manualHeightScale);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Height scale will be set to the elevation range found " +
                        "in the GeoTIFF (max − min).",
                        MessageType.Info);
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(8);

            // ── Generate Button ─────────────────────────────────────────
            // Button is disabled while a deferred generation is in progress,
            // or if no file is selected.
            GUI.enabled = !string.IsNullOrEmpty(_geoTiffPath) && !_isGenerating;
            Color prevBg = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.5f);

            if (GUILayout.Button(
                _isGenerating ? "⏳  Generating…" : "▶  Generate Terrain",
                GUILayout.Height(36)))
            {
                // Defer terrain generation to run OUTSIDE the OnGUI layout
                // pass. This prevents DisplayProgressBar / DisplayDialog from
                // pumping the message loop and triggering a re-entrant OnGUI
                // call that corrupts the layout group stack.
                _isGenerating = true;
                _errorMessage = null;
                EditorApplication.delayCall += GenerateTerrain;
            }

            GUI.backgroundColor = prevBg;
            GUI.enabled = true;

            // ── Error Display ───────────────────────────────────────────
            if (!string.IsNullOrEmpty(_errorMessage))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(_errorMessage, MessageType.Error);
            }

            // ── Result Preview ──────────────────────────────────────────
            if (_lastResult != null)
            {
                EditorGUILayout.Space(8);
                DrawSeparator();

                EditorGUILayout.BeginVertical(_boxStyle);
                try
                {
                    EditorGUILayout.LabelField("Last Import Results", EditorStyles.boldLabel);
                    EditorGUILayout.Space(2);

                    EditorGUILayout.LabelField(
                        $"Original size:       {_lastResult.OriginalWidth} × {_lastResult.OriginalHeight} px");
                    EditorGUILayout.LabelField(
                        $"Resampled to:        {_lastResult.ResampledResolution} × {_lastResult.ResampledResolution} px");
                    EditorGUILayout.LabelField(
                        $"Elevation min:       {_lastResult.MinElevation:F2} m");
                    EditorGUILayout.LabelField(
                        $"Elevation max:       {_lastResult.MaxElevation:F2} m");
                    EditorGUILayout.LabelField(
                        $"Elevation range:     {(_lastResult.MaxElevation - _lastResult.MinElevation):F2} m");
                }
                finally
                {
                    EditorGUILayout.EndVertical();
                }
            }
        }

        // -----------------------------------------------------------------
        // Terrain generation pipeline
        // -----------------------------------------------------------------

        /// <summary>
        /// Called via EditorApplication.delayCall, so this runs OUTSIDE the
        /// OnGUI layout pass. Safe to use DisplayProgressBar/DisplayDialog.
        /// </summary>
        private void GenerateTerrain()
        {
            try
            {
                // Step 1: Read & process GeoTIFF
                EditorUtility.DisplayProgressBar("DepthWizard", "Reading GeoTIFF…", 0.1f);

                HeightmapData data = GeoTiffHeightmapReader.Read(_geoTiffPath);

                if (data == null)
                {
                    _errorMessage = "Failed to read the GeoTIFF file. Check the Console for details.";
                    return;
                }

                _lastResult = data;
                _errorMessage = null;

                // Step 2: Build TerrainData
                // IMPORTANT: Property assignment order matters for Unity's
                // internal heightmap state. The correct sequence is:
                //   1. heightmapResolution  (allocates internal heightmap)
                //   2. SetHeights           (writes data into allocated map)
                //   3. size                 (scales the terrain geometry)
                // Setting 'size' before SetHeights or using an object
                // initializer can leave the collider's internal heightmap in
                // an inconsistent state that causes NullReferenceException in
                // TerrainInspector.Raycast().
                EditorUtility.DisplayProgressBar("DepthWizard", "Building terrain…", 0.5f);

                float heightScale = _autoHeightScale
                    ? (data.MaxElevation - data.MinElevation)
                    : _manualHeightScale;

                // Clamp to sane minimum so terrain isn't invisible
                heightScale = Mathf.Max(heightScale, 1f);

                int resolution = data.ResampledResolution;

                TerrainData terrainData = new TerrainData();

                // 1. Set resolution first — this allocates the internal heightmap
                terrainData.heightmapResolution = resolution;

                // Read back the actual resolution Unity applied (it clamps to
                // the nearest valid 2^n+1 value internally)
                int actualResolution = terrainData.heightmapResolution;

                // Log array dimensions vs resolution for diagnostic verification
                int heightsRows = data.Heights.GetLength(0);
                int heightsCols = data.Heights.GetLength(1);

                Debug.Log($"[DepthWizard] Heightmap diagnostics:\n" +
                          $"  Requested resolution:  {resolution}\n" +
                          $"  Actual resolution:     {actualResolution}\n" +
                          $"  Heights array:         [{heightsRows}, {heightsCols}]");

                if (heightsRows != actualResolution || heightsCols != actualResolution)
                {
                    Debug.LogError($"[DepthWizard] Heights array dimensions [{heightsRows}, {heightsCols}] " +
                                   $"do not match terrain heightmapResolution {actualResolution}. " +
                                   $"Aborting to prevent invalid terrain state.");
                    _errorMessage = $"Heights array [{heightsRows}×{heightsCols}] does not match " +
                                    $"terrain resolution {actualResolution}. Check GeoTIFF dimensions.";
                    return;
                }

                // 2. Write height data into the allocated heightmap
                terrainData.SetHeights(0, 0, data.Heights);

                // 3. Set terrain size AFTER heights are written
                terrainData.size = new Vector3(_terrainWidth, heightScale, _terrainLength);

                // Step 3: Save TerrainData as a persistent asset BEFORE
                // assigning to any components. TerrainInspector.Raycast()
                // requires a valid, persisted TerrainData asset.
                EditorUtility.DisplayProgressBar("DepthWizard", "Saving terrain data…", 0.7f);

                string assetDir = "Assets/TerrainData";
                if (!AssetDatabase.IsValidFolder(assetDir))
                {
                    AssetDatabase.CreateFolder("Assets", "TerrainData");
                }

                string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                    $"{assetDir}/DepthWizard_TerrainData.asset");
                AssetDatabase.CreateAsset(terrainData, assetPath);
                AssetDatabase.SaveAssets();

                Debug.Log($"[DepthWizard] TerrainData saved to: {assetPath}");

                // Step 4: Create or update Terrain GameObject
                // We build the GameObject manually (instead of using
                // Terrain.CreateTerrainGameObject) to have explicit control
                // over component creation order and terrainData assignment.
                EditorUtility.DisplayProgressBar("DepthWizard", "Creating terrain object…", 0.85f);

                GameObject existingGo = GameObject.Find("DepthWizard_Terrain");

                if (existingGo != null)
                {
                    // --- Update existing terrain ---
                    // Assign terrainData to Terrain component
                    var existingTerrain = existingGo.GetComponent<UnityEngine.Terrain>();
                    if (existingTerrain != null)
                    {
                        existingTerrain.terrainData = terrainData;
                    }

                    // Assign the SAME terrainData to TerrainCollider
                    var existingCollider = existingGo.GetComponent<TerrainCollider>();
                    if (existingCollider == null)
                    {
                        existingCollider = existingGo.AddComponent<TerrainCollider>();
                    }
                    existingCollider.terrainData = terrainData;

                    // Force Unity to rebuild internal collision state
                    existingCollider.enabled = false;
                    existingCollider.enabled = true;

                    Undo.RegisterCompleteObjectUndo(existingGo, "Update DepthWizard Terrain");
                    Debug.Log("[DepthWizard] Updated existing terrain.");
                }
                else
                {
                    // --- Create new terrain manually ---
                    GameObject terrainGo = new GameObject("DepthWizard_Terrain");

                    // Add Terrain component and assign terrainData
                    var terrain = terrainGo.AddComponent<UnityEngine.Terrain>();
                    terrain.terrainData = terrainData;

                    // Add TerrainCollider and assign the EXACT SAME terrainData
                    var collider = terrainGo.AddComponent<TerrainCollider>();
                    collider.terrainData = terrainData;

                    // Force Unity to rebuild internal collision state
                    collider.enabled = false;
                    collider.enabled = true;

                    Undo.RegisterCreatedObjectUndo(terrainGo, "Create DepthWizard Terrain");

                    // Verify both components reference the same TerrainData
                    Debug.Log($"[DepthWizard] Component verification:\n" +
                              $"  Terrain.terrainData:        {(terrain.terrainData != null ? terrain.terrainData.name : "NULL")}\n" +
                              $"  TerrainCollider.terrainData: {(collider.terrainData != null ? collider.terrainData.name : "NULL")}\n" +
                              $"  Same reference:             {ReferenceEquals(terrain.terrainData, collider.terrainData)}");

                    // Defer selection to the next editor frame so Unity's
                    // TerrainInspector has time to initialize before Raycast()
                    // is invoked by the selection change.
                    var goToSelect = terrainGo;
                    EditorApplication.delayCall += () =>
                    {
                        if (goToSelect != null)
                        {
                            Selection.activeGameObject = goToSelect;
                        }
                    };

                    Debug.Log("[DepthWizard] Created new terrain.");
                }

                Debug.Log($"[DepthWizard] ✓ Terrain generated successfully.\n" +
                          $"  Dimensions: {_terrainWidth} × {heightScale:F1} × {_terrainLength} m\n" +
                          $"  Heightmap:  {actualResolution}×{actualResolution}\n" +
                          $"  Elevation:  {data.MinElevation:F2} – {data.MaxElevation:F2} m");

                // NOTE: We intentionally do NOT call SceneView.FrameSelected()
                // here. Unity's internal TerrainInspector.Raycast() throws a
                // NullReferenceException when FrameSelected is invoked
                // programmatically on a Terrain object. The user can press F
                // in the Scene view to frame the terrain manually.
            }
            catch (System.Exception ex)
            {
                _errorMessage = $"Terrain generation failed: {ex.Message}";
                Debug.LogError($"[DepthWizard] Terrain generation failed.\n{ex}");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                _isGenerating = false;
                Repaint(); // Refresh the window to show results or errors
            }
        }

        // -----------------------------------------------------------------
        // UI Helpers
        // -----------------------------------------------------------------

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(2);
            Rect rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(2);
        }
    }
}

#endif

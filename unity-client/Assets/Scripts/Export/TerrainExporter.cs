// =============================================================================
// TerrainExporter.cs — PRD FR11: Export Generated Mesh / Heightmap
//
// Provides runtime export of the currently generated DepthWizard terrain in
// two formats:
//   1. Wavefront OBJ mesh  — full textured mesh (OBJ + MTL + diffuse PNG),
//      importable in Blender, MeshLab, 3ds Max, etc.
//   2. 16-bit grayscale PNG — raw elevation data for external GIS/DEM tools.
//
// This script is fully self-contained: it reads from the existing Terrain and
// TerrainData at export time and does NOT modify BackendClient.cs,
// RuntimeTerrainBuilder.cs, DroneController.cs, CameraModeController.cs, or
// any backend/ files.
//
// Triggering: Currently called via direct method invocation or a temporary test
// button. A polished export UI (button, file-save dialog) is the Frontend/UI
// Dev's separate work.
// =============================================================================

using System;
using System.IO;
using System.Text;
using UnityEngine;


namespace DepthWizard.Export
{
    /// <summary>
    /// Exports the active Unity Terrain as an OBJ mesh file (with MTL + texture)
    /// or as a 16-bit heightmap PNG.  Attach to any GameObject, or call the
    /// static convenience methods directly from code / a test button.
    /// </summary>
    public class TerrainExporter : MonoBehaviour
    {
        // =====================================================================
        // Configurable fields (visible in Inspector)
        // =====================================================================

        [Header("OBJ Export Settings")]

        [Tooltip("Use every Nth vertex along each axis when building the OBJ mesh. " +
                 "Higher values = smaller file, less detail.  1 = full resolution.")]
        [SerializeField, Range(1, 32)]
        private int exportResolutionStep = 4;

        [Tooltip("Optional explicit Terrain reference. If left null the exporter " +
                 "will use Terrain.activeTerrain at export time.")]
        [SerializeField]
        private UnityEngine.Terrain targetTerrain;

        // =====================================================================
        // Runtime key trigger (E key exports both OBJ + heightmap PNG)
        // =====================================================================

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.E))
            {
                ExportAll();
            }
        }

        /// <summary>
        /// Export both OBJ mesh and 16-bit heightmap PNG to the Exports folder.
        /// </summary>
        public void ExportAll()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string exportDir = Path.Combine(Application.persistentDataPath, "Exports");

            string objPath = Path.Combine(exportDir, $"terrain_{timestamp}.obj");
            string pngPath = Path.Combine(exportDir, $"heightmap_{timestamp}.png");

            UnityEngine.Terrain terrain = targetTerrain != null
                ? targetTerrain : UnityEngine.Terrain.activeTerrain;

            if (!ValidateTerrain(terrain)) return;

            bool objOk = ExportTerrainToObj(terrain, objPath, exportResolutionStep);
            bool pngOk = ExportHeightmapToPng(terrain, pngPath);

            if (objOk || pngOk)
            {
                Debug.Log($"[TerrainExporter] Export complete. Output: {exportDir}");
            }
        }

        /// <summary>
        /// Export the currently active terrain as a Wavefront OBJ file (+ .mtl
        /// + diffuse texture PNG) at <paramref name="outputPath"/>.
        /// </summary>
        /// <param name="outputPath">
        /// Full path for the .obj file, e.g. "C:/Exports/terrain_export.obj".
        /// The .mtl and .png files are written alongside it automatically.
        /// </param>
        /// <param name="resolutionStep">
        /// Vertex decimation step (default 4 = every 4th sample).
        /// </param>
        /// <returns>True on success, false on failure (error is logged).</returns>
        public static bool ExportCurrentTerrainAsObj(string outputPath, int resolutionStep = 4)
        {
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (!ValidateTerrain(terrain)) return false;
            return ExportTerrainToObj(terrain, outputPath, resolutionStep);
        }

        /// <summary>
        /// Export the currently active terrain's heightmap as a 16-bit
        /// grayscale PNG at <paramref name="outputPath"/>.
        /// </summary>
        /// <returns>True on success, false on failure (error is logged).</returns>
        public static bool ExportCurrentTerrainAsHeightmapPng(string outputPath)
        {
            UnityEngine.Terrain terrain = UnityEngine.Terrain.activeTerrain;
            if (!ValidateTerrain(terrain)) return false;
            return ExportHeightmapToPng(terrain, outputPath);
        }

        // =====================================================================
        // Instance methods (for Inspector button / UnityEvent wiring)
        // =====================================================================

        /// <summary>
        /// Instance wrapper that uses the Inspector-assigned terrain (or
        /// activeTerrain) and resolution step.
        /// </summary>
        public bool ExportObj(string outputPath)
        {
            UnityEngine.Terrain terrain = targetTerrain != null ? targetTerrain : UnityEngine.Terrain.activeTerrain;
            if (!ValidateTerrain(terrain)) return false;
            return ExportTerrainToObj(terrain, outputPath, exportResolutionStep);
        }

        /// <summary>
        /// Instance wrapper for heightmap export.
        /// </summary>
        public bool ExportHeightmap(string outputPath)
        {
            UnityEngine.Terrain terrain = targetTerrain != null ? targetTerrain : UnityEngine.Terrain.activeTerrain;
            if (!ValidateTerrain(terrain)) return false;
            return ExportHeightmapToPng(terrain, outputPath);
        }

        // =====================================================================
        // Core OBJ export
        // =====================================================================

        /// <summary>
        /// Convert a Terrain's heightmap into an OBJ mesh file with an
        /// accompanying MTL material file and diffuse texture PNG.
        /// </summary>
        public static bool ExportTerrainToObj(UnityEngine.Terrain terrain, string outputPath, int step = 4)
        {
            if (!ValidateTerrain(terrain)) return false;

            step = Mathf.Max(1, step);

            try
            {
                UnityEngine.TerrainData td = terrain.terrainData;
                Vector3 terrainPos = terrain.transform.position;
                Vector3 terrainSize = td.size; // (width, height, length)
                int hmWidth = td.heightmapResolution;
                int hmHeight = td.heightmapResolution;
                float[,] heights = td.GetHeights(0, 0, hmWidth, hmHeight);

                // Compute grid dimensions after decimation
                int gridW = (hmWidth - 1) / step + 1;
                int gridH = (hmHeight - 1) / step + 1;

                Debug.Log(
                    $"[TerrainExporter] Exporting OBJ: heightmap {hmWidth}×{hmHeight}, " +
                    $"step {step} → grid {gridW}×{gridH} " +
                    $"({gridW * gridH} verts, {(gridW - 1) * (gridH - 1) * 2} tris)");

                // ----- Derive sibling file paths -----
                string dir = Path.GetDirectoryName(outputPath);
                string baseName = Path.GetFileNameWithoutExtension(outputPath);
                string mtlPath = Path.Combine(dir, baseName + ".mtl");
                string texPath = Path.Combine(dir, baseName + ".png");
                string mtlFileName = baseName + ".mtl";
                string texFileName = baseName + ".png";

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // ----- Write OBJ -----
                using (StreamWriter obj = new StreamWriter(outputPath, false, new UTF8Encoding(false)))
                {
                    obj.WriteLine("# Wavefront OBJ exported by DepthWizard TerrainExporter");
                    obj.WriteLine($"# Source terrain: {terrain.name}");
                    obj.WriteLine($"# Heightmap resolution: {hmWidth}x{hmHeight}");
                    obj.WriteLine($"# Decimation step: {step}  →  grid: {gridW}x{gridH}");
                    obj.WriteLine($"# Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    obj.WriteLine();
                    obj.WriteLine($"mtllib {mtlFileName}");
                    obj.WriteLine();
                    obj.WriteLine("o DepthWizard_Terrain");
                    obj.WriteLine();

                    // --- Vertices (v) ---
                    for (int gy = 0; gy < gridH; gy++)
                    {
                        int hy = Mathf.Min(gy * step, hmHeight - 1);
                        for (int gx = 0; gx < gridW; gx++)
                        {
                            int hx = Mathf.Min(gx * step, hmWidth - 1);

                            // TerrainData.GetHeights returns [y, x] in 0-1 range.
                            // World position:
                            //   X = terrainPos.x + (hx / (hmWidth-1))  * terrainSize.x
                            //   Y = terrainPos.y + heights[hy, hx]     * terrainSize.y
                            //   Z = terrainPos.z + (hy / (hmHeight-1)) * terrainSize.z
                            float worldX = terrainPos.x + ((float)hx / (hmWidth - 1)) * terrainSize.x;
                            float worldY = terrainPos.y + heights[hy, hx] * terrainSize.y;
                            float worldZ = terrainPos.z + ((float)hy / (hmHeight - 1)) * terrainSize.z;

                            obj.WriteLine($"v {worldX:F6} {worldY:F6} {worldZ:F6}");
                        }
                    }

                    obj.WriteLine();

                    // --- Texture coordinates (vt) ---
                    for (int gy = 0; gy < gridH; gy++)
                    {
                        float v = (float)(gy * step) / (hmHeight - 1);
                        v = Mathf.Clamp01(v);
                        for (int gx = 0; gx < gridW; gx++)
                        {
                            float u = (float)(gx * step) / (hmWidth - 1);
                            u = Mathf.Clamp01(u);
                            obj.WriteLine($"vt {u:F6} {v:F6}");
                        }
                    }

                    obj.WriteLine();
                    obj.WriteLine("usemtl terrain_material");
                    obj.WriteLine();

                    // --- Faces (f) ---
                    // Two triangles per quad.  OBJ indices are 1-based.
                    for (int gy = 0; gy < gridH - 1; gy++)
                    {
                        for (int gx = 0; gx < gridW - 1; gx++)
                        {
                            //  TL --- TR
                            //  |  \   |
                            //  |   \  |
                            //  BL --- BR
                            int tl = gy * gridW + gx + 1;       // +1 for OBJ 1-based
                            int tr = tl + 1;
                            int bl = (gy + 1) * gridW + gx + 1;
                            int br = bl + 1;

                            // Triangle 1: TL → BL → TR
                            obj.WriteLine($"f {tl}/{tl} {bl}/{bl} {tr}/{tr}");
                            // Triangle 2: TR → BL → BR
                            obj.WriteLine($"f {tr}/{tr} {bl}/{bl} {br}/{br}");
                        }
                    }
                }

                // ----- Write MTL -----
                using (StreamWriter mtl = new StreamWriter(mtlPath, false, new UTF8Encoding(false)))
                {
                    mtl.WriteLine("# Material exported by DepthWizard TerrainExporter");
                    mtl.WriteLine();
                    mtl.WriteLine("newmtl terrain_material");
                    mtl.WriteLine("Ka 1.000 1.000 1.000");   // ambient
                    mtl.WriteLine("Kd 1.000 1.000 1.000");   // diffuse
                    mtl.WriteLine("Ks 0.000 0.000 0.000");   // specular (none)
                    mtl.WriteLine("Ns 10.000");               // shininess
                    mtl.WriteLine("d 1.000");                 // opacity
                    mtl.WriteLine("illum 1");                 // flat illumination model
                    mtl.WriteLine($"map_Kd {texFileName}");   // diffuse texture map
                }

                // ----- Write diffuse texture PNG -----
                bool textureExported = ExportDiffuseTexture(td, texPath);
                if (!textureExported)
                {
                    Debug.LogWarning(
                        "[TerrainExporter] No diffuse texture found on the terrain. " +
                        "OBJ + MTL written but texture PNG is missing — the mesh will " +
                        "import untextured in external software.");
                }

                Debug.Log(
                    $"[TerrainExporter] ✓ OBJ export complete!\n" +
                    $"  OBJ : {outputPath}\n" +
                    $"  MTL : {mtlPath}\n" +
                    $"  TEX : {(textureExported ? texPath : "(none)")}");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TerrainExporter] OBJ export failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // =====================================================================
        // Core heightmap PNG export (16-bit grayscale)
        // =====================================================================

        /// <summary>
        /// Export the terrain's heightmap as a 16-bit grayscale PNG.
        /// The PNG is encoded using Unity's built-in PNG encoder at the
        /// maximum available precision (R16 or RFloat → 16-bit).
        /// </summary>
        public static bool ExportHeightmapToPng(UnityEngine.Terrain terrain, string outputPath)
        {
            if (!ValidateTerrain(terrain)) return false;

            try
            {
                UnityEngine.TerrainData td = terrain.terrainData;
                int res = td.heightmapResolution;
                float[,] heights = td.GetHeights(0, 0, res, res);

                // Create a 16-bit single-channel texture
                // R16 stores each pixel as a ushort (0–65535).
                Texture2D tex = new Texture2D(res, res, TextureFormat.R16, false);
                byte[] rawData = new byte[res * res * 2]; // 2 bytes per pixel

                for (int y = 0; y < res; y++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        // GetHeights returns [y, x] with y=0 at terrain "top" (north).
                        // We write bottom-to-top for standard image orientation.
                        int srcY = res - 1 - y;
                        ushort val = (ushort)(Mathf.Clamp01(heights[srcY, x]) * 65535f);

                        int idx = (y * res + x) * 2;
                        rawData[idx] = (byte)(val & 0xFF);        // low byte
                        rawData[idx + 1] = (byte)(val >> 8);      // high byte (little-endian)
                    }
                }

                tex.LoadRawTextureData(rawData);
                tex.Apply();

                byte[] pngBytes = tex.EncodeToPNG();
                Destroy(tex);

                if (pngBytes == null || pngBytes.Length == 0)
                {
                    // Fallback: R16 EncodeToPNG isn't supported on all platforms.
                    // Use RGBA32 and pack 16-bit value across R+G channels.
                    Debug.LogWarning(
                        "[TerrainExporter] R16 EncodeToPNG not supported; falling " +
                        "back to RGBA32 encoding (height packed in R+G channels).");
                    pngBytes = EncodeHeightmapAsRGBA32(heights, res);
                }

                string dir = Path.GetDirectoryName(outputPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(outputPath, pngBytes);

                Debug.Log(
                    $"[TerrainExporter] ✓ Heightmap PNG exported: {outputPath} " +
                    $"({res}×{res}, {pngBytes.Length / 1024} KB)");

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TerrainExporter] Heightmap export failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        /// <summary>
        /// Validate that a terrain reference is usable.
        /// </summary>
        private static bool ValidateTerrain(UnityEngine.Terrain terrain)
        {
            if (terrain == null)
            {
                Debug.LogError(
                    "[TerrainExporter] No terrain found. Generate a terrain first " +
                    "before exporting. (Terrain.activeTerrain is null)");
                return false;
            }

            if (terrain.terrainData == null)
            {
                Debug.LogError(
                    "[TerrainExporter] Terrain exists but has no TerrainData. " +
                    "This should not happen — the terrain may be corrupt.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Export the first TerrainLayer's diffuseTexture as a PNG file.
        /// </summary>
        private static bool ExportDiffuseTexture(UnityEngine.TerrainData td, string outputPath)
        {
            if (td.terrainLayers == null || td.terrainLayers.Length == 0)
                return false;

            Texture2D diffuse = td.terrainLayers[0].diffuseTexture;
            if (diffuse == null)
                return false;

            try
            {
                // The source texture may not be readable (GPU-only).  Create a
                // readable copy via a temporary RenderTexture blit.
                RenderTexture rt = RenderTexture.GetTemporary(
                    diffuse.width, diffuse.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(diffuse, rt);

                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;

                Texture2D readableTex = new Texture2D(
                    diffuse.width, diffuse.height, TextureFormat.RGBA32, false);
                readableTex.ReadPixels(
                    new Rect(0, 0, diffuse.width, diffuse.height), 0, 0);
                readableTex.Apply();

                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);

                byte[] pngBytes = readableTex.EncodeToPNG();
                Destroy(readableTex);

                if (pngBytes == null || pngBytes.Length == 0)
                    return false;

                File.WriteAllBytes(outputPath, pngBytes);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[TerrainExporter] Could not export diffuse texture: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Fallback: encode heightmap into an RGBA32 PNG with the 16-bit height
        /// packed across R (high byte) and G (low byte) channels.  B=0, A=255.
        /// </summary>
        private static byte[] EncodeHeightmapAsRGBA32(float[,] heights, int res)
        {
            Texture2D tex = new Texture2D(res, res, TextureFormat.RGBA32, false);

            for (int y = 0; y < res; y++)
            {
                int srcY = res - 1 - y; // flip for standard image orientation
                for (int x = 0; x < res; x++)
                {
                    ushort val = (ushort)(Mathf.Clamp01(heights[srcY, x]) * 65535f);
                    float r = (val >> 8) / 255f;    // high byte
                    float g = (val & 0xFF) / 255f;  // low byte
                    tex.SetPixel(x, y, new Color(r, g, 0f, 1f));
                }
            }

            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            Destroy(tex);
            return png;
        }
    }
}

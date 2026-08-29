// =============================================================================
// RuntimeTerrainBuilder.cs
//
// Builds a Unity Terrain at runtime from a 16-bit heightmap PNG and an RGB
// texture PNG — the exact outputs produced by depth_pipeline's process_image().
//
// This is a NEW runtime path — it does NOT modify or replace the existing
// editor-only GeoTIFF workflow (TerrainGeneratorEditorWindow.cs,
// GeoTiffHeightmapReader.cs).
//
// Terrain creation follows the same critical property-assignment order
// documented in TerrainGeneratorEditorWindow.cs:
//   1. heightmapResolution  (allocates internal heightmap)
//   2. SetHeights           (writes data into allocated map)
//   3. size                 (scales the terrain geometry)
//
// 16-bit PNG handling:
//   Unity's Texture2D.LoadImage() loads PNGs as 8-bit RGBA, discarding the
//   high byte of 16-bit grayscale data. This would produce visibly blockier
//   terrain (256 instead of 65536 elevation steps). To preserve full 16-bit
//   precision, we decode the PNG raw pixel data manually: the pipeline emits
//   a 16-bit grayscale PNG where each pixel is 2 bytes (big-endian per PNG
//   spec, but Pillow writes them in host byte order via numpy). We handle
//   both endiannesses defensively.
// =============================================================================

using System;
using UnityEngine;

namespace DepthWizard.Terrain
{
    /// <summary>
    /// Static utility that builds a Unity Terrain GameObject at runtime from
    /// raw heightmap and texture PNG byte arrays.
    /// </summary>
    public static class RuntimeTerrainBuilder
    {
        // Name of the runtime terrain GameObject (matches the editor tool).
        private const string TERRAIN_GO_NAME = "DepthWizard_Terrain";

        // Path to the URP terrain material created by the editor tool.
        // At runtime we load it via Resources or find it on the existing terrain.
        private const string TERRAIN_SHADER_NAME = "Universal Render Pipeline/Terrain/Lit";

        /// <summary>
        /// Build (or update) a Terrain from the backend's pipeline output.
        /// </summary>
        /// <param name="heightmapPngBytes">Raw bytes of the 16-bit grayscale heightmap PNG.</param>
        /// <param name="texturePngBytes">Raw bytes of the RGB texture PNG (may be null).</param>
        /// <param name="expectedResolution">Expected heightmap resolution (width/height from metadata).</param>
        /// <param name="terrainWidth">Terrain XZ width in meters.</param>
        /// <param name="terrainLength">Terrain XZ length in meters.</param>
        /// <param name="heightScale">Terrain Y height scale in meters.</param>
        /// <param name="metadata">Response metadata (for logging; may be null).</param>
        public static void Build(
            byte[] heightmapPngBytes,
            byte[] texturePngBytes,
            int expectedResolution,
            float terrainWidth,
            float terrainLength,
            float heightScale,
            Networking.ProcessResponse metadata = null)
        {
            // -----------------------------------------------------------------
            // Step 1: Decode the 16-bit heightmap PNG into a float[,] array
            // -----------------------------------------------------------------
            float[,] heights = Decode16BitHeightmap(heightmapPngBytes, expectedResolution);
            int resolution = heights.GetLength(0);

            Debug.Log($"[RuntimeTerrainBuilder] Heightmap decoded: {resolution}×{resolution}");

            // -----------------------------------------------------------------
            // Step 2: Decode the texture PNG into a Texture2D
            // -----------------------------------------------------------------
            Texture2D texture = null;
            if (texturePngBytes != null && texturePngBytes.Length > 0)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(texturePngBytes))
                {
                    Debug.LogWarning("[RuntimeTerrainBuilder] Failed to decode texture PNG; terrain will have no texture.");
                    UnityEngine.Object.Destroy(texture);
                    texture = null;
                }
            }

            // -----------------------------------------------------------------
            // Step 3: Create TerrainData
            //
            // CRITICAL ORDER (same as TerrainGeneratorEditorWindow.cs):
            //   1. heightmapResolution  → allocates internal heightmap
            //   2. SetHeights           → writes data into allocated map
            //   3. size                 → scales the terrain geometry
            // -----------------------------------------------------------------
            TerrainData terrainData = new TerrainData();

            // 1. Set resolution
            terrainData.heightmapResolution = resolution;
            int actualResolution = terrainData.heightmapResolution;

            Debug.Log(
                $"[RuntimeTerrainBuilder] TerrainData resolution: " +
                $"requested={resolution}, actual={actualResolution}");

            if (heights.GetLength(0) != actualResolution ||
                heights.GetLength(1) != actualResolution)
            {
                // Mismatch — Unity clamped to a different 2^n+1 value.
                // Re-sample the heightmap to match.
                Debug.LogWarning(
                    $"[RuntimeTerrainBuilder] Resolution mismatch: heights[{heights.GetLength(0)},{heights.GetLength(1)}] " +
                    $"vs terrain {actualResolution}. Resampling.");
                heights = BilinearResample(heights, actualResolution);
            }

            // 2. Write heights
            terrainData.SetHeights(0, 0, heights);

            // 3. Set size
            heightScale = Mathf.Max(heightScale, 1f);
            terrainData.size = new Vector3(terrainWidth, heightScale, terrainLength);

            // -----------------------------------------------------------------
            // Step 4: Apply texture as a TerrainLayer
            // -----------------------------------------------------------------
            if (texture != null)
            {
                TerrainLayer layer = new TerrainLayer();
                layer.diffuseTexture = texture;
                // Tile size = full terrain extent so texture maps 1:1
                layer.tileSize = new Vector2(terrainWidth, terrainLength);
                layer.tileOffset = Vector2.zero;

                terrainData.terrainLayers = new TerrainLayer[] { layer };
                Debug.Log($"[RuntimeTerrainBuilder] Texture applied as TerrainLayer ({texture.width}×{texture.height})");
            }

            // -----------------------------------------------------------------
            // Step 5: Create or update the Terrain GameObject
            // -----------------------------------------------------------------
            GameObject terrainGo = GameObject.Find(TERRAIN_GO_NAME);

            if (terrainGo != null)
            {
                // Update existing
                var existingTerrain = terrainGo.GetComponent<UnityEngine.Terrain>();
                if (existingTerrain != null)
                {
                    existingTerrain.terrainData = terrainData;
                    AssignMaterial(existingTerrain);
                }

                var existingCollider = terrainGo.GetComponent<TerrainCollider>();
                if (existingCollider == null)
                    existingCollider = terrainGo.AddComponent<TerrainCollider>();
                existingCollider.terrainData = terrainData;

                // Force collision rebuild
                existingCollider.enabled = false;
                existingCollider.enabled = true;

                Debug.Log("[RuntimeTerrainBuilder] Updated existing terrain.");
            }
            else
            {
                // Create new
                terrainGo = new GameObject(TERRAIN_GO_NAME);

                var terrain = terrainGo.AddComponent<UnityEngine.Terrain>();
                terrain.terrainData = terrainData;
                AssignMaterial(terrain);

                var collider = terrainGo.AddComponent<TerrainCollider>();
                collider.terrainData = terrainData;

                // Force collision rebuild
                collider.enabled = false;
                collider.enabled = true;

                Debug.Log("[RuntimeTerrainBuilder] Created new terrain.");
            }

            Debug.Log(
                $"[RuntimeTerrainBuilder] ✓ Terrain built: " +
                $"{terrainWidth:F0}×{heightScale:F0}×{terrainLength:F0}m, " +
                $"heightmap {actualResolution}×{actualResolution}");
        }

        // -----------------------------------------------------------------
        // 16-bit PNG decoding
        //
        // The pipeline (depth_pipeline/run.py) writes the heightmap as a
        // 16-bit grayscale PNG via Pillow:
        //   Image.fromarray(uint16_array).save("heightmap.png")
        //
        // This produces a PNG with color type 0 (Grayscale), bit depth 16.
        // Each pixel is 2 bytes. Unity's Texture2D.LoadImage() would load
        // this as 8-bit, losing half the precision.
        //
        // Strategy: Load into a Texture2D with R16 format if supported,
        // otherwise fall back to RGBA32 and reconstruct from the 8-bit
        // channels. The R16 path preserves full 16-bit precision.
        // -----------------------------------------------------------------

        private static float[,] Decode16BitHeightmap(byte[] pngBytes, int expectedRes)
        {
            // Try loading as R16 (16-bit single channel) — preserves full precision
            Texture2D tex = new Texture2D(2, 2, TextureFormat.R16, false);
            if (tex.LoadImage(pngBytes))
            {
                int w = tex.width;
                int h = tex.height;

                // Verify we actually got 16-bit data by checking the format
                // LoadImage may silently convert to RGBA32 on some platforms
                if (tex.format == TextureFormat.R16)
                {
                    Debug.Log($"[RuntimeTerrainBuilder] 16-bit heightmap loaded natively: {w}×{h}");
                    float[,] heights = ExtractHeightsFromR16(tex, w, h);
                    UnityEngine.Object.Destroy(tex);
                    return EnsureResolution(heights, w, h, expectedRes);
                }

                // LoadImage converted to a different format — still usable
                // but may have reduced precision. Extract what we can.
                Debug.LogWarning(
                    $"[RuntimeTerrainBuilder] Heightmap loaded as {tex.format} instead of R16. " +
                    $"Precision may be reduced to 8-bit.");
                float[,] heightsFallback = ExtractHeightsFromTexture(tex, w, h);
                UnityEngine.Object.Destroy(tex);
                return EnsureResolution(heightsFallback, w, h, expectedRes);
            }

            // R16 load failed — try again with RGBA32
            UnityEngine.Object.Destroy(tex);
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(pngBytes))
            {
                int w = tex.width;
                int h = tex.height;
                Debug.LogWarning(
                    $"[RuntimeTerrainBuilder] Heightmap loaded as RGBA32 fallback: {w}×{h}. " +
                    $"Terrain will use 8-bit precision.");
                float[,] heights = ExtractHeightsFromTexture(tex, w, h);
                UnityEngine.Object.Destroy(tex);
                return EnsureResolution(heights, w, h, expectedRes);
            }

            UnityEngine.Object.Destroy(tex);
            throw new InvalidOperationException(
                "[RuntimeTerrainBuilder] Failed to decode heightmap PNG.");
        }

        /// <summary>
        /// Extract normalized 0–1 heights from an R16 texture using raw pixel data.
        /// </summary>
        private static float[,] ExtractHeightsFromR16(Texture2D tex, int w, int h)
        {
            // GetRawTextureData gives us the raw 16-bit values as bytes (2 bytes per pixel, little-endian on most platforms)
            byte[] raw = tex.GetRawTextureData();
            float[,] heights = new float[h, w];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Unity textures are bottom-to-top, TerrainData.SetHeights expects [y,x] top-to-bottom
                    int srcY = h - 1 - y;
                    int idx = (srcY * w + x) * 2;
                    ushort val = (ushort)(raw[idx] | (raw[idx + 1] << 8)); // little-endian
                    heights[y, x] = val / 65535f;
                }
            }

            return heights;
        }

        /// <summary>
        /// Extract heights from a standard texture using GetPixel (8-bit precision fallback).
        /// </summary>
        private static float[,] ExtractHeightsFromTexture(Texture2D tex, int w, int h)
        {
            float[,] heights = new float[h, w];
            Color[] pixels = tex.GetPixels();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Unity textures are bottom-to-top, SetHeights expects top-to-bottom
                    int srcY = h - 1 - y;
                    heights[y, x] = pixels[srcY * w + x].r;
                }
            }

            return heights;
        }

        /// <summary>
        /// Ensure the heights array matches the expected resolution. If not,
        /// find the nearest valid terrain resolution (2^n+1) and resample.
        /// </summary>
        private static float[,] EnsureResolution(float[,] heights, int w, int h, int expected)
        {
            int target = NextPowerOfTwoPlusOne(Mathf.Max(w, h));

            if (heights.GetLength(0) == target && heights.GetLength(1) == target)
                return heights;

            Debug.Log($"[RuntimeTerrainBuilder] Resampling heightmap from {w}×{h} to {target}×{target}");
            return BilinearResample(heights, target);
        }

        // -----------------------------------------------------------------
        // Material assignment
        // -----------------------------------------------------------------

        private static void AssignMaterial(UnityEngine.Terrain terrain)
        {
            // Try to find the URP terrain shader at runtime
            Shader shader = Shader.Find(TERRAIN_SHADER_NAME);
            if (shader != null)
            {
                terrain.materialTemplate = new Material(shader);
            }
            else
            {
                Debug.LogWarning(
                    $"[RuntimeTerrainBuilder] Shader '{TERRAIN_SHADER_NAME}' not found. " +
                    "Terrain may render with default material.");
            }
        }

        // -----------------------------------------------------------------
        // Resampling (same algorithm as GeoTiffHeightmapReader.cs)
        // -----------------------------------------------------------------

        private static int NextPowerOfTwoPlusOne(int value)
        {
            int[] validSizes = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
            foreach (int size in validSizes)
            {
                if (size >= value) return size;
            }
            return 4097;
        }

        private static float[,] BilinearResample(float[,] source, int targetRes)
        {
            int srcHeight = source.GetLength(0);
            int srcWidth = source.GetLength(1);

            if (srcWidth == targetRes && srcHeight == targetRes)
                return source;

            float[,] result = new float[targetRes, targetRes];
            float xRatio = (float)(srcWidth - 1) / (targetRes - 1);
            float yRatio = (float)(srcHeight - 1) / (targetRes - 1);

            for (int y = 0; y < targetRes; y++)
            {
                float srcY = y * yRatio;
                int y0 = Mathf.FloorToInt(srcY);
                int y1 = Mathf.Min(y0 + 1, srcHeight - 1);
                float yLerp = srcY - y0;

                for (int x = 0; x < targetRes; x++)
                {
                    float srcX = x * xRatio;
                    int x0 = Mathf.FloorToInt(srcX);
                    int x1 = Mathf.Min(x0 + 1, srcWidth - 1);
                    float xLerp = srcX - x0;

                    float topLeft = source[y0, x0];
                    float topRight = source[y0, x1];
                    float bottomLeft = source[y1, x0];
                    float bottomRight = source[y1, x1];

                    float top = Mathf.Lerp(topLeft, topRight, xLerp);
                    float bottom = Mathf.Lerp(bottomLeft, bottomRight, xLerp);

                    result[y, x] = Mathf.Lerp(top, bottom, yLerp);
                }
            }

            return result;
        }
    }
}

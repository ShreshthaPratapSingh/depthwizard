// =============================================================================
// GeoTiffHeightmapReader.cs
//
// Reads a single-band GeoTIFF file and converts it to a normalized float[,]
// heightmap suitable for Unity's TerrainData.SetHeights().
//
// REPLACEMENT POINT: When the real backend pipeline is wired in (FastAPI sends
// a pre-processed 16-bit PNG heightmap), replace the Read() call site with
// a PNG loader. The normalization + resampling helpers can be reused as-is.
// =============================================================================

using System;
using System.IO;
using BitMiracle.LibTiff.Classic;
using UnityEngine;

namespace DepthWizard.Terrain
{
    /// <summary>
    /// Result of reading a GeoTIFF heightmap, containing the raw elevation
    /// grid plus metadata useful for terrain construction and debugging.
    /// </summary>
    public class HeightmapData
    {
        /// <summary>Normalized 0-1 heightmap ready for TerrainData.SetHeights().</summary>
        public float[,] Heights;

        /// <summary>Original raster width in pixels.</summary>
        public int OriginalWidth;

        /// <summary>Original raster height (rows) in pixels.</summary>
        public int OriginalHeight;

        /// <summary>Minimum raw elevation value found in the raster.</summary>
        public float MinElevation;

        /// <summary>Maximum raw elevation value found in the raster.</summary>
        public float MaxElevation;

        /// <summary>Final heightmap resolution after resampling (power-of-two-plus-one).</summary>
        public int ResampledResolution;
    }

    /// <summary>
    /// Reads GeoTIFF elevation rasters and produces Unity-compatible heightmaps.
    /// Supports 8-bit, 16-bit unsigned, 32-bit signed integer, and 32-bit float
    /// single-band grayscale data.
    /// </summary>
    public static class GeoTiffHeightmapReader
    {
        // ---------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------

        /// <summary>
        /// Read a GeoTIFF file and return a normalized heightmap.
        /// </summary>
        /// <param name="filePath">Absolute path to the .tif / .tiff file.</param>
        /// <returns>HeightmapData on success, null on failure (errors are logged).</returns>
        public static HeightmapData Read(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                Debug.LogError("[GeoTiffHeightmapReader] File path is null or empty.");
                return null;
            }

            if (!File.Exists(filePath))
            {
                Debug.LogError($"[GeoTiffHeightmapReader] File not found: {filePath}");
                return null;
            }

            try
            {
                return ReadInternal(filePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GeoTiffHeightmapReader] Failed to read GeoTIFF.\n{ex}");
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Internal implementation
        // ---------------------------------------------------------------------

        private static HeightmapData ReadInternal(string filePath)
        {
            using (Tiff tiff = Tiff.Open(filePath, "r"))
            {
                if (tiff == null)
                {
                    Debug.LogError($"[GeoTiffHeightmapReader] LibTiff could not open: {filePath}");
                    return null;
                }

                // --- Read image dimensions ---
                int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

                if (width <= 0 || height <= 0)
                {
                    Debug.LogError($"[GeoTiffHeightmapReader] Invalid dimensions: {width}x{height}");
                    return null;
                }

                // --- Determine sample format & bit depth ---
                int bitsPerSample = GetTagInt(tiff, TiffTag.BITSPERSAMPLE, 8);
                int samplesPerPixel = GetTagInt(tiff, TiffTag.SAMPLESPERPIXEL, 1);
                int sampleFormat = GetTagInt(tiff, TiffTag.SAMPLEFORMAT,
                    (int)SampleFormat.UINT);

                Debug.Log($"[GeoTiffHeightmapReader] Image: {width}x{height}, " +
                          $"{bitsPerSample}-bit, {samplesPerPixel} sample(s)/px, " +
                          $"format={((SampleFormat)sampleFormat)}");

                if (samplesPerPixel > 1)
                {
                    Debug.LogWarning("[GeoTiffHeightmapReader] Multi-band image detected. " +
                                     "Using first band only.");
                }

                // --- Read raw elevation values ---
                float[,] rawElevations = ReadElevationData(tiff, width, height,
                    bitsPerSample, (SampleFormat)sampleFormat);

                if (rawElevations == null) return null;

                // --- Compute min/max ---
                float minElev = float.MaxValue;
                float maxElev = float.MinValue;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float v = rawElevations[y, x];

                        // Skip common nodata sentinel values
                        if (IsNoData(v)) continue;

                        if (v < minElev) minElev = v;
                        if (v > maxElev) maxElev = v;
                    }
                }

                // Guard against all-nodata rasters
                if (minElev >= maxElev)
                {
                    Debug.LogWarning("[GeoTiffHeightmapReader] Flat or all-nodata raster; " +
                                     "heightmap will be uniformly zero.");
                    minElev = 0f;
                    maxElev = 1f;
                }

                Debug.Log($"[GeoTiffHeightmapReader] Elevation range: " +
                          $"min={minElev:F2} m, max={maxElev:F2} m");

                // --- Normalize to 0-1 ---
                float range = maxElev - minElev;
                float[,] normalized = new float[height, width];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        float v = rawElevations[y, x];

                        if (IsNoData(v))
                        {
                            normalized[y, x] = 0f; // Nodata → lowest point
                        }
                        else
                        {
                            normalized[y, x] = Mathf.Clamp01((v - minElev) / range);
                        }
                    }
                }

                // --- Resample to power-of-two-plus-one ---
                int targetRes = NextPowerOfTwoPlusOne(Mathf.Max(width, height));
                float[,] resampled = BilinearResample(normalized, width, height, targetRes);

                Debug.Log($"[GeoTiffHeightmapReader] Resampled {width}x{height} → " +
                          $"{targetRes}x{targetRes}");

                return new HeightmapData
                {
                    Heights = resampled,
                    OriginalWidth = width,
                    OriginalHeight = height,
                    MinElevation = minElev,
                    MaxElevation = maxElev,
                    ResampledResolution = targetRes
                };
            }
        }

        // ---------------------------------------------------------------------
        // Raster reading — supports 8/16-bit uint, 32-bit int, 32-bit float
        // ---------------------------------------------------------------------

        private static float[,] ReadElevationData(Tiff tiff, int width, int height,
            int bitsPerSample, SampleFormat sampleFormat)
        {
            float[,] elevations = new float[height, width];
            int scanlineSize = tiff.ScanlineSize();
            byte[] buffer = new byte[scanlineSize];

            for (int row = 0; row < height; row++)
            {
                if (!tiff.ReadScanline(buffer, row))
                {
                    Debug.LogError($"[GeoTiffHeightmapReader] Failed to read scanline {row}.");
                    return null;
                }

                for (int col = 0; col < width; col++)
                {
                    elevations[row, col] = DecodePixelValue(buffer, col,
                        bitsPerSample, sampleFormat);
                }
            }

            return elevations;
        }

        private static float DecodePixelValue(byte[] buffer, int pixelIndex,
            int bitsPerSample, SampleFormat sampleFormat)
        {
            switch (bitsPerSample)
            {
                case 8:
                    return buffer[pixelIndex];

                case 16:
                    int offset16 = pixelIndex * 2;
                    if (sampleFormat == SampleFormat.INT)
                        return BitConverter.ToInt16(buffer, offset16);
                    else
                        return BitConverter.ToUInt16(buffer, offset16);

                case 32:
                    int offset32 = pixelIndex * 4;
                    if (sampleFormat == SampleFormat.IEEEFP)
                        return BitConverter.ToSingle(buffer, offset32);
                    else if (sampleFormat == SampleFormat.INT)
                        return BitConverter.ToInt32(buffer, offset32);
                    else
                        return BitConverter.ToUInt32(buffer, offset32);

                default:
                    Debug.LogWarning($"[GeoTiffHeightmapReader] Unsupported bit depth: " +
                                     $"{bitsPerSample}. Treating as 0.");
                    return 0f;
            }
        }

        // ---------------------------------------------------------------------
        // Nodata detection
        // ---------------------------------------------------------------------

        /// <summary>
        /// Returns true for common nodata sentinel values used in DEMs.
        /// Covers -9999, -32768, very large negatives, NaN, and Infinity.
        /// </summary>
        private static bool IsNoData(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return true;
            if (v <= -9999f) return true;
            return false;
        }

        // ---------------------------------------------------------------------
        // Resampling
        // ---------------------------------------------------------------------

        /// <summary>
        /// Returns the smallest (2^n)+1 value ≥ the input.
        /// Unity terrain heightmap resolution must be (2^n)+1, e.g. 33, 65, 129, 257, 513, 1025.
        /// </summary>
        private static int NextPowerOfTwoPlusOne(int value)
        {
            // Clamp to sensible bounds for Unity terrains
            int[] validSizes = { 33, 65, 129, 257, 513, 1025, 2049, 4097 };
            foreach (int size in validSizes)
            {
                if (size >= value) return size;
            }
            return 4097; // Maximum Unity supports
        }

        /// <summary>
        /// Bilinear-interpolation resample of a 2D float array to targetRes × targetRes.
        /// </summary>
        private static float[,] BilinearResample(float[,] source,
            int srcWidth, int srcHeight, int targetRes)
        {
            // If source already matches target, return as-is
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

                    // Bilinear interpolation
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

        // ---------------------------------------------------------------------
        // Tag helpers
        // ---------------------------------------------------------------------

        private static int GetTagInt(Tiff tiff, TiffTag tag, int defaultValue)
        {
            FieldValue[] field = tiff.GetField(tag);
            if (field != null && field.Length > 0)
                return field[0].ToInt();
            return defaultValue;
        }
    }
}

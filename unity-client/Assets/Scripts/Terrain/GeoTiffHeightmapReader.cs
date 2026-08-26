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

        /// <summary>Real-world east-west extent in meters, derived from GeoTIFF metadata.</summary>
        public float WorldWidth;

        /// <summary>Real-world north-south extent in meters, derived from GeoTIFF metadata.</summary>
        public float WorldHeight;

        /// <summary>True if WorldWidth/WorldHeight were derived from valid GeoTIFF
        /// geospatial tags. False means fallback defaults were used.</summary>
        public bool HasGeoExtent;
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

                // --- Compute real-world extent from GeoTIFF metadata ---
                GeoExtent extent = ReadGeoExtent(tiff, width, height);

                return new HeightmapData
                {
                    Heights = resampled,
                    OriginalWidth = width,
                    OriginalHeight = height,
                    MinElevation = minElev,
                    MaxElevation = maxElev,
                    ResampledResolution = targetRes,
                    WorldWidth = extent.WidthMeters,
                    WorldHeight = extent.HeightMeters,
                    HasGeoExtent = extent.IsValid
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

        // ---------------------------------------------------------------------
        // GeoTIFF extent calculation
        // ---------------------------------------------------------------------

        // GeoTIFF tag IDs not in LibTiff.NET's TiffTag enum
        private const TiffTag TAG_MODEL_PIXEL_SCALE  = (TiffTag)33550;
        private const TiffTag TAG_MODEL_TIEPOINT     = (TiffTag)33922;
        private const TiffTag TAG_GEO_KEY_DIRECTORY  = (TiffTag)34735;

        // GeoKey IDs within the GeoKeyDirectoryTag
        private const int GEO_KEY_MODEL_TYPE = 1024;  // GTModelTypeGeoKey
        private const int MODEL_TYPE_PROJECTED  = 1;  // meters
        private const int MODEL_TYPE_GEOGRAPHIC = 2;  // degrees

        // Approximate meters per degree of latitude (constant)
        private const double METERS_PER_DEGREE_LAT = 111320.0;

        /// <summary>
        /// Default fallback extent when GeoTIFF lacks georeferencing.
        /// </summary>
        private const float DEFAULT_EXTENT = 500f;

        private struct GeoExtent
        {
            public float WidthMeters;
            public float HeightMeters;
            public bool IsValid;
        }

        /// <summary>
        /// Reads GeoTIFF geospatial tags and computes the real-world extent
        /// of the raster in meters. Returns a fallback of 500×500 m if the
        /// required tags are missing or malformed.
        /// </summary>
        private static GeoExtent ReadGeoExtent(Tiff tiff, int pixelWidth, int pixelHeight)
        {
            // --- Read ModelPixelScaleTag (33550): [scaleX, scaleY, scaleZ] ---
            double scaleX, scaleY;
            if (!TryReadPixelScale(tiff, out scaleX, out scaleY))
            {
                Debug.LogWarning("[GeoTiffHeightmapReader] ModelPixelScaleTag (33550) not found. " +
                                 $"Using default terrain extent of {DEFAULT_EXTENT}×{DEFAULT_EXTENT} m.");
                return new GeoExtent { WidthMeters = DEFAULT_EXTENT, HeightMeters = DEFAULT_EXTENT, IsValid = false };
            }

            // Raw extent in the raster's native CRS units
            double rawWidth  = pixelWidth  * scaleX;
            double rawHeight = pixelHeight * scaleY;

            Debug.Log($"[GeoTiffHeightmapReader] Pixel scale: ({scaleX:G6}, {scaleY:G6}), " +
                      $"raw extent: {rawWidth:G6} × {rawHeight:G6}");

            // --- Determine coordinate system ---
            int modelType = ReadModelType(tiff);

            float worldWidth, worldHeight;

            if (modelType == MODEL_TYPE_PROJECTED)
            {
                // Already in meters (UTM, etc.)
                worldWidth  = (float)rawWidth;
                worldHeight = (float)rawHeight;

                Debug.Log($"[GeoTiffHeightmapReader] Projected CRS (meters): " +
                          $"extent = {worldWidth:F1} × {worldHeight:F1} m");
            }
            else
            {
                // Geographic CRS (degrees) — need latitude for longitude scaling
                if (modelType != MODEL_TYPE_GEOGRAPHIC)
                {
                    Debug.LogWarning("[GeoTiffHeightmapReader] GeoKeyDirectoryTag (34735) missing " +
                                     "or GTModelTypeGeoKey not found. Assuming geographic (degrees).");
                }

                double centerLat = ReadCenterLatitude(tiff, pixelHeight, scaleY);
                double latRad = centerLat * Math.PI / 180.0;

                double metersPerDegreeLon = METERS_PER_DEGREE_LAT * Math.Cos(latRad);

                worldWidth  = (float)(rawWidth  * metersPerDegreeLon);
                worldHeight = (float)(rawHeight * METERS_PER_DEGREE_LAT);

                Debug.Log($"[GeoTiffHeightmapReader] Geographic CRS (degrees): " +
                          $"center lat = {centerLat:F4}°, " +
                          $"m/deg lon = {metersPerDegreeLon:F1}, " +
                          $"extent = {worldWidth:F1} × {worldHeight:F1} m");
            }

            // Sanity check — guard against nonsensical values
            if (worldWidth <= 0f || worldHeight <= 0f ||
                float.IsNaN(worldWidth) || float.IsNaN(worldHeight) ||
                float.IsInfinity(worldWidth) || float.IsInfinity(worldHeight))
            {
                Debug.LogWarning($"[GeoTiffHeightmapReader] Computed extent ({worldWidth}×{worldHeight}) " +
                                 $"is invalid. Using default {DEFAULT_EXTENT}×{DEFAULT_EXTENT} m.");
                return new GeoExtent { WidthMeters = DEFAULT_EXTENT, HeightMeters = DEFAULT_EXTENT, IsValid = false };
            }

            return new GeoExtent { WidthMeters = worldWidth, HeightMeters = worldHeight, IsValid = true };
        }

        /// <summary>
        /// Reads ModelPixelScaleTag (33550) and extracts scaleX, scaleY.
        /// Returns false if the tag is missing or malformed.
        /// </summary>
        private static bool TryReadPixelScale(Tiff tiff, out double scaleX, out double scaleY)
        {
            scaleX = 0;
            scaleY = 0;

            FieldValue[] field = tiff.GetField(TAG_MODEL_PIXEL_SCALE);
            if (field == null || field.Length < 2)
                return false;

            // LibTiff.NET returns this tag as (count, byte[]) for DOUBLE arrays.
            // The byte array contains 3 doubles: scaleX, scaleY, scaleZ.
            byte[] data = field[1].ToByteArray();
            if (data == null || data.Length < 16) // Need at least 2 doubles (16 bytes)
                return false;

            scaleX = BitConverter.ToDouble(data, 0);
            scaleY = BitConverter.ToDouble(data, 8);

            return scaleX > 0 && scaleY > 0;
        }

        /// <summary>
        /// Reads GeoKeyDirectoryTag (34735) and extracts the GTModelTypeGeoKey
        /// to determine if the CRS is projected (1=meters) or geographic (2=degrees).
        /// Returns -1 if the tag or key is not found.
        /// </summary>
        private static int ReadModelType(Tiff tiff)
        {
            FieldValue[] field = tiff.GetField(TAG_GEO_KEY_DIRECTORY);
            if (field == null || field.Length < 2)
                return -1;

            // GeoKeyDirectoryTag is an array of unsigned shorts.
            // Layout: [keyDirectoryVersion, keyRevision, minorRevision, numberOfKeys,
            //          keyID1, tiffTagLocation1, count1, valueOffset1,
            //          keyID2, ...]
            byte[] data = field[1].ToByteArray();
            if (data == null || data.Length < 8) // Need at least the header (4 ushorts = 8 bytes)
                return -1;

            int numberOfKeys = BitConverter.ToUInt16(data, 6);

            for (int i = 0; i < numberOfKeys; i++)
            {
                int offset = 8 + i * 8; // Each key entry is 4 ushorts = 8 bytes
                if (offset + 8 > data.Length) break;

                int keyId = BitConverter.ToUInt16(data, offset);
                if (keyId == GEO_KEY_MODEL_TYPE)
                {
                    // tiffTagLocation=0 means the value is in valueOffset directly
                    int tiffTagLocation = BitConverter.ToUInt16(data, offset + 2);
                    if (tiffTagLocation == 0)
                    {
                        return BitConverter.ToUInt16(data, offset + 6);
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Reads the center latitude of the raster from ModelTiepointTag (33922).
        /// Falls back to 0° (equator) if the tag is missing, which gives a
        /// worst-case-correct longitude scaling.
        /// </summary>
        private static double ReadCenterLatitude(Tiff tiff, int pixelHeight, double scaleY)
        {
            FieldValue[] field = tiff.GetField(TAG_MODEL_TIEPOINT);
            if (field == null || field.Length < 2)
            {
                Debug.LogWarning("[GeoTiffHeightmapReader] ModelTiepointTag (33922) not found. " +
                                 "Assuming equator (lat=0°) for longitude scaling.");
                return 0.0;
            }

            // ModelTiepointTag contains sets of 6 doubles:
            // [I, J, K, X, Y, Z] where (I,J,K) is raster space and (X,Y,Z) is model space.
            // We need Y (latitude of the tiepoint, usually the top-left corner).
            byte[] data = field[1].ToByteArray();
            if (data == null || data.Length < 48) // Need at least 6 doubles
            {
                Debug.LogWarning("[GeoTiffHeightmapReader] ModelTiepointTag too short. " +
                                 "Assuming equator (lat=0°).");
                return 0.0;
            }

            double tiepointY = BitConverter.ToDouble(data, 32); // Y = latitude of origin
            double tiepointJ = BitConverter.ToDouble(data, 8);  // J = row in raster

            // Compute center latitude: origin lat - (half raster height in degrees)
            // scaleY is positive (pixel scale), but latitude decreases going south
            double centerLat = tiepointY - (pixelHeight / 2.0 - tiepointJ) * scaleY;

            return centerLat;
        }
    }
}

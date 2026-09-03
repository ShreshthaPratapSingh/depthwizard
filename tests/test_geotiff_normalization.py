"""Tests for GeoTIFF pixel-data normalization in _read_rgb.

These tests exercise the fix for broken depth inference / rainbow-noise
textures when uploading non-uint8 GeoTIFFs (e.g. Sentinel-2 UInt16
reflectance data).  They create synthetic GeoTIFFs with rasterio so no
real satellite imagery is needed.

Run: python -m pytest tests/test_geotiff_normalization.py -v
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np

try:
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import from_bounds

    HAS_RASTERIO = True
except ImportError:
    HAS_RASTERIO = False

from depth_pipeline.run import _read_rgb


def _write_geotiff(
    path: Path,
    data: np.ndarray,
    dtype: str,
    count: int = 3,
) -> None:
    """Write a minimal GeoTIFF with the given band data and dtype."""
    h, w = data.shape[1], data.shape[2]
    transform = from_bounds(-77.1, 38.8, -76.9, 39.0, w, h)
    profile = {
        "driver": "GTiff",
        "height": h,
        "width": w,
        "count": count,
        "dtype": dtype,
        "crs": CRS.from_epsg(4326),
        "transform": transform,
    }
    with rasterio.open(path, "w", **profile) as dst:
        for i in range(count):
            dst.write(data[i], i + 1)


@unittest.skipUnless(HAS_RASTERIO, "rasterio not installed")
class ReadRgbNormalizationTests(unittest.TestCase):

    def test_uint8_geotiff_passes_through_unchanged(self) -> None:
        """A uint8 GeoTIFF should come back as-is, no normalization."""
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "uint8.tif"
            bands = np.random.randint(0, 256, (3, 16, 16), dtype=np.uint8)
            _write_geotiff(tif, bands, "uint8")

            rgb = _read_rgb(str(tif))

        self.assertEqual(rgb.dtype, np.uint8)
        self.assertEqual(rgb.shape, (16, 16, 3))
        # Values should be identical — no remapping
        for ch in range(3):
            np.testing.assert_array_equal(rgb[..., ch], bands[ch])

    def test_uint16_geotiff_normalized_to_uint8(self) -> None:
        """A UInt16 GeoTIFF with high values must be rescaled to 0–255."""
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "uint16.tif"
            # Simulate Sentinel-2-like reflectance: values ≈ 11000–50000
            rng = np.random.default_rng(42)
            bands = rng.integers(11_000, 50_000, (3, 32, 32), dtype=np.uint16)
            _write_geotiff(tif, bands, "uint16")

            rgb = _read_rgb(str(tif))

        self.assertEqual(rgb.dtype, np.uint8)
        self.assertEqual(rgb.shape, (32, 32, 3))
        # After normalization, values must span a sensible 0–255 range
        # (not all crammed near 0 or 255).
        for ch in range(3):
            ch_data = rgb[..., ch]
            self.assertGreater(ch_data.max(), 200,
                               "max should be close to 255 after stretch")
            self.assertLess(ch_data.min(), 50,
                            "min should be close to 0 after stretch")

    def test_uint16_geotiff_texture_not_rainbow_noise(self) -> None:
        """Normalized output should resemble a smooth gradient, not noise.

        We feed a known smooth gradient (ramp per band) and verify the
        output preserves monotonicity — if the old truncation bug were
        present, the ramp would wrap around and lose monotonicity.
        """
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "ramp.tif"
            ramp = np.linspace(10_000, 60_000, 64 * 64, dtype=np.uint16).reshape(1, 64, 64)
            bands = np.concatenate([ramp, ramp, ramp], axis=0)
            _write_geotiff(tif, bands, "uint16")

            rgb = _read_rgb(str(tif))

        self.assertEqual(rgb.dtype, np.uint8)
        # The per-row means should be monotonically non-decreasing (allowing
        # for clipping at the percentile edges).
        row_means = rgb[..., 0].astype(float).mean(axis=1)
        diffs = np.diff(row_means)
        # Allow up to 2 tiny violations from rounding; the old bug would
        # produce dozens of sign changes.
        violations = int((diffs < -1.0).sum())
        self.assertLessEqual(violations, 2,
                             "normalized ramp should be roughly monotonic")

    def test_single_band_uint16_replicated_to_rgb(self) -> None:
        """A single-band uint16 GeoTIFF must be replicated to 3 channels
        and normalized."""
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "single.tif"
            band = np.full((1, 8, 8), 30_000, dtype=np.uint16)
            _write_geotiff(tif, band, "uint16", count=1)

            rgb = _read_rgb(str(tif))

        self.assertEqual(rgb.dtype, np.uint8)
        self.assertEqual(rgb.shape, (8, 8, 3))
        # All three channels should be identical (replicated from single band)
        np.testing.assert_array_equal(rgb[..., 0], rgb[..., 1])
        np.testing.assert_array_equal(rgb[..., 1], rgb[..., 2])

    def test_float32_geotiff_normalized(self) -> None:
        """Float32 GeoTIFFs (e.g. from NDVI products) must also normalize."""
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "float32.tif"
            rng = np.random.default_rng(99)
            bands = rng.uniform(0.1, 0.9, (3, 16, 16)).astype(np.float32)
            _write_geotiff(tif, bands, "float32")

            rgb = _read_rgb(str(tif))

        self.assertEqual(rgb.dtype, np.uint8)
        self.assertEqual(rgb.shape, (16, 16, 3))
        # Should span a reasonable uint8 range
        self.assertGreater(rgb.max(), 100)

    def test_png_path_untouched(self) -> None:
        """PNG loading path must not be affected by the GeoTIFF changes."""
        from PIL import Image

        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "test.png"
            img = Image.new("RGB", (8, 8), color=(42, 128, 200))
            img.save(png)

            rgb = _read_rgb(str(png))

        self.assertEqual(rgb.dtype, np.uint8)
        self.assertEqual(rgb.shape, (8, 8, 3))
        # Exact pixel values from PIL
        self.assertEqual(rgb[0, 0, 0], 42)
        self.assertEqual(rgb[0, 0, 1], 128)
        self.assertEqual(rgb[0, 0, 2], 200)


if __name__ == "__main__":
    unittest.main()

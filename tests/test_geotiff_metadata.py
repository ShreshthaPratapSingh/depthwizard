"""Unit tests for read_georef — no Unity, no depth model.

Run: python -m unittest tests.test_geotiff_metadata
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from PIL import Image

from depth_pipeline.geospatial import read_georef

try:
    import numpy as np
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import Affine, from_bounds

    HAS_RASTERIO = True
except ImportError:
    HAS_RASTERIO = False


def _write_png(path: Path) -> None:
    Image.new("RGB", (8, 8), color=(0, 0, 0)).save(path)


class ReadGeorefFallbackTests(unittest.TestCase):
    def test_png_is_not_georeferenced(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "plain.png"
            _write_png(png)
            info = read_georef(png)
        self.assertFalse(info["is_georeferenced"])
        self.assertIsNone(info["crs"])
        self.assertIsNone(info["bbox"])
        self.assertIsInstance(info["warning"], str)
        self.assertTrue(info["warning"])

    def test_missing_file_does_not_raise(self) -> None:
        info = read_georef("/no/such/file.tif")
        self.assertFalse(info["is_georeferenced"])
        self.assertIn("not found", info["warning"].lower())


@unittest.skipUnless(HAS_RASTERIO, "rasterio not installed")
class ReadGeorefGeoTiffTests(unittest.TestCase):

    def test_valid_geotiff(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "ok.tif"
            transform = from_bounds(-77.1, 38.8, -76.9, 39.0, 4, 4)
            profile = {
                "driver": "GTiff",
                "height": 4,
                "width": 4,
                "count": 1,
                "dtype": "uint8",
                "crs": CRS.from_epsg(4326),
                "transform": transform,
            }
            with rasterio.open(tif, "w", **profile) as dst:
                dst.write(np.zeros((1, 4, 4), dtype=np.uint8))

            info = read_georef(tif)
        self.assertTrue(info["is_georeferenced"])
        self.assertEqual(info["crs"], "EPSG:4326")
        self.assertIsNone(info["warning"])
        west, south, east, north = info["bbox"]
        self.assertAlmostEqual(west, -77.1, places=5)
        self.assertAlmostEqual(south, 38.8, places=5)
        self.assertAlmostEqual(east, -76.9, places=5)
        self.assertAlmostEqual(north, 39.0, places=5)

    def test_geotiff_without_crs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "no_crs.tif"
            profile = {
                "driver": "GTiff",
                "height": 4,
                "width": 4,
                "count": 1,
                "dtype": "uint8",
                "transform": from_bounds(0, 0, 100, 100, 4, 4),
            }
            with rasterio.open(tif, "w", **profile) as dst:
                dst.write(np.zeros((1, 4, 4), dtype=np.uint8))

            info = read_georef(tif)
        self.assertFalse(info["is_georeferenced"])
        self.assertIn("CRS", info["warning"])

    def test_geotiff_identity_transform(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "identity.tif"
            profile = {
                "driver": "GTiff",
                "height": 4,
                "width": 4,
                "count": 1,
                "dtype": "uint8",
                "crs": CRS.from_epsg(4326),
                "transform": Affine.identity(),
            }
            with rasterio.open(tif, "w", **profile) as dst:
                dst.write(np.zeros((1, 4, 4), dtype=np.uint8))

            info = read_georef(tif)
        self.assertFalse(info["is_georeferenced"])
        self.assertIn("transform", info["warning"].lower())


if __name__ == "__main__":
    unittest.main()

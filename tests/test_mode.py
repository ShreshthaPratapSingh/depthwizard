"""Tests for resolve_elevation_mode fallback codes."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image

from depth_pipeline.geospatial import contract_fields, resolve_elevation_mode

try:
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import Affine, from_bounds

    HAS_RASTERIO = True
except ImportError:
    HAS_RASTERIO = False


def _relative():
    hm = np.linspace(0, 65535, 36, dtype=np.uint16).reshape(6, 6)
    rel = hm.astype(np.float64) / 65535.0
    return rel, hm


class ModeFallbackTests(unittest.TestCase):
    def test_png_is_no_georef(self) -> None:
        rel, hm = _relative()
        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "x.png"
            Image.new("RGB", (8, 8)).save(png)
            mode = resolve_elevation_mode(png, rel, hm, output_dir=tmp)
        self.assertFalse(mode["is_georeferenced"])
        self.assertFalse(mode["is_calibrated"])
        self.assertEqual(mode["warning"], "no_georef")
        self.assertEqual(
            set(contract_fields(mode)),
            {
                "is_georeferenced",
                "is_calibrated",
                "min_elev_m",
                "max_elev_m",
                "r_squared",
                "sample_count",
                "srtm_tile_id",
                "warning",
            },
        )


@unittest.skipUnless(HAS_RASTERIO, "rasterio not installed")
class ModeGeoTiffTests(unittest.TestCase):
    def test_broken_crs_is_bad_crs(self) -> None:
        rel, hm = _relative()
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "nocrs.tif"
            profile = {
                "driver": "GTiff",
                "height": 4,
                "width": 4,
                "count": 1,
                "dtype": "uint8",
                "transform": from_bounds(0, 0, 1, 1, 4, 4),
            }
            with rasterio.open(tif, "w", **profile) as dst:
                dst.write(np.zeros((1, 4, 4), dtype=np.uint8))
            mode = resolve_elevation_mode(tif, rel, hm, output_dir=tmp)
        self.assertFalse(mode["is_georeferenced"])
        self.assertFalse(mode["is_calibrated"])
        self.assertEqual(mode["warning"], "bad_crs")

    def test_identity_transform_is_bad_crs(self) -> None:
        rel, hm = _relative()
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "ident.tif"
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
            mode = resolve_elevation_mode(tif, rel, hm, output_dir=tmp)
        self.assertEqual(mode["warning"], "bad_crs")
        self.assertFalse(mode["is_calibrated"])

    def test_valid_geotiff_without_tile_is_srtm_unavailable(self) -> None:
        rel, hm = _relative()
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "ok.tif"
            transform = from_bounds(1.0, 2.0, 1.1, 2.1, 8, 8)
            profile = {
                "driver": "GTiff",
                "height": 8,
                "width": 8,
                "count": 3,
                "dtype": "uint8",
                "crs": CRS.from_epsg(4326),
                "transform": transform,
                "photometric": "RGB",
            }
            with rasterio.open(tif, "w", **profile) as dst:
                dst.write(np.full((3, 8, 8), 10, dtype=np.uint8))
            mode = resolve_elevation_mode(
                tif, rel, hm, output_dir=tmp, tile_dir=tmp,
            )
        self.assertTrue(mode["is_georeferenced"])
        self.assertFalse(mode["is_calibrated"])
        self.assertEqual(mode["warning"], "srtm_unavailable")
        self.assertIsNone(mode["min_elev_m"])


if __name__ == "__main__":
    unittest.main()

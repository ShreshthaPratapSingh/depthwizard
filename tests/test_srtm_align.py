"""Unit tests for SRTM tile lookup and alignment.

Run: python -m unittest tests.test_srtm_align tests.test_geotiff_metadata
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

from depth_pipeline.geospatial import align_srtm, find_covering_tile
from depth_pipeline.run import process_image


def _fake_infer_depth(image):
    arr = np.asarray(image)
    h, w = arr.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    return ((xx / max(w - 1, 1) + yy / max(h - 1, 1)) / 2.0).astype(np.float32)

try:
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import from_bounds

    HAS_RASTERIO = True
except ImportError:
    HAS_RASTERIO = False


def _write_srtm_tile(path: Path, bbox, value: float = 250.0, size: int = 32) -> None:
    west, south, east, north = bbox
    transform = from_bounds(west, south, east, north, size, size)
    data = np.full((size, size), value, dtype=np.float32)
    # A ramp so resampling has structure to check.
    yy, xx = np.mgrid[0:size, 0:size]
    data = data + xx.astype(np.float32) + yy.astype(np.float32)
    profile = {
        "driver": "GTiff",
        "height": size,
        "width": size,
        "count": 1,
        "dtype": "float32",
        "crs": CRS.from_epsg(4326),
        "transform": transform,
        "nodata": -32768,
    }
    with rasterio.open(path, "w", **profile) as dst:
        dst.write(data, 1)


def _write_catalog(directory: Path, bbox, filename: str = "demo.tif") -> None:
    catalog = {
        "regions": [
            {
                "id": "demo_urban",
                "terrain": "urban",
                "bbox": list(bbox),
                "filename": filename,
            }
        ]
    }
    (directory / "regions.json").write_text(json.dumps(catalog), encoding="utf-8")


def _write_rgb_geotiff(path: Path, bbox, size: int = 16) -> None:
    west, south, east, north = bbox
    transform = from_bounds(west, south, east, north, size, size)
    rgb = np.full((3, size, size), 80, dtype=np.uint8)
    profile = {
        "driver": "GTiff",
        "height": size,
        "width": size,
        "count": 3,
        "dtype": "uint8",
        "crs": CRS.from_epsg(4326),
        "transform": transform,
        "photometric": "RGB",
    }
    with rasterio.open(path, "w", **profile) as dst:
        dst.write(rgb)


@unittest.skipUnless(HAS_RASTERIO, "rasterio not installed")
class SrtmAlignTests(unittest.TestCase):
    TILE_BBOX = (77.18, 28.58, 77.26, 28.66)

    def test_missing_tile_skips(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            result = align_srtm(
                bbox=[10.0, 10.0, 10.1, 10.1],
                dst_crs="EPSG:4326",
                dst_shape=(16, 16),
                tile_dir=tmp,
            )
        self.assertFalse(result["ok"])
        self.assertIsNone(result["elevation_m"])
        self.assertIn("unavailable", result["warning"].lower())

    def test_align_matches_grid_shape(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tile_dir = Path(tmp)
            _write_catalog(tile_dir, self.TILE_BBOX)
            _write_srtm_tile(tile_dir / "demo.tif", self.TILE_BBOX, value=200.0)
            query = [77.19, 28.59, 77.25, 28.65]
            out = tile_dir / "aligned.tif"
            result = align_srtm(
                bbox=query,
                dst_crs="EPSG:4326",
                dst_shape=(40, 50),
                tile_dir=tile_dir,
                output_path=out,
            )
            self.assertTrue(result["ok"], result["warning"])
            self.assertEqual(result["elevation_m"].shape, (40, 50))
            self.assertGreater(result["coverage"], 0.9)
            self.assertEqual(result["tile_id"], "demo_urban")
            self.assertTrue(out.is_file())
            with rasterio.open(out) as src:
                self.assertEqual(src.shape, (40, 50))
                self.assertTrue(np.isfinite(src.read(1)).mean() > 0.9)

    def test_find_covering_tile_prefers_catalog(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tile_dir = Path(tmp)
            _write_catalog(tile_dir, self.TILE_BBOX)
            _write_srtm_tile(tile_dir / "demo.tif", self.TILE_BBOX)
            hit = find_covering_tile(
                [77.20, 28.60, 77.21, 28.61],
                dst_crs="EPSG:4326",
                tile_dir=tile_dir,
            )
            miss = find_covering_tile(
                [12.0, 12.0, 12.2, 12.2],
                dst_crs="EPSG:4326",
                tile_dir=tile_dir,
            )
        self.assertIsNotNone(hit)
        self.assertEqual(hit["id"], "demo_urban")
        self.assertIsNone(miss)

    def test_process_image_png_unchanged_relative(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "plain.png"
            Image.new("RGB", (16, 16)).save(png)
            with patch(
                "depth_pipeline.inference.infer_depth", side_effect=_fake_infer_depth
            ):
                meta = process_image(str(png), str(Path(tmp) / "out"), target_res=33)
            self.assertFalse(meta["is_georeferenced"])
            self.assertFalse(meta["is_calibrated"])
            self.assertFalse(meta["srtm_aligned"])
            self.assertIsNone(meta["srtm_tile_id"])
            self.assertEqual(meta["warning"], "no_georef")
            self.assertIsNone(meta["r_squared"])
            self.assertTrue(Path(meta["heightmap_path"]).is_file())

    def test_process_image_geotiff_without_tile_stays_relative(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tif = Path(tmp) / "elsewhere.tif"
            _write_rgb_geotiff(tif, (1.0, 2.0, 1.1, 2.1))
            with patch(
                "depth_pipeline.inference.infer_depth", side_effect=_fake_infer_depth
            ):
                meta = process_image(
                    str(tif),
                    str(Path(tmp) / "out"),
                    target_res=33,
                )
            self.assertTrue(meta["is_georeferenced"])
            self.assertFalse(meta["is_calibrated"])
            self.assertFalse(meta["srtm_aligned"])
            self.assertEqual(meta["warning"], "srtm_unavailable")
            self.assertTrue(Path(meta["heightmap_path"]).is_file())

    def test_process_image_geotiff_with_tile_aligns(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tile_dir = Path(tmp) / "tiles"
            tile_dir.mkdir()
            _write_catalog(tile_dir, self.TILE_BBOX)
            _write_srtm_tile(tile_dir / "demo.tif", self.TILE_BBOX)
            img = Path(tmp) / "delhi.tif"
            _write_rgb_geotiff(img, (77.19, 28.59, 77.25, 28.65))
            out = Path(tmp) / "out"
            import os

            os.environ["DEPTHWIZARD_SRTM_DIR"] = str(tile_dir)
            os.environ["DEPTHWIZARD_ACCURACY_LOG"] = str(out / "accuracy_log.jsonl")
            try:
                with patch(
                    "depth_pipeline.inference.infer_depth",
                    side_effect=_fake_infer_depth,
                ):
                    meta = process_image(str(img), str(out), target_res=33)
            finally:
                os.environ.pop("DEPTHWIZARD_SRTM_DIR", None)
                os.environ.pop("DEPTHWIZARD_ACCURACY_LOG", None)
            self.assertTrue(meta["is_georeferenced"])
            self.assertTrue(meta["srtm_aligned"])
            self.assertEqual(meta["srtm_tile_id"], "demo_urban")
            self.assertTrue(Path(meta["srtm_aligned_path"]).is_file())
            hm = np.array(Image.open(meta["heightmap_path"]))
            self.assertEqual(hm.shape, (33, 33))
            # Calibration runs when sklearn is available; otherwise relative mode.
            try:
                import sklearn  # noqa: F401
            except ImportError:
                self.assertFalse(meta["is_calibrated"])
            else:
                self.assertTrue(meta["is_calibrated"])
                self.assertIsNone(meta["warning"])
                self.assertIsNotNone(meta["r_squared"])
                self.assertGreaterEqual(meta["sample_count"], 32)
                self.assertIsNotNone(meta["calibration"])


if __name__ == "__main__":
    unittest.main()

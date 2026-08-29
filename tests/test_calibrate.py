"""Unit tests for relative-depth → SRTM elevation calibration.

Run: python -m unittest tests.test_calibrate
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

from depth_pipeline.geospatial.calibrate import calibrate_to_srtm, encode_elevation_u16


def _fake_infer_depth(image):
    arr = np.asarray(image)
    h, w = arr.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    return ((xx / max(w - 1, 1) + yy / max(h - 1, 1)) / 2.0).astype(np.float32)

try:
    import sklearn  # noqa: F401

    HAS_SKLEARN = True
except ImportError:
    HAS_SKLEARN = False

try:
    import rasterio
    from rasterio.crs import CRS
    from rasterio.transform import from_bounds

    HAS_RASTERIO = True
except ImportError:
    HAS_RASTERIO = False


@unittest.skipUnless(HAS_SKLEARN, "scikit-learn not installed")
class CalibrateFitTests(unittest.TestCase):
    def test_linear_relationship_stays_linear(self) -> None:
        rng = np.random.default_rng(1)
        rel = rng.random((48, 48))
        srtm = 120.0 + 380.0 * rel + rng.normal(0.0, 0.3, rel.shape)
        result = calibrate_to_srtm(rel, srtm)
        self.assertTrue(result["ok"], result["warning"])
        self.assertEqual(result["model"], "linear")
        self.assertEqual(result["degree"], 1)
        self.assertGreater(result["r2"], 0.99)
        self.assertLess(result["rmse_m"], 1.0)
        self.assertEqual(result["elevation_m"].shape, rel.shape)
        self.assertGreaterEqual(result["sample_count"], 32)

    def test_quadratic_escalates_to_polynomial(self) -> None:
        yy, xx = np.mgrid[0:40, 0:40]
        rel = (xx + yy) / (40.0 + 40.0)
        srtm = 50.0 + 400.0 * (rel - 0.5) ** 2
        result = calibrate_to_srtm(rel, srtm)
        self.assertTrue(result["ok"], result["warning"])
        self.assertEqual(result["model"], "polynomial")
        self.assertEqual(result["degree"], 2)
        self.assertGreater(result["r2"], 0.95)

    def test_too_few_samples_skips(self) -> None:
        rel = np.linspace(0, 1, 16).reshape(4, 4)
        srtm = np.full((4, 4), np.nan)
        srtm[0, 0] = 10.0
        result = calibrate_to_srtm(rel, srtm)
        self.assertFalse(result["ok"])
        self.assertIn("not enough paired samples", result["warning"])

    def test_shape_mismatch_skips(self) -> None:
        result = calibrate_to_srtm(np.zeros((8, 8)), np.zeros((4, 4)))
        self.assertFalse(result["ok"])
        self.assertIn("shape", result["warning"])

    def test_encode_roundtrip_range(self) -> None:
        elev = np.linspace(100.0, 400.0, 25).reshape(5, 5)
        packed, min_e, max_e = encode_elevation_u16(elev)
        self.assertEqual(packed.dtype, np.uint16)
        self.assertAlmostEqual(min_e, 100.0, places=5)
        self.assertAlmostEqual(max_e, 400.0, places=5)
        self.assertEqual(int(packed.min()), 0)
        self.assertEqual(int(packed.max()), 65535)


@unittest.skipUnless(HAS_SKLEARN and HAS_RASTERIO, "sklearn/rasterio not installed")
class CalibratePipelineTests(unittest.TestCase):
    TILE_BBOX = (77.18, 28.58, 77.26, 28.66)

    def test_process_image_png_has_no_calibration(self) -> None:
        from depth_pipeline.run import process_image

        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "plain.png"
            Image.new("RGB", (16, 16)).save(png)
            with patch(
                "depth_pipeline.inference.infer_depth",
                side_effect=_fake_infer_depth,
            ):
                meta = process_image(str(png), str(Path(tmp) / "out"), target_res=33)
            self.assertFalse(meta["is_calibrated"])
            self.assertEqual(meta["warning"], "no_georef")
            self.assertIsNone(meta["r_squared"])
            self.assertIsNone(meta["sample_count"])
            self.assertIsNone(meta["calibration"])
            self.assertIsNone(meta["min_elev_m"])
            self.assertFalse((Path(tmp) / "out" / "calibration.json").is_file())

    def test_process_image_with_srtm_writes_calibration(self) -> None:
        from depth_pipeline.run import process_image

        with tempfile.TemporaryDirectory() as tmp:
            tile_dir = Path(tmp) / "tiles"
            tile_dir.mkdir()
            catalog = {
                "regions": [
                    {
                        "id": "demo_urban",
                        "terrain": "urban",
                        "bbox": list(self.TILE_BBOX),
                        "filename": "demo.tif",
                    }
                ]
            }
            (tile_dir / "regions.json").write_text(json.dumps(catalog), encoding="utf-8")
            west, south, east, north = self.TILE_BBOX
            transform = from_bounds(west, south, east, north, 32, 32)
            yy, xx = np.mgrid[0:32, 0:32]
            dem = 200.0 + xx.astype(np.float32) + yy.astype(np.float32)
            profile = {
                "driver": "GTiff",
                "height": 32,
                "width": 32,
                "count": 1,
                "dtype": "float32",
                "crs": CRS.from_epsg(4326),
                "transform": transform,
                "nodata": -32768,
            }
            with rasterio.open(tile_dir / "demo.tif", "w", **profile) as dst:
                dst.write(dem, 1)

            img = Path(tmp) / "delhi.tif"
            rgb = np.full((3, 16, 16), 80, dtype=np.uint8)
            rgb_tf = from_bounds(77.19, 28.59, 77.25, 28.65, 16, 16)
            with rasterio.open(
                img, "w", driver="GTiff", height=16, width=16, count=3,
                dtype="uint8", crs="EPSG:4326", transform=rgb_tf, photometric="RGB",
            ) as dst:
                dst.write(rgb)

            import os

            os.environ["DEPTHWIZARD_SRTM_DIR"] = str(tile_dir)
            os.environ["DEPTHWIZARD_ACCURACY_LOG"] = str(Path(tmp) / "accuracy_log.jsonl")
            try:
                with patch(
                    "depth_pipeline.inference.infer_depth",
                    side_effect=_fake_infer_depth,
                ):
                    meta = process_image(str(img), str(Path(tmp) / "out"), target_res=33)
            finally:
                os.environ.pop("DEPTHWIZARD_SRTM_DIR", None)
                os.environ.pop("DEPTHWIZARD_ACCURACY_LOG", None)

            self.assertTrue(meta["is_georeferenced"])
            self.assertTrue(meta["srtm_aligned"])
            self.assertTrue(meta["is_calibrated"])
            self.assertIsNone(meta["warning"])
            self.assertIsNotNone(meta["min_elev_m"])
            self.assertIsNotNone(meta["max_elev_m"])
            self.assertIsNotNone(meta["r_squared"])
            self.assertGreaterEqual(meta["sample_count"], 32)
            self.assertEqual(meta["srtm_tile_id"], "demo_urban")
            self.assertIsNotNone(meta["calibration"])
            self.assertIn(meta["calibration"]["model"], ("linear", "polynomial"))
            self.assertGreaterEqual(meta["calibration"]["sample_count"], 32)
            cal_path = Path(tmp) / "out" / "calibration.json"
            self.assertTrue(cal_path.is_file())
            sidecar = json.loads(cal_path.read_text(encoding="utf-8"))
            self.assertEqual(sidecar["sample_count"], meta["calibration"]["sample_count"])
            self.assertEqual(sidecar["r2"], meta["calibration"]["r2"])
            self.assertTrue(Path(meta["heightmap_path"]).is_file())


if __name__ == "__main__":
    unittest.main()

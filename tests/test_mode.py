"""Tests for resolve_elevation_mode fallback codes."""

from __future__ import annotations

import json
import os
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

try:
    import sklearn  # noqa: F401

    HAS_SKLEARN = True
except ImportError:
    HAS_SKLEARN = False


def _relative():
    hm = np.linspace(0, 65535, 36, dtype=np.uint16).reshape(6, 6)
    rel = hm.astype(np.float64) / 65535.0
    return rel, hm


def _assert_never_raises(test, path, rel, hm, tmp, tile_dir=None) -> None:
    try:
        mode = resolve_elevation_mode(
            path, rel, hm, output_dir=tmp, tile_dir=tile_dir,
        )
    except Exception as exc:  # pragma: no cover - failure path
        test.fail(f"resolve_elevation_mode raised {type(exc).__name__}: {exc}")
    test.assertIn(mode["warning"], (None, "no_georef", "bad_crs", "srtm_unavailable"))
    test.assertIsInstance(mode["is_calibrated"], bool)


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

    def test_never_raises_on_mvp_edge_inputs(self) -> None:
        rel, hm = _relative()
        with tempfile.TemporaryDirectory() as tmp:
            png = Path(tmp) / "plain.png"
            Image.new("RGB", (8, 8)).save(png)
            empty = Path(tmp) / "empty.png"
            empty.write_bytes(b"")
            junk = Path(tmp) / "corrupt.tif"
            junk.write_bytes(b"II*\x00not-a-tiff")
            missing = Path(tmp) / "nope.tif"
            _assert_never_raises(self, png, rel, hm, tmp)
            _assert_never_raises(self, empty, rel, hm, tmp)
            _assert_never_raises(self, junk, rel, hm, tmp)
            _assert_never_raises(self, missing, rel, hm, tmp)
            _assert_never_raises(self, Path(tmp), rel, hm, tmp)  # directory


@unittest.skipUnless(HAS_RASTERIO, "rasterio not installed")
class ModeGeoTiffTests(unittest.TestCase):
    TILE_BBOX = (77.18, 28.58, 77.26, 28.66)

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
            _assert_never_raises(self, tif, rel, hm, tmp)
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
            _assert_never_raises(self, tif, rel, hm, tmp)
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
            _assert_never_raises(self, tif, rel, hm, tmp, tile_dir=tmp)
        self.assertTrue(mode["is_georeferenced"])
        self.assertFalse(mode["is_calibrated"])
        self.assertEqual(mode["warning"], "srtm_unavailable")
        self.assertIsNone(mode["min_elev_m"])

    @unittest.skipUnless(HAS_SKLEARN, "scikit-learn not installed")
    def test_valid_geotiff_with_srtm_calibrates_and_logs(self) -> None:
        rel, hm = _relative()
        # Larger grid so sample count clears the calibration minimum.
        hm = (np.linspace(0, 65535, 48 * 48).reshape(48, 48)).astype(np.uint16)
        rel = hm.astype(np.float64) / 65535.0
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
            with rasterio.open(
                tile_dir / "demo.tif", "w",
                driver="GTiff", height=32, width=32, count=1, dtype="float32",
                crs=CRS.from_epsg(4326), transform=transform, nodata=-32768,
            ) as dst:
                dst.write(dem, 1)

            img = Path(tmp) / "delhi.tif"
            rgb_tf = from_bounds(77.19, 28.59, 77.25, 28.65, 16, 16)
            with rasterio.open(
                img, "w", driver="GTiff", height=16, width=16, count=3,
                dtype="uint8", crs="EPSG:4326", transform=rgb_tf, photometric="RGB",
            ) as dst:
                dst.write(np.full((3, 16, 16), 80, dtype=np.uint8))

            log = Path(tmp) / "accuracy_log.jsonl"
            os.environ["DEPTHWIZARD_ACCURACY_LOG"] = str(log)
            try:
                mode = resolve_elevation_mode(
                    img, rel, hm, output_dir=Path(tmp) / "out", tile_dir=tile_dir,
                )
                _assert_never_raises(
                    self, img, rel, hm, Path(tmp) / "out2", tile_dir=tile_dir,
                )
            finally:
                os.environ.pop("DEPTHWIZARD_ACCURACY_LOG", None)

            self.assertTrue(mode["is_georeferenced"])
            self.assertTrue(mode["is_calibrated"])
            self.assertIsNone(mode["warning"])
            self.assertEqual(mode["srtm_tile_id"], "demo_urban")
            self.assertTrue(log.is_file())
            row = json.loads(log.read_text(encoding="utf-8").splitlines()[-1])
            self.assertEqual(row["terrain"], "urban")
            self.assertIn("r_squared", row)
            self.assertIn("rmse_m", row)
            self.assertIn("mae_m", row)


if __name__ == "__main__":
    unittest.main()

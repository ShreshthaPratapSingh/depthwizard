"""Tests for depth_pipeline.export.export_artifacts.

The critical one is the Unity vertical-flip test: a bright spot in row 0 (north,
top of the image) must end up in the LAST row of the saved heightmap PNG (south
edge, where Unity's SetHeights puts row 0). This flip is silent to the eye, so
it needs an explicit guard.
"""
import numpy as np
from PIL import Image

from depth_pipeline.export import export_artifacts


def _read(path) -> np.ndarray:
    return np.asarray(Image.open(path))


def test_heightmap_shape_and_dtype(tmp_path):
    elev = np.random.rand(64, 80).astype(np.float32)
    src = (np.random.rand(64, 80, 3) * 255).astype(np.uint8)

    res = export_artifacts(elev, src, target_res=129, texture_size=64,
                           output_dir=tmp_path)

    hm = _read(res["heightmap_path"])
    assert hm.shape == (129, 129)
    assert hm.dtype == np.uint16


def test_heightmap_is_flipped_north_to_south(tmp_path):
    # Distinct bright spot in row 0 (north / top of image).
    n = 32
    elev = np.zeros((n, n), dtype=np.float32)
    elev[0, :] = 1.0
    src = np.zeros((n, n, 3), dtype=np.uint8)

    # target_res == n so the resize is 1:1 and the bright row stays crisp.
    res = export_artifacts(elev, src, target_res=n, texture_size=n,
                           output_dir=tmp_path)
    hm = _read(res["heightmap_path"]).astype(np.float64)

    brightest_row = int(np.argmax(hm.sum(axis=1)))
    assert brightest_row == n - 1, "row 0 (north) must land in the LAST PNG row"
    assert hm[-1].min() > hm[0].max(), "south edge brighter than north edge"


def test_texture_is_not_flipped(tmp_path):
    # Red top row, blue bottom row; must keep that order after export.
    n = 24
    elev = np.random.rand(n, n).astype(np.float32)
    src = np.zeros((n, n, 3), dtype=np.uint8)
    src[0, :] = (255, 0, 0)    # top = red
    src[-1, :] = (0, 0, 255)   # bottom = blue

    res = export_artifacts(elev, src, target_res=n, texture_size=n,
                           output_dir=tmp_path)
    tex = np.asarray(Image.open(res["texture_path"]).convert("RGB"))

    # Top row still red-dominant, bottom row still blue-dominant => unflipped.
    assert tex[0, :, 0].mean() > tex[0, :, 2].mean(), "top row should stay red"
    assert tex[-1, :, 2].mean() > tex[-1, :, 0].mean(), "bottom row should stay blue"


def test_confidence_defaults_to_full(tmp_path):
    n = 16
    elev = np.random.rand(n, n).astype(np.float32)
    src = np.zeros((n, n, 3), dtype=np.uint8)

    res = export_artifacts(elev, src, target_res=n, texture_size=n,
                           output_dir=tmp_path)
    conf = _read(res["confidence_mask_path"])

    assert conf.shape == (n, n)
    assert conf.dtype == np.uint8
    assert np.all(conf == 255)

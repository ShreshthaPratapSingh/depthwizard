"""Tests for the tiling module (C1 / FR19)."""

import numpy as np
import pytest


def test_cosine_weight_shape():
    """Weight mask has the correct shape and range."""
    from depth_pipeline.tiling import _cosine_weight

    w = _cosine_weight(100, 200, 20)
    assert w.shape == (100, 200)
    assert w.dtype == np.float32
    assert w.min() >= 0.0
    assert w.max() <= 1.0


def test_cosine_weight_center_is_one():
    """Center of the weight mask should be 1.0."""
    from depth_pipeline.tiling import _cosine_weight

    w = _cosine_weight(100, 100, 20)
    assert w[50, 50] == pytest.approx(1.0, abs=1e-5)


def test_cosine_weight_edge_is_zero():
    """Corners of the weight mask should be near 0.0."""
    from depth_pipeline.tiling import _cosine_weight

    w = _cosine_weight(100, 100, 20)
    assert w[0, 0] < 0.01
    assert w[0, 99] < 0.01
    assert w[99, 0] < 0.01


def test_tile_coords_single_tile():
    """Small image that fits in one tile → one pair."""
    from depth_pipeline.tiling import _tile_coords

    coords = _tile_coords(512, 1024, 128)
    assert len(coords) == 1
    assert coords[0] == (0, 512)


def test_tile_coords_multiple_tiles():
    """Larger image → multiple tiles with correct coverage."""
    from depth_pipeline.tiling import _tile_coords

    coords = _tile_coords(2048, 1024, 128)
    assert len(coords) >= 2
    # First tile starts at 0
    assert coords[0][0] == 0
    # Last tile ends at total
    assert coords[-1][1] == 2048
    # All tiles are at most tile_size wide
    for s, e in coords:
        assert e - s <= 1024


def test_needs_tiling():
    """needs_tiling returns True only for large images."""
    from depth_pipeline.tiling import needs_tiling

    assert not needs_tiling(512, 512, 1_000_000)
    assert needs_tiling(2000, 2000, 1_000_000)


def test_tile_and_infer_identity():
    """tile_and_infer with identity infer_fn produces the original shape."""
    from depth_pipeline.tiling import tile_and_infer

    rgb = np.random.randint(0, 255, (256, 256, 3), dtype=np.uint8)

    def identity_infer(tile_rgb):
        h, w = tile_rgb.shape[:2]
        return np.ones((h, w), dtype=np.float32) * 0.5

    result = tile_and_infer(rgb, identity_infer, tile_size=128, overlap=32)
    assert result.shape == (256, 256)
    assert result.dtype == np.float32
    # With constant output, blending should produce the same constant
    assert np.allclose(result, 0.5, atol=0.01)


def test_tile_and_infer_seamless():
    """Verify blending produces smooth transitions, not hard seams."""
    from depth_pipeline.tiling import tile_and_infer

    # Create a smooth gradient image
    rgb = np.zeros((300, 300, 3), dtype=np.uint8)

    # Infer function that returns a gradient based on position
    def gradient_infer(tile_rgb):
        h, w = tile_rgb.shape[:2]
        return np.linspace(0.0, 1.0, w, dtype=np.float32)[np.newaxis, :].repeat(h, axis=0)

    result = tile_and_infer(rgb, gradient_infer, tile_size=200, overlap=50)
    assert result.shape == (300, 300)

    # Check that there are no sharp discontinuities
    # The max gradient between adjacent pixels should be small
    diffs = np.abs(np.diff(result, axis=1))
    assert diffs.max() < 0.05, f"Sharp seam detected: max diff = {diffs.max()}"

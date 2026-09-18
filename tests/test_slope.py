"""Tests for the slope raster module (C2 / FR20)."""

import numpy as np
import pytest
from pathlib import Path
import tempfile


def test_compute_slope_flat():
    """Flat surface → zero slope everywhere."""
    from depth_pipeline.slope import compute_slope

    flat = np.ones((100, 100), dtype=np.float32) * 50.0
    slope = compute_slope(flat)
    assert slope.shape == (100, 100)
    assert slope.dtype == np.float32
    assert np.allclose(slope, 0.0, atol=0.01)


def test_compute_slope_ramp():
    """Linear ramp → uniform non-zero slope."""
    from depth_pipeline.slope import compute_slope

    ramp = np.tile(np.arange(100, dtype=np.float32), (100, 1))
    slope = compute_slope(ramp, pixel_size=1.0)
    assert slope.shape == (100, 100)
    # Interior pixels should all have the same slope
    interior = slope[5:-5, 5:-5]
    expected = np.degrees(np.arctan(1.0))  # gradient mag = 1.0
    assert np.allclose(interior, expected, atol=0.1)


def test_compute_slope_range():
    """Slope values are clamped to [0, 90]."""
    from depth_pipeline.slope import compute_slope

    np.random.seed(42)
    noisy = np.random.randn(50, 50).astype(np.float32) * 1000
    slope = compute_slope(noisy)
    assert slope.min() >= 0.0
    assert slope.max() <= 90.0


def test_export_slope_png():
    """export_slope_png produces a valid PNG file."""
    from depth_pipeline.slope import compute_slope, export_slope_png

    elevation = np.random.rand(64, 64).astype(np.float32) * 100
    slope = compute_slope(elevation)

    with tempfile.TemporaryDirectory() as tmpdir:
        path = export_slope_png(slope, Path(tmpdir))
        assert path is not None
        assert path.exists()
        assert path.suffix == ".png"
        assert path.stat().st_size > 0

        # Verify it's a valid image
        from PIL import Image
        with Image.open(path) as img:
            assert img.mode == "L"
            assert img.size == (64, 64)

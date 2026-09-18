"""Tests for the GCP correction module (C4 / FR18)."""

import numpy as np
import pytest


def test_gcp_pixel_correction_identity():
    """GCPs that match the prediction → scale≈1, shift≈0."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_pixel

    # Create a simple elevation ramp
    elev = np.arange(100, dtype=np.float32).reshape(10, 10)

    # GCPs that exactly match the values
    gcps = [
        (2, 2, float(elev[2, 2])),
        (5, 5, float(elev[5, 5])),
        (8, 3, float(elev[3, 8])),
    ]

    result = correct_with_gcps_pixel(elev, gcps)
    assert result.ok
    assert result.scale == pytest.approx(1.0, abs=0.01)
    assert result.shift == pytest.approx(0.0, abs=0.5)
    assert result.r2 is not None and result.r2 > 0.99
    assert result.n_used == 3


def test_gcp_pixel_correction_scaled():
    """GCPs with a known scale+shift → correction recovers them."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_pixel

    # Predicted elevation (0-1 range)
    elev = np.arange(100, dtype=np.float32).reshape(10, 10) / 100.0

    # "True" elevation is 2x + 10
    true_scale = 2.0
    true_shift = 10.0

    gcps = [
        (1, 1, true_scale * elev[1, 1] + true_shift),
        (3, 7, true_scale * elev[7, 3] + true_shift),
        (8, 2, true_scale * elev[2, 8] + true_shift),
        (5, 5, true_scale * elev[5, 5] + true_shift),
    ]

    result = correct_with_gcps_pixel(elev, gcps)
    assert result.ok
    assert result.scale == pytest.approx(true_scale, abs=0.1)
    assert result.shift == pytest.approx(true_shift, abs=0.5)
    assert result.elevation_m is not None
    assert result.elevation_m.shape == (10, 10)


def test_gcp_too_few():
    """Fewer than 3 GCPs → not ok."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_pixel

    elev = np.ones((10, 10), dtype=np.float32)
    result = correct_with_gcps_pixel(elev, [(0, 0, 1.0), (5, 5, 2.0)])
    assert not result.ok
    assert "need at least" in result.warning.lower()


def test_gcp_out_of_bounds():
    """GCPs outside the elevation grid are skipped."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_pixel

    elev = np.arange(25, dtype=np.float32).reshape(5, 5)
    gcps = [
        (100, 100, 1.0),  # way out of bounds
        (200, 200, 2.0),
        (300, 300, 3.0),
    ]
    result = correct_with_gcps_pixel(elev, gcps)
    assert not result.ok


def test_gcp_geo_correction():
    """Geographic GCPs are projected to pixel coordinates correctly."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_geo

    elev = np.arange(100, dtype=np.float32).reshape(10, 10)
    bbox = [0.0, 0.0, 1.0, 1.0]  # simple unit square

    # GCPs in geographic coords (lon, lat, elevation)
    gcps = [
        (0.2, 0.8, float(elev[2, 2])),  # top-left-ish
        (0.5, 0.5, float(elev[5, 5])),  # center
        (0.8, 0.2, float(elev[8, 8])),  # bottom-right-ish
    ]

    result = correct_with_gcps_geo(elev, gcps, bbox)
    assert result.ok
    assert result.n_used >= 3


def test_gcp_to_dict():
    """GCPCorrectionResult.to_dict() returns serializable dict."""
    from depth_pipeline.geospatial.gcp import correct_with_gcps_pixel
    import json

    elev = np.arange(100, dtype=np.float32).reshape(10, 10)
    gcps = [
        (2, 2, float(elev[2, 2])),
        (5, 5, float(elev[5, 5])),
        (8, 3, float(elev[3, 8])),
    ]
    result = correct_with_gcps_pixel(elev, gcps)
    d = result.to_dict()

    # Must be JSON-serializable
    json_str = json.dumps(d)
    assert "ok" in json_str

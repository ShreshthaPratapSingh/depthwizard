"""Tests for depth_pipeline.postprocess.

Covers the two spatial corrections that show up in the output — water gets
flattened to a low baseline and marked low-confidence, vegetation gets marked
low-confidence — plus the shape contract. Runs on whichever smoothing backend
is installed (guided filter or bilateral fallback).
"""
import numpy as np

from depth_pipeline.config import (
    FULL_CONFIDENCE,
    WATER_BASELINE_PERCENTILE,
    WATER_CONFIDENCE,
)
from depth_pipeline.postprocess import _water_mask, postprocess


def test_water_rectangle_flattened_and_low_confidence():
    h, w = 64, 64
    # A noisy elevation so water masking has something to flatten.
    rng = np.random.default_rng(0)
    elev = rng.random((h, w)).astype(np.float32)

    # Neutral gray background (not blue, textured enough to not be "flat").
    src = np.full((h, w, 3), 120, dtype=np.uint8)
    src[:, :, 0] = (rng.random((h, w)) * 255).astype(np.uint8)  # texture on R
    src[:, :, 1] = (rng.random((h, w)) * 255).astype(np.uint8)  # texture on G

    # A clear blue, low-variance (uniform) rectangle == water.
    ys, xs = slice(8, 24), slice(8, 24)
    src[ys, xs] = (10, 10, 230)

    out = postprocess(elev, src)
    elevation, confidence = out["elevation"], out["confidence"]

    # The blue, uniform rectangle interior must be detected as water. (Edge
    # pixels straddle the boundary and are legitimately not "flat", so check a
    # core well inside the rectangle.)
    mask = _water_mask(src)
    assert mask[12:20, 12:20].all(), "rectangle interior should read as water"

    # Every masked pixel is flattened to the 5th percentile of the non-water
    # elevation (of the smoothed output, which is what the algorithm sees).
    baseline = np.percentile(elevation[~mask], WATER_BASELINE_PERCENTILE)
    assert np.allclose(elevation[mask], baseline), "water should be a flat baseline"

    # Masked pixels get the reduced water confidence, not full.
    assert np.all(confidence[mask] == WATER_CONFIDENCE)
    assert WATER_CONFIDENCE < FULL_CONFIDENCE


def test_vegetation_region_low_confidence_and_contiguous():
    h, w = 64, 64
    elev = np.full((h, w), 0.5, dtype=np.float32)
    rng = np.random.default_rng(3)

    # Neutral gray background -> full confidence.
    src = np.full((h, w, 3), 120, dtype=np.uint8)

    # A textured olive-canopy rectangle: green-dominant but LOW saturation, with
    # per-crown noise and scattered dark "shadow" holes -- i.e. what real aerial
    # forest looks like, not a saturated pure-green block. Without percentile
    # normalization + morphological closing this comes out speckled.
    ys, xs = slice(24, 52), slice(24, 52)   # 28x28
    rh, rw = 28, 28
    canopy = np.stack([
        rng.integers(35, 65, (rh, rw)),      # R low
        rng.integers(90, 130, (rh, rw)),     # G high
        rng.integers(25, 55, (rh, rw)),      # B low
    ], axis=-1).astype(np.uint8)
    holes = rng.random((rh, rw)) < 0.25      # ~25% dark, low-ExG shadow gaps
    canopy[holes] = (15, 18, 12)
    src[ys, xs] = canopy

    out = postprocess(elev, src)
    confidence = out["confidence"]

    # The canopy must read as a broad, contiguous low-confidence zone: at least
    # 70% of the rectangle bounds, not a scattered handful of crown pixels.
    rect = confidence[ys, xs]
    low_frac = float(np.mean(rect < FULL_CONFIDENCE))
    assert low_frac >= 0.70, f"canopy should be broadly low-confidence, got {low_frac:.2f}"

    # A neutral corner stays fully confident.
    assert confidence[0, 0] == FULL_CONFIDENCE


def test_output_shapes_match_input():
    h, w = 40, 55
    elev = np.random.default_rng(1).random((h, w)).astype(np.float32)
    src = (np.random.default_rng(2).random((h, w, 3)) * 255).astype(np.uint8)

    out = postprocess(elev, src)

    assert out["elevation"].shape == (h, w)
    assert out["elevation"].dtype == np.float32
    assert out["confidence"].shape == (h, w)
    assert out["confidence"].dtype == np.uint8

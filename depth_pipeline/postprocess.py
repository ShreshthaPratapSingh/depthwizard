"""Postprocessing on the raw ``elevation01`` between inference and export.

Runs on the float32 HxW 0-1 elevation map that ``infer_depth`` returns and the
matching RGB source image. Produces a cleaned elevation and a confidence mask:

1. Edge-preserving smoothing (guided filter, bilateral fallback) — never a
   plain Gaussian blur, which would wash out ridgelines, the detail that
   carries a flythrough.
2. Water masking — flatten blue, low-texture regions to a low baseline instead
   of leaving noisy monocular depth there.
3. Vegetation confidence — dense canopy (high ExG) is unreliable for monocular
   depth, so it is flagged in the confidence mask (elevation is NOT altered).

All thresholds live in ``config`` — no magic numbers here.
"""
import logging

import cv2
import numpy as np

from .config import (
    BILATERAL_D,
    BILATERAL_SIGMA_COLOR,
    BILATERAL_SIGMA_SPACE,
    EXG_PERCENTILE_HIGH,
    EXG_PERCENTILE_LOW,
    EXG_VEG_THRESHOLD,
    FULL_CONFIDENCE,
    GUIDED_FILTER_EPS,
    GUIDED_FILTER_RADIUS,
    VEG_CONFIDENCE,
    VEG_MORPH_KERNEL_SIZE,
    WATER_BASELINE_PERCENTILE,
    WATER_BLUE_RATIO_THRESHOLD,
    WATER_CONFIDENCE,
    WATER_LOCAL_STDDEV_KERNEL,
    WATER_VARIANCE_THRESHOLD,
)

logger = logging.getLogger(__name__)

# Emit the "ximgproc missing, using bilateral" warning only once per process.
_warned_no_ximgproc = False


def _smooth_edge_preserving(elevation01: np.ndarray,
                            source_rgb: np.ndarray) -> np.ndarray:
    """Guided-filter smooth guided by the RGB image; bilateral fallback.

    Both preserve edges (ridgelines); a Gaussian blur would not, so it is
    deliberately never used here.
    """
    elev = elevation01.astype(np.float32)
    ximgproc = getattr(cv2, "ximgproc", None)
    if ximgproc is not None:
        guide = source_rgb.astype(np.float32) / 255.0
        smoothed = ximgproc.guidedFilter(
            guide=guide, src=elev,
            radius=GUIDED_FILTER_RADIUS, eps=GUIDED_FILTER_EPS,
        )
    else:
        global _warned_no_ximgproc
        if not _warned_no_ximgproc:
            logger.warning(
                "cv2.ximgproc unavailable (opencv-contrib not installed); "
                "falling back to bilateralFilter for edge-preserving smoothing."
            )
            _warned_no_ximgproc = True
        smoothed = cv2.bilateralFilter(
            elev, d=BILATERAL_D,
            sigmaColor=BILATERAL_SIGMA_COLOR, sigmaSpace=BILATERAL_SIGMA_SPACE,
        )
    return np.clip(smoothed, 0.0, 1.0).astype(np.float32)


def _local_stddev(gray: np.ndarray, kernel: int) -> np.ndarray:
    """Per-pixel stddev over a ``kernel`` x ``kernel`` window (0-255 scale)."""
    g = gray.astype(np.float32)
    ksize = (kernel, kernel)
    mean = cv2.blur(g, ksize)
    mean_sq = cv2.blur(g * g, ksize)
    var = np.clip(mean_sq - mean * mean, 0.0, None)
    return np.sqrt(var)


def _water_mask(source_rgb: np.ndarray) -> np.ndarray:
    """Water = blue-ish AND locally flat (both conditions, to cut false hits)."""
    rgb = source_rgb.astype(np.float32)
    g, b = rgb[..., 1], rgb[..., 2]

    blue_ratio = (b - g) / (b + g + 1e-6)
    is_blue = blue_ratio > WATER_BLUE_RATIO_THRESHOLD

    gray = cv2.cvtColor(source_rgb, cv2.COLOR_RGB2GRAY)
    is_flat = _local_stddev(gray, WATER_LOCAL_STDDEV_KERNEL) < WATER_VARIANCE_THRESHOLD

    return is_blue & is_flat


def _veg_confidence(source_rgb: np.ndarray) -> np.ndarray:
    """255 everywhere, dropped to VEG_CONFIDENCE over the (closed) canopy zone.

    ExG is normalized against its own [low, high] percentiles per image so the
    same real, low-saturation olive canopy fires regardless of exposure. A
    morphological closing then merges the speckled per-crown detections into a
    contiguous region before thresholding, so the overlay reads as a clear zone
    rather than salt-and-pepper texture noise.
    """
    rgb = source_rgb.astype(np.float32)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]

    exg = 2.0 * g - r - b

    # Per-image percentile normalization (adapts to this image's contrast).
    lo, hi = np.percentile(exg, [EXG_PERCENTILE_LOW, EXG_PERCENTILE_HIGH])
    exg_norm = np.clip((exg - lo) / (hi - lo + 1e-6), 0.0, 1.0).astype(np.float32)

    # Close over the normalized ExG to bridge shadow gaps between tree crowns,
    # turning speckle into a smooth, contiguous canopy region before threshold.
    kernel = np.ones((VEG_MORPH_KERNEL_SIZE, VEG_MORPH_KERNEL_SIZE), np.uint8)
    exg_closed = cv2.morphologyEx(exg_norm, cv2.MORPH_CLOSE, kernel)

    confidence = np.full(source_rgb.shape[:2], FULL_CONFIDENCE, dtype=np.uint8)
    confidence[exg_closed >= EXG_VEG_THRESHOLD] = VEG_CONFIDENCE
    return confidence


def postprocess(elevation01: np.ndarray, source_rgb: np.ndarray) -> dict:
    """Clean ``elevation01`` and build its confidence mask.

    Returns ``{"elevation": float32 HxW 0-1, "confidence": uint8 HxW 0-255}``,
    both the same HxW as the input.
    """
    source_rgb = np.asarray(source_rgb)

    # 1. Edge-preserving smoothing.
    elevation = _smooth_edge_preserving(elevation01, source_rgb)

    # 2. Water mask: flatten to a low baseline (percentile of non-water depth).
    water = _water_mask(source_rgb)
    non_water = elevation[~water]
    if non_water.size:
        baseline = float(np.percentile(non_water, WATER_BASELINE_PERCENTILE))
    else:  # everything read as water — fall back to the global percentile.
        baseline = float(np.percentile(elevation, WATER_BASELINE_PERCENTILE))
    elevation = elevation.copy()
    elevation[water] = baseline

    # 3. Confidence: vegetation lowers it; water lowers it too (elevation is a
    #    guess there). Apply water last so masked water wins over any veg value.
    confidence = _veg_confidence(source_rgb)
    confidence[water] = WATER_CONFIDENCE

    # Sanity-check signal: how much of the image ended up low-confidence.
    low_conf_fraction = float(np.mean(confidence < FULL_CONFIDENCE))
    logger.info(
        "postprocess: %.1f%% of the image classified low-confidence "
        "(water + vegetation)", low_conf_fraction * 100.0,
    )

    return {
        "elevation": elevation.astype(np.float32),
        "confidence": confidence,
    }

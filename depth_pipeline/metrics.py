"""Elevation accuracy metrics against a reference DEM.

RMSE/MAE after scale+shift alignment can look acceptable even when the
prediction carries little real elevation signal - a smooth gradient with
the right global bias but no real local structure will still score
deceptively well on RMSE alone. pearson_r and slope_rmse must always be
reported alongside RMSE/MAE, not omitted, since they are not fooled by
alignment. A pearson_r below ~0.3-0.4 indicates the prediction is not
meaningfully tracking real elevation, regardless of what RMSE says.
"""

from __future__ import annotations

import cv2
import numpy as np


def _gradient_magnitude(arr: np.ndarray) -> np.ndarray:
    """Per-pixel gradient magnitude via np.gradient + hypot(dy, dx)."""
    dy, dx = np.gradient(arr)
    return np.hypot(dy, dx)


def evaluate(pred_elevation: np.ndarray, ref_dem: np.ndarray,
             valid_mask: np.ndarray = None) -> dict:
    """Score a predicted elevation map against a reference DEM.

    ``pred_elevation`` is float HxW (relative 0-1 or metric; a least-squares
    scale+shift is fit before any error metric, so absolute units do not
    matter). ``ref_dem`` may be a different shape - it is resampled to
    ``pred_elevation``'s shape with ``cv2.resize`` / ``INTER_LINEAR`` before
    comparison. ``valid_mask`` is an optional bool array matching
    ``pred_elevation``'s shape (True = usable); it should exclude nodata in
    ``ref_dem`` (e.g. SRTM voids). If None, all pixels are candidates.

    Returns a dict with ``mae_m``, ``rmse_m``, ``pearson_r``, ``slope_rmse``,
    ``n_valid_px``, ``fit_scale`` and ``fit_shift``. See the module docstring:
    ``pearson_r`` and ``slope_rmse`` are the metrics that alignment cannot game.
    """
    pred = np.asarray(pred_elevation, dtype=np.float64)
    ref = np.asarray(ref_dem, dtype=np.float64)

    # 1. Resample ref to pred's grid if the shapes differ. cv2.resize takes
    #    dsize as (width, height), i.e. (cols, rows).
    if ref.shape != pred.shape:
        h, w = pred.shape[:2]
        ref = cv2.resize(
            ref.astype(np.float32), (w, h), interpolation=cv2.INTER_LINEAR
        ).astype(np.float64)

    # Only compare where both arrays are finite (drops NaN/inf nodata in ref)
    # and, if given, where the caller's mask says the pixel is usable.
    valid = np.isfinite(pred) & np.isfinite(ref)
    if valid_mask is not None:
        valid &= np.asarray(valid_mask, dtype=bool)

    n_valid = int(valid.sum())
    p = pred[valid]
    r = ref[valid]

    # Degenerate: not enough overlap (or a constant prediction) to fit anything.
    if n_valid < 2 or np.std(p) == 0.0:
        return {
            "mae_m": float("nan"),
            "rmse_m": float("nan"),
            "pearson_r": float("nan"),
            "slope_rmse": float("nan"),
            "n_valid_px": n_valid,
            "fit_scale": float("nan"),
            "fit_shift": float("nan"),
        }

    # 2. Least-squares scale+shift: pred_aligned = a*pred + b, minimizing
    #    squared error against ref. Required because our output is not in the
    #    same units/scale as the reference DEM.
    a, b = np.polyfit(p, r, 1)
    p_aligned = a * p + b

    # 3a. MAE / RMSE of the aligned prediction vs ref.
    diff = p_aligned - r
    mae = float(np.mean(np.abs(diff)))
    rmse = float(np.sqrt(np.mean(diff ** 2)))

    # 3b. Pearson r on the RAW pred vs ref. Correlation is scale/shift
    #     invariant, so alignment cannot change it - it is computed here on the
    #     unaligned values purely for clarity.
    pearson_r = float(np.corrcoef(p, r)[0, 1])

    # 3c. Slope RMSE: compare local shape via gradient magnitude, independent
    #     of any global bias. Computed on the full 2D arrays, then restricted to
    #     valid pixels where both gradient maps are finite (gradients next to a
    #     nodata hole go NaN and are dropped).
    grad_pred = _gradient_magnitude(a * pred + b)
    grad_ref = _gradient_magnitude(ref)
    slope_valid = valid & np.isfinite(grad_pred) & np.isfinite(grad_ref)
    if slope_valid.any():
        gdiff = grad_pred[slope_valid] - grad_ref[slope_valid]
        slope_rmse = float(np.sqrt(np.mean(gdiff ** 2)))
    else:
        slope_rmse = float("nan")

    return {
        "mae_m": mae,
        "rmse_m": rmse,
        "pearson_r": pearson_r,
        "slope_rmse": slope_rmse,
        "n_valid_px": n_valid,
        "fit_scale": float(a),
        "fit_shift": float(b),
    }

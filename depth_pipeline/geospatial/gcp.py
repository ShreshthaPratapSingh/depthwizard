"""Ground Control Point (GCP) correction for predicted DSMs.

When a user provides surveyed ground-truth elevations at known coordinates,
this module applies a least-squares affine correction to the predicted DSM,
reducing systematic bias without re-running the depth model.

The correction model is:

    corrected_elevation = a * predicted_elevation + b

where ``a`` (scale) and ``b`` (shift) are fit by ordinary least squares from
the GCP pairs.  This is deliberately a simple affine (degree-1) correction:
higher-order polynomial correction is risky with few GCPs because it can
overfit and warp the DSM wildly between control points.

For georeferenced DSMs the GCPs are specified as ``(lon, lat, elevation_m)``
and are projected to the DSM's pixel grid.  For relative DSMs the GCPs are
``(col_px, row_px, reference_value)`` and indexing is direct.

Never raises to its caller — returns a result dict with ``ok=False`` and a
human-readable ``warning`` on any failure.
"""

from __future__ import annotations

import logging
import math
from typing import Any

import numpy as np

logger = logging.getLogger(__name__)

_MIN_GCPS = 3  # absolute minimum to fit scale + shift


class GCPCorrectionResult:
    """Container for a GCP correction result (dict-like for JSON compat)."""

    def __init__(
        self,
        ok: bool,
        elevation_m: np.ndarray | None = None,
        scale: float | None = None,
        shift: float | None = None,
        r2: float | None = None,
        rmse_m: float | None = None,
        n_used: int = 0,
        warning: str | None = None,
    ):
        self.ok = ok
        self.elevation_m = elevation_m
        self.scale = scale
        self.shift = shift
        self.r2 = r2
        self.rmse_m = rmse_m
        self.n_used = n_used
        self.warning = warning

    def to_dict(self) -> dict[str, Any]:
        return {
            "ok": self.ok,
            "scale": self.scale,
            "shift": self.shift,
            "r2": self.r2,
            "rmse_m": self.rmse_m,
            "n_used": self.n_used,
            "warning": self.warning,
        }


def _skipped(warning: str, n_used: int = 0) -> GCPCorrectionResult:
    return GCPCorrectionResult(ok=False, warning=warning, n_used=n_used)


def correct_with_gcps_pixel(
    elevation: np.ndarray,
    gcps: list[tuple[int, int, float]],
) -> GCPCorrectionResult:
    """Apply GCP correction using pixel coordinates.

    Parameters
    ----------
    elevation : float HxW array
        Predicted elevation (metric or relative).
    gcps : list of (col, row, reference_value)
        Each GCP is ``(x_pixel, y_pixel, known_elevation)``.

    Returns
    -------
    GCPCorrectionResult
    """
    elev = np.asarray(elevation, dtype=np.float64)
    h, w = elev.shape[:2]

    if len(gcps) < _MIN_GCPS:
        return _skipped(
            f"need at least {_MIN_GCPS} GCPs, got {len(gcps)}",
            n_used=len(gcps),
        )

    pred_vals = []
    ref_vals = []
    for col, row, ref_z in gcps:
        if 0 <= row < h and 0 <= col < w and np.isfinite(elev[row, col]):
            pred_vals.append(float(elev[row, col]))
            ref_vals.append(float(ref_z))

    n_used = len(pred_vals)
    if n_used < _MIN_GCPS:
        return _skipped(
            f"only {n_used} GCPs land on valid pixels (need {_MIN_GCPS})",
            n_used=n_used,
        )

    return _fit_and_apply(elev, np.array(pred_vals), np.array(ref_vals), n_used)


def correct_with_gcps_geo(
    elevation: np.ndarray,
    gcps: list[tuple[float, float, float]],
    bbox: list[float],
) -> GCPCorrectionResult:
    """Apply GCP correction using geographic coordinates.

    Parameters
    ----------
    elevation : float HxW array
        Predicted elevation in meters.
    gcps : list of (longitude, latitude, elevation_m)
        Each GCP is in the same CRS as the DSM's bbox.
    bbox : [west, south, east, north]
        Geographic extent of the elevation array.

    Returns
    -------
    GCPCorrectionResult
    """
    elev = np.asarray(elevation, dtype=np.float64)
    h, w = elev.shape[:2]
    west, south, east, north = bbox

    if len(gcps) < _MIN_GCPS:
        return _skipped(
            f"need at least {_MIN_GCPS} GCPs, got {len(gcps)}",
            n_used=len(gcps),
        )

    if east <= west or north <= south:
        return _skipped("invalid bounding box")

    # Project GCPs to pixel coordinates
    pixel_gcps = []
    for lon, lat, ref_z in gcps:
        col = int(round((lon - west) / (east - west) * (w - 1)))
        row = int(round((north - lat) / (north - south) * (h - 1)))
        pixel_gcps.append((col, row, ref_z))

    return correct_with_gcps_pixel(elev, pixel_gcps)


def _fit_and_apply(
    elevation: np.ndarray,
    pred: np.ndarray,
    ref: np.ndarray,
    n_used: int,
) -> GCPCorrectionResult:
    """Fit affine correction and apply to the full elevation grid."""
    try:
        if np.std(pred) < 1e-10:
            return _skipped(
                "predicted elevation at GCPs has no variance; "
                "correction would be meaningless",
                n_used=n_used,
            )

        # Least-squares: ref = a * pred + b
        coeffs = np.polyfit(pred, ref, 1)
        a, b = float(coeffs[0]), float(coeffs[1])

        # Apply
        corrected = a * elevation + b

        # Evaluate fit quality
        fitted = a * pred + b
        residuals = ref - fitted
        rmse = float(np.sqrt(np.mean(residuals ** 2)))

        ss_res = float(np.sum(residuals ** 2))
        ss_tot = float(np.sum((ref - np.mean(ref)) ** 2))
        r2 = 1.0 - ss_res / ss_tot if ss_tot > 0 else float("nan")

        warn = None
        if not math.isfinite(r2) or r2 < 0.5:
            warn = (
                f"low GCP correction R² ({r2:.3f}); corrected elevations "
                "may be unreliable"
            )

        logger.info(
            "GCP correction: a=%.4f b=%.4f R²=%.3f RMSE=%.2f m (n=%d)",
            a, b, r2, rmse, n_used,
        )

        return GCPCorrectionResult(
            ok=True,
            elevation_m=corrected.astype(np.float32),
            scale=a,
            shift=b,
            r2=r2,
            rmse_m=rmse,
            n_used=n_used,
            warning=warn,
        )

    except Exception as exc:
        return _skipped(
            f"GCP correction fit failed: {exc}",
            n_used=n_used,
        )

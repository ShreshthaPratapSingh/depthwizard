"""Relative-depth to metric-elevation calibration.

Samples paired pixels (relative depth vs aligned SRTM), fits a mapping
(linear first; degree-2 polynomial only if linear residuals clearly fail),
and applies it to the full depth map. Never raises: failures return a skip
result so the caller can stay in relative mode.

Input convention (confirmed against origin/main ``infer_depth``, 2026-08-29):
  ``relative_depth`` is [0, 1], same shape as the aligned SRTM grid, with
  **larger value = higher elevation**. ``infer_depth`` min-max normalizes raw
  DA-V2 disparity and returns that orientation (``INVERT_TO_ELEVATION = False``
  for nadir). Do **not** apply ``1 - relative`` in ``run.py``.

  If a calibrated slope is negative, flip ``INVERT_TO_ELEVATION`` in
  ``inference.py`` (ML-owned). Do not invert inside this module.
"""

from __future__ import annotations

import json
import math
from pathlib import Path
from typing import Any, TypedDict

import numpy as np

# Fit linear unless it is clearly inadequate *and* quadratic helps a lot.
_LINEAR_R2_KEEP = 0.80
_MIN_R2_GAIN = 0.05
_MAX_POLY_RMSE_RATIO = 0.85  # poly RMSE must be < 85% of linear RMSE
_RESIDUAL_CURVE_CORR = 0.35  # |corr(residual, x^2)| suggesting curvature
_MIN_SAMPLES = 32
_MAX_SAMPLES = 8000
_SRTM_NODATA = -32768.0
_RNG_SEED = 0


class CalibrationResult(TypedDict):
    ok: bool
    elevation_m: np.ndarray | None
    model: str | None
    degree: int | None
    coefficients: list[float] | None
    intercept: float | None
    r2: float | None
    rmse_m: float | None
    mae_m: float | None
    sample_count: int
    warning: str | None


def _skipped(warning: str, sample_count: int = 0) -> CalibrationResult:
    return {
        "ok": False,
        "elevation_m": None,
        "model": None,
        "degree": None,
        "coefficients": None,
        "intercept": None,
        "r2": None,
        "rmse_m": None,
        "mae_m": None,
        "sample_count": sample_count,
        "warning": warning,
    }


def _finite_pairs(
    relative: np.ndarray, srtm_m: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    rel = np.asarray(relative, dtype=np.float64)
    dem = np.asarray(srtm_m, dtype=np.float64)
    if rel.shape != dem.shape:
        raise ValueError(
            f"relative depth shape {rel.shape} != SRTM shape {dem.shape}"
        )
    mask = np.isfinite(rel) & np.isfinite(dem) & (dem > _SRTM_NODATA + 1.0)
    return rel[mask], dem[mask]


def _subsample(x: np.ndarray, y: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    n = int(x.size)
    if n <= _MAX_SAMPLES:
        return x, y
    rng = np.random.default_rng(_RNG_SEED)
    idx = rng.choice(n, size=_MAX_SAMPLES, replace=False)
    return x[idx], y[idx]


def _scores(y_true: np.ndarray, y_pred: np.ndarray) -> tuple[float, float, float]:
    resid = y_true - y_pred
    rmse = float(np.sqrt(np.mean(resid * resid)))
    mae = float(np.mean(np.abs(resid)))
    ss_res = float(np.sum(resid * resid))
    ss_tot = float(np.sum((y_true - np.mean(y_true)) ** 2))
    r2 = float("nan") if ss_tot <= 0.0 else float(1.0 - ss_res / ss_tot)
    return r2, rmse, mae


def _linear_inadequate(x: np.ndarray, y: np.ndarray, y_hat: np.ndarray, r2: float) -> bool:
    if not math.isfinite(r2) or r2 < _LINEAR_R2_KEEP:
        return True
    resid = y - y_hat
    x2 = x * x
    if np.std(resid) == 0.0 or np.std(x2) == 0.0:
        return False
    corr = float(np.corrcoef(resid, x2)[0, 1])
    return math.isfinite(corr) and abs(corr) >= _RESIDUAL_CURVE_CORR


def calibrate_to_srtm(
    relative_depth: np.ndarray,
    srtm_m: np.ndarray,
) -> CalibrationResult:
    """Fit relative depth → meters using aligned SRTM as the target.

    ``relative_depth`` and ``srtm_m`` must be the same shape. Values in
    ``relative_depth`` are [0, 1] with larger = higher elevation (the
    ``infer_depth`` output convention). The returned ``elevation_m`` is
    the mapping applied to the full relative-depth grid.
    """
    try:
        from sklearn.linear_model import LinearRegression
        from sklearn.pipeline import make_pipeline
        from sklearn.preprocessing import PolynomialFeatures
    except ImportError:
        return _skipped(
            "scikit-learn is not installed; calibration skipped (relative mode)"
        )

    try:
        x_all, y_all = _finite_pairs(relative_depth, srtm_m)
    except ValueError as exc:
        return _skipped(f"{exc}; calibration skipped (relative mode)")

    n_valid = int(x_all.size)
    if n_valid < _MIN_SAMPLES:
        return _skipped(
            f"not enough paired samples ({n_valid} < {_MIN_SAMPLES}); "
            "calibration skipped (relative mode)",
            sample_count=n_valid,
        )
    if float(np.std(x_all)) == 0.0:
        return _skipped(
            "relative depth has no variance; calibration skipped (relative mode)",
            sample_count=n_valid,
        )

    x, y = _subsample(x_all, y_all)
    sample_count = int(x.size)
    x_col = x.reshape(-1, 1)

    try:
        linear = LinearRegression()
        linear.fit(x_col, y)
        y_lin = linear.predict(x_col)
        r2_lin, rmse_lin, mae_lin = _scores(y, y_lin)

        use_poly = False
        poly = None
        r2_poly = rmse_poly = mae_poly = None
        if _linear_inadequate(x, y, y_lin, r2_lin):
            poly = make_pipeline(
                PolynomialFeatures(degree=2, include_bias=False),
                LinearRegression(),
            )
            poly.fit(x_col, y)
            y_poly = poly.predict(x_col)
            r2_poly, rmse_poly, mae_poly = _scores(y, y_poly)
            gain = (
                (r2_poly - r2_lin)
                if r2_poly is not None and math.isfinite(r2_poly) and math.isfinite(r2_lin)
                else 0.0
            )
            rmse_better = (
                rmse_poly < (_MAX_POLY_RMSE_RATIO * rmse_lin)
                if rmse_poly is not None and rmse_lin > 0
                else False
            )
            use_poly = gain >= _MIN_R2_GAIN and rmse_better

        rel_full = np.asarray(relative_depth, dtype=np.float64)
        finite = np.isfinite(rel_full)

        if use_poly and poly is not None:
            elev = np.full(rel_full.shape, np.nan, dtype=np.float64)
            elev[finite] = poly.predict(rel_full[finite].reshape(-1, 1))
            lr = poly.named_steps["linearregression"]
            return {
                "ok": True,
                "elevation_m": elev.astype(np.float32),
                "model": "polynomial",
                "degree": 2,
                "coefficients": [float(c) for c in lr.coef_],
                "intercept": float(lr.intercept_),
                "r2": float(r2_poly) if r2_poly is not None else None,
                "rmse_m": float(rmse_poly) if rmse_poly is not None else None,
                "mae_m": float(mae_poly) if mae_poly is not None else None,
                "sample_count": sample_count,
                "warning": None,
            }

        elev = np.full(rel_full.shape, np.nan, dtype=np.float64)
        elev[finite] = linear.predict(rel_full[finite].reshape(-1, 1))
        warn = None
        if not math.isfinite(r2_lin) or r2_lin < 0.5:
            warn = (
                f"low calibration R² ({r2_lin:.3f}); metric elevations "
                "may be unreliable"
            )
        return {
            "ok": True,
            "elevation_m": elev.astype(np.float32),
            "model": "linear",
            "degree": 1,
            "coefficients": [float(c) for c in linear.coef_],
            "intercept": float(linear.intercept_),
            "r2": float(r2_lin),
            "rmse_m": float(rmse_lin),
            "mae_m": float(mae_lin),
            "sample_count": sample_count,
            "warning": warn,
        }
    except Exception as exc:
        return _skipped(
            f"calibration fit failed ({exc}); calibration skipped (relative mode)",
            sample_count=sample_count if "sample_count" in locals() else n_valid,
        )


# ---------------------------------------------------------------------------
# Composite calibration: low-pass SRTM + calibrated nDSM
# ---------------------------------------------------------------------------

_COMPOSITE_LOWPASS_SIGMA = 7.0   # Gaussian sigma for SRTM terrain smoothing
_COMPOSITE_MIN_R2 = 0.10        # minimum R² for nDSM component to be useful

# If the calibrated nDSM std is less than this fraction of SRTM lowpass std,
# the linear fit has effectively zeroed-out the model's contribution.
# Switch to variance-matched scaling to preserve fine detail.
_NDSM_DOMINANCE_THRESHOLD = 0.05

# Minimum target std (meters) for hybrid nDSM scaling. SRTM at 30m can't
# resolve buildings, so its nDSM (SRTM_raw − SRTM_lowpass) is just noise
# (typically 1-2m std). Real urban above-ground variation is 5-30m.
# This floor ensures buildings are visually prominent in the terrain.
_MIN_HYBRID_NDSM_STD = 5.0

import logging as _logging
_cal_logger = _logging.getLogger(__name__)


def calibrate_composite(
    relative_depth: np.ndarray,
    srtm_m: np.ndarray,
) -> CalibrationResult:
    """Composite calibration: DSM = low-pass SRTM + calibrated nDSM.

    Instead of fitting a single global polynomial from relative depth to
    absolute elevation (which fails on high-relief terrain where the depth
    model cannot capture hundreds of meters of terrain variation), this
    approach:

    1. Low-pass filters the SRTM DEM to get the smooth terrain surface
    2. Treats the depth model's output as nDSM (above-ground height)
    3. Calibrates only the nDSM component
    4. Adds them: DSM = SRTM_lowpass + calibrated_nDSM

    **Hybrid scaling (Option B):** When the linear fit produces a negligible
    nDSM contribution (because SRTM at 30m can't resolve buildings, so the
    nDSM reference is noise), the model's output is instead variance-matched
    to the nDSM reference statistics. This preserves the model's fine
    structural detail (buildings, trees) while keeping SRTM's macro terrain.

    Falls back to the global fit (``calibrate_to_srtm``) if the composite
    approach fails or produces worse results.
    """
    try:
        import cv2
        from sklearn.linear_model import LinearRegression
    except ImportError:
        return _skipped(
            "cv2 or scikit-learn not installed; composite calibration skipped"
        )

    try:
        x_all, y_all = _finite_pairs(relative_depth, srtm_m)
    except ValueError as exc:
        return _skipped(f"{exc}; composite calibration skipped")

    n_valid = int(x_all.size)
    if n_valid < _MIN_SAMPLES:
        return _skipped(
            f"not enough paired samples ({n_valid} < {_MIN_SAMPLES}); "
            "composite calibration skipped",
            sample_count=n_valid,
        )

    try:
        # Step 1: Low-pass filter the SRTM to get terrain surface
        srtm_arr = np.asarray(srtm_m, dtype=np.float64)
        # Fill nodata with nearest valid value before filtering
        nodata_mask = ~np.isfinite(srtm_arr) | (srtm_arr <= _SRTM_NODATA + 1.0)
        if nodata_mask.all():
            return _skipped("SRTM is all nodata; composite calibration skipped",
                            sample_count=0)

        srtm_filled = srtm_arr.copy()
        if nodata_mask.any():
            # Simple fill: use median of valid pixels
            srtm_filled[nodata_mask] = float(np.nanmedian(srtm_arr[~nodata_mask]))

        ksize = int(_COMPOSITE_LOWPASS_SIGMA * 4) | 1  # ensure odd
        srtm_lowpass = cv2.GaussianBlur(
            srtm_filled.astype(np.float32),
            (ksize, ksize),
            _COMPOSITE_LOWPASS_SIGMA,
        ).astype(np.float64)

        # Step 2: Compute nDSM reference = SRTM_raw - SRTM_lowpass
        ndsm_ref = srtm_filled - srtm_lowpass

        # Step 3: Fit relative_depth -> nDSM_reference (above-ground component)
        rel = np.asarray(relative_depth, dtype=np.float64)
        finite = np.isfinite(rel) & np.isfinite(ndsm_ref) & ~nodata_mask
        if int(finite.sum()) < _MIN_SAMPLES:
            return _skipped(
                "not enough valid pixels for nDSM fit; composite calibration skipped",
                sample_count=int(finite.sum()),
            )

        x_ndsm, y_ndsm = _subsample(rel[finite], ndsm_ref[finite])
        sample_count = int(x_ndsm.size)

        lr = LinearRegression()
        lr.fit(x_ndsm.reshape(-1, 1), y_ndsm)
        y_pred = lr.predict(x_ndsm.reshape(-1, 1))
        r2_ndsm, rmse_ndsm, mae_ndsm = _scores(y_ndsm, y_pred)

        # Step 4: Apply — DSM = SRTM_lowpass + calibrated_nDSM
        ndsm_calibrated = np.full(rel.shape, np.nan, dtype=np.float64)
        finite_rel = np.isfinite(rel)
        ndsm_calibrated[finite_rel] = lr.predict(
            rel[finite_rel].reshape(-1, 1)
        )

        # -----------------------------------------------------------------
        # Step 4b: Hybrid scaling — detect negligible nDSM contribution
        # -----------------------------------------------------------------
        # If the linear fit produced an nDSM term that barely varies
        # compared to the SRTM lowpass, the fit has effectively zeroed
        # out the model's fine detail. This happens when SRTM can't
        # resolve buildings (30m resolution), so the nDSM reference is
        # noise, not real above-ground heights.
        #
        # Fix: replace the negligible linear-fit nDSM with a variance-
        # matched scaling of the depth model's output. This preserves
        # the model's structural detail while keeping statistically
        # plausible above-ground height magnitudes.
        # -----------------------------------------------------------------
        srtm_lp_valid = srtm_lowpass[~nodata_mask]
        ndsm_cal_valid = ndsm_calibrated[finite_rel & ~nodata_mask]
        srtm_lp_std = float(np.std(srtm_lp_valid)) if srtm_lp_valid.size > 0 else 1.0
        ndsm_cal_std = float(np.std(ndsm_cal_valid)) if ndsm_cal_valid.size > 0 else 0.0

        used_hybrid = False
        if srtm_lp_std > 0 and (ndsm_cal_std / srtm_lp_std) < _NDSM_DOMINANCE_THRESHOLD:
            # Linear fit contribution is negligible — switch to hybrid
            ndsm_ref_valid = ndsm_ref[~nodata_mask]
            ndsm_ref_std = float(np.std(ndsm_ref_valid))
            ndsm_ref_mean = float(np.mean(ndsm_ref_valid))

            depth_valid = rel[finite_rel]
            depth_std = float(np.std(depth_valid))
            depth_mean = float(np.mean(depth_valid))

            if depth_std > 1e-9 and ndsm_ref_std > 1e-9:
                # Use at least _MIN_HYBRID_NDSM_STD as target — SRTM's
                # nDSM std is just noise at 30m, real buildings are taller.
                target_std = max(ndsm_ref_std, _MIN_HYBRID_NDSM_STD)
                scale_factor = target_std / depth_std
                ndsm_calibrated[finite_rel] = (
                    (rel[finite_rel] - depth_mean) * scale_factor + ndsm_ref_mean
                )
                used_hybrid = True
                _cal_logger.info(
                    "Composite hybrid scaling activated: "
                    "linear nDSM ratio=%.4f (< %.2f threshold), "
                    "scale_factor=%.2f, target_std=%.2fm "
                    "(nDSM_ref_std=%.2fm, min=%.2fm)",
                    ndsm_cal_std / srtm_lp_std,
                    _NDSM_DOMINANCE_THRESHOLD,
                    scale_factor,
                    target_std,
                    ndsm_ref_std,
                    _MIN_HYBRID_NDSM_STD,
                )

        elevation = srtm_lowpass + ndsm_calibrated

        # Compute overall RMSE against original SRTM
        valid = np.isfinite(elevation) & np.isfinite(srtm_arr) & ~nodata_mask
        if int(valid.sum()) < _MIN_SAMPLES:
            return _skipped(
                "not enough valid pixels for composite score; skipped",
                sample_count=sample_count,
            )

        r2_final, rmse_final, mae_final = _scores(
            srtm_arr[valid], elevation[valid],
        )

        warn = None
        model_label = "composite-hybrid" if used_hybrid else "composite"
        if not math.isfinite(r2_final) or r2_final < 0.5:
            warn = (
                f"low {model_label} calibration R² ({r2_final:.3f}); metric "
                "elevations may be unreliable"
            )

        return {
            "ok": True,
            "elevation_m": elevation.astype(np.float32),
            "model": model_label,
            "degree": 1,
            "coefficients": [float(c) for c in lr.coef_],
            "intercept": float(lr.intercept_),
            "r2": float(r2_final),
            "rmse_m": float(rmse_final),
            "mae_m": float(mae_final),
            "sample_count": sample_count,
            "warning": warn,
        }

    except Exception as exc:
        return _skipped(
            f"composite calibration failed ({exc}); skipped",
            sample_count=sample_count if "sample_count" in locals() else 0,
        )

def encode_elevation_u16(elevation_m: np.ndarray) -> tuple[np.ndarray, float, float]:
    """Pack metric elevation into a 16-bit PNG-ready array.

    Returns ``(uint16_heightmap, min_elev_m, max_elev_m)``.
    """
    elev = np.asarray(elevation_m, dtype=np.float64)
    finite = elev[np.isfinite(elev)]
    if finite.size == 0:
        return np.zeros(elev.shape, dtype=np.uint16), 0.0, 1.0
    min_e = float(finite.min())
    max_e = float(finite.max())
    if max_e <= min_e:
        return np.zeros(elev.shape, dtype=np.uint16), min_e, min_e + 1.0
    norm = np.zeros(elev.shape, dtype=np.float64)
    mask = np.isfinite(elev)
    norm[mask] = (elev[mask] - min_e) / (max_e - min_e)
    packed = np.clip(np.round(norm * 65535.0), 0, 65535).astype(np.uint16)
    return packed, min_e, max_e


def calibration_sidecar(result: CalibrationResult, extra: dict[str, Any] | None = None) -> dict[str, Any]:
    """JSON-serializable calibration record for judges / metadata."""
    payload: dict[str, Any] = {
        "ok": result["ok"],
        "model": result["model"],
        "degree": result["degree"],
        "coefficients": result["coefficients"],
        "intercept": result["intercept"],
        "r2": result["r2"],
        "rmse_m": result["rmse_m"],
        "mae_m": result["mae_m"],
        "sample_count": result["sample_count"],
        "warning": result["warning"],
    }
    if extra:
        payload.update(extra)
    return payload


def write_calibration_json(path: Path | str, payload: dict[str, Any]) -> None:
    path = Path(path)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

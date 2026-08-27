"""Relative-depth to metric-elevation calibration.

Samples paired pixels (relative depth vs aligned SRTM), fits a mapping
(linear first; degree-2 polynomial only if linear residuals clearly fail),
and applies it to the full depth map. Never raises: failures return a skip
result so the caller can stay in relative mode.

Input convention (confirm in writing with the ML lead before swapping the
synthetic stub for a real monocular model):
  ``relative_depth`` is [0, 1], same shape as the aligned SRTM grid, with
  **larger value = higher elevation** (SRTM-aligned, not camera disparity).
  origin/main ``infer_depth`` currently documents this.

  If the model emits inverted disparity (larger = closer / lower), invert
  before calling ``calibrate_to_srtm`` (e.g. ``1.0 - depth``). An unaccounted
  inversion makes the linear slope flip sign and R² look like it "broke"
  after the real model lands; that is a convention mismatch, not a bad fit.
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
    ``relative_depth`` are [0, 1] with larger = higher elevation. Invert
    first if the model is disparity-like. The returned ``elevation_m`` is
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

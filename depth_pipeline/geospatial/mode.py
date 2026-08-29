"""Single decision point: calibrate vs relative fallback.

valid GeoTIFF + covering SRTM → calibrate
anything else → relative, with warning code:
  no_georef | bad_crs | srtm_unavailable

run.py merge contract (for main/dev owners — do not rewrite this module
into process_image):
  1. Produce relative depth in [0, 1] at the output grid (ML inference).
  2. heightmap_u16 = (relative * 65535).astype(uint16)  # or equivalent
  3. mode = resolve_elevation_mode(image_path, relative, heightmap_u16,
                                   output_dir=out)
  4. Write mode["heightmap_u16"] as heightmap.png
  5. metadata.update(apply_to_pipeline_metadata({}, mode))
  Delete the inline read_georef / align_srtm / is_calibrated: False block
  instead of editing both copies.
"""

from __future__ import annotations

from pathlib import Path
from typing import TypedDict

import numpy as np

from ..config import CALIBRATION_NAME
from .accuracy import append_accuracy_record
from .calibrate import (
    calibration_sidecar,
    calibrate_to_srtm,
    encode_elevation_u16,
    write_calibration_json,
)
from .geotiff import WARNING_BAD_CRS, WARNING_NO_GEOREF, read_georef
from .srtm import aligned_output_path, align_srtm

WARNING_SRTM_UNAVAILABLE = "srtm_unavailable"

_CONTRACT_KEYS = (
    "is_georeferenced",
    "is_calibrated",
    "min_elev_m",
    "max_elev_m",
    "r_squared",
    "sample_count",
    "srtm_tile_id",
    "warning",
)


class ElevationMode(TypedDict):
    is_georeferenced: bool
    is_calibrated: bool
    min_elev_m: float | None
    max_elev_m: float | None
    r_squared: float | None
    sample_count: int | None
    srtm_tile_id: str | None
    warning: str | None
    heightmap_u16: np.ndarray
    georef_crs: str | None
    georef_bbox: list[float] | None
    srtm_aligned: bool
    srtm_aligned_path: str | None
    srtm_coverage: float | None
    calibration: dict | None
    detail: str | None


def _relative(
    heightmap_u16: np.ndarray,
    *,
    warning: str,
    is_georeferenced: bool = False,
    georef_crs: str | None = None,
    georef_bbox: list[float] | None = None,
    srtm_tile_id: str | None = None,
    srtm_aligned: bool = False,
    srtm_aligned_path: str | None = None,
    srtm_coverage: float | None = None,
    calibration: dict | None = None,
    sample_count: int | None = None,
    r_squared: float | None = None,
    detail: str | None = None,
) -> ElevationMode:
    return {
        "is_georeferenced": is_georeferenced,
        "is_calibrated": False,
        "min_elev_m": None,
        "max_elev_m": None,
        "r_squared": r_squared,
        "sample_count": sample_count,
        "srtm_tile_id": srtm_tile_id,
        "warning": warning,
        "heightmap_u16": heightmap_u16,
        "georef_crs": georef_crs,
        "georef_bbox": georef_bbox,
        "srtm_aligned": srtm_aligned,
        "srtm_aligned_path": srtm_aligned_path,
        "srtm_coverage": srtm_coverage,
        "calibration": calibration,
        "detail": detail,
    }


def contract_fields(mode: ElevationMode) -> dict:
    """The judge-facing metadata subset."""
    return {key: mode[key] for key in _CONTRACT_KEYS}


def apply_to_pipeline_metadata(base: dict, mode: ElevationMode) -> dict:
    """Merge elevation-mode fields into process_image metadata.

    Intended as the only geospatial touchpoint in run.py so inference
    changes and calibration changes do not edit the same block.
    """
    out = dict(base)
    out.update(contract_fields(mode))
    out["crs"] = mode["georef_crs"]
    out["bbox"] = mode["georef_bbox"]
    out["srtm_aligned"] = mode["srtm_aligned"]
    out["srtm_aligned_path"] = mode["srtm_aligned_path"]
    out["srtm_coverage"] = mode["srtm_coverage"]
    out["calibration"] = mode["calibration"]
    return out


def resolve_elevation_mode(
    image_path: str | Path,
    relative_depth: np.ndarray,
    heightmap_u16: np.ndarray,
    *,
    output_dir: str | Path | None = None,
    tile_dir: str | Path | None = None,
) -> ElevationMode:
    """Choose calibrated metric elevation or relative fallback.

    ``relative_depth`` is 0–1, same shape as ``heightmap_u16``. Never raises.
    """
    try:
        return _resolve_elevation_mode(
            image_path,
            relative_depth,
            heightmap_u16,
            output_dir=output_dir,
            tile_dir=tile_dir,
        )
    except Exception as exc:
        return _relative(
            heightmap_u16,
            warning=WARNING_NO_GEOREF,
            detail=f"elevation mode failed ({exc})",
        )


def _resolve_elevation_mode(
    image_path: str | Path,
    relative_depth: np.ndarray,
    heightmap_u16: np.ndarray,
    *,
    output_dir: str | Path | None = None,
    tile_dir: str | Path | None = None,
) -> ElevationMode:
    georef = read_georef(image_path)
    if not georef["is_georeferenced"]:
        code = georef.get("warning_code") or WARNING_NO_GEOREF
        if code not in (WARNING_NO_GEOREF, WARNING_BAD_CRS):
            code = WARNING_NO_GEOREF
        return _relative(
            heightmap_u16,
            warning=code,
            detail=georef["warning"],
        )

    out_dir = Path(output_dir) if output_dir is not None else None
    srtm_path = aligned_output_path(out_dir) if out_dir is not None else None
    srtm = align_srtm(
        bbox=georef["bbox"],
        dst_crs=georef["crs"],
        dst_shape=tuple(int(n) for n in relative_depth.shape),
        tile_dir=tile_dir,
        output_path=srtm_path,
    )
    if not srtm["ok"] or srtm["elevation_m"] is None:
        return _relative(
            heightmap_u16,
            warning=WARNING_SRTM_UNAVAILABLE,
            is_georeferenced=True,
            georef_crs=georef["crs"],
            georef_bbox=georef["bbox"],
            detail=srtm["warning"],
        )

    cal = calibrate_to_srtm(relative_depth, srtm["elevation_m"])
    sidecar = calibration_sidecar(
        cal,
        extra={
            "srtm_tile_id": srtm["tile_id"],
            "srtm_coverage": srtm["coverage"],
            "terrain": srtm.get("terrain"),
        },
    )
    if out_dir is not None:
        write_calibration_json(out_dir / CALIBRATION_NAME, sidecar)

    if not cal["ok"] or cal["elevation_m"] is None:
        return _relative(
            heightmap_u16,
            warning=WARNING_SRTM_UNAVAILABLE,
            is_georeferenced=True,
            georef_crs=georef["crs"],
            georef_bbox=georef["bbox"],
            srtm_tile_id=srtm["tile_id"],
            srtm_aligned=True,
            srtm_aligned_path=srtm["aligned_path"],
            srtm_coverage=srtm["coverage"],
            calibration=sidecar,
            sample_count=cal["sample_count"] or None,
            r_squared=cal["r2"],
            detail=cal["warning"],
        )

    packed, min_e, max_e = encode_elevation_u16(cal["elevation_m"])
    append_accuracy_record(
        {
            "terrain": srtm.get("terrain") or "unknown",
            "srtm_tile_id": srtm["tile_id"],
            "r_squared": cal["r2"],
            "rmse_m": cal["rmse_m"],
            "mae_m": cal["mae_m"],
            "sample_count": cal["sample_count"],
            "fit_model": cal["model"],
            "min_elev_m": min_e,
            "max_elev_m": max_e,
        }
    )
    return {
        "is_georeferenced": True,
        "is_calibrated": True,
        "min_elev_m": min_e,
        "max_elev_m": max_e,
        "r_squared": cal["r2"],
        "sample_count": cal["sample_count"],
        "srtm_tile_id": srtm["tile_id"],
        "warning": None,
        "heightmap_u16": packed,
        "georef_crs": georef["crs"],
        "georef_bbox": georef["bbox"],
        "srtm_aligned": True,
        "srtm_aligned_path": srtm["aligned_path"],
        "srtm_coverage": srtm["coverage"],
        "calibration": sidecar,
        "detail": cal["warning"],
    }

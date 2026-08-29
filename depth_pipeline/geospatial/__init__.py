"""Geospatial pipeline: GeoTIFF metadata, SRTM alignment, metric calibration."""

from .calibrate import CalibrationResult, calibrate_to_srtm
from .geotiff import GeorefInfo, read_georef
from .mode import (
    ElevationMode,
    apply_to_pipeline_metadata,
    contract_fields,
    resolve_elevation_mode,
)
from .srtm import SrtmAlignResult, align_srtm, find_covering_tile

__all__ = [
    "CalibrationResult",
    "ElevationMode",
    "GeorefInfo",
    "SrtmAlignResult",
    "align_srtm",
    "calibrate_to_srtm",
    "apply_to_pipeline_metadata",
    "contract_fields",
    "find_covering_tile",
    "read_georef",
    "resolve_elevation_mode",
]

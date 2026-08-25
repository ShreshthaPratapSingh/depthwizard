"""Geospatial pipeline: GeoTIFF metadata, SRTM alignment, metric calibration."""

from .geotiff import GeorefInfo, read_georef
from .srtm import SrtmAlignResult, align_srtm, find_covering_tile

__all__ = [
    "GeorefInfo",
    "SrtmAlignResult",
    "align_srtm",
    "find_covering_tile",
    "read_georef",
]

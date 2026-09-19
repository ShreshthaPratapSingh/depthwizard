"""Slope raster generation from elevation data.

Computes a per-pixel slope map (in degrees) from either a metric elevation
array or a relative 0-1 elevation map.  The slope is the angle between the
surface normal and the vertical, derived from the gradient magnitude:

    slope_deg = arctan(sqrt(dz/dx² + dz/dy²))

When the elevation is metric (float32, meters) the gradients are in m/px; for
relative elevation the result is a unitless proxy that still captures *where*
the terrain is steep.

The output is a float32 HxW array in [0, 90] degrees (clipped to avoid noise
spikes above 90° from interpolation artifacts).

Exports:
    compute_slope  — core slope computation
    export_slope_geotiff — write slope raster as a GeoTIFF
    export_slope_png — write slope as a 8-bit grayscale PNG (for Unity)
"""

from __future__ import annotations

import logging
from pathlib import Path

import cv2
import numpy as np

logger = logging.getLogger(__name__)

SLOPE_GEOTIFF_NAME = "slope.tif"
SLOPE_PNG_NAME = "slope.png"


def compute_slope(elevation: np.ndarray, pixel_size: float = 1.0) -> np.ndarray:
    """Compute slope in degrees from an elevation array.

    Parameters
    ----------
    elevation : float HxW array
        Elevation in meters (metric) or relative 0-1.
    pixel_size : float
        Ground distance per pixel in the same units as elevation.
        For metric elevation this should be the GSD in meters;
        for relative elevation use 1.0 (default).

    Returns
    -------
    np.ndarray
        float32 HxW, slope in degrees [0, 90].
    """
    elev = np.asarray(elevation, dtype=np.float64)

    # Gradient in array coordinates (row, col).  np.gradient returns
    # (dy, dx) with the same shape as input.
    dy, dx = np.gradient(elev, pixel_size)

    # Slope magnitude = sqrt(dz/dx² + dz/dy²), then convert to degrees.
    gradient_mag = np.hypot(dx, dy)
    slope_rad = np.arctan(gradient_mag)
    slope_deg = np.degrees(slope_rad)

    return np.clip(slope_deg, 0.0, 90.0).astype(np.float32)


def export_slope_geotiff(
    slope_deg: np.ndarray,
    crs: str,
    bbox: list[float],
    output_dir: Path,
) -> Path | None:
    """Write slope raster as a float32 GeoTIFF with CRS.

    Parameters
    ----------
    slope_deg : float32 HxW array, slope in degrees.
    crs : CRS string (e.g. ``"EPSG:4326"``).
    bbox : ``[west, south, east, north]`` in the CRS units.
    output_dir : directory to write into (must exist).

    Returns the path to the written file, or None on failure.
    """
    try:
        import rasterio
        from rasterio.transform import from_bounds
    except ImportError:
        logger.warning("rasterio not available; skipping slope GeoTIFF export")
        return None

    out_path = Path(output_dir) / SLOPE_GEOTIFF_NAME
    slope = np.asarray(slope_deg, dtype=np.float32)
    h, w = slope.shape[:2]
    west, south, east, north = bbox

    transform = from_bounds(west, south, east, north, w, h)

    try:
        with rasterio.open(
            out_path,
            "w",
            driver="GTiff",
            height=h,
            width=w,
            count=1,
            dtype="float32",
            crs=crs,
            transform=transform,
            nodata=np.nan,
        ) as dst:
            dst.write(slope, 1)
            dst.update_tags(AREA_OR_POINT="Area")
            dst.set_band_description(1, "slope_degrees")

        logger.info("Slope GeoTIFF exported: %s (%dx%d)", out_path, w, h)
        return out_path

    except Exception as exc:
        logger.error("Slope GeoTIFF export failed: %s", exc)
        return None


def export_slope_png(
    slope_deg: np.ndarray,
    output_dir: Path,
) -> Path | None:
    """Write slope as an 8-bit grayscale PNG (0°→0, 90°→255).

    Useful for Unity overlay or quick visual inspection.
    """
    from PIL import Image as PILImage

    out_path = Path(output_dir) / SLOPE_PNG_NAME
    slope = np.asarray(slope_deg, dtype=np.float32)

    # Normalize: 0°→0, 90°→255
    slope_u8 = np.clip(slope * (255.0 / 90.0), 0, 255).astype(np.uint8)

    try:
        PILImage.fromarray(slope_u8, mode="L").save(out_path)
        logger.info("Slope PNG exported: %s", out_path)
        return out_path
    except Exception as exc:
        logger.error("Slope PNG export failed: %s", exc)
        return None

"""GeoTIFF / raster georeference metadata.

Never raises on missing or broken georeference: PNG/JPG, unreadable files,
and GeoTIFFs without a usable CRS+transform all return
``is_georeferenced: False`` plus a warning string.
"""

from __future__ import annotations

import math
import warnings
from pathlib import Path
from typing import Any, TypedDict


class GeorefInfo(TypedDict):
    is_georeferenced: bool
    crs: str | None
    bbox: list[float] | None  # [west, south, east, north]
    warning: str | None
    warning_code: str | None  # no_georef | bad_crs | None
    width: int | None
    height: int | None
    transform: list[float] | None  # affine a, b, c, d, e, f


WARNING_NO_GEOREF = "no_georef"
WARNING_BAD_CRS = "bad_crs"
_GTIFF_DRIVERS = {"GTiff", "COG"}


def _not_georeferenced(
    warning: str, warning_code: str = WARNING_NO_GEOREF
) -> GeorefInfo:
    return {
        "is_georeferenced": False,
        "crs": None,
        "bbox": None,
        "warning": warning,
        "warning_code": warning_code,
        "width": None,
        "height": None,
        "transform": None,
    }


def _code_for_driver(driver: str | None) -> str:
    if driver in _GTIFF_DRIVERS:
        return WARNING_BAD_CRS
    return WARNING_NO_GEOREF


def _crs_string(crs: Any) -> str | None:
    """Serialize a rasterio CRS; None if it cannot be represented."""
    if crs is None:
        return None
    try:
        epsg = crs.to_epsg() if hasattr(crs, "to_epsg") else None
        if epsg is not None:
            return f"EPSG:{epsg}"
        text = crs.to_string() if hasattr(crs, "to_string") else str(crs)
        return text or None
    except Exception:
        return None


def _transform_is_usable(transform: Any) -> bool:
    """True when the affine maps pixels to a real coordinate space."""
    if transform is None:
        return False
    try:
        if getattr(transform, "is_identity", False):
            return False
    except Exception:
        return False
    # At least one scale/rotation term on each axis must be non-zero.
    try:
        a, b, _c, d, e, _f = transform[:6]
    except Exception:
        return False
    if abs(a) == 0.0 and abs(b) == 0.0:
        return False
    if abs(d) == 0.0 and abs(e) == 0.0:
        return False
    return True


def read_georef(image_path: str | Path) -> GeorefInfo:
    """Read CRS and bounding box from ``image_path``.

    Returns a dict with keys ``is_georeferenced``, ``crs``, ``bbox``,
    ``warning``. ``bbox`` is ``[west, south, east, north]`` in the CRS
    units when georeferenced, otherwise ``None``.
    """
    path = Path(image_path)
    if not path.exists():
        return _not_georeferenced(f"file not found: {path}")
    if not path.is_file():
        return _not_georeferenced(f"not a file: {path}")

    try:
        import rasterio
        from rasterio.errors import NotGeoreferencedWarning, RasterioError
    except ImportError:
        return _not_georeferenced(
            "rasterio is not installed; treating as non-georeferenced"
        )

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", NotGeoreferencedWarning)
            with rasterio.open(path) as src:
                driver = getattr(src, "driver", None)
                crs_str = _crs_string(src.crs)
                if crs_str is None:
                    return _not_georeferenced(
                        "no valid CRS in file metadata; treating as non-georeferenced",
                        _code_for_driver(driver),
                    )
                if not _transform_is_usable(src.transform):
                    return _not_georeferenced(
                        "missing or identity geotransform; treating as non-georeferenced",
                        WARNING_BAD_CRS if driver in _GTIFF_DRIVERS else WARNING_NO_GEOREF,
                    )
                bounds = src.bounds
                bbox = [
                    float(bounds.left),
                    float(bounds.bottom),
                    float(bounds.right),
                    float(bounds.top),
                ]
                if not all(math.isfinite(v) for v in bbox):
                    return _not_georeferenced(
                        "bounds are not finite; treating as non-georeferenced",
                        WARNING_BAD_CRS if driver in _GTIFF_DRIVERS else WARNING_NO_GEOREF,
                    )
                width = int(src.width)
                height = int(src.height)
                transform = [float(v) for v in src.transform[:6]]
    except RasterioError as exc:
        return _not_georeferenced(
            f"not a readable georeferenced raster ({exc}); treating as non-georeferenced"
        )
    except Exception as exc:
        return _not_georeferenced(
            f"failed to read georeference ({exc}); treating as non-georeferenced"
        )

    return {
        "is_georeferenced": True,
        "crs": crs_str,
        "bbox": bbox,
        "warning": None,
        "warning_code": None,
        "width": width,
        "height": height,
        "transform": transform,
    }

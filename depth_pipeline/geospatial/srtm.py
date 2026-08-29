"""SRTM tile lookup and alignment onto a destination depth-map grid.

Local tiles for known demo regions are preferred. If nothing covers the
query, return a skip result (relative mode) instead of raising.
"""

from __future__ import annotations

import json
import math
import os
from pathlib import Path
from typing import Any, Sequence, TypedDict

import numpy as np

from ..config import DEFAULT_SRTM_TILE_DIR, SRTM_ALIGNED_NAME

REGIONS_NAME = "regions.json"
# SRTM void / ocean sentinel used by most GL1 products.
_SRTM_NODATA = -32768.0
_COVER_EPS = 1e-4  # degrees (or CRS units) of slack when matching tiles


class SrtmAlignResult(TypedDict):
    ok: bool
    elevation_m: np.ndarray | None
    tile_id: str | None
    tile_path: str | None
    aligned_path: str | None
    coverage: float | None
    terrain: str | None
    warning: str | None


def _skipped(warning: str) -> SrtmAlignResult:
    return {
        "ok": False,
        "elevation_m": None,
        "tile_id": None,
        "tile_path": None,
        "aligned_path": None,
        "coverage": None,
        "terrain": None,
        "warning": warning,
    }


def srtm_tile_dir(tile_dir: str | Path | None = None) -> Path:
    if tile_dir is not None:
        return Path(tile_dir)
    env = os.environ.get("DEPTHWIZARD_SRTM_DIR")
    if env:
        return Path(env)
    return DEFAULT_SRTM_TILE_DIR


def _load_catalog(directory: Path) -> list[dict[str, Any]]:
    path = directory / REGIONS_NAME
    if not path.is_file():
        return []
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    regions = data.get("regions", data if isinstance(data, list) else [])
    out: list[dict[str, Any]] = []
    for item in regions:
        bbox = item.get("bbox")
        filename = item.get("filename")
        if not (isinstance(bbox, list) and len(bbox) == 4 and filename):
            continue
        out.append(
            {
                "id": str(item.get("id") or Path(filename).stem),
                "terrain": item.get("terrain"),
                "name": item.get("name"),
                "bbox": [float(v) for v in bbox],
                "path": directory / filename,
            }
        )
    return out


def _tile_entries(directory: Path) -> list[dict[str, Any]]:
    """Catalog entries plus any extra GeoTIFFs sitting in the tile directory."""
    entries = _load_catalog(directory)
    catalog_paths = {e["path"].resolve() for e in entries}
    if not directory.is_dir():
        return entries
    for tif in sorted(directory.glob("*.tif")) + sorted(directory.glob("*.tiff")):
        if tif.resolve() in catalog_paths:
            continue
        entries.append(
            {
                "id": tif.stem,
                "terrain": None,
                "name": tif.stem,
                "bbox": None,  # filled from the file when matching
                "path": tif,
            }
        )
    return entries


def _bbox_covers(tile_bbox: Sequence[float], query_bbox: Sequence[float]) -> bool:
    tw, ts, te, tn = tile_bbox
    qw, qs, qe, qn = query_bbox
    return (
        tw <= qw + _COVER_EPS
        and ts <= qs + _COVER_EPS
        and te >= qe - _COVER_EPS
        and tn >= qn - _COVER_EPS
    )


def _file_bbox_wgs84(path: Path) -> list[float] | None:
    try:
        import rasterio
        from rasterio.warp import transform_bounds
    except ImportError:
        return None
    try:
        with rasterio.open(path) as src:
            if src.crs is None:
                return None
            w, s, e, n = transform_bounds(src.crs, "EPSG:4326", *src.bounds)
            return [float(w), float(s), float(e), float(n)]
    except Exception:
        return None


def _query_bbox_wgs84(bbox: Sequence[float], dst_crs: str) -> list[float] | None:
    try:
        from rasterio.warp import transform_bounds
    except ImportError:
        return None
    try:
        w, s, e, n = transform_bounds(dst_crs, "EPSG:4326", *bbox)
        if not all(math.isfinite(v) for v in (w, s, e, n)):
            return None
        return [float(w), float(s), float(e), float(n)]
    except Exception:
        return None


def find_covering_tile(
    bbox: Sequence[float],
    dst_crs: str = "EPSG:4326",
    tile_dir: str | Path | None = None,
) -> dict[str, Any] | None:
    """Return the smallest local tile that fully covers ``bbox``, or None."""
    directory = srtm_tile_dir(tile_dir)
    query = _query_bbox_wgs84(bbox, dst_crs)
    if query is None:
        return None

    covering: list[tuple[float, dict[str, Any]]] = []
    for entry in _tile_entries(directory):
        path: Path = entry["path"]
        if not path.is_file():
            continue
        tile_bbox = entry.get("bbox") or _file_bbox_wgs84(path)
        if tile_bbox is None:
            continue
        if not _bbox_covers(tile_bbox, query):
            continue
        area = (tile_bbox[2] - tile_bbox[0]) * (tile_bbox[3] - tile_bbox[1])
        covering.append((area, {**entry, "bbox": tile_bbox}))
    if not covering:
        return None
    covering.sort(key=lambda item: item[0])
    return covering[0][1]


def _dst_transform(bbox: Sequence[float], width: int, height: int):
    from rasterio.transform import from_bounds

    west, south, east, north = bbox
    return from_bounds(west, south, east, north, width, height)


def _write_aligned(
    path: Path,
    elevation: np.ndarray,
    transform,
    crs: str,
) -> None:
    import rasterio

    path.parent.mkdir(parents=True, exist_ok=True)
    height, width = elevation.shape
    profile = {
        "driver": "GTiff",
        "height": height,
        "width": width,
        "count": 1,
        "dtype": "float32",
        "crs": crs,
        "transform": transform,
        "nodata": math.nan,
        "compress": "lzw",
    }
    with rasterio.open(path, "w", **profile) as dst:
        dst.write(elevation.astype(np.float32, copy=False), 1)


def align_srtm(
    bbox: Sequence[float],
    dst_crs: str,
    dst_shape: tuple[int, int],
    dst_transform: Sequence[float] | None = None,
    tile_dir: str | Path | None = None,
    output_path: str | Path | None = None,
) -> SrtmAlignResult:
    """Reproject a local SRTM tile onto ``dst_shape``.

    ``bbox`` is ``[west, south, east, north]`` in ``dst_crs``. On any failure
    (no tile, rasterio missing, warp error) returns ``ok: False`` and a warning.
    """
    try:
        import rasterio
        from rasterio.enums import Resampling
        from rasterio.transform import Affine
        from rasterio.warp import reproject
    except ImportError:
        return _skipped("rasterio is not installed; calibration skipped")

    height, width = dst_shape
    if height <= 0 or width <= 0:
        return _skipped("invalid destination grid shape; calibration skipped")

    tile = find_covering_tile(bbox, dst_crs=dst_crs, tile_dir=tile_dir)
    if tile is None:
        return _skipped(
            "SRTM tile unavailable for this location; calibration skipped "
            "(relative mode)"
        )

    try:
        if dst_transform is not None:
            transform = Affine(*dst_transform[:6])
        else:
            transform = _dst_transform(bbox, width, height)

        destination = np.full((height, width), np.nan, dtype=np.float32)
        with rasterio.open(tile["path"]) as src:
            src_nodata = src.nodata
            if src_nodata is None:
                src_nodata = _SRTM_NODATA
            reproject(
                source=rasterio.band(src, 1),
                destination=destination,
                src_transform=src.transform,
                src_crs=src.crs,
                dst_transform=transform,
                dst_crs=dst_crs,
                resampling=Resampling.bilinear,
                src_nodata=src_nodata,
                dst_nodata=math.nan,
            )

        valid = np.isfinite(destination) & (destination > _SRTM_NODATA + 1)
        coverage = float(valid.mean()) if destination.size else 0.0
        if coverage <= 0.0:
            return _skipped(
                f"SRTM tile {tile['id']} produced no valid samples; "
                "calibration skipped (relative mode)"
            )

        aligned_path = None
        if output_path is not None:
            out = Path(output_path)
            _write_aligned(out, destination, transform, dst_crs)
            aligned_path = str(out)

        return {
            "ok": True,
            "elevation_m": destination,
            "tile_id": str(tile["id"]),
            "tile_path": str(tile["path"]),
            "aligned_path": aligned_path,
            "coverage": coverage,
            "terrain": tile.get("terrain"),
            "warning": None,
        }
    except Exception as exc:
        return _skipped(
            f"SRTM alignment failed ({exc}); calibration skipped (relative mode)"
        )


def aligned_output_path(output_dir: str | Path) -> Path:
    return Path(output_dir) / SRTM_ALIGNED_NAME

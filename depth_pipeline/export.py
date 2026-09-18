"""Export helpers: write the heightmap / texture / confidence artifacts to disk.

Centralizes the on-disk write logic that used to live inline in
``run.process_image``. The heightmap resize uses cv2.INTER_CUBIC (not PIL
Lanczos) so the resampling story is consistent across the pipeline going
forward.

The single most important thing in this module is the vertical flip applied to
the heightmap (and the confidence mask, which is spatially aligned to it) but
NOT to the texture. See ``export_artifacts`` for the full explanation.
"""
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


def resize_elevation01(elevation01: np.ndarray, target_res: int) -> np.ndarray:
    """Resize a float32 HxW 0-1 elevation map to ``target_res`` square.

    cv2.INTER_CUBIC can overshoot the [0, 1] range near sharp edges, so we clip
    afterwards. A 1:1 resize (input already ``target_res`` square) is returned
    as a clipped copy without re-interpolating, which keeps distinct rows crisp.
    """
    resized = np.asarray(elevation01, dtype=np.float32)
    if resized.shape[:2] != (target_res, target_res):
        resized = cv2.resize(
            resized, (target_res, target_res), interpolation=cv2.INTER_CUBIC
        )
    return np.clip(resized, 0.0, 1.0).astype(np.float32)


def export_artifacts(
    elevation01: np.ndarray,
    source_rgb: np.ndarray,
    target_res: int,
    texture_size: int,
    output_dir: Path,
    *,
    confidence: np.ndarray | None = None,
    heightmap_u16: np.ndarray | None = None,
) -> dict:
    """Write heightmap / texture / confidence artifacts and report the paths.

    Parameters
    ----------
    elevation01
        float32, HxW, values in [0, 1] (higher = higher elevation).
    source_rgb
        HxWx3 uint8 RGB source image, used for the texture.
    target_res
        Square resolution of the heightmap and confidence mask.
    texture_size
        Square resolution of the texture.
    output_dir
        Directory the artifacts are written into (must already exist).
    confidence
        Optional uint8 mask spatially aligned to the heightmap. Defaults to an
        all-255 (fully confident) mask.
    heightmap_u16
        Optional pre-encoded uint16 heightmap (``target_res`` square) to write
        instead of ``elevation01 * 65535`` — used by the caller to hand back a
        calibrated (metric) heightmap while keeping the flip/save logic here.

    Returns ``{"heightmap_path", "texture_path", "confidence_mask_path",
    "relative_min", "relative_max"}``.
    """
    out = Path(output_dir)

    from .config import CONFIDENCE_NAME, HEIGHTMAP_NAME, TEXTURE_NAME

    elev_resized = resize_elevation01(elevation01, target_res)
    relative_min = float(elev_resized.min())
    relative_max = float(elev_resized.max())

    if heightmap_u16 is None:
        heightmap_u16 = (elev_resized * 65535.0).round().astype(np.uint16)
    else:
        heightmap_u16 = np.asarray(heightmap_u16, dtype=np.uint16)

    # Unity's TerrainData.SetHeights() reads the heights array with row 0 = the
    # SOUTH edge (minimum world Z / bottom of the terrain), increasing rows
    # moving NORTH. Our array (like the source satellite image) has row 0 = the
    # TOP of the image = NORTH. Without this flip the terrain still "looks like
    # terrain" in Unity — it is just silently mirrored top-to-bottom (north and
    # south swapped), which is the single most likely bug to slip through
    # unnoticed. Flip vertically so array-row 0 becomes Unity's south edge.
    heightmap_out = np.flipud(heightmap_u16)
    heightmap_path = out / HEIGHTMAP_NAME
    Image.fromarray(heightmap_out).save(heightmap_path)

    # Texture is NOT flipped. Unity's Terrain UV mapping samples the diffuse
    # texture in normal (top-left origin) image orientation, so the texture must
    # stay unflipped even though the heightmap above is flipped. Do NOT "fix"
    # this to match the heightmap flip — that would mirror the imagery.
    source_rgb = np.asarray(source_rgb)
    texture_resized = cv2.resize(
        source_rgb, (texture_size, texture_size), interpolation=cv2.INTER_AREA
    )
    texture_path = out / TEXTURE_NAME
    Image.fromarray(texture_resized).save(texture_path)

    # Confidence mask is spatially aligned to the heightmap, so it takes the
    # same vertical flip as the heightmap above.
    if confidence is None:
        confidence = np.full((target_res, target_res), 255, dtype=np.uint8)
    else:
        confidence = np.asarray(confidence, dtype=np.uint8)
        if confidence.shape[:2] != (target_res, target_res):
            # Nearest-neighbour so the discrete confidence levels (255/128/100)
            # survive the resize to the heightmap grid.
            confidence = cv2.resize(
                confidence, (target_res, target_res),
                interpolation=cv2.INTER_NEAREST,
            )
    confidence_out = np.flipud(confidence)
    confidence_path = out / CONFIDENCE_NAME
    Image.fromarray(confidence_out, mode="L").save(confidence_path)

    return {
        "heightmap_path": heightmap_path,
        "texture_path": texture_path,
        "confidence_mask_path": confidence_path,
        "relative_min": relative_min,
        "relative_max": relative_max,
    }


def export_dsm_geotiff(
    elevation_m: np.ndarray,
    crs: str,
    bbox: list[float],
    output_dir: Path,
) -> Path | None:
    """Write calibrated elevation as a float32 GeoTIFF with CRS.

    Only meaningful when the input was georeferenced AND calibration succeeded
    (providing ``elevation_m`` in meters and a real CRS). Callers should skip
    this when either condition is absent.

    Parameters
    ----------
    elevation_m : float32 HxW array, elevation in meters.
    crs : CRS string (e.g. "EPSG:4326").
    bbox : [west, south, east, north] in the CRS units.
    output_dir : directory to write into (must exist).

    Returns the path to the written file, or None on failure.
    """
    import logging
    logger = logging.getLogger(__name__)

    try:
        import rasterio
        from rasterio.transform import from_bounds
    except ImportError:
        logger.warning("rasterio not available; skipping GeoTIFF DSM export")
        return None

    from .config import DSM_GEOTIFF_NAME

    out_path = Path(output_dir) / DSM_GEOTIFF_NAME
    elev = np.asarray(elevation_m, dtype=np.float32)
    h, w = elev.shape[:2]
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
            dst.write(elev, 1)
            dst.update_tags(AREA_OR_POINT="Area")
            dst.set_band_description(1, "elevation_m")

        logger.info("GeoTIFF DSM exported: %s (%dx%d, CRS=%s)", out_path, w, h, crs)
        return out_path

    except Exception as exc:
        logger.error("GeoTIFF DSM export failed: %s", exc)
        return None

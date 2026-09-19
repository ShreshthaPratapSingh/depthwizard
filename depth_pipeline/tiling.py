"""Full-resolution tiling with overlap and feathered blending.

Instead of downscaling large images to fit GPU memory, this module tiles
them into overlapping patches, runs inference on each patch independently,
and blends the results with a cosine-feathered seam so the output is
seamless at full resolution.

Design:
    1. Split the input HxW image into tiles of ``tile_size`` with ``overlap``
       pixels on each edge.
    2. Each tile is fed to the inference function independently.
    3. A cosine weight mask fades each tile's contribution near its edges,
       so overlapping regions blend smoothly instead of showing hard seams.
    4. The blended output is the same size as the input.

The caller supplies the inference function as a callback so this module
stays decoupled from the model. The expected signature is:

    infer_fn(tile_rgb: np.ndarray) -> np.ndarray

where ``tile_rgb`` is HxWx3 uint8 and the return is float32 HxW in [0, 1].
"""

from __future__ import annotations

import logging
import math

import numpy as np

logger = logging.getLogger(__name__)

# Defaults: 1024×1024 tiles with 128px overlap on each side.
DEFAULT_TILE_SIZE = 1024
DEFAULT_OVERLAP = 128


def _cosine_weight(
    h: int,
    w: int,
    overlap: int = DEFAULT_OVERLAP,
    top_fade: int | None = None,
    bottom_fade: int | None = None,
    left_fade: int | None = None,
    right_fade: int | None = None,
) -> np.ndarray:
    """Build a 2-D cosine feather mask for blending overlapping tiles.

    The mask is 1.0 in the interior and fades smoothly at edges where
    overlap occurs. At image boundaries (where a fade is 0), no fade
    is applied so border pixels retain full weight.
    """
    def _ramp(n: int, fade_start: int, fade_end: int) -> np.ndarray:
        arr = np.ones(n, dtype=np.float32)
        if fade_start > 0:
            arr[:fade_start] = 0.5 * (1.0 - np.cos(
                (np.arange(fade_start, dtype=np.float32) + 0.5) / float(fade_start) * np.pi
            ))
        if fade_end > 0:
            arr[-fade_end:] = 0.5 * (1.0 + np.cos(
                (np.arange(fade_end, dtype=np.float32) + 0.5) / float(fade_end) * np.pi
            ))
        return arr

    tf = overlap if top_fade is None else top_fade
    bf = overlap if bottom_fade is None else bottom_fade
    lf = overlap if left_fade is None else left_fade
    rf = overlap if right_fade is None else right_fade

    row_w = _ramp(h, tf, bf)
    col_w = _ramp(w, lf, rf)
    return np.outer(row_w, col_w).astype(np.float32)


def _tile_coords(
    total: int, tile_size: int, overlap: int,
) -> list[tuple[int, int]]:
    """Generate (start, end) pairs that tile ``total`` with ``overlap``.

    The last tile is shifted left so it ends exactly at ``total``, which means
    the last overlap may be larger than ``overlap`` — that's fine for blending.
    """
    step = tile_size - overlap
    if step <= 0:
        raise ValueError(
            f"tile_size ({tile_size}) must be > overlap ({overlap})"
        )
    coords = []
    start = 0
    while start < total:
        end = min(start + tile_size, total)
        if end - start < tile_size and coords:
            # Shift the last tile so it's full-size (if possible).
            start = max(0, total - tile_size)
            end = total
        coords.append((start, end))
        if end >= total:
            break
        start += step
    return coords


def needs_tiling(h: int, w: int, max_pixels: int) -> bool:
    """Return True if the image exceeds ``max_pixels`` and should be tiled."""
    return h * w > max_pixels


def tile_and_infer(
    source_rgb: np.ndarray,
    infer_fn,
    *,
    tile_size: int = DEFAULT_TILE_SIZE,
    overlap: int = DEFAULT_OVERLAP,
    on_tile=None,
) -> np.ndarray:
    """Tile a large image, run inference on each tile, blend results.

    Parameters
    ----------
    source_rgb : HxWx3 uint8 array.
    infer_fn : callable
        ``infer_fn(tile_rgb) -> float32 HxW [0, 1]``.
    tile_size : int
        Each tile's target edge length in pixels.
    overlap : int
        Overlap in pixels on each side between adjacent tiles.
    on_tile : callable, optional
        ``on_tile(index, total)`` progress callback.

    Returns
    -------
    np.ndarray
        float32 HxW in [0, 1], same spatial size as ``source_rgb``.
    """
    h, w = source_rgb.shape[:2]

    row_tiles = _tile_coords(h, tile_size, overlap)
    col_tiles = _tile_coords(w, tile_size, overlap)
    total_tiles = len(row_tiles) * len(col_tiles)

    logger.info(
        "Tiling %dx%d image into %d tiles (%dx%d, overlap=%d)",
        w, h, total_tiles, tile_size, tile_size, overlap,
    )

    # Accumulators for weighted blending.
    accum = np.zeros((h, w), dtype=np.float64)
    weight_sum = np.zeros((h, w), dtype=np.float64)

    idx = 0
    for ri, (r_start, r_end) in enumerate(row_tiles):
        top_fade = 0 if r_start == 0 else (row_tiles[ri - 1][1] - r_start)
        bottom_fade = 0 if r_end == h else (r_end - row_tiles[ri + 1][0])
        for ci, (c_start, c_end) in enumerate(col_tiles):
            left_fade = 0 if c_start == 0 else (col_tiles[ci - 1][1] - c_start)
            right_fade = 0 if c_end == w else (c_end - col_tiles[ci + 1][0])

            tile_rgb = source_rgb[r_start:r_end, c_start:c_end]
            tile_h, tile_w = tile_rgb.shape[:2]

            # Run inference on this tile.
            tile_depth = infer_fn(tile_rgb)

            if tile_depth.shape != (tile_h, tile_w):
                # Resize if the model returned a different shape.
                import cv2
                tile_depth = cv2.resize(
                    tile_depth, (tile_w, tile_h),
                    interpolation=cv2.INTER_CUBIC,
                )

            # Build weight mask for this tile with border-aware fades.
            weight = _cosine_weight(
                tile_h, tile_w, overlap,
                top_fade=top_fade,
                bottom_fade=bottom_fade,
                left_fade=left_fade,
                right_fade=right_fade,
            )

            # Accumulate.
            accum[r_start:r_end, c_start:c_end] += tile_depth * weight
            weight_sum[r_start:r_end, c_start:c_end] += weight

            idx += 1
            if on_tile is not None:
                on_tile(idx, total_tiles)
            logger.debug(
                "Tile %d/%d [%d:%d, %d:%d] done",
                idx, total_tiles, r_start, r_end, c_start, c_end,
            )

    # Normalize by total weight (avoid div-by-zero at edges, though it
    # shouldn't happen if tiles cover the full image).
    weight_sum = np.maximum(weight_sum, 1e-8)
    result = (accum / weight_sum).astype(np.float32)

    return np.clip(result, 0.0, 1.0)

"""Public entrypoint + CLI for the depth pipeline.

Currently a working stub: instead of running a real depth model it writes a
synthetic sine-wave heightmap plus a resized texture, a full-confidence mask,
and a metadata sidecar. No torch, no GPU — runs anywhere Pillow + NumPy do.

CLI:
    python -m depth_pipeline.run <image> <outdir> [--target-res N]
"""
import argparse
import json
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image

from .config import (
    CONFIDENCE_NAME,
    DEFAULT_TARGET_RES,
    HEIGHTMAP_NAME,
    METADATA_NAME,
    MODEL_ID,
    TEXTURE_NAME,
    TEXTURE_SIZE,
)

# Pillow renamed resampling filters in 9.1; prefer the new enum, fall back.
try:
    _LANCZOS = Image.Resampling.LANCZOS
except AttributeError:  # Pillow < 9.1
    _LANCZOS = Image.LANCZOS


def _synthetic_heightmap(target_res: int) -> np.ndarray:
    """Build a deterministic sine-wave heightmap as a uint16 array.

    Values span the full 16-bit range so downstream 16-bit consumers see a
    realistic dynamic range. This is a placeholder for real model output.
    """
    yy, xx = np.mgrid[0:target_res, 0:target_res].astype(np.float64)
    # A few cycles across each axis so the surface has visible structure.
    fx = 2.0 * np.pi * 3.0 / target_res
    fy = 2.0 * np.pi * 5.0 / target_res
    wave = np.sin(xx * fx) + np.cos(yy * fy)      # range [-2, 2]
    norm = (wave + 2.0) / 4.0                      # range [0, 1]
    return (norm * 65535.0).round().astype(np.uint16)


def process_image(image_path: str, output_dir: str,
                  target_res: int = DEFAULT_TARGET_RES) -> dict:
    """Process a single image into heightmap/texture/confidence + metadata.

    STUB: emits a synthetic sine-wave heightmap (no depth model, no torch).

    Writes into ``output_dir``:
      * heightmap.png   16-bit grayscale, target_res x target_res
      * texture.png     RGB, 1024x1024 (resized source)
      * confidence.png  8-bit grayscale, all 255
      * metadata.json   sidecar with fixed schema

    Returns the metadata dict.
    """
    start = time.perf_counter()
    warnings: list[str] = []

    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    # --- texture: resized source, RGB, 1024x1024 ---
    with Image.open(image_path) as src:
        texture = src.convert("RGB").resize((TEXTURE_SIZE, TEXTURE_SIZE), _LANCZOS)
    texture_path = out / TEXTURE_NAME
    texture.save(texture_path)

    # --- heightmap: synthetic sine wave, 16-bit grayscale, target_res^2 ---
    height16 = _synthetic_heightmap(target_res)
    heightmap_path = out / HEIGHTMAP_NAME
    # A uint16 2-D array maps to Pillow mode "I;16" -> 16-bit grayscale PNG.
    Image.fromarray(height16).save(heightmap_path)

    # --- confidence: 8-bit grayscale, all 255 (fully confident placeholder) ---
    confidence = np.full((target_res, target_res), 255, dtype=np.uint8)
    confidence_path = out / CONFIDENCE_NAME
    Image.fromarray(confidence, mode="L").save(confidence_path)

    warnings.append("synthetic sine-wave heightmap: not produced by a depth model")
    warnings.append("not georeferenced and not calibrated: elevations are relative")

    inference_ms = round((time.perf_counter() - start) * 1000.0, 3)

    # Relative extent actually present in the emitted heightmap (0..1).
    relative_min = float(height16.min()) / 65535.0
    relative_max = float(height16.max()) / 65535.0

    metadata = {
        "heightmap_path": str(heightmap_path),
        "texture_path": str(texture_path),
        "confidence_mask_path": str(confidence_path),
        "width": target_res,
        "height": target_res,
        "is_georeferenced": False,
        "is_calibrated": False,
        "min_elev_m": None,          # unknown until calibrated to meters
        "max_elev_m": None,
        "relative_min": relative_min,
        "relative_max": relative_max,
        "model_id": MODEL_ID,
        "inference_ms": inference_ms,
        "status": "ok",
        "warnings": warnings,
    }
    (out / METADATA_NAME).write_text(
        json.dumps(metadata, indent=2) + "\n", encoding="utf-8"
    )
    return metadata


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="python -m depth_pipeline.run",
        description="Process an image into a heightmap/texture/confidence set "
                    "(currently a synthetic sine-wave stub).",
    )
    parser.add_argument("image", help="path to the source image")
    parser.add_argument("outdir", help="output directory for the artifacts")
    parser.add_argument(
        "--target-res", type=int, default=DEFAULT_TARGET_RES,
        help=f"heightmap resolution (square); default {DEFAULT_TARGET_RES}",
    )
    args = parser.parse_args(argv)

    metadata = process_image(args.image, args.outdir, args.target_res)
    print(json.dumps(metadata, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())

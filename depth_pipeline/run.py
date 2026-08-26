"""Public entrypoint + CLI for the depth pipeline.

Runs Depth-Anything-V2-Small (via depth_pipeline.inference) to produce a real
heightmap, plus a resized texture, a full-confidence mask, and a metadata
sidecar. torch is imported lazily inside inference, so importing this module
stays CPU/torch-free until process_image is actually called.

CLI:
    python -m depth_pipeline.run <image> <outdir> [--target-res N]
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

from .config import (
    CONFIDENCE_NAME,
    DEFAULT_TARGET_RES,
    HEIGHTMAP_NAME,
    METADATA_NAME,
    TEXTURE_NAME,
    TEXTURE_SIZE,
)

# Pillow renamed resampling filters in 9.1; prefer the new enum, fall back.
try:
    _LANCZOS = Image.Resampling.LANCZOS
except AttributeError:  # Pillow < 9.1
    _LANCZOS = Image.LANCZOS


def process_image(image_path: str, output_dir: str,
                  target_res: int = DEFAULT_TARGET_RES) -> dict:
    """Process a single image into heightmap/texture/confidence + metadata.

    Runs Depth-Anything-V2-Small to estimate a normalized elevation map
    (higher value = higher elevation), then writes into ``output_dir``:
      * heightmap.png   16-bit grayscale, target_res x target_res
      * texture.png     RGB, 1024x1024 (resized source)
      * confidence.png  8-bit grayscale, all 255
      * metadata.json   sidecar with fixed schema

    Returns the metadata dict.
    """
    # Imported here (not at module top) so torch stays out of import time.
    from .inference import MODEL_ID, get_last_inference_ms, infer_depth

    warnings: list[str] = []

    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    # --- load source once; RGB array feeds inference, resized copy is texture ---
    with Image.open(image_path) as src:
        rgb = src.convert("RGB")
        source_rgb = np.asarray(rgb)  # HxWx3 uint8
        texture = rgb.resize((TEXTURE_SIZE, TEXTURE_SIZE), _LANCZOS)
    texture_path = out / TEXTURE_NAME
    texture.save(texture_path)

    # --- heightmap: real depth, normalized [0,1] (higher = higher elevation) ---
    elevation01 = infer_depth(source_rgb)  # float32 HxW, [0,1]
    inference_ms = get_last_inference_ms()

    # Resize to target_res^2 in float ('F' mode) to avoid quantizing twice,
    # then map to the full 16-bit range. Lanczos can overshoot, so clip.
    elev_img = Image.fromarray(elevation01.astype(np.float32))  # mode 'F'
    elev_img = elev_img.resize((target_res, target_res), _LANCZOS)
    elev_resized = np.clip(np.asarray(elev_img, dtype=np.float32), 0.0, 1.0)
    height16 = (elev_resized * 65535.0).round().astype(np.uint16)

    heightmap_path = out / HEIGHTMAP_NAME
    # A uint16 2-D array maps to Pillow mode "I;16" -> 16-bit grayscale PNG.
    Image.fromarray(height16).save(heightmap_path)

    # --- confidence: 8-bit grayscale, all 255 (fully confident placeholder) ---
    confidence = np.full((target_res, target_res), 255, dtype=np.uint8)
    confidence_path = out / CONFIDENCE_NAME
    Image.fromarray(confidence, mode="L").save(confidence_path)

    warnings.append("not georeferenced and not calibrated: elevations are relative")

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
                    "using Depth-Anything-V2-Small.",
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

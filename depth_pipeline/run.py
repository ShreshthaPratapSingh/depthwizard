"""Public entrypoint + CLI for the depth pipeline.

Runs Depth-Anything-V2-Small (via depth_pipeline.inference) to produce a
normalized elevation map, then resolve_elevation_mode for SRTM calibration.
torch is imported lazily inside inference.

Do not invert with ``1 - relative`` here. infer_depth already returns
[0, 1] with higher = higher elevation (INVERT_TO_ELEVATION lives in
inference.py and belongs to the ML owner).

CLI:
    python -m depth_pipeline.run <image> <outdir> [--target-res N]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Callable

import numpy as np
from PIL import Image

from .config import (
    DEFAULT_TARGET_RES,
    METADATA_NAME,
    TEXTURE_SIZE,
)
from .export import export_artifacts, resize_elevation01
from .geospatial import apply_to_pipeline_metadata, resolve_elevation_mode


def process_image(image_path: str, output_dir: str,
                  target_res: int = DEFAULT_TARGET_RES,
                  on_stage: Callable[[str, str], None] | None = None) -> dict:
    """Process a single image into heightmap/texture/confidence + metadata.

    ``infer_depth`` must already be [0, 1], higher = higher elevation.
    Do not apply ``1 - relative`` before resolve_elevation_mode.

    If *on_stage* is provided it is called as ``on_stage(stage, detail)``
    at each major step so callers (e.g. the async backend endpoint) can
    report progress.
    """
    def _stage(name: str, detail: str = "") -> None:
        if on_stage is not None:
            on_stage(name, detail)
    # Imported here so torch stays out of import time.
    from .inference import MODEL_ID, get_last_inference_ms, infer_depth

    warnings: list[str] = []

    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)

    with Image.open(image_path) as src:
        source_rgb = np.asarray(src.convert("RGB"))

    _stage("inferring", "running depth model")
    elevation01 = infer_depth(source_rgb)
    inference_ms = get_last_inference_ms()

    # Resize to the output grid here so calibration sees exactly the same array
    # export_artifacts encodes; passing elev_resized (already target_res square)
    # makes export's internal resize a 1:1 no-op, so there is no double resample.
    elev_resized = resize_elevation01(elevation01, target_res)
    height16 = (elev_resized * 65535.0).round().astype(np.uint16)

    _stage("calibrating", "SRTM alignment and calibration")
    mode = resolve_elevation_mode(
        image_path, elev_resized, height16, output_dir=out,
    )
    if mode["detail"]:
        warnings.append(mode["detail"])
    if mode["is_calibrated"]:
        r2 = mode["r_squared"]
        r2_txt = f"{r2:.3f}" if r2 is not None else "n/a"
        warnings.append(f"calibrated to SRTM (R²={r2_txt}, n={mode['sample_count']})")

    _stage("exporting", "writing artifacts")
    artifacts = export_artifacts(
        elev_resized, source_rgb, target_res, TEXTURE_SIZE, out,
        heightmap_u16=mode["heightmap_u16"],
    )

    metadata = apply_to_pipeline_metadata(
        {
            "heightmap_path": str(artifacts["heightmap_path"]),
            "texture_path": str(artifacts["texture_path"]),
            "confidence_mask_path": str(artifacts["confidence_mask_path"]),
            "width": target_res,
            "height": target_res,
            "relative_min": artifacts["relative_min"],
            "relative_max": artifacts["relative_max"],
            "model_id": MODEL_ID,
            "inference_ms": inference_ms,
            "status": "ok",
            "warnings": warnings,
        },
        mode,
    )
    (out / METADATA_NAME).write_text(
        json.dumps(metadata, indent=2) + "\n", encoding="utf-8"
    )
    _stage("done")
    return metadata


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        prog="python -m depth_pipeline.run",
        description="Process an image into a heightmap/texture/confidence set "
                    "using Depth-Anything-V2-Small, then SRTM calibration when "
                    "the input is a valid GeoTIFF with a covering tile.",
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

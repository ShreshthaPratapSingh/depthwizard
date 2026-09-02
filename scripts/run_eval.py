"""Batch-evaluate the depth pipeline against reference DEMs.

Usage:
    python scripts/run_eval.py <pairs_dir> <output_csv>

``pairs_dir`` holds one subfolder per evaluation pair. Each subfolder contains:
  * an image file (.png/.jpg/.jpeg/.tif/.tiff) - the pipeline input, and
  * a matching reference DEM. The reference is identified by a stem containing
    "dem" or "ref" (case-insensitive); .tif/.tiff is read via rasterio, and
    .png/.npy are accepted as a fallback for quick local testing without real
    DEMs.

For each pair we run ``process_image``, load the resulting heightmap, align it
to the reference with ``metrics.evaluate`` and write one CSV row. Pairs missing
a reference are logged and skipped rather than aborting the whole run.

The reported metrics always include ``pearson_r`` and ``slope_rmse`` next to
RMSE/MAE - see ``depth_pipeline.metrics`` for why RMSE alone can mislead.
"""

from __future__ import annotations

import argparse
import csv
import logging
import sys
import tempfile
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from depth_pipeline.metrics import evaluate
from depth_pipeline.run import process_image

logger = logging.getLogger("run_eval")

IMAGE_EXTS = {".png", ".jpg", ".jpeg", ".tif", ".tiff"}
REF_EXTS = {".tif", ".tiff", ".png", ".npy"}
CSV_COLUMNS = [
    "pair_name", "terrain_type", "mae_m", "rmse_m", "pearson_r",
    "slope_rmse", "n_valid_px", "fit_scale", "fit_shift",
]


def _is_reference(path: Path) -> bool:
    """A file is the reference DEM if its stem hints at 'dem'/'ref'."""
    stem = path.stem.lower()
    return "dem" in stem or "ref" in stem


def find_pair_files(folder: Path) -> tuple[Path | None, Path | None]:
    """Return ``(image_path, ref_path)`` for a pair folder (either may be None).

    The reference is a DEM-like file (stem contains 'dem'/'ref'); the image is
    the first remaining image-extension file that is not the reference.
    """
    files = sorted(p for p in folder.iterdir() if p.is_file())

    ref = next(
        (p for p in files if p.suffix.lower() in REF_EXTS and _is_reference(p)),
        None,
    )
    image = next(
        (p for p in files
         if p.suffix.lower() in IMAGE_EXTS and p != ref and not _is_reference(p)),
        None,
    )
    return image, ref


def load_reference(path: Path) -> tuple[np.ndarray, np.ndarray | None]:
    """Load a reference DEM, returning ``(elevation, valid_mask)``.

    ``valid_mask`` marks finite, non-nodata pixels (None when everything is
    usable). GeoTIFFs are read via rasterio to honour the file's nodata value.
    """
    suffix = path.suffix.lower()
    if suffix in {".tif", ".tiff"}:
        import rasterio

        with rasterio.open(path) as src:
            dem = src.read(1).astype(np.float64)
            nodata = src.nodata
        valid = np.isfinite(dem)
        if nodata is not None and np.isfinite(nodata):
            valid &= dem != nodata
        return dem, valid
    if suffix == ".npy":
        dem = np.load(path).astype(np.float64)
        return dem, np.isfinite(dem)
    # .png fallback (uint8/uint16 grayscale).
    with Image.open(path) as im:
        dem = np.asarray(im.convert("I")).astype(np.float64)
    return dem, np.isfinite(dem)


def load_heightmap(path: Path) -> np.ndarray:
    """Load the exported heightmap, undoing export.py's vertical flip.

    ``export_artifacts`` flips the heightmap top-to-bottom (Unity south-origin)
    before saving, so flip it back to north-up to spatially match the DEM.
    """
    with Image.open(path) as im:
        arr = np.asarray(im).astype(np.float64)
    return np.flipud(arr)


def terrain_type(pair_name: str) -> str:
    """Folder-name prefix before the first underscore, else 'unknown'."""
    return pair_name.split("_", 1)[0] if "_" in pair_name else "unknown"


def evaluate_pair(folder: Path) -> dict | None:
    """Run the pipeline on one pair folder and return a CSV row, or None."""
    image_path, ref_path = find_pair_files(folder)
    if ref_path is None:
        logger.warning("skipping %s: no reference DEM found", folder.name)
        return None
    if image_path is None:
        logger.warning("skipping %s: no input image found", folder.name)
        return None

    ref_dem, valid_mask = load_reference(ref_path)

    with tempfile.TemporaryDirectory(prefix="run_eval_") as tmp:
        metadata = process_image(str(image_path), tmp)
        pred = load_heightmap(Path(metadata["heightmap_path"]))

    # Resize ref (and its nodata mask) onto the prediction grid here so
    # evaluate() sees matching shapes and the mask stays aligned to the DEM.
    if ref_dem.shape != pred.shape:
        h, w = pred.shape[:2]
        ref_dem = cv2.resize(
            ref_dem.astype(np.float32), (w, h), interpolation=cv2.INTER_LINEAR
        ).astype(np.float64)
        if valid_mask is not None:
            valid_mask = cv2.resize(
                valid_mask.astype(np.uint8), (w, h),
                interpolation=cv2.INTER_NEAREST,
            ).astype(bool)

    result = evaluate(pred, ref_dem, valid_mask)
    return {
        "pair_name": folder.name,
        "terrain_type": terrain_type(folder.name),
        **{k: result[k] for k in (
            "mae_m", "rmse_m", "pearson_r", "slope_rmse",
            "n_valid_px", "fit_scale", "fit_shift",
        )},
    }


def main(argv=None) -> int:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")
    parser = argparse.ArgumentParser(
        prog="python scripts/run_eval.py",
        description="Evaluate the depth pipeline against reference DEMs.",
    )
    parser.add_argument("pairs_dir", help="directory of pair subfolders")
    parser.add_argument("output_csv", help="CSV path to write results to")
    args = parser.parse_args(argv)

    pairs_dir = Path(args.pairs_dir)
    if not pairs_dir.is_dir():
        parser.error(f"pairs_dir is not a directory: {pairs_dir}")

    rows: list[dict] = []
    for folder in sorted(p for p in pairs_dir.iterdir() if p.is_dir()):
        try:
            row = evaluate_pair(folder)
        except Exception as exc:  # one bad pair must not sink the whole run
            logger.warning("skipping %s: %s", folder.name, exc)
            continue
        if row is not None:
            rows.append(row)
            logger.info(
                "%s: rmse=%.3f pearson_r=%.3f",
                folder.name, row["rmse_m"], row["pearson_r"],
            )

    out_path = Path(args.output_csv)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with out_path.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=CSV_COLUMNS)
        writer.writeheader()
        writer.writerows(rows)

    logger.info("wrote %d row(s) to %s", len(rows), out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main())

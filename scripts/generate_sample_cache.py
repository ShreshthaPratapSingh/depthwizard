#!/usr/bin/env python
"""Pre-generate cached pipeline results for the 3 demo sample images.

Writes heightmap.png, texture.png, and metadata.json into
backend/data/samples/cached/<sample_id>/ for each sample.

These cached results are used as an offline fallback when the live
pipeline is unavailable during judging (see main.py process_sample).

Usage:
    python scripts/generate_sample_cache.py
"""
from __future__ import annotations

import json
import logging
import shutil
import sys
import tempfile
from pathlib import Path

logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
logger = logging.getLogger("generate_sample_cache")

_REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_REPO_ROOT))

SAMPLES_DIR = _REPO_ROOT / "backend" / "data" / "samples"
CACHE_DIR = SAMPLES_DIR / "cached"
MANIFEST = SAMPLES_DIR / "samples.json"


def main() -> None:
    if not MANIFEST.is_file():
        logger.error("samples.json not found at %s", MANIFEST)
        raise SystemExit(1)

    catalog = json.loads(MANIFEST.read_text(encoding="utf-8"))
    samples = catalog.get("samples", [])

    if not samples:
        logger.warning("No samples in manifest — nothing to cache.")
        return

    from depth_pipeline.run import process_image

    for entry in samples:
        sid = entry["id"]
        filename = entry["filename"]
        input_path = SAMPLES_DIR / filename

        if not input_path.is_file():
            logger.warning("Skipping %s — file not found: %s", sid, input_path)
            continue

        logger.info("Processing %s (%s)...", sid, filename)
        cache_out = CACHE_DIR / sid
        cache_out.mkdir(parents=True, exist_ok=True)

        tmp_dir = tempfile.mkdtemp(prefix=f"dw_cache_{sid}_")
        try:
            output_dir = Path(tmp_dir) / "output"
            metadata = process_image(
                image_path=str(input_path),
                output_dir=str(output_dir),
            )

            # Copy artifacts to cache
            for key in ("heightmap_path", "texture_path", "confidence_mask_path"):
                src = metadata.get(key)
                if src and Path(src).is_file():
                    dst = cache_out / Path(src).name
                    shutil.copy2(src, dst)
                    metadata[key] = str(dst)
                    logger.info("  Cached %s → %s", Path(src).name, dst)

            # Write metadata
            meta_path = cache_out / "metadata.json"
            # Remove paths that contain temp dirs
            meta_clean = {
                k: v for k, v in metadata.items()
                if not (isinstance(v, str) and tmp_dir in v)
            }
            meta_clean["cached"] = True
            meta_clean["sample_id"] = sid
            meta_path.write_text(json.dumps(meta_clean, indent=2, default=str) + "\n",
                                 encoding="utf-8")
            logger.info("  Wrote metadata → %s", meta_path)

        finally:
            shutil.rmtree(tmp_dir, ignore_errors=True)

    logger.info("Cache generation complete. %d samples cached to %s",
                len(samples), CACHE_DIR)


if __name__ == "__main__":
    main()

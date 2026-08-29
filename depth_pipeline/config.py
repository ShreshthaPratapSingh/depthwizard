"""Pipeline configuration: output names, sizes, and stub model identity."""

from pathlib import Path

# Output resolution of the (square) heightmap. 1025 = 2^10 + 1, a common
# terrain/heightmap size that tiles cleanly for GPU meshing.
DEFAULT_TARGET_RES = 1025

# Texture is always resized to this square RGB size.
TEXTURE_SIZE = 1024

# Default model id for accuracy-log rows. process_image metadata uses
# depth_pipeline.inference.MODEL_ID when infer_depth runs.
MODEL_ID = "depth-anything-v2-small"

# Canonical output filenames written into output_dir.
HEIGHTMAP_NAME = "heightmap.png"
TEXTURE_NAME = "texture.png"
CONFIDENCE_NAME = "confidence.png"
METADATA_NAME = "metadata.json"
SRTM_ALIGNED_NAME = "srtm_aligned.tif"
CALIBRATION_NAME = "calibration.json"

_REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_SRTM_TILE_DIR = _REPO_ROOT / "backend" / "data" / "srtm_tiles"
ACCURACY_LOG_PATH = _REPO_ROOT / "docs" / "accuracy_log.jsonl"

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

# ---------------------------------------------------------------------------
# Postprocessing (depth_pipeline.postprocess) — all thresholds live here so
# there are no magic numbers inline. See postprocess.py for the algorithm.
# ---------------------------------------------------------------------------

# Edge-preserving smoothing. Guided filter is preferred (opencv-contrib /
# cv2.ximgproc); bilateral is the fallback when ximgproc is unavailable.
GUIDED_FILTER_RADIUS = 8
GUIDED_FILTER_EPS = 1e-3
BILATERAL_D = 9
BILATERAL_SIGMA_COLOR = 75.0
BILATERAL_SIGMA_SPACE = 75.0

# Water heuristic: a pixel is water only when it is BOTH blue-ish (blue/green
# color ratio above threshold) AND locally flat (5x5 stddev below threshold).
# The AND keeps blue-but-textured surfaces (rooftops, tarps) from flagging.
WATER_BLUE_RATIO_THRESHOLD = 0.05    # (B - G) / (B + G + eps), 0 = neutral
WATER_VARIANCE_THRESHOLD = 6.0       # local 5x5 stddev, on the 0-255 gray scale
WATER_LOCAL_STDDEV_KERNEL = 5        # window for the local stddev
# Masked water elevation is flattened to this percentile of the non-water
# elevation, a plausible low baseline instead of noisy monocular depth.
WATER_BASELINE_PERCENTILE = 5.0

# Vegetation confidence: ExG = 2G - R - B, then normalized per-image. Dense
# canopy (high ExG) is a low-confidence zone for monocular depth.
#
# Normalization is percentile-based PER IMAGE (not a fixed absolute range): ExG
# is clipped to its own [low, high] percentile before mapping to 0-1, so it
# adapts to each image's exposure/contrast. A fixed range tuned on a saturated
# synthetic green rectangle fails to fire on real, low-saturation olive canopy.
EXG_PERCENTILE_LOW = 5.0             # ExG below this image percentile -> 0
EXG_PERCENTILE_HIGH = 95.0           # ExG above this image percentile -> 1
EXG_VEG_THRESHOLD = 0.20             # normalized ExG above this = vegetation
# Morphological closing merges speckled per-tree-crown detections into a
# contiguous canopy zone (a useful overlay, not salt-and-pepper noise).
VEG_MORPH_KERNEL_SIZE = 15

# Confidence values written into confidence.png (0-255, higher = more trust).
FULL_CONFIDENCE = 255                # everything not flagged
WATER_CONFIDENCE = 128              # elevation was replaced with a baseline guess
VEG_CONFIDENCE = 100               # canopy: monocular depth unreliable

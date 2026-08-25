"""Pipeline configuration: output names, sizes, and stub model identity."""

# Output resolution of the (square) heightmap. 1025 = 2^10 + 1, a common
# terrain/heightmap size that tiles cleanly for GPU meshing.
DEFAULT_TARGET_RES = 1025

# Texture is always resized to this square RGB size.
TEXTURE_SIZE = 1024

# Identifies which model produced the outputs. While we emit a synthetic
# heightmap this is a placeholder; it becomes the real HF repo id later.
MODEL_ID = "synthetic-sinewave-v0"

# Canonical output filenames written into output_dir.
HEIGHTMAP_NAME = "heightmap.png"
TEXTURE_NAME = "texture.png"
CONFIDENCE_NAME = "confidence.png"
METADATA_NAME = "metadata.json"

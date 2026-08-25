"""Export helpers: write heightmap/texture/confidence/metadata to disk.

Not implemented yet as a standalone module. The current entrypoint writes these
inline; this module will centralize the write logic (and formats like GeoTIFF)
later.
"""


def write_outputs(output_dir: str, **artifacts):
    """Write pipeline artifacts (heightmap, texture, confidence, metadata)."""
    raise NotImplementedError("export.write_outputs is not implemented yet")

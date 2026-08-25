"""Depth model inference.

Not implemented yet. Will load Depth-Anything-V2 (via transformers/torch) and
run it here. NOTE: torch must NOT be imported at module import time elsewhere —
keep any torch import local to this module so the rest of the pipeline stays
importable on CPU-only machines with no torch installed.
"""


def run_depth(model_input):
    """Run the depth model and return a raw relative-depth array."""
    raise NotImplementedError("inference.run_depth is not implemented yet")

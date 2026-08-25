"""Image preprocessing: load, orient, resize, normalize before inference.

Not implemented yet. The current entrypoint does its own minimal resize inline;
this module will hold the real preprocessing once the model is wired in.
"""


def prepare(image_path: str):
    """Load and preprocess an image into model input tensors/arrays."""
    raise NotImplementedError("preprocess.prepare is not implemented yet")

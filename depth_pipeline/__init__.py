"""DepthWizard depth pipeline.

Public API:
    process_image(image_path, output_dir, target_res=1025) -> dict

Only the entrypoint is implemented so far; it emits a synthetic sine-wave
heightmap (no model, no torch) so the rest of the system can be wired up and
tested on a plain CPU (e.g. a MacBook with no GPU).
"""
__all__ = ["process_image"]


def __getattr__(name):
    # Lazy re-export so importing the package does not eagerly import run.py.
    # This keeps `from depth_pipeline import process_image` working while
    # avoiding a double-import warning under `python -m depth_pipeline.run`.
    if name == "process_image":
        from .run import process_image
        return process_image
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")

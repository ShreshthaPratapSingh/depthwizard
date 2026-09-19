"""Depth model inference: Depth-Anything-V2-Small, loaded locally.

Design notes
------------
* torch / transformers are imported lazily *inside* functions so this module
  (and the rest of the package) stays importable on machines without torch.
* The model is a module-level lazy singleton: it loads once on the first
  ``infer_depth`` call and stays resident. Cold load is ~10-20s; we only pay it
  once against the 60s end-to-end budget.
* Determinism: seeds are pinned and cuDNN is forced deterministic so the same
  input yields byte-identical output across repeated runs on the same machine.

Output convention (IMPORTANT)
-----------------------------
The *relative* Depth-Anything-V2 checkpoints (this "-small" one) output
``predicted_depth`` as DISPARITY / inverse depth: NEAR surfaces -> LARGE value,
FAR surfaces -> SMALL value. (Metric DA-V2 variants differ; this one is
relative.) This is the documented transformers convention and is confirmable
with:  ``python -m depth_pipeline.inference <image>``  which prints the raw
disparity stats before any normalization.

We must return "higher value = higher ELEVATION". For this project's near-nadir
terrain captures, sensor depth = altitude - elevation, so disparity (~1/depth)
*increases* with elevation. Therefore the raw disparity is already in the
"higher = higher elevation" orientation and we do NOT invert.

If your imagery is oblique (horizon in frame, distant terrain sits high in the
scene), "near" no longer means "high elevation" — flip ``INVERT_TO_ELEVATION``
to True. It is deliberately a single, well-named switch.
"""
import logging
import os
import threading
import time
from pathlib import Path

import numpy as np

logger = logging.getLogger(__name__)

# ---------------------------------------------------------------------------
# Backbone selection: base vs fine-tuned
#
# Set env var DEPTHWIZARD_BACKBONE to one of:
#   "base"             — original DA-V2-Small (zero-shot)
#   "gamus-finetuned"  — GAMUS fine-tuned checkpoint
#
# Default: "gamus-finetuned" if the checkpoint directory exists, else "base".
# ---------------------------------------------------------------------------
_MODELS_ROOT = Path(__file__).resolve().parent.parent / "models"
_BASE_DIR = _MODELS_ROOT / "depth-anything-v2-small"
_FINETUNED_DIR = _MODELS_ROOT / "depth-anything-v2-small-gamus"
_FINETUNED_V3_DIR = _MODELS_ROOT / "gamus-v3-stageA"

_BACKBONE_CHOICES = {
    "base": (_BASE_DIR, "depth-anything-v2-small"),
    "gamus-finetuned": (_FINETUNED_DIR, "depth-anything-v2-small-gamus"),
    "gamus-v3": (_FINETUNED_V3_DIR, "gamus-v3-stageA"),
}

def _resolve_backbone() -> tuple[Path, str]:
    """Pick the model directory and ID based on env / availability."""
    env = os.environ.get("DEPTHWIZARD_BACKBONE", "").strip().lower()
    if env in _BACKBONE_CHOICES:
        choice = env
    elif _FINETUNED_V3_DIR.is_dir() and (_FINETUNED_V3_DIR / "model.safetensors").is_file():
        choice = "gamus-v3"
    elif _FINETUNED_DIR.is_dir() and (_FINETUNED_DIR / "model.safetensors").is_file():
        choice = "gamus-finetuned"
    else:
        choice = "base"
    model_dir, model_id = _BACKBONE_CHOICES[choice]
    logger.info("Backbone selected: %s  (dir=%s)", choice, model_dir)
    return model_dir, model_id

MODEL_DIR, MODEL_ID = _resolve_backbone()

SEED = 42

# See the module docstring. False = keep raw disparity orientation (near/high =
# high elevation), which is correct for near-nadir captures.
INVERT_TO_ELEVATION = False

# --- module-level singleton state (guarded by _LOCK) ---
_LOCK = threading.Lock()
_model = None
_processor = None
_device = None
_dtype = None

# Last measured model inference time (ms); read by run.py for metadata.
_last_inference_ms: float | None = None


def get_last_inference_ms() -> float | None:
    """Return the inference time (ms) of the most recent infer_depth call."""
    return _last_inference_ms


def _load():
    """Load the model + processor once and cache them on the module."""
    global _model, _processor, _device, _dtype
    if _model is not None:
        return _model, _processor, _device, _dtype

    with _LOCK:
        if _model is not None:  # re-check inside the lock
            return _model, _processor, _device, _dtype

        import torch
        from transformers import AutoImageProcessor, AutoModelForDepthEstimation

        if not MODEL_DIR.exists():
            raise FileNotFoundError(
                f"Local weights not found at {MODEL_DIR}. "
                f"Run: python scripts/download_weights.py"
            )

        # Determinism: pin seeds and force deterministic cuDNN.
        np.random.seed(SEED)
        torch.manual_seed(SEED)
        if torch.cuda.is_available():
            torch.cuda.manual_seed_all(SEED)
        torch.backends.cudnn.deterministic = True
        torch.backends.cudnn.benchmark = False

        # Device / dtype: fp16 on CUDA, float32 on MPS/CPU.
        if torch.cuda.is_available():
            device = torch.device("cuda")
            dtype = torch.float16
        elif getattr(torch.backends, "mps", None) is not None and torch.backends.mps.is_available():
            device = torch.device("mps")
            dtype = torch.float32
        else:
            device = torch.device("cpu")
            dtype = torch.float32

        t0 = time.perf_counter()
        processor = AutoImageProcessor.from_pretrained(
            MODEL_DIR, local_files_only=True
        )
        model = AutoModelForDepthEstimation.from_pretrained(
            MODEL_DIR, local_files_only=True, torch_dtype=dtype
        )
        model.to(device).eval()
        load_ms = (time.perf_counter() - t0) * 1000.0

        # Log checkpoint details for diagnosing stale-model issues
        ckpt_path = MODEL_DIR / "model.safetensors"
        if ckpt_path.is_file():
            import datetime
            stat = ckpt_path.stat()
            mtime = datetime.datetime.fromtimestamp(stat.st_mtime)
            logger.info(
                "Checkpoint: %s  (%.1f MB, modified %s)",
                ckpt_path, stat.st_size / 1e6, mtime.strftime("%Y-%m-%d %H:%M:%S"),
            )

        logger.info(
            "Loaded %s on %s (%s) in %.0f ms", MODEL_ID, device, dtype, load_ms
        )

        _model, _processor, _device, _dtype = model, processor, device, dtype
        return _model, _processor, _device, _dtype


def _to_pil(image: np.ndarray):
    """Coerce an HxW or HxWx3 numpy image into an RGB PIL image."""
    from PIL import Image

    arr = np.asarray(image)
    if arr.ndim == 2:
        pil = Image.fromarray(arr).convert("RGB")
    elif arr.ndim == 3 and arr.shape[2] in (3, 4):
        pil = Image.fromarray(arr[..., :3].astype(np.uint8), "RGB")
    else:
        raise ValueError(f"Unsupported image shape for inference: {arr.shape}")
    return pil


def _raw_disparity(image: np.ndarray) -> np.ndarray:
    """Run the model and return raw disparity (near=high) at the input HxW.

    Times the model forward pass and stores it in ``_last_inference_ms``.
    """
    global _last_inference_ms
    import torch

    model, processor, device, dtype = _load()
    pil = _to_pil(image)
    orig_h, orig_w = np.asarray(image).shape[:2]

    if device.type == "cuda":
        torch.cuda.synchronize()
    t0 = time.perf_counter()

    inputs = processor(images=pil, return_tensors="pt")
    pixel_values = inputs["pixel_values"].to(device=device, dtype=dtype)

    with torch.inference_mode():
        predicted_depth = model(pixel_values=pixel_values).predicted_depth

    # Upsample back to the original resolution. Cast to float32 first: fp16
    # bicubic is both lossy and a determinism risk.
    prediction = torch.nn.functional.interpolate(
        predicted_depth.unsqueeze(1).float(),
        size=(orig_h, orig_w),
        mode="bicubic",
        align_corners=False,
    ).squeeze(1).squeeze(0)

    raw = prediction.detach().to("cpu", torch.float32).numpy()

    if device.type == "cuda":
        torch.cuda.synchronize()
    _last_inference_ms = (time.perf_counter() - t0) * 1000.0
    logger.info("infer_depth: %.1f ms on %s", _last_inference_ms, device)

    return raw


def infer_depth(image: np.ndarray) -> np.ndarray:
    """Estimate a normalized elevation map from an RGB image.

    Parameters
    ----------
    image : np.ndarray
        HxW (grayscale) or HxWx3/HxWx4 (RGB/RGBA) uint8 image.

    Returns
    -------
    np.ndarray
        float32, shape HxW, values in [0, 1], where HIGHER = HIGHER ELEVATION.
    """
    raw = _raw_disparity(image)

    # Normalize to [0, 1]. raw is disparity (near=high); see module docstring.
    dmin = float(raw.min())
    dmax = float(raw.max())
    disparity01 = (raw - dmin) / (dmax - dmin + 1e-8)

    elevation01 = 1.0 - disparity01 if INVERT_TO_ELEVATION else disparity01
    return elevation01.astype(np.float32)


def _debug_stats(image_path: str) -> None:
    """Print raw + normalized stats so the depth convention can be verified."""
    from PIL import Image

    img = np.asarray(Image.open(image_path).convert("RGB"))
    raw = _raw_disparity(img)
    out = infer_depth(img)
    print(f"image            : {image_path}  {img.shape}")
    print(f"device / dtype   : {_device} / {_dtype}")
    print(f"inference_ms     : {_last_inference_ms:.1f}")
    print(
        "raw disparity    : min=%.4f max=%.4f mean=%.4f  (near=high for DA-V2)"
        % (raw.min(), raw.max(), raw.mean())
    )
    print(f"INVERT_TO_ELEVATION = {INVERT_TO_ELEVATION}")
    print(
        "elevation output : min=%.4f max=%.4f mean=%.4f  (higher = higher elevation)"
        % (out.min(), out.max(), out.mean())
    )


if __name__ == "__main__":
    import sys

    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    if len(sys.argv) != 2:
        print("usage: python -m depth_pipeline.inference <image>")
        raise SystemExit(2)
    _debug_stats(sys.argv[1])

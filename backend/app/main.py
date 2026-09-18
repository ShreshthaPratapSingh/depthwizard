# =============================================================================
# backend/app/main.py — Minimal FastAPI integration endpoint for DepthWizard
#
# This is a THIN HTTP wrapper around the existing depth_pipeline.process_image()
# function.  It exists solely to let Unity (or any HTTP client) trigger the
# pipeline via a POST request instead of a CLI invocation.
#
# Intentionally deferred (per MVP-first integration priority):
#   - Metric calibration (depth -> meters fitting)
#   - Robust / structured error handling & validation
#   - Authentication, rate limiting, request size limits
#   - Streaming / chunked file download for large outputs
#
# Run:
#   cd <repo_root>
#   uvicorn backend.app.main:app --reload --host 0.0.0.0 --port 8000
# =============================================================================

from __future__ import annotations

# Prevent OMP Error #15 when numpy and torch link different OpenMP runtimes.
# Must be set before any numpy/torch import.
import os
os.environ.setdefault("KMP_DUPLICATE_LIB_OK", "TRUE")

import base64
import json
import shutil
import tempfile
import threading
import traceback
import uuid
from pathlib import Path

from fastapi import FastAPI, File, UploadFile
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

# ---------------------------------------------------------------------------
# App
# ---------------------------------------------------------------------------

app = FastAPI(
    title="DepthWizard Backend",
    description="Minimal integration endpoint exposing depth_pipeline over HTTP.",
    version="0.1.0",
)

# Permissive CORS — this is a hackathon demo running locally.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)


@app.on_event("startup")
def _startup_warmup():
    """Preload the depth model so the first real request is fast."""
    from depth_pipeline.run import warmup
    warmup()


# ---------------------------------------------------------------------------
# GET /health
# ---------------------------------------------------------------------------

@app.get("/health")
async def health():
    """Basic reachability check.  Returns 200 with {"status": "ok"}."""
    return {"status": "ok"}

# ---------------------------------------------------------------------------
# POST /process
# ---------------------------------------------------------------------------

@app.post("/process")
async def process(file: UploadFile = File(...)):
    """Accept an image upload, run the depth pipeline, and return results.

    The uploaded file is saved to a temporary directory, processed by
    ``depth_pipeline.process_image()`` (the same function the CLI uses),
    and the generated heightmap, texture, and full metadata are returned
    in the JSON response.

    Heightmap and texture are base64-encoded PNG bytes so the caller gets
    everything in a single response without needing to manage file paths
    or make follow-up download requests.
    """
    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_")
    try:
        # --- Save the upload to disk ---
        filename = Path(file.filename or "upload.png").name
        input_path = Path(tmp_dir) / filename
        with open(input_path, "wb") as f:
            shutil.copyfileobj(file.file, f)

        return _run_pipeline(input_path, tmp_dir)

    except Exception as exc:
        # Minimal error handling: return the exception as a JSON error so the
        # caller gets a structured response instead of an HTML 500 page.
        return JSONResponse(
            status_code=500,
            content={
                "status": "error",
                "detail": str(exc),
                "traceback": traceback.format_exc(),
            },
        )
    finally:
        # Clean up the temp directory. Best-effort, don't crash if removal
        # fails (e.g. file lock on Windows).
        shutil.rmtree(tmp_dir, ignore_errors=True)

# ---------------------------------------------------------------------------
# GET /samples
# ---------------------------------------------------------------------------

_SAMPLES_DIR = Path(__file__).resolve().parent.parent / "data" / "samples"
_SAMPLES_MANIFEST = _SAMPLES_DIR / "samples.json"


def _load_samples() -> list[dict]:
    """Read the samples manifest and filter to entries whose file exists."""
    if not _SAMPLES_MANIFEST.is_file():
        return []
    catalog = json.loads(_SAMPLES_MANIFEST.read_text(encoding="utf-8"))
    out = []
    for entry in catalog.get("samples", []):
        if (_SAMPLES_DIR / entry["filename"]).is_file():
            out.append({
                "id": entry["id"],
                "name": entry["name"],
                "filename": entry["filename"],
                "is_georef": entry["is_georef"],
            })
    return out


@app.get("/samples")
async def list_samples():
    """Return the list of available preloaded sample images.

    Only entries whose image file is present on disk are included.
    """
    return {"samples": _load_samples()}

# ---------------------------------------------------------------------------
# POST /process-sample/{sample_id}
# ---------------------------------------------------------------------------

@app.post("/process-sample/{sample_id}")
async def process_sample(sample_id: str):
    """Run the depth pipeline on a preloaded sample image.

    Uses the same pipeline and returns the same response schema as
    POST /process, but reads from backend/data/samples/ instead of
    an uploaded file.

    If the live pipeline fails, falls back to cached results (if available)
    from backend/data/samples/cached/<sample_id>/.
    """
    catalog = _load_samples()
    entry = next((s for s in catalog if s["id"] == sample_id), None)
    if entry is None:
        return JSONResponse(
            status_code=404,
            content={
                "status": "error",
                "detail": f"Sample '{sample_id}' not found or image file missing.",
            },
        )

    input_path = _SAMPLES_DIR / entry["filename"]
    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_")
    try:
        return _run_pipeline(input_path, tmp_dir)

    except Exception as exc:
        # --- Fallback: serve cached results if available ---
        cached = _try_cached_fallback(sample_id)
        if cached is not None:
            return cached

        return JSONResponse(
            status_code=500,
            content={
                "status": "error",
                "detail": str(exc),
                "traceback": traceback.format_exc(),
            },
        )
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def _try_cached_fallback(sample_id: str):
    """Attempt to serve cached results for a sample.

    Returns a JSONResponse if cached data is available, None otherwise.
    Logs clearly when fallback is used.
    """
    import logging
    logger = logging.getLogger("depthwizard.cache")

    cache_dir = _SAMPLES_DIR / "cached" / sample_id
    meta_path = cache_dir / "metadata.json"
    heightmap_path = cache_dir / "heightmap.png"
    texture_path = cache_dir / "texture.png"

    if not meta_path.is_file():
        logger.warning(
            "No cached fallback for sample '%s' (missing %s)", sample_id, meta_path
        )
        return None

    logger.warning(
        "⚠ USING CACHED FALLBACK for sample '%s' — live pipeline failed. "
        "Results are pre-computed, not from a live run.",
        sample_id,
    )

    try:
        metadata = json.loads(meta_path.read_text(encoding="utf-8"))
        heightmap_b64 = _read_b64(str(heightmap_path))
        texture_b64 = _read_b64(str(texture_path))

        response = {
            **metadata,
            "heightmap_b64": heightmap_b64,
            "texture_b64": texture_b64,
            "cached_fallback": True,
        }
        return JSONResponse(content=response)

    except Exception as cache_exc:
        logger.error("Cached fallback failed for '%s': %s", sample_id, cache_exc)
        return None


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _read_b64(path: str | None) -> str | None:
    """Read a file and return its contents as a base64-encoded string."""
    if path is None:
        return None
    p = Path(path)
    if not p.is_file():
        return None
    return base64.b64encode(p.read_bytes()).decode("ascii")


def _run_pipeline(input_path: Path, tmp_dir: str) -> JSONResponse:
    """Run process_image on *input_path* and return a JSONResponse.

    The caller is responsible for creating and cleaning up *tmp_dir*.
    """
    # Import lazily so the module stays importable even when torch is absent
    # (e.g. during linting or lightweight tests that don't hit /process).
    from depth_pipeline.run import process_image

    output_dir = Path(tmp_dir) / "output"
    metadata = process_image(
        image_path=str(input_path),
        output_dir=str(output_dir),
    )

    # --- Read generated files and base64-encode them ---
    heightmap_b64 = _read_b64(metadata.get("heightmap_path"))
    texture_b64 = _read_b64(metadata.get("texture_path"))
    confidence_b64 = _read_b64(metadata.get("confidence_mask_path"))
    dsm_b64 = _read_b64(metadata.get("dsm_geotiff_path"))
    slope_b64 = _read_b64(metadata.get("slope_path"))

    # --- Build response mirroring process_image's metadata dict ---
    # We include the full metadata dict as-is, plus the encoded file
    # contents so the caller has everything in one round trip.
    response = {
        **metadata,
        # Replace server-local absolute paths with encoded file content.
        # The original path keys are kept for reference but will point to
        # the (soon-deleted) temp directory, the b64 fields are the
        # usable payload.
        "heightmap_b64": heightmap_b64,
        "texture_b64": texture_b64,
        "confidence_b64": confidence_b64,
        "dsm_b64": dsm_b64,
        "slope_b64": slope_b64,
    }

    return JSONResponse(content=response)


# ---------------------------------------------------------------------------
# Async job tracking (in-memory, suitable for single-process hackathon demo)
# ---------------------------------------------------------------------------

_jobs: dict[str, dict] = {}
_jobs_lock = threading.Lock()


@app.post("/process-async")
async def process_async(file: UploadFile = File(...)):
    """Accept an image upload and start processing in the background.

    Returns immediately with a job_id. Poll GET /jobs/{job_id} for progress
    and results.
    """
    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_")
    filename = Path(file.filename or "upload.png").name
    input_path = Path(tmp_dir) / filename
    with open(input_path, "wb") as f:
        shutil.copyfileobj(file.file, f)

    job_id = uuid.uuid4().hex[:12]
    with _jobs_lock:
        _jobs[job_id] = {
            "status": "queued",
            "stage": "",
            "progress_detail": "",
            "result": None,
            "error": None,
        }

    thread = threading.Thread(
        target=_run_pipeline_job,
        args=(job_id, input_path, tmp_dir),
        daemon=True,
    )
    thread.start()

    return {"job_id": job_id}


@app.get("/jobs/{job_id}")
async def get_job(job_id: str):
    """Poll the status and progress of an async job.

    While running, returns the current pipeline stage. When done, returns
    the same response payload as POST /process.
    """
    with _jobs_lock:
        job = _jobs.get(job_id)
        if job is not None:
            job = dict(job)

    if job is None:
        return JSONResponse(
            status_code=404,
            content={"status": "error", "detail": f"Job '{job_id}' not found."},
        )
    return job


def _run_pipeline_job(job_id: str, input_path: Path, tmp_dir: str) -> None:
    """Background thread target. Runs the pipeline and updates _jobs."""
    from depth_pipeline.run import process_image

    def _on_stage(stage: str, detail: str = "") -> None:
        with _jobs_lock:
            if job_id in _jobs:
                _jobs[job_id]["status"] = "running"
                _jobs[job_id]["stage"] = stage
                _jobs[job_id]["progress_detail"] = detail

    try:
        output_dir = Path(tmp_dir) / "output"
        metadata = process_image(
            image_path=str(input_path),
            output_dir=str(output_dir),
            on_stage=_on_stage,
        )

        heightmap_b64 = _read_b64(metadata.get("heightmap_path"))
        texture_b64 = _read_b64(metadata.get("texture_path"))
        confidence_b64 = _read_b64(metadata.get("confidence_mask_path"))
        dsm_b64 = _read_b64(metadata.get("dsm_geotiff_path"))
        slope_b64 = _read_b64(metadata.get("slope_path"))

        result = {
            **metadata,
            "heightmap_b64": heightmap_b64,
            "texture_b64": texture_b64,
            "confidence_b64": confidence_b64,
            "dsm_b64": dsm_b64,
            "slope_b64": slope_b64,
        }

        with _jobs_lock:
            if job_id in _jobs:
                _jobs[job_id]["status"] = "done"
                _jobs[job_id]["stage"] = "done"
                _jobs[job_id]["result"] = result

    except Exception as exc:
        with _jobs_lock:
            if job_id in _jobs:
                _jobs[job_id]["status"] = "error"
                _jobs[job_id]["stage"] = "error"
                _jobs[job_id]["error"] = str(exc)
                _jobs[job_id]["progress_detail"] = traceback.format_exc()

    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


# ---------------------------------------------------------------------------
# POST /validate (FR21)
# ---------------------------------------------------------------------------

@app.post("/validate")
async def validate(
    predicted: UploadFile = File(...),
    reference: UploadFile = File(...),
):
    """Compare a predicted DSM against a reference DSM.

    Upload both as raster files (GeoTIFF or single-band image). Returns
    RMSE, MAE, Pearson r, slope RMSE, and a base64-encoded error heatmap PNG.
    """
    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_validate_")
    try:
        import numpy as np
        from depth_pipeline.metrics import evaluate

        pred_path = Path(tmp_dir) / "predicted"
        ref_path = Path(tmp_dir) / "reference"
        with open(pred_path, "wb") as f:
            shutil.copyfileobj(predicted.file, f)
        with open(ref_path, "wb") as f:
            shutil.copyfileobj(reference.file, f)

        # Read both as single-band arrays
        pred_arr = _read_single_band(str(pred_path))
        ref_arr = _read_single_band(str(ref_path))

        if pred_arr is None or ref_arr is None:
            return JSONResponse(
                status_code=400,
                content={"status": "error", "detail": "Could not read one or both files as raster data."},
            )

        scores = evaluate(pred_arr, ref_arr)

        # Build error heatmap (absolute difference, normalized to 0-255)
        import cv2
        from PIL import Image as PILImage
        import io

        if ref_arr.shape != pred_arr.shape:
            ref_resized = cv2.resize(
                ref_arr.astype(np.float32),
                (pred_arr.shape[1], pred_arr.shape[0]),
                interpolation=cv2.INTER_LINEAR,
            )
        else:
            ref_resized = ref_arr.astype(np.float32)

        # Scale+shift align prediction for error map
        a, b = scores["fit_scale"], scores["fit_shift"]
        pred_aligned = (a * pred_arr.astype(np.float64) + b).astype(np.float32)
        error = np.abs(pred_aligned - ref_resized)

        # Normalize error to 0-255 for visualization
        emax = float(np.nanpercentile(error[np.isfinite(error)], 98)) if np.any(np.isfinite(error)) else 1.0
        if emax < 1e-6:
            emax = 1.0
        error_u8 = np.clip(error / emax * 255.0, 0, 255).astype(np.uint8)

        # Apply colormap (hot = high error)
        error_colored = cv2.applyColorMap(error_u8, cv2.COLORMAP_JET)
        error_rgb = cv2.cvtColor(error_colored, cv2.COLOR_BGR2RGB)

        buf = io.BytesIO()
        PILImage.fromarray(error_rgb).save(buf, format="PNG")
        error_b64 = base64.b64encode(buf.getvalue()).decode("ascii")

        return {
            "status": "ok",
            "rmse_m": scores["rmse_m"],
            "mae_m": scores["mae_m"],
            "pearson_r": scores["pearson_r"],
            "slope_rmse": scores["slope_rmse"],
            "n_valid_px": scores["n_valid_px"],
            "fit_scale": scores["fit_scale"],
            "fit_shift": scores["fit_shift"],
            "error_heatmap_b64": error_b64,
        }

    except Exception as exc:
        return JSONResponse(
            status_code=500,
            content={"status": "error", "detail": str(exc), "traceback": traceback.format_exc()},
        )
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)


def _read_single_band(path: str):
    """Read a file as a single-band float32 array. Tries rasterio, then PIL."""
    import numpy as np
    from pathlib import Path as P

    ext = P(path).suffix.lower()
    if ext in {".tif", ".tiff"}:
        try:
            import rasterio
            with rasterio.open(path) as src:
                return src.read(1).astype(np.float32)
        except Exception:
            pass

    try:
        from PIL import Image as PILImage
        with PILImage.open(path) as im:
            return np.asarray(im.convert("F"), dtype=np.float32)
    except Exception:
        return None


# ---------------------------------------------------------------------------
# POST /correct-gcp (FR18)
# ---------------------------------------------------------------------------

@app.post("/correct-gcp")
async def correct_gcp(
    file: UploadFile = File(...),
    gcps: str = "",
):
    """Apply GCP correction to an uploaded image's predicted DSM.

    The ``gcps`` parameter is a JSON string: a list of [lon, lat, elevation_m]
    triples (for georeferenced images) or [col, row, value] triples.

    Returns the corrected heightmap and correction metadata.
    """
    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_gcp_")
    try:
        import numpy as np
        from depth_pipeline.run import process_image
        from depth_pipeline.geospatial.gcp import correct_with_gcps_geo, correct_with_gcps_pixel

        # Parse GCPs
        gcp_list = json.loads(gcps) if gcps else []
        if not gcp_list or len(gcp_list) < 3:
            return JSONResponse(
                status_code=400,
                content={"status": "error", "detail": "Need at least 3 GCPs as [[x,y,z], ...]."},
            )

        # Save upload and run pipeline
        filename = Path(file.filename or "upload.png").name
        input_path = Path(tmp_dir) / filename
        with open(input_path, "wb") as f:
            shutil.copyfileobj(file.file, f)

        output_dir = Path(tmp_dir) / "output"
        metadata = process_image(str(input_path), str(output_dir))

        if metadata.get("status") != "ok":
            return JSONResponse(
                status_code=500,
                content={"status": "error", "detail": "Pipeline failed", **metadata},
            )

        # Get elevation to correct
        # Use calibrated elevation if available, else the heightmap
        heightmap_path = metadata.get("heightmap_path")
        if heightmap_path is None:
            return JSONResponse(
                status_code=500,
                content={"status": "error", "detail": "No heightmap produced."},
            )

        from PIL import Image as PILImage
        with PILImage.open(heightmap_path) as im:
            elev = np.asarray(im).astype(np.float32) / 65535.0

        # Apply GCP correction
        gcp_tuples = [tuple(g) for g in gcp_list]
        bbox = metadata.get("bbox")
        if bbox and metadata.get("is_georeferenced"):
            result = correct_with_gcps_geo(elev, gcp_tuples, bbox)
        else:
            result = correct_with_gcps_pixel(elev, gcp_tuples)

        response = {
            "status": "ok" if result.ok else "error",
            "gcp_correction": result.to_dict(),
        }

        if result.ok and result.elevation_m is not None:
            # Encode corrected heightmap
            corrected_u8 = np.clip(result.elevation_m * 255, 0, 255).astype(np.uint8)
            import io
            buf = io.BytesIO()
            PILImage.fromarray(corrected_u8, mode="L").save(buf, format="PNG")
            response["corrected_heightmap_b64"] = base64.b64encode(buf.getvalue()).decode("ascii")

        return response

    except Exception as exc:
        return JSONResponse(
            status_code=500,
            content={"status": "error", "detail": str(exc), "traceback": traceback.format_exc()},
        )
    finally:
        shutil.rmtree(tmp_dir, ignore_errors=True)

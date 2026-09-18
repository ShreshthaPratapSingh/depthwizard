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

        result = {
            **metadata,
            "heightmap_b64": heightmap_b64,
            "texture_b64": texture_b64,
            "confidence_b64": confidence_b64,
            "dsm_b64": dsm_b64,
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

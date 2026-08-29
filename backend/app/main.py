# =============================================================================
# backend/app/main.py — Minimal FastAPI integration endpoint for DepthWizard
#
# This is a THIN HTTP wrapper around the existing depth_pipeline.process_image()
# function.  It exists solely to let Unity (or any HTTP client) trigger the
# pipeline via a POST request instead of a CLI invocation.
#
# Intentionally deferred (per MVP-first integration priority):
#   • Metric calibration (depth → meters fitting)
#   • Robust / structured error handling & validation
#   • Async / background job queue for long-running inference
#   • Authentication, rate limiting, request size limits
#   • Streaming / chunked file download for large outputs
#
# Run:
#   cd <repo_root>
#   uvicorn backend.app.main:app --reload --host 0.0.0.0 --port 8000
# =============================================================================

from __future__ import annotations

import base64
import json
import shutil
import tempfile
import traceback
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
    # Import lazily so the module stays importable even when torch is absent
    # (e.g. during linting or lightweight tests that don't hit /process).
    from depth_pipeline.run import process_image

    tmp_dir = tempfile.mkdtemp(prefix="depthwizard_")
    try:
        # --- Save the upload to disk ---
        input_path = Path(tmp_dir) / (file.filename or "upload.png")
        with open(input_path, "wb") as f:
            shutil.copyfileobj(file.file, f)

        # --- Run the existing pipeline (same defaults as the CLI) ---
        output_dir = Path(tmp_dir) / "output"
        metadata = process_image(
            image_path=str(input_path),
            output_dir=str(output_dir),
        )

        # --- Read generated files and base64-encode them ---
        heightmap_b64 = _read_b64(metadata.get("heightmap_path"))
        texture_b64 = _read_b64(metadata.get("texture_path"))

        # --- Build response mirroring process_image's metadata dict ---
        # We include the full metadata dict as-is, plus the encoded file
        # contents so the caller has everything in one round trip.
        response = {
            **metadata,
            # Replace server-local absolute paths with encoded file content.
            # The original path keys are kept for reference but will point to
            # the (soon-deleted) temp directory — the b64 fields are the
            # usable payload.
            "heightmap_b64": heightmap_b64,
            "texture_b64": texture_b64,
        }

        return JSONResponse(content=response)

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
        # Clean up the temp directory.  Best-effort — don't crash if removal
        # fails (e.g. file lock on Windows).
        shutil.rmtree(tmp_dir, ignore_errors=True)


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

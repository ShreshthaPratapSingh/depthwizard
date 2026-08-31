# DepthWizard — SIH26175 (ISRO)

Single-image elevation reconstruction and 3D flythrough.

## Structure
- `depth_pipeline/` — Python: depth inference, geospatial calibration, export
- `backend/` — FastAPI service wrapping `depth_pipeline` for HTTP access
- `unity-client/` — Unity: terrain generation, rendering, camera controls, export
- `docs/` — architecture, API contract, accuracy log

## Setup

### Backend (local)

Requires Python 3.11+ and a CUDA-capable GPU (the root `requirements.txt` pins
`torch==2.7.1+cu128` for Blackwell GPUs). This wheel will not install on macOS;
Mac users should use the Docker path below.

```bash
cd <repo_root>
python -m venv backend/.venv
source backend/.venv/bin/activate
pip install -r requirements.txt           # torch, transformers, rasterio, etc.
pip install -r backend/requirements.txt   # fastapi, uvicorn, python-multipart
```

Download SRTM tiles (needed for metric calibration):
```bash
python scripts/download_srtm.py
```

Optionally drop sample image files into `backend/data/samples/` and register
them in `samples.json` (see `backend/data/samples/README.md` for format).

Start the server:
```bash
bash backend/start_backend.sh
# or directly: uvicorn backend.app.main:app --host 0.0.0.0 --port 8000 --reload
```

### Backend (Docker)

Build from the repo root:
```bash
docker build -t depthwizard-backend -f backend/Dockerfile .
docker run --gpus all -p 8000:8000 depthwizard-backend
```

### Unity client
Open `unity-client/` as a project in Unity Hub (version: TBD).

## API

The backend exposes these endpoints on `localhost:8000`:

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Reachability check |
| `/process` | POST | Synchronous depth pipeline (file upload) |
| `/process-async` | POST | Async pipeline, returns a job_id |
| `/jobs/{job_id}` | GET | Poll async job progress and result |
| `/samples` | GET | List preloaded sample images |
| `/process-sample/{id}` | POST | Run pipeline on a preloaded sample |

Full request/response schemas and error codes are in
[docs/api-contract.md](docs/api-contract.md).

## Team
See docs/architecture.md for roles and pipeline breakdown.

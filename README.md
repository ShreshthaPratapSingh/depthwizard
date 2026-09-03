<p align="center">
  <h1 align="center">DepthWizard</h1>
  <p align="center">
    Single-image elevation reconstruction and 3D flythrough<br/>
    <strong>SIH 2026 &middot; Problem Statement 26175 &middot; ISRO</strong>
  </p>
</p>

---

## What It Does

Drop a single satellite image. Get a 3D terrain you can fly through.

DepthWizard takes one 2D satellite image (GeoTIFF or plain PNG/JPG) and produces a digital elevation model plus an interactive 3D flythrough, replacing the traditional dependency on stereo pairs, LiDAR, or InSAR with a single-image, AI-driven pipeline. Designed for disaster response, rapid reconnaissance, and preliminary terrain analysis where speed matters more than survey-grade precision.

**Two modes:**

| Input type | Output |
|-----------|--------|
| **GeoTIFF** (with CRS + bbox) | Metric elevation calibrated against SRTM (meters above sea level) |
| **PNG / JPG** (no coordinates) | Relative elevation, scaled for visualization |

---

## Architecture

```
 ┌─────────────────────────────────────────────────────────────┐
 │                    Unity Client (C#)                        │
 │  Landing Page → Upload/Sample → Processing UI → Flythrough │
 │  Terrain builder · Drone camera · Orbit · Cinematic path   │
 │  Elevation toggle · Export (OBJ + heightmap) · Metrics HUD  │
 └──────────────────────────┬──────────────────────────────────┘
                            │  HTTP (localhost:8000)
 ┌──────────────────────────▼──────────────────────────────────┐
 │                  Python Backend (FastAPI)                    │
 │  /process-async → depth_pipeline.run.process_image()        │
 │  /samples · /process-sample/{id} · /jobs/{id} · /health     │
 └──────────────────────────┬──────────────────────────────────┘
                            │
 ┌──────────────────────────▼──────────────────────────────────┐
 │                    Depth Pipeline                            │
 │  Depth-Anything-V2-Small (PyTorch)                          │
 │  → Postprocess (guided filter, water mask, veg confidence)  │
 │  → SRTM calibration (linear/polynomial fit when GeoTIFF)    │
 │  → Export (16-bit heightmap + RGB texture + confidence mask) │
 └─────────────────────────────────────────────────────────────┘
```

---

## Repository Structure

```
depthwizard/
├── depth_pipeline/           # Core ML + geospatial pipeline
│   ├── inference.py          #   Depth-Anything-V2-Small inference
│   ├── postprocess.py        #   Edge-preserving smoothing, water/veg masking
│   ├── export.py             #   16-bit heightmap + texture + confidence export
│   ├── metrics.py            #   RMSE, MAE, Pearson r, slope RMSE evaluation
│   ├── config.py             #   Centralized thresholds and constants
│   ├── run.py                #   Pipeline orchestrator + CLI
│   └── geospatial/           #   GeoTIFF reader, SRTM alignment, calibration
├── backend/
│   ├── app/main.py           #   FastAPI endpoints (thin HTTP wrapper)
│   ├── start_backend.sh      #   One-line server launcher
│   ├── Dockerfile            #   GPU-ready container build
│   └── data/
│       ├── samples/          #   3 demo GeoTIFFs (Delhi, Mussoorie, Chennai)
│       └── srtm_tiles/       #   Reference SRTM tiles for calibration
├── unity-client/
│   └── Assets/Scripts/
│       ├── Networking/       #   BackendClient (HTTP, base64 decode, async polling)
│       ├── Terrain/          #   RuntimeTerrainBuilder, TerrainElevationController
│       ├── Camera/           #   DroneController, CinematicFlythrough, SpawnPositioner
│       ├── UI/               #   Landing page, processing panel, metrics HUD,
│       │                     #   sample catalog, control hints, image picker
│       └── Export/           #   OBJ + MTL + heightmap exporter
├── tests/                    #   53 pytest cases (calibration, export, hardening, ...)
├── scripts/                  #   download_srtm.py, download_weights.py, run_eval.py
├── docs/                     #   PRD, API contract, model report
└── requirements.txt          #   Python dependencies (CUDA 12.8 build)
```

---

## Quick Start

### Prerequisites

- **Python 3.11+** with pip
- **NVIDIA GPU** with CUDA 12.8 drivers (for `torch==2.7.1+cu128`)
- **Unity 2022.3 LTS** or later (for the 3D client)

> **macOS / no GPU:** The root `requirements.txt` pins CUDA wheels that won't install on Mac. Use the Docker path below, or swap to CPU-only torch (slower inference).

### 1. Clone and install

```bash
git clone https://github.com/ShreshthaPratapSingh/depthwizard.git
cd depthwizard

python -m venv backend/.venv
source backend/.venv/bin/activate
pip install -r requirements.txt
pip install -r backend/requirements.txt
```

### 2. Download model weights and SRTM tiles

```bash
python scripts/download_weights.py     # Depth-Anything-V2-Small (~100 MB)
python scripts/download_srtm.py        # SRTM GL1 30m tiles for demo regions
```

### 3. Start the backend

```bash
bash backend/start_backend.sh
# or: uvicorn backend.app.main:app --host 0.0.0.0 --port 8000 --reload
```

The server preloads the depth model on startup so the first request is fast.  
Verify: `curl http://localhost:8000/health` should return `{"status": "ok"}`.

### 4. Open the Unity client

Open `unity-client/` in Unity Hub. Open the **LandingPage** scene. Press Play.

- Upload an image or click a sample button
- Wait for processing (progress bar shown)
- Explore the 3D terrain

### Docker (alternative)

```bash
docker build -t depthwizard-backend -f backend/Dockerfile .
docker run --gpus all -p 8000:8000 depthwizard-backend
```

---

## Controls (Flythrough Scene)

| Key | Action |
|-----|--------|
| **WASD** | Move (free-fly mode) |
| **Mouse** | Look / rotate |
| **Space / Ctrl** | Ascend / descend |
| **Shift** | Boost speed |
| **Tab** | Toggle free-fly / orbit mode |
| **M** | Toggle relative / absolute elevation |
| **C** | Start / stop cinematic flythrough |
| **E** | Export terrain (OBJ + heightmap) |
| **Esc** | Release cursor |

---

## API

Base URL: `http://localhost:8000`

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health` | GET | Reachability check |
| `/process` | POST | Synchronous pipeline (multipart file upload) |
| `/process-async` | POST | Async pipeline, returns `job_id` |
| `/jobs/{job_id}` | GET | Poll async job progress and result |
| `/samples` | GET | List preloaded demo images |
| `/process-sample/{id}` | POST | Run pipeline on a preloaded sample |

Full schemas: [docs/api-contract.md](docs/api-contract.md)

---

## ML Pipeline

**Model:** Depth-Anything-V2-Small (PyTorch, fp16 on CUDA, float32 fallback on MPS/CPU).

**Pipeline stages:**

1. **Inference** -- normalized [0,1] elevation map (higher = higher elevation)
2. **Postprocessing** -- guided-filter smoothing (preserves ridgelines), water flattening (blue + low variance regions), vegetation confidence masking (ExG index)
3. **Calibration** (GeoTIFF only) -- linear/polynomial fit against SRTM GL1 30m reference. Outputs R², RMSE, MAE, sample count.
4. **Export** -- 16-bit heightmap PNG (vertical flip for Unity), RGB texture, confidence mask

**Hardened:** `process_image()` never raises. Returns structured `status="error"` metadata on any failure. Oversized inputs auto-downscale. Model preloaded at startup via `warmup()`.

---

## Accuracy

Measured against SRTM GL1 after scale+shift alignment:

| Region | Terrain | R² | RMSE | Verdict |
|--------|---------|-----|------|---------|
| Delhi | flat urban | ~0 | ~10 m | Meets <50 m |
| Chennai | low-relief coast | ~0.68 | ~4.5 m | Meets <50 m |
| Mussoorie | high-relief hills | ~0.39 | ~243 m | Misses |
| Arunachal | high-relief hills | ~0.26 | ~318 m | Misses |

Low-relief terrain meets the 50 m RMSE target. Steep terrain fails systematically due to DA-V2's zero-shot limitation on nadir imagery (weak correlation, pearson_r ~ 0.38). This is an inherent model limitation, not a code bug. See [docs/model_report.md](docs/model_report.md) for full analysis.

**Why RMSE alone is misleading:** RMSE/MAE are computed after a least-squares fit that can make weak-signal predictions look acceptable. We report pearson_r and slope_rmse alongside RMSE/MAE because they are scale/shift invariant and not fooled by the alignment.

---

## Testing

```bash
pip install pytest
pytest tests/ -v
```

53 test cases covering:
- Calibration fitting and edge cases
- GeoTIFF metadata extraction and normalization
- SRTM alignment and tile matching
- Postprocessing (water flattening, vegetation confidence)
- Pipeline hardening (error paths, warmup safety, input validation)
- Accuracy metrics (correlation, resampling, nodata handling)
- Export artifact generation

---

## Demo Samples

Three GeoTIFFs are included for out-of-the-box demo:

| Sample | Region | Terrain | Size |
|--------|--------|---------|------|
| `delhi_urban.tif` | Central Delhi | Flat urban | 4.1 MB |
| `mussoorie_hilly.tif` | Mussoorie hills | High-relief | 4.0 MB |
| `chennai_coastal.tif` | Chennai Marina coast | Low-relief coastal | 1.2 MB |

These appear as clickable buttons on the landing page when the backend is running.

---

## Known Limitations

- **High-relief RMSE** is systematically above 50 m (Mussoorie ~243 m, Arunachal ~318 m). This is a zero-shot monocular depth model limitation on nadir imagery, not a calibration bug.
- **Pearson r ~ 0.38** on urban terrain -- the model picks up some real signal but is not reliably reconstructing true elevation.
- **Confidence thresholds** are not tuned per terrain type. The ExG vegetation detector over-triggers on dense urban texture (~93.5% low-confidence on Delhi).
- **Tiling for very large images** was descoped. Oversized inputs are auto-downscaled instead of tiled.
- Improving accuracy would require **supervised fine-tuning on paired satellite/LiDAR data** (e.g., DFC2019).

---

## Documentation

| Document | Contents |
|----------|----------|
| [docs/prd.md](docs/prd.md) | Full Product Requirements Document |
| [docs/api-contract.md](docs/api-contract.md) | REST API schemas and error codes |
| [docs/model_report.md](docs/model_report.md) | Model selection, pipeline details, accuracy analysis |

---

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Depth estimation | Depth Anything V2 (PyTorch) |
| Geospatial | rasterio, GDAL, scipy, scikit-learn |
| Backend API | FastAPI + uvicorn |
| Container | Docker (NVIDIA CUDA base) |
| 3D rendering | Unity (URP) |
| Input system | Unity Input System (new) |
| Version control | Git + GitHub |

---

## License

[MIT](LICENSE)

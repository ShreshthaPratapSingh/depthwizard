# DepthWizard — SIH26175 (ISRO)

Single-image elevation reconstruction and 3D flythrough.

## Structure
- `backend/` — Python: depth inference, geospatial calibration, FastAPI service
- `unity-client/` — Unity: terrain generation, rendering, UI
- `docs/` — architecture, API contract, accuracy log

## Setup
### Backend
    cd backend
    python -m venv .venv
    source .venv/bin/activate
    pip install -r requirements.txt
    uvicorn app.main:app --reload

### Unity client
Open `unity-client/` as a project in Unity Hub (version: TBD — fill in once decided).

## Team
See docs/architecture.md for roles and pipeline breakdown.

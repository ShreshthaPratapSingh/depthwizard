# DepthWizard Backend
#
# Requires NVIDIA GPU + nvidia-container-toolkit at runtime for CUDA inference.
# The CUDA 12.8 torch wheel is installed as pinned in the root requirements.txt.
#
# Build:
#   docker build -t depthwizard-backend -f backend/Dockerfile .
#
# Run:
#   docker run --gpus all -p 8000:8000 depthwizard-backend

FROM python:3.11-slim

# System dependencies for opencv-python and rasterio.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgl1 \
    libglib2.0-0 \
    libgdal-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Install Python dependencies. Root requirements first (torch, transformers,
# imaging, geospatial), then backend-specific (fastapi, uvicorn).
COPY requirements.txt /app/requirements.txt
COPY backend/requirements.txt /app/backend_requirements.txt
RUN pip install --no-cache-dir -r requirements.txt \
    && pip install --no-cache-dir -r backend_requirements.txt

# Copy application code.
COPY depth_pipeline/ /app/depth_pipeline/
COPY backend/ /app/backend/

EXPOSE 8000

CMD ["uvicorn", "backend.app.main:app", "--host", "0.0.0.0", "--port", "8000"]

FROM python:3.11-slim

# System deps for rasterio/GDAL and opencv
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdal-dev gdal-bin libgl1 libglib2.0-0 git git-lfs \
    && rm -rf /var/lib/apt/lists/*

# Create app dir
WORKDIR /app

# Copy requirements first (cache layer)
COPY requirements.txt .
COPY backend/requirements.txt backend/requirements.txt

# Install Python deps (CPU-only torch to keep image small)
RUN pip install --no-cache-dir \
    torch==2.7.1+cpu torchvision==0.22.1+cpu \
    --index-url https://download.pytorch.org/whl/cpu

# Install remaining deps (skip torch lines from requirements.txt)
RUN pip install --no-cache-dir \
    "transformers>=4.45.0" \
    "huggingface_hub>=0.24.0" \
    "safetensors>=0.4.3" \
    "pillow>=10.3.0" \
    "numpy>=1.26,<3" \
    "opencv-python>=4.9.0" \
    "rasterio>=1.3.10" \
    "scipy>=1.13.0" \
    "scikit-learn>=1.4.0" \
    "fastapi>=0.111.0" \
    "uvicorn[standard]>=0.30.0" \
    "python-multipart>=0.0.9"

# Copy application code
COPY backend/ backend/
COPY depth_pipeline/ depth_pipeline/
COPY scripts/ scripts/

# Download model weights from HuggingFace Hub at build time
RUN python scripts/download_weights.py

# Expose port (HF Spaces expects port 7860)
EXPOSE 7860

# HF Spaces sets $PORT=7860. Run uvicorn on that port.
ENV KMP_DUPLICATE_LIB_OK=TRUE
CMD ["uvicorn", "backend.app.main:app", "--host", "0.0.0.0", "--port", "7860"]

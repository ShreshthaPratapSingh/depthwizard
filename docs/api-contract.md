# DepthWizard API Contract

Base URL: `http://localhost:8000`

All responses are JSON. Heightmap and texture payloads are base64-encoded PNG.

---

## GET /health

Reachability check.

**Response 200**
```json
{"status": "ok"}
```

---

## POST /process

Synchronous depth pipeline. Blocks until inference, calibration, and export complete (10-60s typical).

**Request**: `multipart/form-data` with field `file` (PNG, JPEG, or GeoTIFF).

**Response 200**
```json
{
  "status": "ok",
  "heightmap_b64": "<base64 PNG>",
  "texture_b64": "<base64 PNG>",
  "width": 1025,
  "height": 1025,
  "model_id": "depth-anything-v2-small",
  "inference_ms": 1234.5,
  "relative_min": 0.0,
  "relative_max": 1.0,
  "is_georeferenced": true,
  "crs": "EPSG:32643",
  "bbox": [73.0, 18.0, 73.1, 18.1],
  "is_calibrated": true,
  "min_elev_m": 450.2,
  "max_elev_m": 892.7,
  "r_squared": 0.943,
  "sample_count": 128,
  "srtm_tile_id": "N18E073",
  "srtm_aligned": true,
  "srtm_aligned_path": "/tmp/.../srtm_aligned.tif",
  "srtm_coverage": 1.0,
  "calibration": {"model": "linear", "coeffs": [0.89, 12.3]},
  "warning": null,
  "warnings": ["calibrated to SRTM (R2=0.943, n=128)"],
  "heightmap_path": "/tmp/.../heightmap.png",
  "texture_path": "/tmp/.../texture.png",
  "confidence_mask_path": "/tmp/.../confidence.png"
}
```

Fields `min_elev_m`, `max_elev_m`, `r_squared`, `calibration` are `null` when `is_calibrated` is `false`.

**Response 500**
```json
{
  "status": "error",
  "detail": "error message",
  "traceback": "full traceback string"
}
```

---

## GET /samples

List available preloaded sample images. Only entries with an existing file on disk are returned.

**Response 200**
```json
{
  "samples": [
    {
      "id": "mumbai_coastal",
      "name": "Mumbai Coastal",
      "filename": "mumbai_coastal.tif",
      "is_georef": true
    }
  ]
}
```

---

## POST /process-sample/{sample_id}

Run the depth pipeline on a preloaded sample image. Same response schema as POST /process.

**Path parameters**: `sample_id` (string) from GET /samples.

**Response 200**: Same as POST /process.

**Response 404**
```json
{
  "status": "error",
  "detail": "Sample 'bad_id' not found or image file missing."
}
```

**Response 500**: Same as POST /process.

---

## POST /process-async

Submit an image for async processing. Returns immediately with a job ID.

**Request**: `multipart/form-data` with field `file` (same as POST /process).

**Response 200**
```json
{"job_id": "a1b2c3d4e5f6"}
```

---

## GET /jobs/{job_id}

Poll the progress of an async job.

**Path parameters**: `job_id` (string) from POST /process-async.

**While running (200)**
```json
{
  "status": "running",
  "stage": "inferring",
  "progress_detail": "running depth model",
  "result": null,
  "error": null
}
```

Stages in order: `inferring`, `calibrating`, `exporting`, `done`.

**When complete (200)**
```json
{
  "status": "done",
  "stage": "done",
  "progress_detail": "",
  "result": { "...same schema as POST /process response..." },
  "error": null
}
```

**On failure (200)**
```json
{
  "status": "error",
  "stage": "error",
  "progress_detail": "full traceback",
  "result": null,
  "error": "error message"
}
```

**Response 404**
```json
{
  "status": "error",
  "detail": "Job 'bad_id' not found."
}
```

---

## Error Codes Summary

| Code | Meaning |
|------|---------|
| 200  | Success (or job still running/failed for /jobs) |
| 404  | Sample or job not found |
| 500  | Unhandled server error |

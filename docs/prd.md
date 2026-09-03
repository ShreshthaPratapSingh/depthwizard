# Product Requirements Document (PRD)

## DepthWizard — Single-Image Elevation Reconstruction & 3D Flythrough

**SIH26175 | Organization: ISRO | Category: Software**
**Version: 1.0 | Date: August 2026**

---

## 1. Product Overview

DepthWizard is a software system that takes a single 2D satellite image as input and produces:

- A digital elevation/surface model (relative or metric, depending on input type)
- A navigable 3D reconstruction of the terrain, rendered as an interactive flythrough

The product removes the traditional dependency on stereo image pairs, LiDAR flights, or InSAR processing for generating elevation data, replacing a multi-pass, sensor-heavy pipeline with a single-image, AI-driven one. It is aimed at scenarios where speed and data availability matter more than survey-grade precision: disaster response, rapid reconnaissance, and preliminary urban/terrain analysis.

The product has two operating modes:

- **Georeferenced mode** -- input includes coordinate metadata (GeoTIFF), output is calibrated to real-world metric elevation (meters)
- **Non-georeferenced mode** -- input is a plain image (PNG/JPG), output is relative elevation, scaled for visualization only

## 2. Problem Statement

Generating accurate 3D elevation models today requires one of:

- **Stereo imagery** -- needs two well-aligned satellite passes, not always available for a given time/location
- **LiDAR** -- expensive, requires dedicated aerial survey missions
- **InSAR** -- computationally heavy, radar-hardware dependent

These methods are slow, costly, and often unavailable in time-critical scenarios (e.g., 6-24 hours after a flood or earthquake, when only a single fresh satellite pass exists). There is no accessible way to go from "one image just came in" to "we understand the 3D shape of this area" without one of the above pipelines.

**Core ask (from the PS):** build an AI system that infers elevation from a single image and visualizes it as a navigable 3D environment, working across both georeferenced and non-georeferenced inputs, evaluated on both elevation accuracy and visualization quality.

## 3. Objectives

| # | Objective | Success Metric |
|---|-----------|---------------|
| O1 | Estimate depth/elevation from a single satellite image | Depth map generated for 100% of valid test inputs |
| O2 | Calibrate relative depth to real-world metric elevation when georeference data is available | RMSE/MAE against SRTM within acceptable range (track and minimize continuously) |
| O3 | Render a navigable, textured 3D flythrough of the reconstructed terrain | Smooth (>=30fps target), stable, intuitive camera controls |
| O4 | Support both georeferenced and non-georeferenced inputs | Both paths functional and demoable |
| O5 | Package as a standalone, judge-runnable application | Single build, no external dependency at demo time |
| O6 | Generalize across terrain types | Tested and documented performance on urban, hilly, forested, flat, coastal terrain |

## 4. Target Users

| User Type | Use Case |
|-----------|----------|
| Disaster response teams | Rapid 3D situational awareness from a single post-event satellite image |
| Urban planners / municipal bodies | Preliminary terrain/structure understanding without commissioning LiDAR |
| Defense/reconnaissance analysts | Fast terrain visualization from limited-pass imagery |
| ISRO internal teams / researchers | Extracting elevation value from existing single-pass archival imagery |
| SIH Judges (immediate user) | Need to evaluate accuracy and visual quality quickly and clearly in a demo setting |

For the hackathon build, design primarily for the judge-as-user experience (clarity, speed, visual impact) while keeping the architecture realistic enough to defend as production-viable for the actual target users above.

## 5. User Workflow

**Standard flow:**

1. User launches the application
2. User uploads a satellite image (GeoTIFF or plain PNG/JPG)
3. System auto-detects whether georeference metadata is present
4. System runs depth inference, calibration (if applicable), mesh generation
5. Progress indicator shown during processing (this may take real time, manage expectations in UI)
6. User is dropped into a 3D flythrough view of the reconstructed terrain
7. User can toggle between "Relative" and "Absolute" (metric) elevation display modes, when both are available
8. User can freely navigate (fly/orbit camera) around the reconstructed scene
9. User can export the mesh/heightmap or a screenshot/recording for reporting purposes
10. User can load a new image to repeat the process

**Fallback flow (for demo reliability):**

Pre-loaded sample cases available as a "Try a sample" option, in case live upload fails or the network/image is problematic during judging.

## 6. Functional Requirements

| ID | Requirement |
|----|-------------|
| FR1 | System shall accept image upload in PNG, JPG, and GeoTIFF formats |
| FR2 | System shall detect presence/absence of georeference metadata automatically |
| FR3 | System shall run a monocular depth estimation model on the input image |
| FR4 | System shall calibrate relative depth to metric elevation using SRTM reference data when georeference is available |
| FR5 | System shall generate a heightmap from the depth/elevation output |
| FR6 | System shall generate a textured 3D mesh from the heightmap, using the original image as texture |
| FR7 | System shall render the mesh in a navigable 3D view with free-fly or orbit camera controls |
| FR8 | System shall allow toggling between relative and absolute elevation modes where both exist |
| FR9 | System shall display a processing/progress state during inference |
| FR10 | System shall handle failure gracefully (e.g., unsupported format, corrupted file) with a clear user-facing message |
| FR11 | System shall allow exporting the generated mesh/heightmap as a file (e.g., OBJ, PNG heightmap) |
| FR12 | System shall include at least 2-3 preloaded sample images for demo/fallback use |
| FR13 | System shall log or display basic accuracy metrics (RMSE/MAE) when ground truth is available for a test case |

## 7. Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR1 | Performance: End-to-end processing (upload to renderable mesh) should complete within a reasonable time for a live demo (target: under 60 seconds for typical image sizes) |
| NFR2 | Stability: No crashes on malformed or edge-case input; must fail gracefully |
| NFR3 | Portability: Standalone build (Unity executable) must run on the demo machine without live internet dependency, wherever possible |
| NFR4 | Usability: UI must be understandable without instructions, judges will not read a manual |
| NFR5 | Visual quality: Rendering must be smooth and free of major artifacts (z-fighting, texture seams, popping) |
| NFR6 | Maintainability: Pipeline stages (inference, calibration, mesh gen, render) should be modular and independently testable |
| NFR7 | Reproducibility: Given the same input, output should be consistent across runs |

## 8. System Architecture

```
+-----------------------------------------------------------+
|                        Client Layer                        |
|         Unity Application (UI + 3D Rendering Engine)       |
+---------------------------+--------------------------------+
                            | (image upload / result retrieval)
                            v
+-----------------------------------------------------------+
|                 Processing Backend (Python)                |
|  +---------------+  +------------------+  +--------------+ |
|  | Preprocessing | ->| Depth Inference  | ->| Calibration | |
|  | (format check,|  | (Depth Anything  |  | (SRTM align, | |
|  |  georef check)|  |  V2 / ZoeDepth)  |  |  metric conv)| |
|  +---------------+  +------------------+  +------+-------+ |
|                                                   v        |
|                                        +------------------+|
|                                        | Heightmap Export  ||
|                                        +------------------+|
+-----------------------------------------------------------+
                            | (heightmap + texture files)
                            v
+-----------------------------------------------------------+
|              Unity Terrain & Rendering Pipeline            |
|   Heightmap import -> Terrain mesh -> Texture drape ->     |
|   Flythrough camera -> UI overlay (mode toggle, controls)  |
+-----------------------------------------------------------+
```

Integration approach: The Python backend is exposed via a lightweight local API (FastAPI) that the Unity client calls. Unity sends the uploaded image, receives back a heightmap file (and metadata like elevation range, calibration status), and handles all rendering internally.

## 9. AI/ML Pipeline

**Stage 1 -- Preprocessing:** Validate image format and size. Resize/normalize as required by the depth model. Detect georeference metadata (CRS, bounding box) if GeoTIFF.

**Stage 2 -- Depth Inference:** Primary model: Depth Anything V2 (Large for accuracy, Small for fast iteration during development). Output: a per-pixel relative depth map (grayscale, normalized).

**Stage 3 -- Post-processing / Domain-specific correction:** Vegetation masking (NDVI-style heuristic on RGB bands, or simple green-channel thresholding if no NIR available) to flag low-confidence canopy zones. Water body masking (color/texture heuristic) to flatten flat reflective surfaces that break depth models. Smoothing pass to reduce per-pixel noise before mesh generation.

## 10. Geospatial Pipeline

Purpose: Convert relative depth values into real-world metric elevation, when georeference is available.

Steps:
1. Extract coordinate bounding box and CRS from GeoTIFF metadata using rasterio
2. Query/download the matching SRTM tile(s) for that bounding box
3. Reproject/align SRTM data to match the input image's resolution and extent
4. Sample paired points (predicted relative depth vs. SRTM real elevation)
5. Fit a calibration function (linear regression; polynomial if linear underperforms)
6. Apply the fitted mapping across the full depth map to produce final elevation map
7. Store calibration metadata (fit quality, R-squared, sample count) alongside the output

Non-georeferenced path: Skip steps 1-6 entirely; normalize relative depth to a visualization-friendly range and proceed directly to heightmap export, clearly labeled as "relative, not metric" in the UI.

## 11. 3D Visualization / Unity

**Terrain generation:** Use Unity's native Terrain system, driven by `TerrainData.SetHeights()` for programmatic heightmap import.

**Texturing:** Apply the original satellite image as a Terrain Layer / base texture, aligned to match the heightmap's UV space.

**Camera system:** Free-fly camera (WASD + mouse look) for exploratory navigation. Optional: a scripted "cinematic flythrough" path for the demo.

**Mode toggle:** UI control to switch between "Relative" and "Absolute" elevation display.

## 12. UI/UX

Screens/states:
- **Landing/Upload screen** -- drag-and-drop or file-picker upload, "Try a sample" fallback option
- **Processing screen** -- progress indicator, brief status text
- **Flythrough/Result screen** -- the 3D view with mode toggle, camera control hint, basic stats overlay, export button, "Load new image" button

## 13. Data & File Formats

| Data | Format | Notes |
|------|--------|-------|
| Input image (georeferenced) | GeoTIFF | Must contain CRS + bounding box metadata |
| Input image (non-georeferenced) | PNG / JPG | No coordinate data assumed |
| Reference elevation data | SRTM (GeoTIFF, 30m resolution) | Used only for calibration |
| Heightmap for Unity | 16-bit PNG | Imported via TerrainData API |
| Exported mesh | OBJ | For downstream use outside the app |
| Calibration metadata | JSON | Fit parameters, R-squared, sample count |

## 14. Technology Stack

| Layer | Technology | Purpose |
|-------|-----------|---------|
| Depth estimation | Depth Anything V2 (PyTorch) | Core ML inference |
| Geospatial processing | rasterio, GDAL, numpy, scikit-learn | Georeference handling, calibration |
| Backend API | FastAPI (Python) | Serve pipeline to Unity client |
| Packaging (backend) | Docker (optional) | Consistent runtime environment |
| Rendering & UI | Unity (C#) | Terrain generation, texturing, flythrough, UI |
| Version control | Git + GitHub | Team collaboration |
| Testing/QA | Python (numpy-based RMSE/MAE scripts) | Accuracy tracking against SRTM |

## 15. Error Handling & Edge Cases

| Case | Handling |
|------|---------|
| Unsupported file format | Reject with clear message |
| Corrupted/unreadable image | Catch on load, show error, do not crash |
| Missing/invalid georeference metadata | Fall back to non-georeferenced (relative) mode automatically |
| SRTM tile unavailable | Fall back to relative mode, notify user |
| Extremely large image | Auto-downscale before processing, notify user |
| Dense forest/vegetation | Flag low-confidence regions visually |
| Water bodies causing depth artifacts | Apply water mask, flatten to baseline |
| Network/backend unavailable during demo | Preloaded sample results cached locally |

## 16. Evaluation & Accuracy

Per the official grading split:
- 50% -- DSM/elevation accuracy (RMSE, MAE, correlation against reference data, across terrain types)
- 50% -- Visualization quality (projection accuracy, fidelity, navigability, UI intuitiveness, stability)

## 17. MVP Scope

**Must-have:**
- Depth inference working on both sample types (georeferenced + plain image)
- Calibration module functional for at least the provided/known test regions
- Heightmap to Unity terrain pipeline working end-to-end
- Basic flythrough camera, functional and stable
- Mode toggle (relative/absolute)
- Upload + preloaded sample fallback
- Basic error handling for the most likely failure cases

**Should-have (if time permits):**
- Vegetation/water masking for improved accuracy
- Export functionality (mesh/heightmap download)
- Cinematic scripted flythrough path
- Accuracy stats overlay in-app

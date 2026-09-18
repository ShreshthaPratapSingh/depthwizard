# DepthWizard — Status Audit v2

**Audited against:** PRD v2.0 (September 15, 2026)  
**Audit date:** September 18, 2026  
**Auditor:** Automated codebase analysis  
**Previous audit:** docs/prd.md (v1.0-based)

---

## STEP 1 — PRD Diff (v1.0 → v2.0)

### Summary of What Changed

The v2.0 PRD is a **ground-up rewrite**, not an incremental edit. It retains the same project identity (SIH26175, ISRO, single-image depth → 3D flythrough) but revises the specification in every major area based on the prototype built and an ML review.

### New Requirements Added (v2.0 only)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR14 | Fine-tune DA-V2-Small on GAMUS dataset for metric nDSM | P1 |
| FR15 | Evaluation harness on committed GAMUS test manifest with per-city/class/bucket breakdowns | P1 |
| FR16 | Export DSM as float32 GeoTIFF in meters with CRS preserved | P2 |
| FR17 | Calibration redesign: DSM = low-pass SRTM terrain + predicted nDSM | P2 |
| FR18 | Optional Ground Control Point correction | P2 |
| FR19 | Full-resolution tiling with overlap and feathered blending | P2 |
| FR20 | Slope raster (degrees) computed from DSM, exposed via API | P2 |
| FR21 | Reference validation endpoint (`/validate`) | P3 |
| FR22 | Fix vegetation confidence bug (absolute chromatic threshold) | P3 |
| FR23 | Render confidence mask as overlay in Unity | P3 |
| FR24 | Visible on-screen UI controls for all keyboard-only actions | P3 |
| O7 | Domain-adapt the backbone (new objective) | — |
| O8 | Standard geospatial DSM output (new objective) | — |
| NFR8–NFR11 | Laptop training constraints, MPS inference, zero budget, honest reporting | — |

### Requirements Removed or Superseded

| v1.0 Item | What happened |
|-----------|---------------|
| Depth Anything V2 Large | Explicitly dropped; Small retained by design choice |
| ZoeDepth / MiDaS comparison | Not carried out; v2.0 says "check model_report.md; record skip decision" |
| FBX mesh export | v1.0 listed FBX; v2.0 only lists OBJ+MTL as built; FBX not built |
| 7-day development plan (§19 v1.0) | Superseded by phased roadmap (Phase 0–5 + Unity parallel) |
| Linear calibration as primary strategy | Superseded by terrain + nDSM composite design (FR17) |
| SRTM as primary evaluation reference | Demoted to sanity check; GAMUS LiDAR nDSM is now the evaluation target |

### Requirements Reworded / Scope Changed

| ID | v1.0 wording | v2.0 change |
|----|-------------|-------------|
| O2 | "RMSE/MAE against SRTM" | Changed to "against LiDAR-derived reference (GAMUS nDSM)" |
| FR4 | "Calibrate to metric using SRTM" | Now "Partial" — works for flat terrain; redesign to composite planned |
| FR8 | "Toggle relative/absolute" | Now requires **visible UI control**, not just M key |
| FR13 | "Show RMSE/MAE when ground truth exists" | Now flagged: HUD shows fit statistics, not independent accuracy |
| §16 Evaluation | "50% DSM accuracy, 50% Viz quality" | Explicitly states evaluation is "against LiDAR/reference data", not SRTM |
| §17 MVP | Tiling of one large image moved **into scope** (was descoped) |
| §17 MVP | Fine-tuning + GeoTIFF DSM export elevated to **Must-have** (were Should/absent) |

### Evaluation Criteria Changes

| Category | v1.0 | v2.0 |
|----------|------|------|
| DSM estimation accuracy (50%) | "RMSE, MAE, correlation against reference data" | Same weight, but reference is now LiDAR/GAMUS, explicitly not SRTM |
| Rendering & UX (50%) | "projection accuracy, fidelity, navigability, UI intuitiveness, stability" | Same, plus "successful standalone deployment" added |

### Impact on Previously "Done" Items

> [!WARNING]
> The following items were marked "Done" under v1.0 but their meaning or scope has changed enough that the status needs re-assessment:

| Item | v1.0 status | v2.0 impact |
|------|-------------|-------------|
| FR4 (Calibration) | Done | **Downgraded to Partial** — linear/poly fit works but fails on high-relief; redesign to composite planned |
| FR8 (Mode toggle) | Done | **Downgraded to Partial** — keyboard-only (M key); v2.0 requires visible UI button |
| FR13 (Accuracy display) | Done | **Downgraded to Partial** — HUD shows SRTM fit stats, not independent accuracy metrics |
| O2 (Metric calibration) | Done | **Changed** — success metric moved from "vs SRTM" to "vs LiDAR"; current SRTM-only approach no longer satisfies |
| Evaluation method | Assumed adequate | **Flawed** — 30m SRTM cannot resolve building-scale structure; new protocol required |

---

## STEP 2 — Full Status Audit Against PRD v2.0

### 2.1 Objectives

| # | Objective | Status | Evidence |
|---|-----------|--------|----------|
| O1 | Estimate elevation from a single image | ✅ Done | `run.py:process_image()` — never-raises contract; `inference.py:infer_depth()` |
| O2 | Calibrate to metric elevation (vs LiDAR reference) | ⚠️ Partially Done | `calibrate.py` does linear/poly fit vs SRTM. No LiDAR-based evaluation integrated into pipeline. GAMUS benchmark exists in scripts only (`benchmark_gamus.py`) |
| O3 | Render navigable textured flythrough | ✅ Done | `DroneController.cs` (free-fly + orbit), `CinematicFlythrough.cs`, `RuntimeTerrainBuilder.cs` |
| O4 | Support georeferenced + plain inputs | ✅ Done | `mode.py` decision tree; `geotiff.py` CRS detection; graceful fallback to relative |
| O5 | Standalone judge-runnable application | ⚠️ Partially Done | `LAUNCH_DEPTHWIZARD.bat` exists. **Missing:** built Unity executable (launches editor, not standalone); no offline cached results; weights require pre-download |
| O6 | Generalize across terrain types | ⚠️ Partially Done | SRTM-based metrics for Delhi/Chennai/Mussoorie/Arunachal exist. GAMUS benchmark (18 tiles) run. **Missing:** per-terrain documented performance with LiDAR reference; forest is visual-only; hilly terrain fails |
| O7 | Domain-adapt backbone (new) | ⚠️ Partially Done | `finetune_gamus.py` exists; fine-tuned checkpoint at `models/depth-anything-v2-small-gamus/` (99 MB); `eval_finetuned.py` exists; `accuracy-log.md` has before/after. **Missing:** backbone flag to switch base/finetuned at runtime; leave-DC-out model; full 3-city ~3000-tile training (current is ~200 tiles); no degradation augmentation; no per-class/per-bucket breakdowns committed |
| O8 | Standard geospatial DSM output | ❌ Not Started | No float32 GeoTIFF DSM export anywhere in the codebase. Export only writes 16-bit PNG and OBJ |

### 2.2 Functional Requirements — v1.0 originals (FR1–FR13)

| ID | Requirement | Status | Evidence / Gap |
|----|-------------|--------|----------------|
| FR1 | Accept PNG, JPG, GeoTIFF | ✅ Done | `run.py:_read_rgb()` — rasterio for TIFF, PIL for PNG/JPG; non-uint8 percentile normalization |
| FR2 | Auto-detect georeference | ✅ Done | `geotiff.py` + `mode.py` decision tree |
| FR3 | Run monocular depth model | ✅ Done | `inference.py` — DA-V2-Small, fp16 on CUDA, fp32 on CPU/MPS, deterministic seeds |
| FR4 | Calibrate to metric using SRTM | ⚠️ Partially Done | `calibrate.py` — linear/poly-2 fit works on flat terrain; **fails on high relief** (RMSE 243m Mussoorie, 318m Arunachal); composite redesign (FR17) planned |
| FR5 | Generate heightmap | ✅ Done | `export.py` — 1025×1025 16-bit PNG, vertically flipped for Unity |
| FR6 | Textured 3D mesh | ✅ Done | `RuntimeTerrainBuilder.cs` — hand-written 16-bit PNG decoder, terrain layer texturing |
| FR7 | Navigable 3D view | ✅ Done | `DroneController.cs` — free-fly + orbit (Tab), terrain/boundary clamping |
| FR8 | Relative/absolute toggle | ⚠️ Partially Done | `TerrainElevationController.cs` — M key only; **no visible on-screen button or units legend** |
| FR9 | Progress state | ✅ Done | `ProcessingController.cs` + `ProcessingPanelUI.cs` — 6 weighted stages, async job polling |
| FR10 | Graceful failure | ✅ Done | `run.py:process_image()` — never-raises contract, shape-stable metadata, hardening tests |
| FR11 | Export mesh/heightmap | ✅ Done | `TerrainExporter.cs` — OBJ+MTL+texture PNG + 16-bit heightmap; configurable vertex step; `ExportButtonHandler.cs` — E key trigger |
| FR12 | 2–3 preloaded samples | ✅ Done | 3 samples in `backend/data/samples/`; `SampleCatalogUI.cs`; `/samples` + `/process-sample/{id}` API |
| FR13 | Show RMSE/MAE when ground truth exists | ⚠️ Partially Done | `AccuracyMetricsHud.cs` — shows R²/RMSE/MAE from SRTM calibration fit. **These are fit statistics, not independent accuracy** |

### 2.3 Functional Requirements — v2.0 new (FR14–FR24)

| ID | Requirement | Priority | Status | Evidence / Gap |
|----|-------------|----------|--------|----------------|
| FR14 | Fine-tune DA-V2-Small on GAMUS for metric nDSM; backbone flag | P1 | ⚠️ Partially Done | `finetune_gamus.py` (SILog + gradient loss, ~200 tiles, neck+head only); checkpoint saved to `models/depth-anything-v2-small-gamus/`. **Missing:** backbone flag in `inference.py` (hardcoded to base model); full ~3000 tile training; leave-DC-out model; degradation augmentation |
| FR15 | Evaluation harness — GAMUS test manifest, per city/class/bucket | P1 | ⚠️ Partially Done | `benchmark_gamus.py` + `eval_finetuned.py` run 18-tile val; `accuracy-log.md` has results. **Missing:** committed test manifest (~300 tiles); per-class breakdowns; per-height-bucket breakdowns; seeded subset; leave-DC-out experiment |
| FR16 | Float32 GeoTIFF DSM export in meters with CRS | P2 | ❌ Not Started | No rasterio write of output DSM anywhere. Only SRTM ref written at `srtm.py:204`. Input CRS parsed but never attached to output |
| FR17 | Calibration: DSM = low-pass SRTM + predicted nDSM | P2 | ❌ Not Started | Current calibration is global linear/poly fit. No composite terrain+nDSM implementation |
| FR18 | Ground Control Point correction | P2 | ❌ Not Started | No GCP code found |
| FR19 | Full-resolution tiling with overlap and blending | P2 | ❌ Not Started | No tiling code in `depth_pipeline/`. Large images are still auto-downscaled |
| FR20 | Slope raster (degrees) via API | P2 | ❌ Not Started | `metrics.py` has `slope_rmse` metric but no slope raster generation or API endpoint |
| FR21 | `/validate` endpoint — upload reference DSM, return metrics + error map | P3 | ❌ Not Started | No `/validate` endpoint in `main.py` |
| FR22 | Fix vegetation confidence — absolute chromatic threshold | P3 | ❌ Not Started | `postprocess.py:_veg_confidence()` still uses per-image percentile normalization + fixed 0.20 threshold (the bug). 93.5% of Delhi flagged |
| FR23 | Render confidence mask as overlay in Unity | P3 | ❌ Not Started | `confidence.png` is exported by backend but **never sent to Unity** (no `confidence_b64` in API response) and **no overlay rendering** in any Unity script |
| FR24 | Visible UI controls for mode toggle, export, cinematic, camera mode | P3 | ❌ Not Started | All actions are keyboard-only (M/E/C/Tab). No on-screen buttons. No load-new-image flow from flythrough scene either |

### 2.4 Non-Functional Requirements

| ID | Requirement | Status | Evidence / Gap |
|----|-------------|--------|----------------|
| NFR1 | Upload → renderable mesh under 60 s | ⚠️ Verify | `inference_ms` tracked in metadata; warmup preloads model. **No formal end-to-end benchmark committed.** Tiling (FR19) would increase time |
| NFR2 | No crashes on malformed input | ✅ Done | Never-raises contract in `process_image()`; 53 pytest cases including `test_hardening.py`; auto-downscale |
| NFR3 | Standalone, no live internet | ⚠️ Partially Done | SRTM tiles + samples local; weights pre-downloadable. **Missing:** standalone Unity build (runs from editor); no offline cached results for demo fallback; fine-tuned weights need download step |
| NFR4 | Understandable without instructions | ⚠️ Partially Done | `ControlHintsOverlay.cs` exists. **But key actions are keyboard-only** (FR24 not done); judges won't discover M/C/E/Tab keys |
| NFR5 | Smooth, artifact-free rendering | ⚠️ Verify | Box-blur smoothing, URP terrain, 16-bit heightmap. **No documented FPS measurement or seam check** |
| NFR6 | Modular, independently testable stages | ✅ Done | Separate modules: `inference.py`, `postprocess.py`, `calibrate.py`, `export.py`, `metrics.py`; 53 pytest cases across 10 test files |
| NFR7 | Reproducible outputs | ✅ Done | `SEED=42`, cuDNN deterministic, SHA-pinned HF revisions in `pinned_revisions.json` |
| NFR8 | Training within laptop constraints | ✅ Done | Measured in Phase 0: head-only 1.5 GB VRAM bs8, full model 2.0 GB bs4 on RTX 5070 |
| NFR9 | Inference on CUDA, CPU, MPS | ✅ Done | `inference.py:_load()` — device selection for all three |
| NFR10 | Zero budget: no paid APIs | ✅ Done | All data open (GAMUS CC-BY-4.0, SRTM); local GPU only |
| NFR11 | Honest reporting | ⚠️ Partially Done | `accuracy-log.md` has initial entries. **Missing:** caveats section; dataset scope statement; regressions tracking; unseen-city number (leave-DC-out) |

### 2.5 MVP Scope (§18 of v2.0 PRD)

| Item | Tier | Status | Notes |
|------|------|--------|-------|
| Depth inference on georef + plain | Must | ✅ Done | |
| Calibration for known test regions | Must | ⚠️ Partially Done | Works for flat/coastal; fails on hilly |
| Heightmap → Unity terrain E2E | Must | ✅ Done | |
| Stable flythrough camera | Must | ✅ Done | |
| Relative/absolute toggle | Must | ⚠️ Partially Done | Keyboard-only |
| Upload + sample fallback | Must | ✅ Done | |
| Basic error handling | Must | ✅ Done | |
| **GAMUS fine-tuning + LiDAR eval (new)** | **Must** | ⚠️ Partially Done | Script + small checkpoint exist; not integrated into pipeline; incomplete eval |
| **Float32 GeoTIFF DSM export (new)** | **Must** | ❌ Not Started | **Hard gap in a Must-have item** |
| Vegetation/water masking | Should | ⚠️ Partially Done | Water masking works; veg masking has confirmed bug (FR22) |
| Export (mesh/heightmap) | Should | ✅ Done | OBJ+MTL+PNG+heightmap |
| Cinematic flythrough | Should | ✅ Done | Catmull-Rom spline, 8 waypoints, C key |
| In-app accuracy overlay | Should | ✅ Done | AccuracyMetricsHud (caveat: shows fit stats, not independent accuracy) |
| Full-resolution tiling (new) | Should | ❌ Not Started | |
| Slope raster + /validate (new) | Should | ❌ Not Started | |

### 2.6 Evaluation Criteria Mapping

#### Category 1: DSM Estimation Accuracy (50% weight)

| What judges look for | Current evidence | Demonstrable? |
|---------------------|------------------|---------------|
| RMSE against reference | SRTM-based: Delhi ~10m, Chennai ~4.5m, Mussoorie ~243m, Arunachal ~318m. GAMUS zero-shot: RMSE 5.14m (18 tiles). Fine-tuned: 4.97m. | ⚠️ Weak — only 18-tile GAMUS eval; SRTM numbers are fit stats, not independent; high-relief fails badly |
| MAE against reference | GAMUS zero-shot MAE 4.08m → fine-tuned 3.89m | ⚠️ Same caveats |
| Pearson r / correlation | GAMUS zero-shot r=0.240 → fine-tuned r=0.393 | ⚠️ Low — fine-tuning improves but still weak |
| Stability across terrain types | Urban + forested + mixed measured on GAMUS; hilly + forest visual-only | ⚠️ Not demonstrated on Indian terrain |
| Per-city / per-class breakdowns | Only per-terrain-type (urban/forested/mixed) in accuracy log; no per-class, no per-height-bucket | ❌ Not demonstrated |
| Unseen-city generalization | Leave-DC-out experiment not done | ❌ Not demonstrated |
| Standard DSM output (GeoTIFF) | Not built (FR16) | ❌ Cannot demonstrate |

#### Category 2: Rendering & UX (50% weight)

| What judges look for | Current evidence | Demonstrable? |
|---------------------|------------------|---------------|
| Projection accuracy | Heightmap-texture alignment via flip convention; UV mapping verified | ✅ Yes |
| Visual fidelity | 16-bit heightmap (65,536 levels); URP rendering; edge-preserving smooth | ✅ Yes |
| Flythrough navigability | Free-fly, orbit, cinematic modes; terrain/boundary clamping; smooth transitions | ✅ Yes |
| UI intuitiveness | Landing page, drag-drop, progress bar, sample buttons, control hints overlay | ⚠️ Partial — key actions keyboard-only; judges won't discover them |
| Software stability | Never-raises backend; graceful error handling; 53 test cases | ✅ Yes |
| Standalone deployment | .bat launcher exists | ❌ No standalone Unity build; launches editor |

### 2.7 Remaining Work Roadmap Status (§19)

| Phase | Status | Notes |
|-------|--------|-------|
| 0 — Recon | ✅ Done | ExG bug confirmed, GAMUS inspected, VRAM measured |
| 1 — Honest eval harness | ⚠️ Partially Done | 18-tile benchmark run; accuracy-log populated. **Missing:** committed ~300-tile manifest, per-class/bucket, leave-DC-out, full 3-city claim verification |
| 2 — Fine-tune | ⚠️ Partially Done | Script + ~200-tile training + checkpoint + eval done. **Missing:** full ~3000 tiles, degradation augmentation, Stage B (unfreeze encoder), backbone flag, leave-DC-out model |
| 3 — Calibration + export | ❌ Not Started | No composite calibration, no GCPs, no float32 GeoTIFF, no slope raster |
| 4 — Tiling | ❌ Not Started | No tiling code |
| 5 — Fixes + validation | ❌ Not Started | ExG bug unfixed, no /validate endpoint |
| Unity (parallel) | ❌ Not Started | No on-screen buttons, no units legend, no confidence overlay, no slope/height probe, no load-new flow, no offline cached results, no standalone build |

### 2.8 Team Responsibilities — Deliverables Status

| Role | Responsibility | Deliverables status |
|------|---------------|---------------------|
| **ML Lead** | Backbone, inference, fine-tuning, evaluation harness | ⚠️ Partial — finetune script + small checkpoint + benchmark exist; backbone flag, full training, leave-DC-out, model report update all missing |
| **Geospatial Engineer** | SRTM handling, calibration, GeoTIFF I/O | ❌ Zero new v2.0 deliverables — composite calibration, GCPs, GeoTIFF export, slope export all not started |
| **Rendering Lead** | Unity terrain, texturing, cameras | ❌ Zero v2.0 deliverables — confidence overlay, slope/height probe, performance check not started |
| **Backend / Pipeline Dev** | FastAPI, glue, packaging | ❌ Zero v2.0 deliverables — no new result fields, no /validate, no tiling integration, no standalone packaging |
| **Frontend / UI Dev** | Upload, processing, result screens | ❌ Zero v2.0 deliverables — no on-screen controls, no units legend, no load-new, no timeout/retry |
| **Data / QA Lead** | Test sets, accuracy tracking, demo rehearsal | ⚠️ Partial — accuracy log populated; manifest not committed, no Indian test scenes, no demo script, no fallback rehearsal |

---

## STEP 3 — Consolidated Gap List (Prioritized)

### Tier A — Hard Blockers for Working End-to-End Demo

| # | Gap | PRD ref | Why it's a blocker |
|---|-----|---------|-------------------|
| A1 | **No standalone Unity build** — application launches Unity Editor, not executable | O5, NFR3 | Judges need single-click launch; editor requires Unity installed |
| A2 | **No on-screen UI controls** — mode toggle, export, cinematic, camera mode are all keyboard-only | FR24, NFR4 | "Judges will not read instructions" (PRD §12). They **will not discover** M/E/C/Tab keys. All demo-critical actions invisible |
| A3 | **No load-new-image flow** from flythrough scene | User Workflow §5.10 | Judge processes one image, then is stuck — no way back to upload another |
| A4 | **No offline cached results** for demo fallback | Error Handling §16 | If backend is down during judging, demo is dead |
| A5 | **Backbone flag missing** — fine-tuned model exists but can't be selected at runtime | FR14 | Fine-tuned checkpoint in `models/` but `inference.py` hardcodes base model path. The improvement never reaches the demo |

### Tier B — Tied to Heavily-Weighted Evaluation Categories

#### DSM Accuracy (50% of grade)

| # | Gap | PRD ref | Impact on score |
|---|-----|---------|----------------|
| B1 | **No float32 GeoTIFF DSM export** | FR16, O8, MVP Must-have | PS mandatory deliverable. Judges expect a standard geospatial output; we can only show a PNG |
| B2 | **Fine-tuning incomplete** — ~200 tiles, head-only, no degradation augmentation | FR14 | Current Pearson r=0.393 is still weak. Full ~3000-tile training with degradation aug is the primary path to meaningful accuracy gains |
| B3 | **No leave-DC-out / unseen-city evaluation** | FR15, §17.2 | v2.0 says "lead with the unseen-city number". Without it, accuracy claims are undefended |
| B4 | **No per-class / per-height-bucket breakdown** | FR15 | Judges expect structured, granular evaluation — not just one aggregate number |
| B5 | **Composite calibration not implemented** — still linear/poly fit to SRTM | FR17 | Current calibration fails on high-relief; composite (terrain + nDSM) is the v2.0 target |
| B6 | **Vegetation mask bug unfixed** — 93.5% of Delhi flagged as low-confidence | FR22 | Actively misleading; looks broken to any evaluator |
| B7 | **accuracy-log.md lacks mandatory caveats** | NFR11, §17.3 | Must state: US-only aerial data, in-city optimistic, no hilly LiDAR, lead with unseen-city number |

#### Rendering & UX (50% of grade)

| # | Gap | PRD ref | Impact on score |
|---|-----|---------|----------------|
| B8 | **No confidence overlay in Unity** | FR23 | Planned visualization feature; confidence.png is computed but never reaches the client |
| B9 | **No units legend beside mode toggle** | §12 | Judges can't tell what "relative" vs "absolute" means or what units are shown |
| B10 | **No slope/height probe** — no in-scene analysis | §11 | PS requires structure height analysis; no tool for judges to inspect specific features |
| B11 | **FPS not measured or documented** | §17.4 | NFR5 requires smooth rendering; no evidence it meets the 30fps target |

### Tier C — Should-Have / Polish / Deferred

| # | Gap | PRD ref | Notes |
|---|-----|---------|-------|
| C1 | Full-resolution tiling | FR19 | Large images still downscaled; seam-free tiling would improve quality |
| C2 | Slope raster export via API | FR20 | Useful geospatial output but not a blocker |
| C3 | `/validate` endpoint | FR21 | Reference DSM comparison via API; exists in `metrics.py` but no HTTP endpoint |
| C4 | GCP correction | FR18 | Optional least-squares affine correction |
| C5 | Timeout + retry-downscaled UI | §12 | Processing screen has no timeout handler |
| C6 | `confidence_b64` in API response | §15 | Confidence mask computed but not sent to Unity |
| C7 | Terrain type in stats overlay | §11 | Not implemented |
| C8 | LOD tuning for large heightmaps | §11 | Default Unity terrain LOD; not measured |
| C9 | ZoeDepth/MiDaS comparison recorded | §9.2, §24 | Decision to skip not formally documented |
| C10 | Product explanation on landing screen | §12 | One-line description not confirmed |

---

## Summary Statistics

| Category | Done | Partially Done | Not Started | Total |
|----------|------|----------------|-------------|-------|
| Objectives (O1–O8) | 4 | 3 | 1 | 8 |
| FRs v1.0 (FR1–FR13) | 9 | 4 | 0 | 13 |
| FRs v2.0 (FR14–FR24) | 0 | 2 | 9 | 11 |
| NFRs (NFR1–NFR11) | 5 | 4 | 0 | 11* |
| MVP Must-haves | 5 | 3 | 1 | 9 |
| MVP Should-haves | 3 | 1 | 2 | 6 |

\* Two NFRs marked "Verify" counted as Partially Done.

> [!CAUTION]
> **Two new Must-have items from v2.0 are Not Started or incomplete:**
> - FR16 (float32 GeoTIFF DSM export) — **Not Started**
> - FR14 (GAMUS fine-tuning) — **Partially Done** (script exists but not integrated into pipeline)
>
> These are the highest-impact gaps because they directly affect the 50%-weighted DSM accuracy score and are explicitly classified as Must-have in the v2.0 PRD.

> [!IMPORTANT]
> **The biggest demo risk is Tier A**: no standalone build, no on-screen controls, no load-new flow, and no offline fallback. Even if the ML pipeline is perfect, judges cannot effectively experience it without these UX items.

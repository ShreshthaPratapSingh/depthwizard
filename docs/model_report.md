# DepthWizard ML Pipeline — Model Report

*A factual summary of the depth model, pipeline, and what our accuracy
evaluation actually shows. Written to be read in under 3 minutes.*

## 1. Model

**Depth-Anything-V2-Small**, run in fp16 on CUDA (float32 fallback on
MPS/CPU), weights loaded locally — the pipeline never hits the network at
inference time.

**Why "small" over "base"?** Two reasons:

- **Latency.** Measured forward-pass time is ~13–300 ms depending on
  hardware and input size — orders of magnitude under our 60 s
  end-to-end budget. There was no latency pressure forcing a larger model,
  and no headroom argument for one either.
- **A bigger model would not have fixed the real problem.** Our accuracy
  findings (§3) show the bottleneck is *signal*, not model capacity:
  zero-shot monocular depth on nadir imagery is weakly correlated with
  true elevation regardless of checkpoint size. Spending latency/VRAM on
  "-base" would not have bought meaningfully better elevation.

## 2. Pipeline

Single RGB image in → metadata + Unity-ready artifacts out:

1. **Depth inference** — `infer_depth` returns a normalized `[0,1]`
   elevation map (higher = higher elevation; the disparity→elevation
   convention is documented and fixed in `inference.py`).
2. **Postprocessing** (`postprocess.py`):
   - **Edge-preserving smoothing** — guided filter (falls back to
     bilateral if `opencv-contrib`/`ximgproc` is absent). Never a plain
     Gaussian blur, which would wash out ridgelines.
   - **Water flattening** — regions that are both blue-ish *and* locally
     flat are flattened to a low baseline instead of keeping noisy
     monocular depth.
   - **Vegetation confidence** — dense canopy is detected via ExG
     (`2G − R − B`, per-image percentile-normalized) and flagged in the
     confidence mask (elevation is *not* altered there).
3. **Export** (`export.py`) — 16-bit heightmap, RGB texture, and
   confidence mask. The heightmap is vertically flipped to match Unity's
   south-origin `TerrainData.SetHeights()` convention; the texture is not.
4. **Optional geospatial calibration** — a valid GeoTIFF with a covering
   SRTM tile is calibrated to metric elevation; otherwise the pipeline
   returns relative elevation with a warning. This step never raises.

**Hardened error handling** (`run.py`): `process_image` never raises to
the caller — on any failure it returns a valid metadata dict with
`status="error"` and a clean message (full traceback logged, not leaked).
Oversized inputs are auto-downscaled to fit a pixel cap (with a warning,
not an error), and `warmup()` preloads the model singleton at startup so
the first real request doesn't pay the cold-load penalty.

## 3. Accuracy findings (the honest part)

Evaluated with `scripts/run_eval.py` against **real SRTM GL1 (30 m)**
ground truth. Results from `outputs/eval_results.csv`:

| pair        | terrain | pearson_r | rmse_m | mae_m | slope_rmse | n_valid_px |
|-------------|---------|-----------|--------|-------|------------|------------|
| delhi_urban | urban   | **0.377** | 9.87   | 7.83  | 1.05       | 1,050,625  |

*(exact values: pearson_r = 0.3765, rmse_m = 9.8738, mae_m = 7.8292,
slope_rmse = 1.0468)*

**Plainly: pearson_r ≈ 0.38 is weak-to-marginal correlation with true
elevation.** The model picks up *some* real signal — plausibly
building/shadow height cues in dense urban terrain — but this is **not a
reliable elevation reconstruction**.

This matches the known limitation of monocular depth models: they are
trained on ground-level, perspective photography and lean on
vanishing-line and occlusion cues. Near-nadir satellite/aerial imagery
lacks exactly those cues, so the learned priors transfer poorly.

## 4. Why RMSE alone would have misled us

RMSE and MAE are computed **after a least-squares scale+shift fit**
(`pred_aligned = a·pred + b`), because our output isn't in metric units.
That alignment can make a weak-signal prediction look deceptively
reasonable — a smooth gradient with the right global bias scores an
okay-looking RMSE while carrying little real local structure.

`pearson_r` (scale/shift invariant) and `slope_rmse` (local gradient
shape, bias-independent) are **not fooled by the fit**. When RMSE looks
fine but `pearson_r` sits at ~0.38, trust `pearson_r`. This is why our
eval reports all four metrics side by side rather than headlining RMSE.

## 5. Confidence overlay as the mitigation

Rather than presenting the heightmap as ground truth, DepthWizard **flags
where its output should not be trusted**: vegetation (via ExG) and water
(via color + local variance) are marked low-confidence in the exported
mask, so users see the uncertain regions directly.

**Open item, flagged honestly:** on the `delhi_urban` tile, **93.5% of
pixels were classified low-confidence.** That is suspiciously high and
likely indicates the thresholds are **over-triggering on dense urban
texture** rather than genuinely flagging unreliable terrain. We're
surfacing this rather than hiding it — it's worth investigating and
retuning.

## 6. Known limitations

- **Weak zero-shot correlation on nadir imagery** — measured r ≈ 0.38 on
  urban terrain; the model is not reconstructing true elevation reliably.
- **Only one terrain type evaluated against real ground truth** — urban
  (delhi), due to time constraints. A forested-terrain *visual* check
  showed tree-texture noise rather than terrain relief, consistent with
  the same underlying limitation.
- **Confidence thresholds are not tuned per terrain type** — see the
  93.5% urban low-confidence rate above.
- **Tiling for large images was descoped** for the hackathon timeline
  (oversized inputs are downscaled instead of tiled).

## 7. What would improve this with more time

- **Supervised fine-tuning on paired satellite/LiDAR data** (e.g.
  DFC2019) to teach the model the nadir-imagery elevation cues it never
  saw in training — the highest-leverage fix.
- **Scope restriction** to terrain types with stronger natural depth cues,
  where zero-shot monocular depth actually correlates with elevation.
- **Per-terrain confidence tuning**, starting with the urban
  over-triggering issue.

---

*Reproduce: `python scripts/run_eval.py <pairs_dir> outputs/eval_results.csv`.
Metrics implemented in `depth_pipeline/metrics.py`; see its module
docstring on why RMSE alone is insufficient.*

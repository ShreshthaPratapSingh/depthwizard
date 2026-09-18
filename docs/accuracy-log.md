# DepthWizard Accuracy Log

## Caveats (NFR11 / §17.3 — read before quoting any number)

1. **Training data scope.** GAMUS (earthflow/GAMUS) contains US-only aerial
   imagery from two cities: Washington DC and Philadelphia. All fine-tuning and
   the majority of validation uses tiles from these two cities. Results may not
   generalize to other geographies, terrain types, or sensor configurations.

2. **In-city results are optimistic.** When train and validation tiles share the
   same city (same flight, same sensor, similar land use), the model sees
   near-identical distribution at evaluation time. The meaningful generalization
   number is the **unseen-city (leave-one-city-out)** result — lead with that
   when available, not the in-distribution validation RMSE.

3. **No hilly-terrain LiDAR reference.** GAMUS covers flat-to-moderate urban and
   suburban terrain. There is no paired LiDAR ground truth for high-relief areas
   (Mussoorie, Arunachal). SRTM-based RMSE numbers for hilly regions reflect
   SRTM alignment quality, not true DSM accuracy.

4. **Headline accuracy should lead with the unseen-city result** once available
   from the leave-one-city-out evaluation (Task 5 / FR15). Until then, do not
   quote the in-distribution 18-tile number as a generalization claim.

---

## GAMUS Benchmark -- 2026-09-16 23:05 UTC

Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) (validation split, 18 tiles)
Model: `depth-anything-v2-small` (pretrained, no fine-tuning)
Metrics computed after least-squares scale+shift alignment (identical to `depth_pipeline.metrics.evaluate`).

### Per-Terrain-Type Results

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 8 | 5.41 | 4.28 | 0.123 | 0.994 |
| Mixed | 1 | 2.55 | 1.69 | 0.408 | 0.703 |
| Urban | 9 | 5.18 | 4.16 | 0.326 | 0.919 |

**Overall** (18 tiles): RMSE=5.14 m, MAE=4.08 m, Pearson r=0.240, Slope RMSE=0.941

<details>
<summary>Per-tile details</summary>

| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |
|---|---|---|---|---|---|---|---|---|
| DC_02_26 | forested | 7.8 | 41.5 | 9.38 | 7.54 | -0.285 | 1.148 | 4823 |
| DC_04_23 | forested | 22.2 | 50.0 | 10.68 | 8.79 | -0.269 | 1.807 | 248 |
| DC_08_31 | urban | 5.9 | 28.4 | 6.23 | 5.13 | 0.112 | 1.056 | 248 |
| DC_11_16 | urban | 7.6 | 29.4 | 6.92 | 5.66 | 0.069 | 1.152 | 250 |
| DC_12_17 | forested | 7.6 | 38.0 | 7.71 | 6.36 | 0.141 | 1.160 | 252 |
| PHL_6150 | forested | 1.3 | 146.9 | 1.85 | 1.36 | 0.393 | 0.597 | 236 |
| PHL_6151 | urban | 1.7 | 35.2 | 1.87 | 1.35 | 0.640 | 0.560 | 244 |
| PHL_6200 | urban | 1.8 | 23.9 | 2.05 | 1.38 | 0.729 | 0.762 | 240 |
| PHL_6300 | forested | 1.2 | 42.9 | 2.12 | 1.61 | 0.188 | 0.622 | 239 |
| PHL_6400 | mixed | 1.0 | 27.6 | 2.55 | 1.69 | 0.408 | 0.703 | 241 |
| PHL_6500 | forested | 1.8 | 28.8 | 2.45 | 1.65 | 0.500 | 0.913 | 239 |
| PHL_6600 | forested | 1.2 | 27.6 | 2.42 | 1.56 | 0.366 | 0.695 | 238 |
| DC_04_27 | urban | 5.2 | 38.0 | 6.24 | 4.88 | 0.117 | 0.965 | 243 |
| DC_09_33 | urban | 4.1 | 23.9 | 5.07 | 4.15 | 0.344 | 0.773 | 238 |
| DC_10_30 | urban | 6.1 | 45.0 | 5.94 | 4.76 | 0.493 | 1.049 | 242 |
| DC_11_33 | forested | 4.4 | 26.8 | 6.69 | 5.39 | -0.053 | 1.009 | 246 |
| DC_11_34 | urban | 5.3 | 25.2 | 5.86 | 4.97 | 0.251 | 0.916 | 232 |
| DC_12_27 | urban | 6.0 | 36.3 | 6.41 | 5.20 | 0.178 | 1.040 | 239 |

</details>

## GAMUS Benchmark (Fine-tuned) -- 2026-09-17 00:39 UTC

Model: `depth-anything-v2-small-gamus` (neck+head fine-tuned on ~200 GAMUS train tiles)
Baseline: `depth-anything-v2-small` (pretrained, no fine-tuning)

### Per-Terrain-Type Results (Fine-tuned)

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 8 | 5.28 | 4.10 | 0.335 | 0.983 |
| Mixed | 1 | 1.98 | 1.26 | 0.705 | 0.685 |
| Urban | 9 | 5.03 | 4.00 | 0.410 | 0.905 |

**Overall** (18 tiles): RMSE=4.97 m, MAE=3.89 m, Pearson r=0.393, Slope RMSE=0.927

### Before vs After Fine-Tuning

| Metric | Pretrained | Fine-tuned | Delta |
|---|---|---|---|
| RMSE (m) | 5.140 | 4.970 | -0.170 v |
| MAE (m) | 4.080 | 3.890 | -0.190 v |
| Pearson r | 0.240 | 0.393 | +0.153 v |
| Slope RMSE | 0.941 | 0.927 | -0.014 v |

## Vegetation Confidence Fix (B6) -- 2026-09-18

**Bug:** Per-image ExG percentile normalization + fixed 0.20 normalized threshold caused
near-universal vegetation flagging. 100% of Delhi urban and Chennai coastal pixels were
flagged as low-confidence vegetation — rendering the confidence mask useless.

**Root cause:** Percentile normalization (5th–95th) maps the ExG distribution of EVERY
image to [0, 1], so even a completely non-vegetated image will have ~15%+ of pixels
above the 0.20 normalized mark. The 15×15 morphological closing then floods the
remaining area, inflating to near-100%.

**Fix:** Switched to an absolute ExG threshold (ExG > 30 on uint8 RGB scale, range
[-510, +510]). Reduced morphological closing kernel from 15×15 to 7×7.

### Before vs After (Vegetation flagged %)

| Sample | Terrain | Before (broken) | After (fixed) | Assessment |
|---|---|---|---|---|
| delhi_urban.tif | flat urban | **100.0%** | **42.6%** | Realistic — Delhi has significant urban tree canopy |
| mussoorie_hilly.tif | high-relief forested | **99.8%** | **87.9%** | Expected — heavily forested mountain |
| chennai_coastal.tif | low-relief coastal | **100.0%** | **48.7%** | Realistic — significant coastal vegetation |

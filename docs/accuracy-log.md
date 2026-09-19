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

## GAMUS Benchmark -- 2026-09-18 15:58 UTC (leave-out: DC)

Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) (validation split, 23 tiles)
Model: `depth-anything-v2-small` (pretrained, no fine-tuning)
Metrics computed after least-squares scale+shift alignment (identical to `depth_pipeline.metrics.evaluate`).

### Per-Terrain-Type Results

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 6 | 9.30 | 7.69 | 0.077 | 1.359 |
| Mixed | 1 | 10.76 | 8.57 | 0.177 | 0.924 |
| Urban | 16 | 6.09 | 4.92 | 0.274 | 1.011 |

**Overall** (23 tiles): RMSE=7.13 m, MAE=5.80 m, Pearson r=0.219, Slope RMSE=1.098

<details>
<summary>Per-tile details</summary>

| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |
|---|---|---|---|---|---|---|---|---|
| DC_02_26 | forested | 7.8 | 41.5 | 9.78 | 7.83 | -0.030 | 1.159 | 5024 |
| DC_04_23 | forested | 22.2 | 50.0 | 10.79 | 8.94 | -0.228 | 1.802 | 260 |
| DC_08_31 | urban | 5.9 | 28.4 | 6.12 | 5.03 | 0.216 | 1.040 | 262 |
| DC_11_16 | urban | 7.6 | 29.4 | 6.89 | 5.63 | 0.120 | 1.143 | 253 |
| DC_12_17 | forested | 7.6 | 38.0 | 7.60 | 6.26 | 0.220 | 1.146 | 248 |
| DC_04_27 | urban | 5.2 | 38.0 | 6.03 | 4.67 | 0.281 | 0.939 | 244 |
| DC_09_33 | urban | 4.1 | 23.9 | 4.79 | 3.84 | 0.460 | 0.764 | 253 |
| DC_10_30 | urban | 6.1 | 45.0 | 6.05 | 4.82 | 0.463 | 1.041 | 254 |
| DC_11_33 | forested | 4.4 | 26.8 | 6.53 | 5.15 | 0.225 | 0.999 | 248 |
| DC_11_34 | urban | 5.3 | 25.2 | 5.69 | 4.81 | 0.338 | 0.899 | 238 |
| DC_12_27 | urban | 6.0 | 36.3 | 6.31 | 5.03 | 0.250 | 1.026 | 246 |
| DC_13_14 | urban | 5.2 | 29.2 | 5.38 | 4.16 | 0.327 | 0.876 | 232 |
| DC_13_15 | urban | 8.3 | 34.4 | 6.77 | 5.43 | -0.011 | 1.175 | 247 |
| DC_13_17 | urban | 9.5 | 39.0 | 8.46 | 6.99 | 0.070 | 1.354 | 234 |
| DC_13_28 | urban | 5.2 | 27.5 | 5.15 | 3.92 | 0.325 | 0.912 | 248 |
| DC_13_34 | urban | 5.1 | 24.7 | 5.31 | 4.38 | 0.232 | 0.918 | 258 |
| DC_14_21 | mixed | 7.1 | 41.1 | 10.76 | 8.57 | 0.177 | 0.924 | 244 |
| DC_14_24 | forested | 20.5 | 50.1 | 13.71 | 11.78 | 0.299 | 1.818 | 236 |
| DC_14_32 | urban | 4.8 | 24.6 | 4.73 | 3.71 | 0.284 | 0.914 | 234 |
| DC_14_35 | urban | 4.6 | 24.2 | 4.67 | 3.56 | 0.442 | 0.763 | 232 |
| DC_15_13 | forested | 10.0 | 32.8 | 7.40 | 6.21 | -0.021 | 1.229 | 280 |
| DC_15_15 | urban | 9.6 | 35.8 | 7.79 | 6.64 | 0.471 | 1.264 | 269 |
| DC_15_17 | urban | 8.6 | 30.8 | 7.24 | 6.06 | 0.127 | 1.156 | 263 |

</details>

### Per-Class Breakdown

| Class | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| Building | 23 | 4.87 | 3.70 | 0.284 |
| Ground | 23 | 0.60 | 0.15 | 0.037 |
| Low_vegetation | 23 | 0.98 | 0.78 | 0.036 |
| Other | 18 | 1.92 | 1.34 | 0.051 |
| Road | 23 | 6.07 | 4.84 | 0.030 |
| Tree | 23 | 6.21 | 5.15 | 0.124 |
| Water | 2 | 5.58 | 2.99 | 0.266 |

### Per-Height-Bucket Breakdown

| Height Bucket | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| 0-2m (ground) | 23 | 0.47 | 0.34 | 0.195 |
| 2-10m (low) | 23 | 2.15 | 1.81 | 0.200 |
| 10-30m (mid) | 23 | 4.16 | 3.44 | -0.090 |
| 30m+ (tall) | 13 | 1.84 | 1.50 | -0.056 |

## GAMUS Benchmark -- 2026-09-19 02:32 UTC (leave-out: DC)

Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) (validation split, 23 tiles)
Model: `depth-anything-v2-small` (pretrained, no fine-tuning)
Metrics computed after least-squares scale+shift alignment (identical to `depth_pipeline.metrics.evaluate`).

### Per-Terrain-Type Results

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 6 | 9.30 | 7.70 | 0.077 | 1.359 |
| Mixed | 1 | 10.76 | 8.57 | 0.178 | 0.924 |
| Urban | 16 | 6.09 | 4.92 | 0.274 | 1.011 |

**Overall** (23 tiles): RMSE=7.13 m, MAE=5.80 m, Pearson r=0.219, Slope RMSE=1.098

<details>
<summary>Per-tile details</summary>

| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |
|---|---|---|---|---|---|---|---|---|
| DC_02_26 | forested | 7.8 | 41.5 | 9.78 | 7.83 | -0.030 | 1.159 | 17266 |
| DC_04_23 | forested | 22.2 | 50.0 | 10.79 | 8.94 | -0.228 | 1.802 | 16 |
| DC_08_31 | urban | 5.9 | 28.4 | 6.12 | 5.04 | 0.215 | 1.040 | 23 |
| DC_11_16 | urban | 7.6 | 29.4 | 6.89 | 5.63 | 0.120 | 1.143 | 21 |
| DC_12_17 | forested | 7.6 | 38.0 | 7.60 | 6.26 | 0.219 | 1.146 | 22 |
| DC_04_27 | urban | 5.2 | 38.0 | 6.03 | 4.67 | 0.281 | 0.939 | 21 |
| DC_09_33 | urban | 4.1 | 23.9 | 4.79 | 3.84 | 0.459 | 0.764 | 22 |
| DC_10_30 | urban | 6.1 | 45.0 | 6.05 | 4.82 | 0.463 | 1.041 | 23 |
| DC_11_33 | forested | 4.4 | 26.8 | 6.53 | 5.15 | 0.225 | 0.999 | 20 |
| DC_11_34 | urban | 5.3 | 25.2 | 5.69 | 4.81 | 0.338 | 0.899 | 39 |
| DC_12_27 | urban | 6.0 | 36.3 | 6.31 | 5.03 | 0.250 | 1.026 | 39 |
| DC_13_14 | urban | 5.2 | 29.2 | 5.38 | 4.16 | 0.326 | 0.876 | 39 |
| DC_13_15 | urban | 8.3 | 34.4 | 6.77 | 5.43 | -0.011 | 1.175 | 39 |
| DC_13_17 | urban | 9.5 | 39.0 | 8.46 | 6.99 | 0.070 | 1.354 | 38 |
| DC_13_28 | urban | 5.2 | 27.5 | 5.15 | 3.92 | 0.325 | 0.912 | 40 |
| DC_13_34 | urban | 5.1 | 24.7 | 5.31 | 4.39 | 0.231 | 0.918 | 38 |
| DC_14_21 | mixed | 7.1 | 41.1 | 10.76 | 8.57 | 0.178 | 0.924 | 39 |
| DC_14_24 | forested | 20.5 | 50.1 | 13.71 | 11.78 | 0.298 | 1.818 | 39 |
| DC_14_32 | urban | 4.8 | 24.6 | 4.73 | 3.71 | 0.284 | 0.914 | 40 |
| DC_14_35 | urban | 4.6 | 24.2 | 4.67 | 3.56 | 0.442 | 0.763 | 39 |
| DC_15_13 | forested | 10.0 | 32.8 | 7.40 | 6.21 | -0.021 | 1.229 | 42 |
| DC_15_15 | urban | 9.6 | 35.8 | 7.79 | 6.64 | 0.471 | 1.264 | 39 |
| DC_15_17 | urban | 8.6 | 30.8 | 7.24 | 6.06 | 0.126 | 1.156 | 38 |

</details>

### Per-Class Breakdown

| Class | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| Building | 23 | 4.87 | 3.70 | 0.284 |
| Ground | 23 | 0.60 | 0.15 | 0.037 |
| Low_vegetation | 23 | 0.98 | 0.78 | 0.036 |
| Other | 18 | 1.92 | 1.34 | 0.051 |
| Road | 23 | 6.07 | 4.84 | 0.030 |
| Tree | 23 | 6.21 | 5.16 | 0.124 |
| Water | 2 | 5.58 | 2.99 | 0.266 |

### Per-Height-Bucket Breakdown

| Height Bucket | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| 0-2m (ground) | 23 | 0.47 | 0.34 | 0.194 |
| 2-10m (low) | 23 | 2.15 | 1.81 | 0.200 |
| 10-30m (mid) | 23 | 4.16 | 3.44 | -0.090 |
| 30m+ (tall) | 13 | 1.84 | 1.50 | -0.056 |

## GAMUS Benchmark -- 2026-09-19 02:37 UTC (leave-out: DC)

Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) (validation split, 23 tiles)
Model: `depth-anything-v2-small` (pretrained, no fine-tuning)
Metrics computed after least-squares scale+shift alignment (identical to `depth_pipeline.metrics.evaluate`).

### Per-Terrain-Type Results

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 6 | 8.08 | 6.53 | 0.444 | 1.306 |
| Mixed | 1 | 10.85 | 8.61 | 0.118 | 0.924 |
| Urban | 16 | 5.82 | 4.70 | 0.405 | 0.987 |

**Overall** (23 tiles): RMSE=6.63 m, MAE=5.34 m, Pearson r=0.403, Slope RMSE=1.068

<details>
<summary>Per-tile details</summary>

| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |
|---|---|---|---|---|---|---|---|---|
| DC_02_26 | forested | 7.8 | 41.5 | 9.51 | 7.64 | 0.234 | 1.124 | 4895 |
| DC_04_23 | forested | 22.2 | 50.0 | 8.62 | 7.11 | 0.629 | 1.716 | 17 |
| DC_08_31 | urban | 5.9 | 28.4 | 5.70 | 4.66 | 0.417 | 1.008 | 17 |
| DC_11_16 | urban | 7.6 | 29.4 | 6.63 | 5.47 | 0.293 | 1.107 | 17 |
| DC_12_17 | forested | 7.6 | 38.0 | 7.21 | 5.84 | 0.379 | 1.109 | 17 |
| DC_04_27 | urban | 5.2 | 38.0 | 5.74 | 4.42 | 0.406 | 0.913 | 22 |
| DC_09_33 | urban | 4.1 | 23.9 | 4.95 | 4.04 | 0.398 | 0.764 | 22 |
| DC_10_30 | urban | 6.1 | 45.0 | 5.73 | 4.55 | 0.542 | 1.032 | 22 |
| DC_11_33 | forested | 4.4 | 26.8 | 5.99 | 4.54 | 0.449 | 0.979 | 28 |
| DC_11_34 | urban | 5.3 | 25.2 | 5.60 | 4.69 | 0.379 | 0.888 | 37 |
| DC_12_27 | urban | 6.0 | 36.3 | 5.86 | 4.64 | 0.438 | 1.000 | 41 |
| DC_13_14 | urban | 5.2 | 29.2 | 5.19 | 4.03 | 0.408 | 0.862 | 68 |
| DC_13_15 | urban | 8.3 | 34.4 | 6.43 | 5.20 | 0.313 | 1.111 | 60 |
| DC_13_17 | urban | 9.5 | 39.0 | 8.24 | 6.84 | 0.238 | 1.314 | 63 |
| DC_13_28 | urban | 5.2 | 27.5 | 4.87 | 3.70 | 0.448 | 0.894 | 72 |
| DC_13_34 | urban | 5.1 | 24.7 | 4.99 | 4.09 | 0.405 | 0.889 | 39 |
| DC_14_21 | mixed | 7.1 | 41.1 | 10.85 | 8.61 | 0.118 | 0.924 | 57 |
| DC_14_24 | forested | 20.5 | 50.1 | 10.01 | 7.91 | 0.717 | 1.732 | 51 |
| DC_14_32 | urban | 4.8 | 24.6 | 4.50 | 3.53 | 0.408 | 0.899 | 60 |
| DC_14_35 | urban | 4.6 | 24.2 | 4.53 | 3.38 | 0.493 | 0.758 | 45 |
| DC_15_13 | forested | 10.0 | 32.8 | 7.15 | 6.13 | 0.258 | 1.174 | 40 |
| DC_15_15 | urban | 9.6 | 35.8 | 7.31 | 6.13 | 0.559 | 1.242 | 62 |
| DC_15_17 | urban | 8.6 | 30.8 | 6.88 | 5.77 | 0.335 | 1.113 | 66 |

</details>

### Per-Class Breakdown

| Class | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| Building | 23 | 4.40 | 3.27 | 0.407 |
| Ground | 23 | 0.59 | 0.15 | 0.072 |
| Low_vegetation | 23 | 1.00 | 0.80 | 0.059 |
| Other | 18 | 1.92 | 1.34 | 0.005 |
| Road | 23 | 5.69 | 4.34 | 0.329 |
| Tree | 23 | 5.93 | 4.89 | 0.344 |
| Water | 2 | 5.71 | 3.19 | 0.145 |

### Per-Height-Bucket Breakdown

| Height Bucket | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| 0-2m (ground) | 23 | 0.48 | 0.35 | 0.145 |
| 2-10m (low) | 23 | 2.11 | 1.78 | 0.259 |
| 10-30m (mid) | 23 | 4.14 | 3.42 | 0.039 |
| 30m+ (tall) | 13 | 1.87 | 1.53 | 0.026 |

## GAMUS Benchmark -- 2026-09-19 02:38 UTC (leave-out: DC)

Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) (validation split, 23 tiles)
Model: `depth-anything-v2-small` (pretrained, no fine-tuning)
Metrics computed after least-squares scale+shift alignment (identical to `depth_pipeline.metrics.evaluate`).

### Per-Terrain-Type Results

| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |
|---|---|---|---|---|---|
| Forested | 6 | 9.35 | 7.81 | -0.065 | 1.367 |
| Mixed | 1 | 10.89 | 8.63 | 0.084 | 0.927 |
| Urban | 16 | 6.17 | 5.02 | 0.204 | 1.025 |

**Overall** (23 tiles): RMSE=7.21 m, MAE=5.91 m, Pearson r=0.129, Slope RMSE=1.110

<details>
<summary>Per-tile details</summary>

| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |
|---|---|---|---|---|---|---|---|---|
| DC_02_26 | forested | 7.8 | 41.5 | 9.38 | 7.54 | -0.285 | 1.148 | 4848 |
| DC_04_23 | forested | 22.2 | 50.0 | 10.67 | 8.78 | -0.272 | 1.807 | 15 |
| DC_08_31 | urban | 5.9 | 28.4 | 6.23 | 5.13 | 0.112 | 1.056 | 16 |
| DC_11_16 | urban | 7.6 | 29.4 | 6.92 | 5.65 | 0.069 | 1.152 | 17 |
| DC_12_17 | forested | 7.6 | 38.0 | 7.71 | 6.36 | 0.141 | 1.160 | 16 |
| DC_04_27 | urban | 5.2 | 38.0 | 6.24 | 4.88 | 0.117 | 0.965 | 24 |
| DC_09_33 | urban | 4.1 | 23.9 | 5.07 | 4.15 | 0.343 | 0.773 | 24 |
| DC_10_30 | urban | 6.1 | 45.0 | 5.94 | 4.76 | 0.493 | 1.049 | 28 |
| DC_11_33 | forested | 4.4 | 26.8 | 6.69 | 5.39 | -0.054 | 1.009 | 27 |
| DC_11_34 | urban | 5.3 | 25.2 | 5.86 | 4.97 | 0.251 | 0.916 | 42 |
| DC_12_27 | urban | 6.0 | 36.3 | 6.41 | 5.20 | 0.178 | 1.040 | 42 |
| DC_13_14 | urban | 5.2 | 29.2 | 5.50 | 4.32 | 0.255 | 0.887 | 37 |
| DC_13_15 | urban | 8.3 | 34.4 | 6.77 | 5.41 | 0.039 | 1.172 | 42 |
| DC_13_17 | urban | 9.5 | 39.0 | 8.48 | 6.96 | 0.009 | 1.364 | 38 |
| DC_13_28 | urban | 5.2 | 27.5 | 5.29 | 4.14 | 0.238 | 0.926 | 35 |
| DC_13_34 | urban | 5.1 | 24.7 | 5.43 | 4.57 | 0.083 | 0.933 | 42 |
| DC_14_21 | mixed | 7.1 | 41.1 | 10.89 | 8.63 | 0.084 | 0.927 | 42 |
| DC_14_24 | forested | 20.5 | 50.1 | 14.28 | 12.55 | 0.109 | 1.846 | 38 |
| DC_14_32 | urban | 4.8 | 24.6 | 4.83 | 3.95 | 0.199 | 0.928 | 65 |
| DC_14_35 | urban | 4.6 | 24.2 | 4.77 | 3.70 | 0.401 | 0.775 | 43 |
| DC_15_13 | forested | 10.0 | 32.8 | 7.40 | 6.22 | -0.032 | 1.230 | 63 |
| DC_15_15 | urban | 9.6 | 35.8 | 7.69 | 6.49 | 0.490 | 1.288 | 67 |
| DC_15_17 | urban | 8.6 | 30.8 | 7.30 | 6.07 | -0.010 | 1.174 | 60 |

</details>

### Per-Class Breakdown

| Class | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| Building | 23 | 4.58 | 3.43 | 0.177 |
| Ground | 23 | 0.59 | 0.15 | 0.019 |
| Low_vegetation | 23 | 1.01 | 0.83 | 0.008 |
| Other | 18 | 2.00 | 1.39 | 0.056 |
| Road | 23 | 6.09 | 4.85 | -0.032 |
| Tree | 23 | 6.29 | 5.25 | 0.019 |
| Water | 2 | 5.49 | 2.91 | 0.328 |

### Per-Height-Bucket Breakdown

| Height Bucket | Tiles | RMSE (m) | MAE (m) | Pearson r |
|---|---|---|---|---|
| 0-2m (ground) | 23 | 0.48 | 0.35 | 0.109 |
| 2-10m (low) | 23 | 2.17 | 1.84 | 0.166 |
| 10-30m (mid) | 23 | 4.12 | 3.42 | -0.103 |
| 30m+ (tall) | 13 | 1.85 | 1.50 | 0.006 |


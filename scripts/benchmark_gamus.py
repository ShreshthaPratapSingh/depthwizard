#!/usr/bin/env python
"""GAMUS validation benchmark -- run the existing depth pipeline against
the recommended GAMUS dataset and report per-terrain-type accuracy.

This script:
 1. Downloads a small representative sample (~18 tiles) from the GAMUS
    validation split on HuggingFace.
 2. Runs the EXISTING, UNMODIFIED ``depth_pipeline.inference.infer_depth``
    against each tile's RGB image.
 3. Evaluates predictions against GAMUS's AGL (Above Ground Level) ground
    truth using ``depth_pipeline.metrics.evaluate``.
 4. Classifies each tile by dominant terrain type using the semantic class
    labels (0=Other, 1=Ground, 2=LowVeg, 3=Building, 4=Water, 5=Road, 6=Tree).
 5. Appends per-terrain and aggregate results to ``docs/accuracy-log.md``.

Usage::

    python scripts/benchmark_gamus.py            # default 18-tile sample
    python scripts/benchmark_gamus.py --n-tiles 6 # quick 6-tile run

Requirements (beyond the project's requirements.txt):
    pip install h5py
"""
from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import time
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

import h5py
import numpy as np

# ---------------------------------------------------------------------------
# Project imports -- these are the EXISTING, UNMODIFIED pipeline modules.
# ---------------------------------------------------------------------------
_REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_REPO_ROOT))

from depth_pipeline.inference import infer_depth          # noqa: E402
from depth_pipeline.metrics import evaluate               # noqa: E402
from depth_pipeline.config import MODEL_ID                # noqa: E402

logger = logging.getLogger("benchmark_gamus")

# ---------------------------------------------------------------------------
# GAMUS constants
# ---------------------------------------------------------------------------
GAMUS_REPO = "earthflow/GAMUS"
GAMUS_SPLIT = "val"
CACHE_DIR = _REPO_ROOT / "backend" / "data" / "gamus_cache"

# Semantic class labels defined by the DFC2019 / GAMUS spec.
CLASS_NAMES = {
    0: "other",
    1: "ground",
    2: "low_vegetation",
    3: "building",
    4: "water",
    5: "road",
    6: "tree",
}

# Terrain type assignment based on dominant land-cover class.
TERRAIN_MAP = {
    "building": "urban",
    "road": "urban",
    "ground": "mixed",        # bare ground: suburban/rural, not purely urban
    "tree": "forested",
    "low_vegetation": "forested",
    "water": "mixed",
    "other": "mixed",
}

# Curated sample IDs covering varied terrain (selected from val split).
# DC_ = Washington DC metro area (varied: trees, buildings, roads)
# PHL_ = Philadelphia (dense urban + parks)
DEFAULT_SAMPLE_IDS = [
    # DC -- mixed/tree-heavy tiles
    "DC_02_26", "DC_04_23", "DC_08_31", "DC_11_16", "DC_12_17",
    "DC_15_28", "DC_20_37", "DC_25_42", "DC_30_45", "DC_35_56",
    # PHL -- urban-heavy tiles
    "PHL_6150", "PHL_6151", "PHL_6200", "PHL_6300", "PHL_6400",
    "PHL_6500", "PHL_6600", "PHL_6700",
]

ACCURACY_LOG_MD = _REPO_ROOT / "docs" / "accuracy-log.md"


# ---------------------------------------------------------------------------
# Data loading
# ---------------------------------------------------------------------------

def _download_tile(sample_id: str) -> tuple[Path, Path, Path]:
    """Download the RGB / AGL / CLS triplet for one GAMUS tile."""
    from huggingface_hub import hf_hub_download

    paths = []
    for subdir, suffix in [("images", "RGB"), ("heights", "AGL"), ("classes", "CLS")]:
        fname = f"{subdir}/{GAMUS_SPLIT}/{sample_id}_{suffix}.h5"
        p = hf_hub_download(
            GAMUS_REPO, fname, repo_type="dataset",
            cache_dir=str(CACHE_DIR),
        )
        paths.append(Path(p))
    return tuple(paths)


def _load_h5_image(path: Path) -> np.ndarray:
    """Load the single 'image' dataset from a GAMUS HDF5 file."""
    with h5py.File(str(path), "r") as f:
        return f["image"][:]


def _classify_terrain(cls_arr: np.ndarray) -> str:
    """Classify a tile's dominant terrain type from its semantic class map."""
    cls_int = cls_arr.astype(int)
    unique, counts = np.unique(cls_int, return_counts=True)
    class_pcts = {int(u): float(c) / cls_int.size for u, c in zip(unique, counts)}

    terrain_pcts: dict[str, float] = defaultdict(float)
    for cls_id, pct in class_pcts.items():
        cls_name = CLASS_NAMES.get(cls_id, "other")
        terrain = TERRAIN_MAP.get(cls_name, "mixed")
        terrain_pcts[terrain] += pct

    dominant = max(terrain_pcts, key=terrain_pcts.get)
    return dominant


def _elevation_variance_category(agl: np.ndarray) -> str:
    """If AGL range > 15m, classify as hilly."""
    finite = agl[np.isfinite(agl)]
    if finite.size == 0:
        return "flat"
    relief = float(finite.max() - finite.min())
    return "hilly" if relief > 15.0 else "flat"


# ---------------------------------------------------------------------------
# Benchmark runner
# ---------------------------------------------------------------------------

def _resolve_sample_ids(requested: list[str], n_tiles: int) -> list[str]:
    """Return up to n_tiles valid sample IDs."""
    from huggingface_hub import list_repo_files

    files = list(list_repo_files(GAMUS_REPO, repo_type="dataset"))
    val_imgs = sorted([f for f in files if f.startswith(f"images/{GAMUS_SPLIT}/")])
    available_ids = {f.split("/")[-1].replace("_RGB.h5", "") for f in val_imgs}

    valid = [sid for sid in requested if sid in available_ids]
    if len(valid) < n_tiles:
        remaining = sorted(available_ids - set(valid))
        valid.extend(remaining[: n_tiles - len(valid)])

    return valid[:n_tiles]


def benchmark(sample_ids: list[str], n_tiles: int,
              leave_out_city: str | None = None) -> list[dict]:
    """Run the full benchmark and return per-tile result dicts.

    If *leave_out_city* is set (e.g. "DC" or "PHL"), only tiles from that
    city are evaluated — simulating the "unseen city" generalization test
    when the model was trained WITHOUT that city's data.
    """
    ids = _resolve_sample_ids(sample_ids, n_tiles)

    if leave_out_city:
        prefix = leave_out_city.upper() + "_"
        ids = [sid for sid in ids if sid.startswith(prefix)]
        logger.info(
            "Leave-one-city-out mode: evaluating %d tiles from %s only",
            len(ids), leave_out_city.upper(),
        )

    logger.info("Benchmarking %d GAMUS tiles from '%s' split", len(ids), GAMUS_SPLIT)

    results = []
    for i, sid in enumerate(ids):
        logger.info("[%d/%d] Processing %s ...", i + 1, len(ids), sid)
        try:
            img_path, ht_path, cls_path = _download_tile(sid)
        except Exception as exc:
            logger.warning("  SKIP %s: download failed: %s", sid, exc)
            continue

        rgb = _load_h5_image(img_path)
        agl = _load_h5_image(ht_path)
        cls = _load_h5_image(cls_path)

        terrain = _classify_terrain(cls)

        t0 = time.perf_counter()
        pred_elevation01 = infer_depth(rgb)
        inference_ms = (time.perf_counter() - t0) * 1000.0

        # Overall tile metrics
        metrics = evaluate(pred_elevation01, agl)

        # Per-class metrics
        per_class = _per_class_metrics(pred_elevation01, agl, cls)

        # Per-height-bucket metrics
        per_bucket = _per_bucket_metrics(pred_elevation01, agl)

        agl_range = float(np.nanmax(agl) - np.nanmin(agl))
        record = {
            "sample_id": sid,
            "terrain_type": terrain,
            "relief_m": agl_range,
            "agl_mean_m": float(np.nanmean(agl)),
            "inference_ms": round(inference_ms, 1),
            **metrics,
            "per_class": per_class,
            "per_bucket": per_bucket,
        }
        results.append(record)

        logger.info(
            "  %s  terrain=%-8s  RMSE=%.2f m  MAE=%.2f m  r=%.3f  slope_rmse=%.3f",
            sid, terrain,
            metrics["rmse_m"], metrics["mae_m"],
            metrics["pearson_r"], metrics["slope_rmse"],
        )

    return results


def _per_class_metrics(
    pred: np.ndarray, ref: np.ndarray, cls: np.ndarray,
) -> dict[str, dict]:
    """Compute metrics per semantic class (building, tree, road, etc.)."""
    cls_int = cls.astype(int)
    # Resize cls to match pred shape if needed
    if cls_int.shape != pred.shape:
        import cv2 as _cv2
        cls_int = _cv2.resize(
            cls_int.astype(np.float32), (pred.shape[1], pred.shape[0]),
            interpolation=_cv2.INTER_NEAREST,
        ).astype(int)

    result = {}
    for cls_id, cls_name in CLASS_NAMES.items():
        mask = cls_int == cls_id
        n_px = int(mask.sum())
        if n_px < 100:  # skip classes with too few pixels
            continue
        m = evaluate(pred, ref, valid_mask=mask)
        m["n_px"] = n_px
        m["pct"] = round(100.0 * n_px / cls_int.size, 1)
        result[cls_name] = m
    return result


# Height buckets for per-AGL analysis.
_HEIGHT_BUCKETS = [
    ("0-2m (ground)",   0.0,   2.0),
    ("2-10m (low)",     2.0,  10.0),
    ("10-30m (mid)",   10.0,  30.0),
    ("30m+ (tall)",    30.0, 999.0),
]


def _per_bucket_metrics(
    pred: np.ndarray, ref: np.ndarray,
) -> dict[str, dict]:
    """Compute metrics per height bucket based on reference AGL."""
    ref_arr = np.asarray(ref, dtype=np.float64)
    result = {}
    for label, lo, hi in _HEIGHT_BUCKETS:
        mask = np.isfinite(ref_arr) & (ref_arr >= lo) & (ref_arr < hi)
        n_px = int(mask.sum())
        if n_px < 100:
            continue
        m = evaluate(pred, ref, valid_mask=mask)
        m["n_px"] = n_px
        m["pct"] = round(100.0 * n_px / ref_arr.size, 1)
        result[label] = m
    return result


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

def _aggregate_by_terrain(results: list[dict]) -> dict[str, dict]:
    """Compute mean metrics per terrain type."""
    by_terrain: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        by_terrain[r["terrain_type"]].append(r)

    agg = {}
    for terrain, recs in sorted(by_terrain.items()):
        n = len(recs)
        agg[terrain] = {
            "n_tiles": n,
            "rmse_m": np.nanmean([r["rmse_m"] for r in recs]),
            "mae_m": np.nanmean([r["mae_m"] for r in recs]),
            "pearson_r": np.nanmean([r["pearson_r"] for r in recs]),
            "slope_rmse": np.nanmean([r["slope_rmse"] for r in recs]),
        }
    return agg


def _aggregate_per_class(results: list[dict]) -> dict[str, dict]:
    """Aggregate per-class metrics across all tiles."""
    class_data: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        for cls_name, m in r.get("per_class", {}).items():
            class_data[cls_name].append(m)

    agg = {}
    for cls_name, recs in sorted(class_data.items()):
        agg[cls_name] = {
            "rmse_m": float(np.nanmean([r["rmse_m"] for r in recs])),
            "mae_m": float(np.nanmean([r["mae_m"] for r in recs])),
            "pearson_r": float(np.nanmean([r["pearson_r"] for r in recs])),
            "n_tiles": len(recs),
        }
    return agg


def _aggregate_per_bucket(results: list[dict]) -> dict[str, dict]:
    """Aggregate per-height-bucket metrics across all tiles."""
    bucket_data: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        for label, m in r.get("per_bucket", {}).items():
            bucket_data[label].append(m)

    agg = {}
    for label, recs in bucket_data.items():  # preserve insertion order
        agg[label] = {
            "rmse_m": float(np.nanmean([r["rmse_m"] for r in recs])),
            "mae_m": float(np.nanmean([r["mae_m"] for r in recs])),
            "pearson_r": float(np.nanmean([r["pearson_r"] for r in recs])),
            "n_tiles": len(recs),
        }
    return agg


def write_accuracy_log(results: list[dict], agg: dict[str, dict],
                       class_agg: dict | None = None,
                       bucket_agg: dict | None = None,
                       leave_out_city: str | None = None) -> None:
    """Append the GAMUS benchmark results to docs/accuracy-log.md."""
    ACCURACY_LOG_MD.parent.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")

    lines = []
    if ACCURACY_LOG_MD.exists():
        existing = ACCURACY_LOG_MD.read_text(encoding="utf-8")
        if existing.strip():
            lines.append(existing.rstrip())
            lines.append("")
    else:
        lines.append("# DepthWizard Accuracy Log")
        lines.append("")

    header = f"## GAMUS Benchmark -- {ts}"
    if leave_out_city:
        header += f" (leave-out: {leave_out_city.upper()})"
    lines.append(header)
    lines.append("")
    lines.append(
        f"Dataset: [earthflow/GAMUS](https://huggingface.co/datasets/earthflow/GAMUS) "
        f"(validation split, {len(results)} tiles)"
    )
    lines.append(f"Model: `{MODEL_ID}` (pretrained, no fine-tuning)")
    lines.append(
        "Metrics computed after least-squares scale+shift alignment "
        "(identical to `depth_pipeline.metrics.evaluate`)."
    )
    lines.append("")

    lines.append("### Per-Terrain-Type Results")
    lines.append("")
    lines.append("| Terrain Type | Tiles | RMSE (m) | MAE (m) | Pearson r | Slope RMSE |")
    lines.append("|---|---|---|---|---|---|")
    for terrain, m in sorted(agg.items()):
        lines.append(
            f"| {terrain.capitalize()} | {m['n_tiles']} | "
            f"{m['rmse_m']:.2f} | {m['mae_m']:.2f} | "
            f"{m['pearson_r']:.3f} | {m['slope_rmse']:.3f} |"
        )
    lines.append("")

    all_rmse = np.nanmean([r["rmse_m"] for r in results])
    all_mae = np.nanmean([r["mae_m"] for r in results])
    all_r = np.nanmean([r["pearson_r"] for r in results])
    all_slope = np.nanmean([r["slope_rmse"] for r in results])
    lines.append(
        f"**Overall** ({len(results)} tiles): "
        f"RMSE={all_rmse:.2f} m, MAE={all_mae:.2f} m, "
        f"Pearson r={all_r:.3f}, Slope RMSE={all_slope:.3f}"
    )
    lines.append("")

    lines.append("<details>")
    lines.append("<summary>Per-tile details</summary>")
    lines.append("")
    lines.append("| Sample ID | Terrain | AGL Mean (m) | Relief (m) | RMSE (m) | MAE (m) | Pearson r | Slope RMSE | Inference (ms) |")
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for r in results:
        lines.append(
            f"| {r['sample_id']} | {r['terrain_type']} | "
            f"{r['agl_mean_m']:.1f} | {r['relief_m']:.1f} | "
            f"{r['rmse_m']:.2f} | {r['mae_m']:.2f} | "
            f"{r['pearson_r']:.3f} | {r['slope_rmse']:.3f} | "
            f"{r['inference_ms']:.0f} |"
        )
    lines.append("")
    lines.append("</details>")
    lines.append("")

    # Per-class breakdown table
    if class_agg:
        lines.append("### Per-Class Breakdown")
        lines.append("")
        lines.append("| Class | Tiles | RMSE (m) | MAE (m) | Pearson r |")
        lines.append("|---|---|---|---|---|")
        for cls_name, m in sorted(class_agg.items()):
            lines.append(
                f"| {cls_name.capitalize()} | {m['n_tiles']} | "
                f"{m['rmse_m']:.2f} | {m['mae_m']:.2f} | "
                f"{m['pearson_r']:.3f} |"
            )
        lines.append("")

    # Per-height-bucket breakdown table
    if bucket_agg:
        lines.append("### Per-Height-Bucket Breakdown")
        lines.append("")
        lines.append("| Height Bucket | Tiles | RMSE (m) | MAE (m) | Pearson r |")
        lines.append("|---|---|---|---|---|")
        for label, m in bucket_agg.items():
            lines.append(
                f"| {label} | {m['n_tiles']} | "
                f"{m['rmse_m']:.2f} | {m['mae_m']:.2f} | "
                f"{m['pearson_r']:.3f} |"
            )
        lines.append("")

    ACCURACY_LOG_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")
    logger.info("Results appended to %s", ACCURACY_LOG_MD)


def write_accuracy_jsonl(results: list[dict]) -> None:
    """Also append to the machine-readable accuracy_log.jsonl."""
    jsonl_path = _REPO_ROOT / "docs" / "accuracy_log.jsonl"
    jsonl_path.parent.mkdir(parents=True, exist_ok=True)
    ts = datetime.now(timezone.utc).isoformat()
    with jsonl_path.open("a", encoding="utf-8") as fh:
        for r in results:
            payload = {
                "ts": ts,
                "model_id": MODEL_ID,
                "source": "gamus_benchmark",
                "split": GAMUS_SPLIT,
                **r,
            }
            fh.write(json.dumps(payload) + "\n")
    logger.info("Results appended to %s", jsonl_path)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Benchmark existing depth pipeline against GAMUS dataset.",
    )
    parser.add_argument(
        "--n-tiles", type=int, default=18,
        help="Number of tiles to benchmark (default: 18).",
    )
    parser.add_argument(
        "--leave-out-city", type=str, default=None,
        help="Only evaluate tiles from this city (e.g. 'DC' or 'PHL') "
             "for unseen-city generalization testing.",
    )
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.INFO,
        format="%(levelname)s %(name)s: %(message)s",
    )
    os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"

    t0 = time.perf_counter()
    results = benchmark(
        DEFAULT_SAMPLE_IDS, n_tiles=args.n_tiles,
        leave_out_city=args.leave_out_city,
    )

    if not results:
        logger.error("No tiles were successfully processed.")
        return 1

    agg = _aggregate_by_terrain(results)

    # Aggregate per-class across all tiles
    class_agg = _aggregate_per_class(results)
    bucket_agg = _aggregate_per_bucket(results)

    print("\n" + "=" * 72)
    print("GAMUS BENCHMARK RESULTS")
    if args.leave_out_city:
        print(f"  (leave-one-city-out: evaluating {args.leave_out_city.upper()} only)")
    print("=" * 72)
    for terrain, m in sorted(agg.items()):
        print(
            f"  {terrain.capitalize():10s}  n={m['n_tiles']:2d}  "
            f"RMSE={m['rmse_m']:.2f}m  MAE={m['mae_m']:.2f}m  "
            f"r={m['pearson_r']:.3f}  slope_rmse={m['slope_rmse']:.3f}"
        )

    if class_agg:
        print("\nPer-class breakdown:")
        for cls_name, m in sorted(class_agg.items()):
            print(
                f"  {cls_name:16s}  RMSE={m['rmse_m']:.2f}m  "
                f"MAE={m['mae_m']:.2f}m  r={m['pearson_r']:.3f}"
            )

    if bucket_agg:
        print("\nPer-height-bucket breakdown:")
        for label, m in bucket_agg.items():
            print(
                f"  {label:20s}  RMSE={m['rmse_m']:.2f}m  "
                f"MAE={m['mae_m']:.2f}m  r={m['pearson_r']:.3f}"
            )

    total_s = time.perf_counter() - t0
    print(f"\nTotal time: {total_s:.1f}s ({len(results)} tiles)")
    print("=" * 72)

    write_accuracy_log(results, agg, class_agg, bucket_agg,
                       leave_out_city=args.leave_out_city)
    write_accuracy_jsonl(results)
    return 0


if __name__ == "__main__":
    sys.exit(main())
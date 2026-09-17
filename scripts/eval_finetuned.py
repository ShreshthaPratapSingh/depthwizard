#!/usr/bin/env python
"""Evaluate a fine-tuned DA-V2 checkpoint against the GAMUS benchmark.

Runs the same 18-tile validation as benchmark_gamus.py but uses the
fine-tuned model from models/depth-anything-v2-small-gamus/.

Usage:
    python scripts/eval_finetuned.py
"""
from __future__ import annotations

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
import torch
import torch.nn.functional as F

_REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_REPO_ROOT))

from depth_pipeline.metrics import evaluate
from depth_pipeline.config import MODEL_ID as PRETRAINED_ID

logger = logging.getLogger("eval_finetuned")

FINETUNED_DIR = _REPO_ROOT / "models" / "depth-anything-v2-small-gamus"
FINETUNED_ID = "depth-anything-v2-small-gamus"
GAMUS_REPO = "earthflow/GAMUS"
GAMUS_SPLIT = "val"
CACHE_DIR = _REPO_ROOT / "backend" / "data" / "gamus_cache"
ACCURACY_LOG_MD = _REPO_ROOT / "docs" / "accuracy-log.md"

# Same sample IDs used in benchmark_gamus.py for direct comparison
BENCHMARK_SAMPLE_IDS = [
    "DC_02_26", "DC_04_23", "DC_08_31", "DC_11_16", "DC_12_17",
    "PHL_6150", "PHL_6151", "PHL_6200", "PHL_6300", "PHL_6400",
    "PHL_6500", "PHL_6600",
    "DC_04_27", "DC_09_33", "DC_10_30", "DC_11_33", "DC_11_34", "DC_12_27",
]

CLASS_NAMES = {
    0: "other", 1: "ground", 2: "low_vegetation",
    3: "building", 4: "water", 5: "road", 6: "tree",
}
TERRAIN_MAP = {
    "building": "urban", "road": "urban", "ground": "mixed",
    "tree": "forested", "low_vegetation": "forested",
    "water": "mixed", "other": "mixed",
}


def _classify_terrain(cls_arr: np.ndarray) -> str:
    cls_int = cls_arr.astype(int)
    unique, counts = np.unique(cls_int, return_counts=True)
    terrain_pcts: dict[str, float] = defaultdict(float)
    for u, c in zip(unique, counts):
        cls_name = CLASS_NAMES.get(int(u), "other")
        terrain = TERRAIN_MAP.get(cls_name, "mixed")
        terrain_pcts[terrain] += float(c) / cls_int.size
    return max(terrain_pcts, key=terrain_pcts.get)


def _download_tile(sample_id: str):
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


def infer_finetuned(rgb: np.ndarray, model, processor, device) -> np.ndarray:
    """Run inference using the fine-tuned model."""
    from PIL import Image

    pil = Image.fromarray(rgb)
    orig_h, orig_w = rgb.shape[:2]

    inputs = processor(images=pil, return_tensors="pt")
    pixel_values = inputs["pixel_values"].to(device)

    with torch.inference_mode():
        outputs = model(pixel_values=pixel_values)
        pred = outputs.predicted_depth

    # Upsample to original resolution
    pred = F.interpolate(
        pred.unsqueeze(1).float(),
        size=(orig_h, orig_w),
        mode="bicubic", align_corners=False,
    ).squeeze(1).squeeze(0)

    raw = pred.detach().cpu().numpy()

    # Normalize to [0, 1]
    dmin, dmax = float(raw.min()), float(raw.max())
    elevation01 = (raw - dmin) / (dmax - dmin + 1e-8)
    return elevation01.astype(np.float32)


def run_eval():
    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    if not FINETUNED_DIR.exists():
        logger.error("Fine-tuned checkpoint not found at %s", FINETUNED_DIR)
        return None

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    logger.info("Loading fine-tuned model from %s", FINETUNED_DIR)

    processor = AutoImageProcessor.from_pretrained(
        str(FINETUNED_DIR), local_files_only=True
    )
    model = AutoModelForDepthEstimation.from_pretrained(
        str(FINETUNED_DIR), local_files_only=True
    )
    model.to(device).eval()

    results = []
    for i, sid in enumerate(BENCHMARK_SAMPLE_IDS):
        logger.info("[%d/%d] %s", i + 1, len(BENCHMARK_SAMPLE_IDS), sid)
        try:
            img_path, ht_path, cls_path = _download_tile(sid)
        except Exception as exc:
            logger.warning("SKIP %s: %s", sid, exc)
            continue

        with h5py.File(str(img_path), "r") as f:
            rgb = f["image"][:]
        with h5py.File(str(ht_path), "r") as f:
            agl = f["image"][:]
        with h5py.File(str(cls_path), "r") as f:
            cls = f["image"][:]

        terrain = _classify_terrain(cls)

        t0 = time.perf_counter()
        pred = infer_finetuned(rgb, model, processor, device)
        inference_ms = (time.perf_counter() - t0) * 1000.0

        metrics = evaluate(pred, agl)
        record = {
            "sample_id": sid,
            "terrain_type": terrain,
            "relief_m": float(np.nanmax(agl) - np.nanmin(agl)),
            "agl_mean_m": float(np.nanmean(agl)),
            "inference_ms": round(inference_ms, 1),
            **metrics,
        }
        results.append(record)
        logger.info(
            "  %s  terrain=%-8s  RMSE=%.2f  MAE=%.2f  r=%.3f",
            sid, terrain, metrics["rmse_m"], metrics["mae_m"], metrics["pearson_r"],
        )

    return results


def write_results(results: list[dict]):
    """Append fine-tuned results to accuracy-log.md."""
    by_terrain: dict[str, list[dict]] = defaultdict(list)
    for r in results:
        by_terrain[r["terrain_type"]].append(r)

    agg = {}
    for terrain, recs in sorted(by_terrain.items()):
        agg[terrain] = {
            "n_tiles": len(recs),
            "rmse_m": np.nanmean([r["rmse_m"] for r in recs]),
            "mae_m": np.nanmean([r["mae_m"] for r in recs]),
            "pearson_r": np.nanmean([r["pearson_r"] for r in recs]),
            "slope_rmse": np.nanmean([r["slope_rmse"] for r in recs]),
        }

    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    lines = []
    if ACCURACY_LOG_MD.exists():
        existing = ACCURACY_LOG_MD.read_text(encoding="utf-8")
        if existing.strip():
            lines.append(existing.rstrip())
            lines.append("")

    lines.append(f"## GAMUS Benchmark (Fine-tuned) -- {ts}")
    lines.append("")
    lines.append(f"Model: `{FINETUNED_ID}` (neck+head fine-tuned on ~200 GAMUS train tiles)")
    lines.append(f"Baseline: `{PRETRAINED_ID}` (pretrained, no fine-tuning)")
    lines.append("")
    lines.append("### Per-Terrain-Type Results (Fine-tuned)")
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

    # Comparison table
    lines.append("### Before vs After Fine-Tuning")
    lines.append("")
    lines.append("| Metric | Pretrained | Fine-tuned | Delta |")
    lines.append("|---|---|---|---|")

    # Pretrained baseline from prior benchmark
    baseline = {"rmse_m": 5.14, "mae_m": 4.08, "pearson_r": 0.240, "slope_rmse": 0.941}
    for metric, label in [("rmse_m", "RMSE (m)"), ("mae_m", "MAE (m)"),
                          ("pearson_r", "Pearson r"), ("slope_rmse", "Slope RMSE")]:
        pre = baseline[metric]
        post_val = float(np.nanmean([r[metric] for r in results]))
        delta = post_val - pre
        better = (delta < 0) if metric in ("rmse_m", "mae_m", "slope_rmse") else (delta > 0)
        arrow = "v" if better else "^" if delta != 0 else "="
        lines.append(f"| {label} | {pre:.3f} | {post_val:.3f} | {delta:+.3f} {arrow} |")
    lines.append("")

    ACCURACY_LOG_MD.write_text("\n".join(lines) + "\n", encoding="utf-8")
    logger.info("Results written to %s", ACCURACY_LOG_MD)


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"

    results = run_eval()
    if not results:
        return 1

    write_results(results)

    # Print summary
    all_r = np.nanmean([r["pearson_r"] for r in results])
    all_rmse = np.nanmean([r["rmse_m"] for r in results])
    print("\n" + "=" * 60)
    print("FINE-TUNED MODEL EVALUATION")
    print("=" * 60)
    print(f"  Pearson r:  {all_r:.3f}  (baseline: 0.240)")
    print(f"  RMSE:       {all_rmse:.2f}m  (baseline: 5.14m)")
    r_improvement = all_r - 0.240
    print(f"  Delta r:    {r_improvement:+.3f}")
    if r_improvement >= 0.1:
        print("  VERDICT:    GO -- meets merge criteria (r improved >= 0.1)")
    else:
        print("  VERDICT:    NO-GO -- does not meet merge criteria")
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())
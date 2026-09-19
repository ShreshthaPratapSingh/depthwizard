#!/usr/bin/env python
"""Throughput benchmark for GAMUS fine-tuning on the current GPU.

Runs a short training burst (50 steps) at various batch sizes to measure
actual images/sec, then reports capacity estimates for a 20-hour budget.

Usage:
    python scripts/benchmark_throughput.py
"""
from __future__ import annotations

import logging
import os
import sys
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import DataLoader

logger = logging.getLogger("benchmark_throughput")

_REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_REPO_ROOT))

from scripts.finetune_gamus import (
    GAMUSDataset, CombinedLoss, predownload_tiles, select_training_tiles,
    GAMUS_TRAIN_SPLIT, CACHE_DIR,
)

TRAINING_HOURS = 20  # hours reserved for training (out of 24)
BENCHMARK_STEPS = 50  # steps per batch-size test


def count_cached_tiles() -> int:
    """Count how many unique tile IDs are already cached locally."""
    if not CACHE_DIR.exists():
        return 0
    h5_files = list(CACHE_DIR.rglob("*.h5"))
    # Each tile has RGB + AGL = 2 files. Count unique tile IDs.
    tile_ids = set()
    for f in h5_files:
        name = f.stem
        # Remove suffixes like _RGB, _AGL, _CLS
        for suffix in ("_RGB", "_AGL", "_CLS"):
            name = name.replace(suffix, "")
        tile_ids.add(name)
    return len(tile_ids)


def benchmark_batch_size(
    model, processor, tile_ids: list[str], device: torch.device,
    batch_size: int, steps: int = BENCHMARK_STEPS,
) -> dict | None:
    """Run `steps` training steps at given batch_size, return throughput."""
    dataset = GAMUSDataset(tile_ids, GAMUS_TRAIN_SPLIT, processor, CACHE_DIR)
    try:
        dataloader = DataLoader(
            dataset, batch_size=batch_size, shuffle=True,
            num_workers=0, pin_memory=True, drop_last=True,
        )
    except Exception as e:
        logger.warning("  batch_size=%d failed to create loader: %s", batch_size, e)
        return None

    criterion = CombinedLoss(silog_weight=1.0, grad_weight=2.0)
    optimizer = torch.optim.AdamW(
        [p for p in model.parameters() if p.requires_grad],
        lr=2e-5, weight_decay=0.01,
    )
    scaler = torch.amp.GradScaler(enabled=(device.type == "cuda"))
    dtype = torch.float16 if device.type == "cuda" else torch.float32

    model.train()
    model.backbone.eval()

    torch.cuda.empty_cache() if device.type == "cuda" else None
    torch.cuda.reset_peak_memory_stats() if device.type == "cuda" else None

    total_images = 0
    t_start = time.perf_counter()

    try:
        for step, batch in enumerate(dataloader):
            if step >= steps:
                break

            pixel_values = batch["pixel_values"].to(device)
            labels = batch["labels"].to(device)
            valid_mask = batch["valid_mask"].to(device)

            with torch.amp.autocast(device_type=device.type, dtype=dtype):
                outputs = model(pixel_values=pixel_values)
                pred_depth = outputs.predicted_depth
                if pred_depth.shape[-2:] != labels.shape[-2:]:
                    pred_depth = F.interpolate(
                        pred_depth.unsqueeze(1),
                        size=labels.shape[-2:],
                        mode="bilinear", align_corners=False,
                    ).squeeze(1)
                pred_depth = F.relu(pred_depth)
                loss = criterion(pred_depth.unsqueeze(1), labels, valid_mask)

            scaler.scale(loss).backward()
            scaler.unscale_(optimizer)
            torch.nn.utils.clip_grad_norm_(
                [p for p in model.parameters() if p.requires_grad], 1.0
            )
            scaler.step(optimizer)
            scaler.update()
            optimizer.zero_grad()

            total_images += pixel_values.shape[0]

            if step == 0:
                # Warmup step — don't count in timing
                t_start = time.perf_counter()
                total_images = 0

    except torch.cuda.OutOfMemoryError:
        logger.warning("  batch_size=%d: OOM after %d steps", batch_size, step)
        torch.cuda.empty_cache()
        return None
    except Exception as e:
        logger.warning("  batch_size=%d: error after %d steps: %s", batch_size, step, e)
        return None

    elapsed = time.perf_counter() - t_start
    if elapsed < 0.1 or total_images == 0:
        return None

    peak_mem = 0
    if device.type == "cuda":
        peak_mem = torch.cuda.max_memory_allocated() / 1e9

    return {
        "batch_size": batch_size,
        "steps": min(step, steps) - 1,  # minus warmup
        "total_images": total_images,
        "elapsed_s": round(elapsed, 2),
        "images_per_sec": round(total_images / elapsed, 2),
        "steps_per_sec": round((min(step, steps) - 1) / elapsed, 3),
        "peak_vram_gb": round(peak_mem, 2),
        "loss_last": round(loss.item(), 4),
    }


def main():
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s: %(message)s")
    os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"

    # --- GPU info ---
    print("=" * 60)
    print("THROUGHPUT BENCHMARK")
    print("=" * 60)

    if torch.cuda.is_available():
        gpu_name = torch.cuda.get_device_name(0)
        vram_total = torch.cuda.get_device_properties(0).total_memory / 1e9
        print(f"GPU: {gpu_name} ({vram_total:.1f} GB VRAM)")
    else:
        print("GPU: NONE (CPU only)")
        vram_total = 0
    print()

    # --- Cache check ---
    cached_tiles = count_cached_tiles()
    print(f"Cached tiles (local): {cached_tiles}")
    print(f"GAMUS full dataset: ~5,900 tiles")
    print(f"Tiles needing download: ~{max(5900 - cached_tiles, 0)}")
    print()

    # --- Load model ---
    print("Loading model...")
    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    model_id = "depth-anything/Depth-Anything-V2-Small-hf"
    processor = AutoImageProcessor.from_pretrained(model_id)
    model = AutoModelForDepthEstimation.from_pretrained(model_id)

    # Freeze backbone
    for param in model.backbone.parameters():
        param.requires_grad = False
    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total_params = sum(p.numel() for p in model.parameters())
    print(f"Parameters: {total_params:,} total, {trainable:,} trainable ({100*trainable/total_params:.1f}%)")

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = model.to(device)
    print(f"Device: {device}")
    print()

    # --- Use cached tiles for benchmark ---
    # Select tiles (will use cache for already-downloaded ones)
    print("Selecting training tiles from GAMUS repo...")
    tile_ids = select_training_tiles(min(100, max(cached_tiles, 50)))
    print(f"Using {len(tile_ids)} tiles for benchmark")
    print()

    # --- Benchmark various batch sizes ---
    batch_sizes = [2, 4, 8, 16, 32]
    if vram_total < 8:
        batch_sizes = [2, 4, 8]

    results = []
    print(f"{'BS':>4} | {'Steps':>5} | {'Imgs':>6} | {'Time(s)':>8} | {'img/s':>8} | {'VRAM(GB)':>9} | {'Loss':>8}")
    print("-" * 70)

    for bs in batch_sizes:
        if len(tile_ids) < bs:
            print(f"{bs:>4} | SKIP (not enough tiles)")
            continue

        r = benchmark_batch_size(model, processor, tile_ids, device, bs)
        if r is None:
            print(f"{r and r.get('batch_size', bs) or bs:>4} | OOM or error")
            continue

        results.append(r)
        print(
            f"{r['batch_size']:>4} | {r['steps']:>5} | {r['total_images']:>6} | "
            f"{r['elapsed_s']:>8.1f} | {r['images_per_sec']:>8.2f} | "
            f"{r['peak_vram_gb']:>9.2f} | {r['loss_last']:>8.4f}"
        )

    if not results:
        print("\nNo successful benchmarks! Check GPU/data.")
        return 1

    # --- Find optimal config ---
    best = max(results, key=lambda r: r["images_per_sec"])
    print()
    print("=" * 60)
    print(f"OPTIMAL: batch_size={best['batch_size']}, "
          f"{best['images_per_sec']:.1f} img/s, "
          f"{best['peak_vram_gb']:.1f} GB VRAM")
    print("=" * 60)
    print()

    # --- Capacity estimates ---
    ips = best["images_per_sec"]
    training_seconds = TRAINING_HOURS * 3600

    print("CAPACITY ESTIMATES (20h training budget):")
    print("-" * 60)
    for n_tiles in [500, 1000, 2000, 3000, 5000]:
        for n_epochs in [3, 5, 10, 15, 20]:
            total_steps = (n_tiles // best["batch_size"]) * n_epochs
            total_images_needed = n_tiles * n_epochs
            time_needed = total_images_needed / ips
            fits = "✓" if time_needed < training_seconds else "✗"
            if time_needed < training_seconds:
                print(
                    f"  {fits} {n_tiles:>5} tiles × {n_epochs:>2} epochs = "
                    f"{total_images_needed:>7,} imgs → {time_needed/3600:>5.1f}h"
                )

    print()
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())

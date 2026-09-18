#!/usr/bin/env python
"""Fine-tune Depth-Anything-V2-Small on a GAMUS subset for aerial depth.

Reduced-scope experiment: freeze backbone, train neck+head only, 5 epochs,
~200 tiles. Saves checkpoint to models/depth-anything-v2-small-gamus/.

Usage:
    python scripts/finetune_gamus.py                 # default settings
    python scripts/finetune_gamus.py --epochs 3      # fewer epochs
    python scripts/finetune_gamus.py --n-tiles 50    # smaller subset
"""
from __future__ import annotations

import argparse
import logging
import math
import os
import sys
import time
from pathlib import Path

import h5py
import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F
from torch.utils.data import Dataset, DataLoader

logger = logging.getLogger("finetune_gamus")

_REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(_REPO_ROOT))

# Paths
PRETRAINED_DIR = _REPO_ROOT / "models" / "depth-anything-v2-small"
FINETUNED_DIR = _REPO_ROOT / "models" / "depth-anything-v2-small-gamus"
FINETUNED_V2_DIR = _REPO_ROOT / "models" / "depth-anything-v2-small-gamus-v2"
CACHE_DIR = _REPO_ROOT / "backend" / "data" / "gamus_cache"

GAMUS_REPO = "earthflow/GAMUS"
GAMUS_TRAIN_SPLIT = "train"


# ===================================================================
# Loss functions (DA-V2 recipe: SILog + gradient matching, ratio 1:2)
# ===================================================================

class SILogLoss(nn.Module):
    """Scale-Invariant Logarithmic loss (Eigen et al., 2014).
    
    Standard loss for metric depth fine-tuning. Handles the
    scale ambiguity inherent in monocular depth estimation.
    """
    def __init__(self, lambd: float = 0.5, eps: float = 0.5):
        super().__init__()
        self.lambd = lambd
        self.eps = eps  # offset to avoid log(0) for AGL=0 ground pixels

    def forward(self, pred: torch.Tensor, target: torch.Tensor,
                mask: torch.Tensor | None = None) -> torch.Tensor:
        # Add epsilon to avoid log(0)
        pred_safe = pred + self.eps
        target_safe = target + self.eps

        log_diff = torch.log(pred_safe) - torch.log(target_safe)

        if mask is not None:
            log_diff = log_diff[mask]

        if log_diff.numel() == 0:
            return torch.tensor(0.0, device=pred.device, requires_grad=True)

        silog = torch.sqrt(
            torch.mean(log_diff ** 2) - self.lambd * (torch.mean(log_diff) ** 2)
        )
        return silog


class GradientMatchingLoss(nn.Module):
    """Gradient matching loss for edge fidelity."""
    def forward(self, pred: torch.Tensor, target: torch.Tensor,
                mask: torch.Tensor | None = None) -> torch.Tensor:
        # Compute image gradients
        pred_dx = pred[:, :, :, 1:] - pred[:, :, :, :-1]
        pred_dy = pred[:, :, 1:, :] - pred[:, :, :-1, :]
        target_dx = target[:, :, :, 1:] - target[:, :, :, :-1]
        target_dy = target[:, :, 1:, :] - target[:, :, :-1, :]

        loss_dx = F.l1_loss(pred_dx, target_dx)
        loss_dy = F.l1_loss(pred_dy, target_dy)

        return loss_dx + loss_dy


class CombinedLoss(nn.Module):
    """SILog + GradientMatching with DA-V2 weight ratio (1:2)."""
    def __init__(self, silog_weight: float = 1.0, grad_weight: float = 2.0):
        super().__init__()
        self.silog = SILogLoss()
        self.grad = GradientMatchingLoss()
        self.silog_weight = silog_weight
        self.grad_weight = grad_weight

    def forward(self, pred: torch.Tensor, target: torch.Tensor,
                mask: torch.Tensor | None = None) -> torch.Tensor:
        l_silog = self.silog(pred, target, mask)
        l_grad = self.grad(pred, target, mask)
        return self.silog_weight * l_silog + self.grad_weight * l_grad


# ===================================================================
# Degradation augmentation (simulates atmospheric/sensor degradation)
# ===================================================================

class DegradationAugment:
    """Random atmospheric/sensor degradation for training robustness.

    Applies to the raw RGB uint8 image BEFORE processor normalization.
    The AGL depth target is NOT modified (only the image changes).
    Augmentations:
    - Gaussian blur (σ 0.5-2.0): simulates atmospheric haze / defocus
    - Brightness jitter (±20%): simulates exposure variation
    - Additive Gaussian noise (σ 0-15): simulates sensor noise
    - Random horizontal flip (with corresponding depth flip)
    - Random vertical flip (with corresponding depth flip)
    """

    def __init__(self, p: float = 0.5, seed: int | None = None):
        self.p = p
        self.rng = np.random.default_rng(seed)

    def __call__(self, rgb: np.ndarray, agl: np.ndarray
                 ) -> tuple[np.ndarray, np.ndarray]:
        rgb = rgb.copy()
        agl = agl.copy()

        # Random horizontal flip
        if self.rng.random() < 0.5:
            rgb = np.flip(rgb, axis=1).copy()
            agl = np.flip(agl, axis=1).copy()

        # Random vertical flip
        if self.rng.random() < 0.5:
            rgb = np.flip(rgb, axis=0).copy()
            agl = np.flip(agl, axis=0).copy()

        if self.rng.random() > self.p:
            return rgb, agl

        img = rgb.astype(np.float32)

        # Gaussian blur
        if self.rng.random() < 0.4:
            import cv2
            sigma = self.rng.uniform(0.5, 2.0)
            ksize = int(sigma * 4) | 1  # ensure odd
            img = cv2.GaussianBlur(img, (ksize, ksize), sigma)

        # Brightness jitter
        if self.rng.random() < 0.4:
            factor = self.rng.uniform(0.8, 1.2)
            img = img * factor

        # Additive Gaussian noise
        if self.rng.random() < 0.3:
            sigma = self.rng.uniform(3.0, 15.0)
            noise = self.rng.normal(0, sigma, img.shape).astype(np.float32)
            img = img + noise

        rgb = np.clip(img, 0, 255).astype(np.uint8)
        return rgb, agl


# ===================================================================
# Dataset
# ===================================================================

class GAMUSDataset(Dataset):
    """Minimal HDF5 data loader for GAMUS training tiles."""

    def __init__(self, tile_ids: list[str], split: str, processor,
                 cache_dir: Path, augment: DegradationAugment | None = None):
        self.tile_ids = tile_ids
        self.split = split
        self.processor = processor
        self.cache_dir = cache_dir
        self.augment = augment

    def __len__(self) -> int:
        return len(self.tile_ids)

    def _get_path(self, tile_id: str, subdir: str, suffix: str) -> Path:
        from huggingface_hub import hf_hub_download
        fname = f"{subdir}/{self.split}/{tile_id}_{suffix}.h5"
        p = hf_hub_download(
            GAMUS_REPO, fname, repo_type="dataset",
            cache_dir=str(self.cache_dir),
        )
        return Path(p)

    def __getitem__(self, idx: int) -> dict:
        tid = self.tile_ids[idx]

        img_path = self._get_path(tid, "images", "RGB")
        ht_path = self._get_path(tid, "heights", "AGL")

        with h5py.File(str(img_path), "r") as f:
            rgb = f["image"][:]           # (1024, 1024, 3) uint8

        with h5py.File(str(ht_path), "r") as f:
            agl = f["image"][:]           # (1024, 1024) float32

        # Apply degradation augmentation (if enabled)
        if self.augment is not None:
            rgb, agl = self.augment(rgb, agl)

        # Process image through the DA-V2 processor
        from PIL import Image
        pil_img = Image.fromarray(rgb)
        inputs = self.processor(images=pil_img, return_tensors="pt")
        pixel_values = inputs["pixel_values"].squeeze(0)  # (3, H, W)

        # Resize AGL to match the processor output size
        proc_h, proc_w = pixel_values.shape[1], pixel_values.shape[2]
        agl_tensor = torch.from_numpy(agl).float().unsqueeze(0).unsqueeze(0)
        agl_resized = F.interpolate(
            agl_tensor, size=(proc_h, proc_w),
            mode="bilinear", align_corners=False
        ).squeeze(0)  # (1, H, W)

        # Valid mask: finite AGL values
        valid_mask = torch.isfinite(agl_resized) & (agl_resized >= 0)

        return {
            "pixel_values": pixel_values,
            "labels": agl_resized,
            "valid_mask": valid_mask,
        }


# ===================================================================
# Data selection
# ===================================================================

def select_training_tiles(n_tiles: int) -> list[str]:
    """Select a balanced set of tile IDs from the GAMUS train split."""
    from huggingface_hub import list_repo_files

    logger.info("Listing GAMUS train split files...")
    files = list(list_repo_files(GAMUS_REPO, repo_type="dataset"))
    train_imgs = sorted([
        f for f in files if f.startswith(f"images/{GAMUS_TRAIN_SPLIT}/")
    ])
    # Only include tiles whose filename ends with _RGB.h5 (some NYC tiles use _IMG.h5)
    train_imgs = [f for f in train_imgs if f.endswith("_RGB.h5")]
    all_ids = [f.split("/")[-1].replace("_RGB.h5", "") for f in train_imgs]

    dc_ids = [i for i in all_ids if i.startswith("DC_")]
    phl_ids = [i for i in all_ids if i.startswith("PHL_")]
    nyc_ids = [i for i in all_ids if i.startswith("NYC_")]
    other_ids = [i for i in all_ids
                 if not any(i.startswith(p) for p in ("DC_", "PHL_", "NYC_"))]

    logger.info(
        "Available: DC=%d, PHL=%d, NYC=%d, other=%d",
        len(dc_ids), len(phl_ids), len(nyc_ids), len(other_ids),
    )

    # Balanced selection: half DC (varied terrain), half urban (PHL/NYC)
    rng = np.random.default_rng(42)
    half = n_tiles // 2

    dc_sample = list(rng.choice(dc_ids, size=min(half, len(dc_ids)), replace=False))
    urban_pool = phl_ids + nyc_ids + other_ids
    urban_sample = list(rng.choice(
        urban_pool, size=min(n_tiles - len(dc_sample), len(urban_pool)),
        replace=False,
    ))

    selected = dc_sample + urban_sample
    rng.shuffle(selected)
    logger.info("Selected %d training tiles", len(selected))
    return list(selected)


def predownload_tiles(tile_ids: list[str], split: str) -> None:
    """Pre-download all tiles so __getitem__ hits cache."""
    from huggingface_hub import hf_hub_download

    total = len(tile_ids) * 2  # RGB + AGL (skip CLS for training)
    done = 0
    failed_ids = []
    for tid in tile_ids:
        try:
            for subdir, suffix in [("images", "RGB"), ("heights", "AGL")]:
                fname = f"{subdir}/{split}/{tid}_{suffix}.h5"
                hf_hub_download(
                    GAMUS_REPO, fname, repo_type="dataset",
                    cache_dir=str(CACHE_DIR),
                )
                done += 1
                if done % 20 == 0:
                    logger.info("  downloaded %d/%d files...", done, total)
        except Exception as exc:
            logger.warning("  SKIP tile %s: %s", tid, exc)
            failed_ids.append(tid)
    # Remove failed tiles from the list
    for fid in failed_ids:
        tile_ids.remove(fid)
    logger.info("Downloaded %d files (%d tiles skipped)", done, len(failed_ids))


# ===================================================================
# Training loop
# ===================================================================

def train(
    n_tiles: int = 200,
    epochs: int = 5,
    batch_size: int = 4,
    lr: float = 2e-5,
    grad_accum: int = 2,
    output_dir: Path | None = None,
    use_augment: bool = False,
) -> Path:
    """Run the fine-tuning loop. Returns path to saved checkpoint."""
    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    dtype = torch.float16 if device.type == "cuda" else torch.float32
    logger.info("Device: %s, dtype: %s", device, dtype)

    # 1. Select and download tiles
    tile_ids = select_training_tiles(n_tiles)
    logger.info("Pre-downloading %d tiles...", len(tile_ids))
    predownload_tiles(tile_ids, GAMUS_TRAIN_SPLIT)

    # 2. Load pretrained model
    logger.info("Loading pretrained model from %s", PRETRAINED_DIR)
    processor = AutoImageProcessor.from_pretrained(
        str(PRETRAINED_DIR), local_files_only=True
    )
    model = AutoModelForDepthEstimation.from_pretrained(
        str(PRETRAINED_DIR), local_files_only=True
    )
    model.to(device)

    # 3. Freeze backbone, train neck+head only
    for param in model.backbone.parameters():
        param.requires_grad = False

    trainable = sum(p.numel() for p in model.parameters() if p.requires_grad)
    total = sum(p.numel() for p in model.parameters())
    logger.info(
        "Trainable: %s / %s (%.1f%%)",
        f"{trainable:,}", f"{total:,}", 100.0 * trainable / total,
    )

    # 4. Dataset and dataloader
    augment = DegradationAugment(p=0.5) if use_augment else None
    if augment:
        logger.info("Degradation augmentation ENABLED (p=0.5)")
    dataset = GAMUSDataset(tile_ids, GAMUS_TRAIN_SPLIT, processor, CACHE_DIR,
                           augment=augment)
    dataloader = DataLoader(
        dataset, batch_size=batch_size, shuffle=True,
        num_workers=0,  # HDF5 not fork-safe on Windows
        pin_memory=(device.type == "cuda"),
        drop_last=True,
    )

    # 5. Loss, optimizer, scheduler
    criterion = CombinedLoss(silog_weight=1.0, grad_weight=2.0)
    optimizer = torch.optim.AdamW(
        [p for p in model.parameters() if p.requires_grad],
        lr=lr, weight_decay=0.01,
    )
    total_steps = len(dataloader) * epochs // grad_accum
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(
        optimizer, T_max=max(total_steps, 1), eta_min=lr * 0.01,
    )

    scaler = torch.amp.GradScaler(enabled=(device.type == "cuda"))

    # 6. Training loop
    logger.info("Starting training: %d epochs, %d tiles, batch=%d, accum=%d",
                epochs, len(tile_ids), batch_size, grad_accum)

    model.train()
    # Keep backbone in eval mode (frozen BatchNorm etc.)
    model.backbone.eval()

    best_loss = float("inf")
    t_start = time.perf_counter()

    for epoch in range(1, epochs + 1):
        epoch_loss = 0.0
        n_batches = 0
        optimizer.zero_grad()

        for step, batch in enumerate(dataloader):
            pixel_values = batch["pixel_values"].to(device)
            labels = batch["labels"].to(device)
            valid_mask = batch["valid_mask"].to(device)

            with torch.amp.autocast(device_type=device.type, dtype=dtype):
                outputs = model(pixel_values=pixel_values)
                pred_depth = outputs.predicted_depth  # (B, H_out, W_out)

                # Resize pred to match labels if needed
                if pred_depth.shape[-2:] != labels.shape[-2:]:
                    pred_depth = F.interpolate(
                        pred_depth.unsqueeze(1),
                        size=labels.shape[-2:],
                        mode="bilinear", align_corners=False,
                    ).squeeze(1)

                # pred_depth is disparity (higher=nearer). For AGL training,
                # we want the model to output values proportional to height.
                # Apply ReLU to ensure non-negative predictions.
                pred_depth = F.relu(pred_depth)

                pred_4d = pred_depth.unsqueeze(1)
                labels_4d = labels

                loss = criterion(pred_4d, labels_4d, valid_mask)
                loss = loss / grad_accum

            scaler.scale(loss).backward()

            if (step + 1) % grad_accum == 0:
                scaler.unscale_(optimizer)
                torch.nn.utils.clip_grad_norm_(
                    [p for p in model.parameters() if p.requires_grad],
                    max_norm=1.0,
                )
                scaler.step(optimizer)
                scaler.update()
                optimizer.zero_grad()
                scheduler.step()

            epoch_loss += loss.item() * grad_accum
            n_batches += 1

        avg_loss = epoch_loss / max(n_batches, 1)
        elapsed = time.perf_counter() - t_start
        logger.info(
            "Epoch %d/%d  loss=%.4f  lr=%.2e  elapsed=%.0fs",
            epoch, epochs, avg_loss,
            optimizer.param_groups[0]["lr"], elapsed,
        )

        if avg_loss < best_loss:
            best_loss = avg_loss

    # 7. Save checkpoint
    save_dir = output_dir or FINETUNED_DIR
    save_dir.mkdir(parents=True, exist_ok=True)
    model.save_pretrained(str(save_dir))
    processor.save_pretrained(str(save_dir))

    total_time = time.perf_counter() - t_start
    logger.info(
        "Training complete in %.0fs. Checkpoint saved to %s",
        total_time, save_dir,
    )
    logger.info("Best loss: %.4f", best_loss)

    return save_dir


# ===================================================================
# CLI
# ===================================================================

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fine-tune DA-V2-Small on GAMUS subset.",
    )
    parser.add_argument("--n-tiles", type=int, default=200)
    parser.add_argument("--epochs", type=int, default=5)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--lr", type=float, default=2e-5)
    parser.add_argument("--grad-accum", type=int, default=2)
    parser.add_argument(
        "--output-dir", type=str, default=None,
        help="Save checkpoint here instead of default FINETUNED_DIR",
    )
    parser.add_argument(
        "--augment", action="store_true", default=False,
        help="Enable degradation augmentation (blur, noise, jitter, flips)",
    )
    args = parser.parse_args(argv)

    logging.basicConfig(
        level=logging.INFO,
        format="%(levelname)s %(name)s: %(message)s",
    )
    os.environ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"

    out_dir = Path(args.output_dir) if args.output_dir else None

    try:
        ckpt_dir = train(
            n_tiles=args.n_tiles,
            epochs=args.epochs,
            batch_size=args.batch_size,
            lr=args.lr,
            grad_accum=args.grad_accum,
            output_dir=out_dir,
            use_augment=args.augment,
        )
        print(f"\nCheckpoint saved to: {ckpt_dir}")
        return 0
    except Exception:
        logger.exception("Training failed")
        return 1


if __name__ == "__main__":
    sys.exit(main())
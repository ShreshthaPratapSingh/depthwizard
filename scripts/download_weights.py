#!/usr/bin/env python3
"""Download and pin Depth-Anything-V2 (Small + Base) weights into ./models/.

Reproducibility model
---------------------
On first run the exact commit SHA of each repo's ``main`` is resolved and
recorded in ``models/pinned_revisions.json``. On every subsequent run the
download uses the pinned SHA (never a floating branch), so the bytes on disk
are always the same. Commit ``pinned_revisions.json`` to version control; the
weight files themselves are git-ignored.

To intentionally bump a model, delete its entry from the lockfile (or the whole
file) and re-run.

Usage:
    python scripts/download_weights.py
"""
import json
import sys
from pathlib import Path

from huggingface_hub import HfApi, snapshot_download

# transformers-format ("-hf") repos — loadable directly via
# transformers.AutoModelForDepthEstimation / AutoImageProcessor.
MODELS = {
    "depth-anything-v2-small": "depth-anything/Depth-Anything-V2-Small-hf",
    "depth-anything-v2-base":  "depth-anything/Depth-Anything-V2-Base-hf",
}

MODELS_DIR = Path(__file__).resolve().parent.parent / "models"
LOCKFILE = MODELS_DIR / "pinned_revisions.json"


def load_lock() -> dict:
    if LOCKFILE.exists():
        return json.loads(LOCKFILE.read_text(encoding="utf-8"))
    return {}


def save_lock(lock: dict) -> None:
    LOCKFILE.write_text(
        json.dumps(lock, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )


def resolve_sha(api: HfApi, repo_id: str, pinned: str | None) -> str:
    """Return the pinned SHA if we have one, else resolve main -> commit SHA."""
    if pinned:
        return pinned
    sha = api.model_info(repo_id).sha
    print(f"  resolved {repo_id}@main -> {sha}")
    return sha


def main() -> int:
    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    api = HfApi()
    lock = load_lock()

    for key, repo_id in MODELS.items():
        print(f"\n[{key}] {repo_id}")
        entry = lock.get(key, {})
        sha = resolve_sha(api, repo_id, entry.get("revision"))
        target = MODELS_DIR / key
        local = snapshot_download(
            repo_id=repo_id,
            revision=sha,          # always an immutable commit SHA
            local_dir=str(target),
        )
        print(f"  downloaded @ {sha[:12]} -> {local}")
        lock[key] = {"repo_id": repo_id, "revision": sha}
        save_lock(lock)  # write after each model so a mid-run failure keeps progress

    print(f"\nPinned revisions written to {LOCKFILE}")
    return 0


if __name__ == "__main__":
    sys.exit(main())

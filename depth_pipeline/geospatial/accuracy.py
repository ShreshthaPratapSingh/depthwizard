"""Append-only accuracy log for Section 16 (RMSE/MAE/R² by terrain type).

``docs/accuracy_log.jsonl`` is gitignored. Unit tests must set
``DEPTHWIZARD_ACCURACY_LOG`` to a temp file so mock-depth R² never lands
in a path someone might copy into a PR. Only log rows from real
``infer_depth`` (Depth-Anything-V2) runs as judge-facing accuracy.
"""

from __future__ import annotations

import json
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from ..config import ACCURACY_LOG_PATH, MODEL_ID


def accuracy_log_path() -> Path:
    env = os.environ.get("DEPTHWIZARD_ACCURACY_LOG")
    if env:
        return Path(env)
    return ACCURACY_LOG_PATH


def append_accuracy_record(record: dict[str, Any]) -> None:
    """Append one JSON line. Never raises — logging must not break the pipeline."""
    try:
        path = accuracy_log_path()
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "ts": datetime.now(timezone.utc).isoformat(),
            "model_id": MODEL_ID,
            **record,
        }
        with path.open("a", encoding="utf-8") as fh:
            fh.write(json.dumps(payload) + "\n")
    except Exception:
        return

"""Tests for depth_pipeline.run stage reporting and execution."""

import sys
from pathlib import Path
from unittest.mock import patch

import numpy as np
from PIL import Image

from depth_pipeline.run import process_image


def test_process_image_on_stage_ordering(tmp_path):
    img_path = tmp_path / "test.png"
    Image.new("RGB", (32, 32), color=(100, 100, 100)).save(img_path)
    out_dir = tmp_path / "output"

    stages = []

    def _record_stage(stage: str, detail: str = "") -> None:
        stages.append(stage)

    fake_depth = np.zeros((32, 32), dtype=np.float32)

    with patch("depth_pipeline.inference.infer_depth", return_value=fake_depth), \
         patch("depth_pipeline.inference.get_last_inference_ms", return_value=12.0):
        metadata = process_image(
            str(img_path),
            str(out_dir),
            target_res=33,
            on_stage=_record_stage,
        )

    assert stages == ["inferring", "calibrating", "exporting", "done"]
    assert metadata["status"] == "ok"
    assert (out_dir / "heightmap.png").exists()

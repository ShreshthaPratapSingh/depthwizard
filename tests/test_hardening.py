"""Hardening tests for depth_pipeline.run.process_image.

process_image must NEVER raise to its caller: every failure returns a valid
metadata dict with status="error" and the SAME key set as a successful run, so
the backend can rely on a consistent shape. Oversized-but-valid inputs are
downscaled (a warning), not rejected (an error).

The depth model is mocked throughout so these run on a plain CPU with no
weights / torch install.
"""

from __future__ import annotations

from unittest.mock import patch

import numpy as np
import pytest
from PIL import Image

from depth_pipeline.run import process_image


def _fake_infer(image: np.ndarray) -> np.ndarray:
    """Stand-in for infer_depth: [0,1] map matching the input HxW."""
    h, w = np.asarray(image).shape[:2]
    return np.zeros((h, w), dtype=np.float32)


def _run(image_path, out_dir):
    """Run process_image with the depth model mocked out."""
    with patch("depth_pipeline.inference.infer_depth", side_effect=_fake_infer), \
         patch("depth_pipeline.inference.get_last_inference_ms", return_value=1.0):
        return process_image(str(image_path), str(out_dir), target_res=33)


def _success_keys(tmp_path) -> set:
    """The exact key set of a genuine successful run, for shape comparison."""
    img = tmp_path / "ok.png"
    Image.new("RGB", (16, 16), color=(120, 120, 120)).save(img)
    result = _run(img, tmp_path / "ok_out")
    assert result["status"] == "ok"
    return set(result.keys())


# --- error paths: never raise, always status="error" ----------------------

def test_nonexistent_file_returns_error(tmp_path):
    result = _run(tmp_path / "does_not_exist.png", tmp_path / "out")
    assert result["status"] == "error"
    assert any("not found" in w.lower() for w in result["warnings"])
    # nothing was written
    assert result["heightmap_path"] is None
    assert result["texture_path"] is None
    assert result["confidence_mask_path"] is None


def test_zero_byte_file_returns_error(tmp_path):
    empty = tmp_path / "empty.png"
    empty.write_bytes(b"")
    result = _run(empty, tmp_path / "out")
    assert result["status"] == "error"
    assert any("empty" in w.lower() for w in result["warnings"])


def test_corrupted_image_returns_error(tmp_path):
    corrupt = tmp_path / "corrupt.png"
    # A real PNG signature followed by garbage: identifiable header, broken body.
    corrupt.write_bytes(b"\x89PNG\r\n\x1a\n" + b"\x00\xff\x01garbage" * 8)
    result = _run(corrupt, tmp_path / "out")
    assert result["status"] == "error"
    assert any("corrupt" in w.lower() or "unsupported" in w.lower()
               for w in result["warnings"])


def test_not_an_image_returns_error(tmp_path):
    junk = tmp_path / "notimage.png"
    junk.write_bytes(b"this is definitely not an image file")
    result = _run(junk, tmp_path / "out")
    assert result["status"] == "error"


def test_directory_path_returns_error(tmp_path):
    # A directory is not a file -> file not found, must not raise.
    result = _run(tmp_path, tmp_path / "out")
    assert result["status"] == "error"


def test_unexpected_internal_error_is_caught(tmp_path):
    """A failure deep in the pipeline still returns error, never raises."""
    img = tmp_path / "ok.png"
    Image.new("RGB", (16, 16), color=(90, 90, 90)).save(img)
    with patch("depth_pipeline.inference.infer_depth",
               side_effect=RuntimeError("boom")), \
         patch("depth_pipeline.inference.get_last_inference_ms", return_value=1.0):
        result = process_image(str(img), str(tmp_path / "out"), target_res=33)
    assert result["status"] == "error"
    # generic, clean message — no traceback / "boom" leaked to the caller
    assert any("internal error" in w.lower() for w in result["warnings"])
    assert all("boom" not in w for w in result["warnings"])


# --- oversized input: downscale + warn, do NOT error ----------------------

def test_oversized_image_downscales_and_succeeds(tmp_path):
    # Patch the cap low so the exact downscale path runs on a tiny image
    # (a real 16.77M-px input would make postprocess needlessly heavy here).
    small_cap = 64 * 64  # 4096 px
    img = tmp_path / "huge.png"
    Image.new("RGB", (256, 256), color=(60, 90, 120)).save(img)  # 65536 px > cap

    with patch("depth_pipeline.run.MAX_INPUT_PIXELS", small_cap):
        result = _run(img, tmp_path / "out")

    assert result["status"] == "ok"
    assert any("downscaled" in w.lower() for w in result["warnings"])
    # the actual downscale kept us within the cap
    import re
    msg = next(w for w in result["warnings"] if "downscaled" in w.lower())
    to_dims = re.search(r"to (\d+)x(\d+)", msg)
    assert to_dims is not None
    new_w, new_h = int(to_dims.group(1)), int(to_dims.group(2))
    assert new_w * new_h <= small_cap
    assert "256x256" in msg  # original dims reported


def test_normal_image_not_downscaled(tmp_path):
    img = tmp_path / "small.png"
    Image.new("RGB", (64, 64), color=(10, 20, 30)).save(img)
    result = _run(img, tmp_path / "out")
    assert result["status"] == "ok"
    assert not any("downscaled" in w.lower() for w in result["warnings"])


# --- consistent shape: every error path matches a successful run ----------

def test_all_error_paths_have_success_key_set(tmp_path):
    expected = _success_keys(tmp_path)

    cases = {}

    # nonexistent
    cases["missing"] = _run(tmp_path / "nope.png", tmp_path / "o1")

    # zero-byte
    empty = tmp_path / "empty.png"
    empty.write_bytes(b"")
    cases["empty"] = _run(empty, tmp_path / "o2")

    # corrupted
    corrupt = tmp_path / "corrupt.png"
    corrupt.write_bytes(b"\x89PNG\r\n\x1a\n" + b"garbage" * 4)
    cases["corrupt"] = _run(corrupt, tmp_path / "o3")

    # internal error
    img = tmp_path / "ok2.png"
    Image.new("RGB", (16, 16), color=(90, 90, 90)).save(img)
    with patch("depth_pipeline.inference.infer_depth",
               side_effect=RuntimeError("boom")), \
         patch("depth_pipeline.inference.get_last_inference_ms", return_value=1.0):
        cases["internal"] = process_image(
            str(img), str(tmp_path / "o4"), target_res=33
        )

    for name, result in cases.items():
        assert result["status"] == "error", name
        assert set(result.keys()) == expected, (
            f"{name}: key set {set(result.keys()) ^ expected} differs from success"
        )


# --- warmup: idempotent, never raises -------------------------------------

def test_warmup_is_idempotent_and_safe():
    from depth_pipeline import run as run_mod

    # Reset the module flag so we exercise the real first-call path.
    run_mod._warmed_up = False
    calls = []

    def _count(image):
        calls.append(1)
        return _fake_infer(image)

    with patch("depth_pipeline.inference.infer_depth", side_effect=_count):
        assert run_mod.warmup() is True
        assert run_mod.warmup() is True  # second call is a no-op

    # Only the first call actually ran inference.
    assert len(calls) == 1


def test_warmup_never_raises_on_failure():
    from depth_pipeline import run as run_mod

    run_mod._warmed_up = False
    with patch("depth_pipeline.inference.infer_depth",
               side_effect=RuntimeError("no weights")):
        assert run_mod.warmup() is False
    run_mod._warmed_up = False  # leave module state clean for other tests


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-v"]))

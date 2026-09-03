"""Tests for depth_pipeline.metrics.evaluate.

The central claim these tests defend is the module docstring's warning: RMSE
after scale+shift alignment can look fine even when the prediction carries no
real elevation signal, so pearson_r (and slope_rmse) must be reported too.
"""
import numpy as np

from depth_pipeline.metrics import evaluate


def test_perfectly_correlated_scores_well():
    rng = np.random.default_rng(0)
    pred = rng.random((64, 64)).astype(np.float32)
    # ref is an exact affine function of pred plus tiny noise: alignment should
    # recover it almost perfectly.
    ref = (2.0 * pred + 5.0 + rng.normal(0, 1e-3, pred.shape)).astype(np.float32)

    out = evaluate(pred, ref)

    assert out["pearson_r"] > 0.95
    assert out["rmse_m"] < 0.05  # small after alignment
    assert out["mae_m"] < 0.05
    # The fit should recover roughly a=2, b=5.
    assert abs(out["fit_scale"] - 2.0) < 0.05
    assert abs(out["fit_shift"] - 5.0) < 0.05
    assert out["n_valid_px"] == 64 * 64


def test_uncorrelated_pred_is_caught_despite_reasonable_rmse():
    # This test exists specifically to prove the "RMSE can be gamed" claim in
    # the module docstring. pred and ref are independent random fields with a
    # similar spread, so after scale+shift alignment the RMSE lands in a
    # deceptively "reasonable" range -- yet pearson_r stays near zero, exposing
    # that the prediction tracks no real elevation. That is the whole point:
    # RMSE alone would pass this; pearson_r does not.
    rng = np.random.default_rng(1)
    pred = rng.random((128, 128)).astype(np.float32)
    ref = rng.random((128, 128)).astype(np.float32)

    out = evaluate(pred, ref)

    assert out["pearson_r"] < 0.3
    # RMSE is still "small" in absolute terms (both fields are ~[0,1]), which is
    # exactly why it must never be reported without pearson_r alongside it.
    assert out["rmse_m"] < 0.5


def test_shape_mismatch_is_auto_resampled():
    rng = np.random.default_rng(2)
    pred = rng.random((64, 64)).astype(np.float32)
    # ref at a different resolution -- evaluate must resize it, not crash.
    ref = rng.random((100, 90)).astype(np.float32)

    out = evaluate(pred, ref)

    assert out["n_valid_px"] == 64 * 64
    assert np.isfinite(out["rmse_m"])
    assert np.isfinite(out["pearson_r"])


def test_valid_mask_excludes_nodata():
    rng = np.random.default_rng(3)
    pred = rng.random((32, 32)).astype(np.float32)
    ref = (3.0 * pred - 1.0).astype(np.float32)
    # Poison a corner with nodata and mask it out; the fit must ignore it.
    ref[:8, :8] = -32768.0
    mask = np.ones(pred.shape, dtype=bool)
    mask[:8, :8] = False

    out = evaluate(pred, ref, valid_mask=mask)

    assert out["n_valid_px"] == 32 * 32 - 8 * 8
    assert out["pearson_r"] > 0.95
    assert abs(out["fit_scale"] - 3.0) < 0.05

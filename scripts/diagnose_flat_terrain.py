"""Diagnostic script: quantify the contribution of SRTM low-pass vs nDSM
in the composite calibration, and check raw model output variance.

Usage: python scripts/diagnose_flat_terrain.py backend/data/samples/delhi_urban.tif
"""
import sys, os, time
import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from depth_pipeline.inference import infer_depth, MODEL_DIR, MODEL_ID
from depth_pipeline.geospatial.srtm import align_srtm
from depth_pipeline.geospatial.geotiff import read_georef
from depth_pipeline.geospatial.calibrate import (
    calibrate_composite, calibrate_to_srtm, _COMPOSITE_LOWPASS_SIGMA,
    _finite_pairs, _SRTM_NODATA
)
from depth_pipeline.postprocess import postprocess
from depth_pipeline.export import resize_elevation01
import rasterio

def load_image(path):
    with rasterio.open(path) as ds:
        # Read as RGB (bands 1,2,3) → HWC
        bands = ds.read([1, 2, 3])  # shape: (3, H, W)
        return np.transpose(bands, (1, 2, 0))  # → (H, W, 3)

def print_stats(name, arr):
    finite = arr[np.isfinite(arr)]
    if finite.size == 0:
        print(f"  {name}: ALL NaN/Inf")
        return
    print(f"  {name}: range=[{finite.min():.4f}, {finite.max():.4f}], "
          f"span={finite.max()-finite.min():.4f}, "
          f"std={finite.std():.4f}, mean={finite.mean():.4f}")

def diagnose(image_path):
    print(f"\n{'='*70}")
    print(f"DIAGNOSTIC: {image_path}")
    print(f"Model: {MODEL_ID}  (dir={MODEL_DIR})")
    print(f"{'='*70}\n")

    # Load image
    source_rgb = load_image(image_path)
    print(f"Image size: {source_rgb.shape}")

    # --- CHECK 2: Raw model output ---
    print(f"\n--- CHECK 2: Raw model output (before postprocess/calibration) ---")
    elevation01 = infer_depth(source_rgb)
    print_stats("raw elevation01", elevation01)

    # After postprocess
    cleaned = postprocess(elevation01, source_rgb)
    elevation01_pp = cleaned["elevation"]
    print_stats("post-processed elevation01", elevation01_pp)

    # Resize to 1025
    target_res = 1025
    elev_resized = resize_elevation01(elevation01_pp, target_res)
    print_stats("resized elevation01", elev_resized)

    # --- Geo/SRTM alignment ---
    print(f"\n--- SRTM alignment ---")
    georef = read_georef(image_path)
    print(f"  Georeferenced: {georef['is_georeferenced']}, CRS: {georef.get('crs')}")
    if not georef["is_georeferenced"]:
        print("  SKIP: not georeferenced")
        return

    srtm = align_srtm(
        bbox=georef["bbox"],
        dst_crs=georef["crs"],
        dst_shape=tuple(int(n) for n in elev_resized.shape),
    )
    if not srtm["ok"] or srtm.get("elevation_m") is None:
        print(f"  SKIP: SRTM alignment failed: {srtm.get('warning')}")
        return
    srtm_elev = srtm["elevation_m"]
    print_stats("SRTM raw elevation", srtm_elev)

    # --- CHECK 1: Composite calibration component breakdown ---
    print(f"\n--- CHECK 1: Composite calibration component breakdown ---")
    print(f"  Gaussian sigma = {_COMPOSITE_LOWPASS_SIGMA}")

    import cv2

    srtm_arr = np.asarray(srtm_elev, dtype=np.float64)
    nodata_mask = ~np.isfinite(srtm_arr) | (srtm_arr <= _SRTM_NODATA + 1.0)
    srtm_filled = srtm_arr.copy()
    if nodata_mask.any():
        srtm_filled[nodata_mask] = float(np.nanmedian(srtm_arr[~nodata_mask]))

    ksize = int(_COMPOSITE_LOWPASS_SIGMA * 4) | 1
    srtm_lowpass = cv2.GaussianBlur(
        srtm_filled.astype(np.float32), (ksize, ksize), _COMPOSITE_LOWPASS_SIGMA
    ).astype(np.float64)

    ndsm_ref = srtm_filled - srtm_lowpass

    print_stats("SRTM low-pass (terrain base)", srtm_lowpass)
    print_stats("nDSM reference (SRTM_raw - SRTM_lowpass)", ndsm_ref)
    print(f"\n  ** SRTM low-pass range: {srtm_lowpass.max()-srtm_lowpass.min():.2f}m")
    print(f"  ** nDSM reference range: {ndsm_ref.max()-ndsm_ref.min():.2f}m")
    print(f"  ** SRTM low-pass std: {srtm_lowpass[np.isfinite(srtm_lowpass)].std():.4f}m")
    print(f"  ** nDSM reference std: {ndsm_ref[np.isfinite(ndsm_ref)].std():.4f}m")

    # Run composite calibration
    cal_comp = calibrate_composite(elev_resized, srtm_elev)
    print(f"\n  Composite calibration ok: {cal_comp['ok']}")
    if cal_comp["ok"]:
        print(f"    R2={cal_comp['r2']:.4f}, RMSE={cal_comp['rmse_m']:.2f}m")
        print(f"    coefficients={cal_comp['coefficients']}, intercept={cal_comp['intercept']:.4f}")
        elev_comp = cal_comp["elevation_m"]
        print_stats("composite DSM output", elev_comp)

        # Break down: how much of final variance comes from each term?
        rel = np.asarray(elev_resized, dtype=np.float64)
        finite_rel = np.isfinite(rel)
        from sklearn.linear_model import LinearRegression
        lr = LinearRegression()
        finite_both = np.isfinite(rel) & np.isfinite(ndsm_ref) & ~nodata_mask
        lr.fit(rel[finite_both].reshape(-1,1), ndsm_ref[finite_both])
        ndsm_calibrated = np.full(rel.shape, 0.0, dtype=np.float64)
        ndsm_calibrated[finite_rel] = lr.predict(rel[finite_rel].reshape(-1,1))

        print_stats("calibrated nDSM (model contribution)", ndsm_calibrated)
        srtm_lp_std = srtm_lowpass[np.isfinite(srtm_lowpass)].std()
        ndsm_cal_std = ndsm_calibrated[np.isfinite(ndsm_calibrated)].std()
        print(f"\n  ** Ratio of nDSM_std / SRTM_lowpass_std = {ndsm_cal_std / srtm_lp_std:.4f}")
        srtm_lp_range = srtm_lowpass.max()-srtm_lowpass.min()
        ndsm_range = ndsm_calibrated.max()-ndsm_calibrated.min()
        print(f"  ** Ratio of nDSM_range / SRTM_lowpass_range = {ndsm_range / srtm_lp_range:.4f}")

    # Run global calibration for comparison
    cal_global = calibrate_to_srtm(elev_resized, srtm_elev)
    print(f"\n  Global calibration ok: {cal_global['ok']}")
    if cal_global["ok"]:
        print(f"    R2={cal_global['r2']:.4f}, RMSE={cal_global['rmse_m']:.2f}m")
        print(f"    coefficients={cal_global['coefficients']}, intercept={cal_global['intercept']:.4f}")
        elev_global = cal_global["elevation_m"]
        print_stats("global DSM output", elev_global)

    # --- CHECK 3: Smoothing impact ---
    print(f"\n--- CHECK 3: Smoothing stack analysis ---")
    print_stats("pre-postprocess elevation01 (raw)", elevation01)
    print_stats("post-postprocess elevation01", elevation01_pp)
    diff = np.abs(elevation01 - elevation01_pp)
    print(f"  Smoothing effect: mean absolute change = {diff.mean():.6f}")
    print(f"  Smoothing effect: max absolute change = {diff.max():.6f}")
    print(f"  Pre-smooth std = {elevation01[np.isfinite(elevation01)].std():.6f}")
    print(f"  Post-smooth std = {elevation01_pp[np.isfinite(elevation01_pp)].std():.6f}")
    print(f"  Std ratio (post/pre) = {elevation01_pp.std()/elevation01.std():.4f}")
    print(f"\n  Unity-side smoothing: SMOOTH_PASSES=1 (3x3 box blur, 1 pass)")
    print(f"  This is applied AFTER calibration, on the final uint16 heightmap.")

    # Which calibration was selected?
    print(f"\n--- SELECTION LOGIC ---")
    if cal_comp["ok"] and cal_global["ok"]:
        if cal_comp["rmse_m"] < cal_global["rmse_m"]:
            print(f"  SELECTED: composite (RMSE {cal_comp['rmse_m']:.2f} < {cal_global['rmse_m']:.2f})")
        else:
            print(f"  SELECTED: global (RMSE {cal_global['rmse_m']:.2f} <= {cal_comp['rmse_m']:.2f})")
    elif cal_comp["ok"]:
        print(f"  SELECTED: composite (global failed)")
    else:
        print(f"  SELECTED: global (composite failed)")

    print(f"\n{'='*70}")
    print("DONE")
    print(f"{'='*70}\n")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        # Try Delhi sample
        sample = os.path.join("backend", "data", "samples", "delhi_urban.tif")
        if os.path.isfile(sample):
            diagnose(sample)
        else:
            print("Usage: python scripts/diagnose_flat_terrain.py <image_path>")
            sys.exit(1)
    else:
        for path in sys.argv[1:]:
            diagnose(path)

#!/usr/bin/env python3
"""Download SRTM GL1 clips for the demo regions in regions.json.

Primary: OpenTopography globaldem API (SRTMGL1).
  export OPENTOPOGRAPHY_API_KEY=...   (optional; falls back to OT's public demo key)

Fallback: public AWS SRTM 1° HGT (same GL1 product), clipped to the region bbox.

Usage:
    python scripts/download_srtm.py
"""

from __future__ import annotations

import gzip
import json
import math
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TILE_DIR = ROOT / "backend" / "data" / "srtm_tiles"
CATALOG = TILE_DIR / "regions.json"
OT_URL = "https://portal.opentopography.org/API/globaldem"
# Documented as the Swagger default in OpenTopography's OpenAPI spec.
OT_DEMO_KEY = "demoapikeyot2022"
SKADI = "https://s3.amazonaws.com/elevation-tiles-prod/skadi"


def _ot_key() -> str:
    return os.environ.get("OPENTOPOGRAPHY_API_KEY") or os.environ.get(
        "OPENTOPO_API_KEY", OT_DEMO_KEY
    )


def _download(url: str, timeout: int = 180) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": "DepthWizard/1.0"})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def _looks_like_tiff(payload: bytes) -> bool:
    return payload[:4] in (b"II*\x00", b"MM\x00*")


def fetch_opentopo(west: float, south: float, east: float, north: float) -> bytes:
    params = {
        "demtype": "SRTMGL1",
        "south": f"{south:.6f}",
        "north": f"{north:.6f}",
        "west": f"{west:.6f}",
        "east": f"{east:.6f}",
        "outputFormat": "GTiff",
        "API_Key": _ot_key(),
    }
    url = OT_URL + "?" + urllib.parse.urlencode(params)
    payload = _download(url)
    if not _looks_like_tiff(payload):
        preview = payload[:200].decode("utf-8", errors="replace")
        raise RuntimeError(f"OpenTopography did not return a GeoTIFF: {preview!r}")
    return payload


def _skadi_name(lat: float, lon: float) -> tuple[str, str]:
    lat_i = int(math.floor(lat))
    lon_i = int(math.floor(lon))
    ns = "N" if lat_i >= 0 else "S"
    ew = "E" if lon_i >= 0 else "W"
    folder = f"{ns}{abs(lat_i):02d}"
    name = f"{ns}{abs(lat_i):02d}{ew}{abs(lon_i):03d}"
    return folder, name


def fetch_skadi_clip(
    west: float, south: float, east: float, north: float, dest: Path
) -> None:
    import numpy as np
    import rasterio
    from rasterio.transform import Affine
    from rasterio.windows import from_bounds

    # Demo clips are smaller than 1° and do not cross a tile boundary.
    folder, name = _skadi_name((south + north) / 2.0, (west + east) / 2.0)
    url = f"{SKADI}/{folder}/{name}.hgt.gz"
    raw = gzip.decompress(_download(url, timeout=180))
    n = int(math.sqrt(len(raw) // 2))
    elev = np.frombuffer(raw, dtype=">i2").reshape(n, n)
    lat0 = int(math.floor((south + north) / 2.0))
    lon0 = int(math.floor((west + east) / 2.0))
    transform = Affine(1.0 / (n - 1), 0, lon0, 0, -1.0 / (n - 1), lat0 + 1)
    tmp = dest.parent / f".{name}.full.tif"
    profile = {
        "driver": "GTiff",
        "height": n,
        "width": n,
        "count": 1,
        "dtype": "int16",
        "crs": "EPSG:4326",
        "transform": transform,
        "nodata": -32768,
    }
    try:
        with rasterio.open(tmp, "w", **profile) as src_w:
            src_w.write(elev.astype("int16"), 1)
        with rasterio.open(tmp) as src:
            window = from_bounds(west, south, east, north, transform=src.transform)
            window = window.round_offsets().round_lengths()
            data = src.read(1, window=window)
            win_transform = src.window_transform(window)
            out_profile = src.profile.copy()
            out_profile.update(
                {
                    "height": int(data.shape[0]),
                    "width": int(data.shape[1]),
                    "transform": win_transform,
                    "compress": "lzw",
                }
            )
            dest.parent.mkdir(parents=True, exist_ok=True)
            with rasterio.open(dest, "w", **out_profile) as dst:
                dst.write(data, 1)
    finally:
        tmp.unlink(missing_ok=True)


def download_region(region: dict) -> Path:
    west, south, east, north = region["bbox"]
    dest = TILE_DIR / region["filename"]
    dest.parent.mkdir(parents=True, exist_ok=True)
    try:
        payload = fetch_opentopo(west, south, east, north)
        dest.write_bytes(payload)
        return dest
    except (RuntimeError, urllib.error.URLError, TimeoutError, OSError) as ot_exc:
        print(f"  OpenTopography failed ({ot_exc}); trying AWS SRTM HGT clip")
        fetch_skadi_clip(west, south, east, north, dest)
        return dest


def main() -> int:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    print(f"tile dir: {TILE_DIR}")
    for region in catalog["regions"]:
        print(f"- {region['id']} ({region['terrain']}) {region['bbox']}")
        try:
            path = download_region(region)
        except Exception as exc:
            print(f"  FAILED: {exc}")
            return 1
        size_kb = path.stat().st_size / 1024.0
        print(f"  wrote {path.name} ({size_kb:.1f} KiB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

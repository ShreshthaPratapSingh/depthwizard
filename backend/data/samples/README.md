# Demo Sample Images

Drop satellite/aerial images here matching the entries in `samples.json`.

The backend's `GET /samples` endpoint returns only entries whose image file
is actually present on disk, so missing files are harmless.

## Expected files

| filename                | format              | notes                                         |
|-------------------------|---------------------|-----------------------------------------------|
| `delhi_urban.tif`       | GeoTIFF (EPSG:4326) | Covers bbox [77.18, 28.58, 77.26, 28.66]      |
| `mussoorie_hilly.tif`   | GeoTIFF (EPSG:4326) | Covers bbox [78.04, 30.42, 78.12, 30.50]      |
| `chennai_coastal.tif`   | GeoTIFF (EPSG:32644)| Reprojects to ~[80.26, 13.02, 80.32, 13.08]   |

These three GeoTIFFs are committed so a fresh clone has a working demo
fallback (`GET /samples` / `POST /process-sample/{id}`).

## Sourcing images

GeoTIFFs: export from Google Earth Engine, USGS EarthExplorer, or Bhuvan
(ISRO). The bounding boxes above match the SRTM regions in
`backend/data/srtm_tiles/regions.json`.

## Git tracking

`samples.json`, this `README.md`, `.gitkeep`, and the three named demo
GeoTIFFs are tracked. Other images dropped in this folder stay gitignored.

# Demo Sample Images

Drop satellite/aerial images here matching the entries in `samples.json`.

The backend's `GET /samples` endpoint returns only entries whose image file
is actually present on disk, so missing files are harmless.

## Expected files

| filename              | format             | notes                                    |
|-----------------------|--------------------|------------------------------------------|
| `delhi_urban.tif`     | GeoTIFF (EPSG:4326)| Covers bbox [77.18, 28.58, 77.26, 28.66] |
| `mussoorie_hilly.tif` | GeoTIFF (EPSG:4326)| Covers bbox [78.04, 30.42, 78.12, 30.50] |
| `chennai_coastal.jpg` | JPEG (no georef)   | Any coastal satellite crop works          |

## Sourcing images

- GeoTIFFs: export from Google Earth Engine, USGS EarthExplorer, or Bhuvan
  (ISRO). The bounding boxes above match the SRTM regions in
  `backend/data/srtm_tiles/regions.json`.
- Plain JPEG: any satellite screenshot or aerial photo works for the
  non-georeferenced demo path.

## Git tracking

Image files in this directory are gitignored (large binaries). Only
`samples.json`, this `README.md`, and `.gitkeep` are committed.

# Worldwide geographic workload

This opt-in tool fills a separate local Immich 3.3.1 library with generated PNGs and GPS coordinates. It complements unit tests and the eight-asset database-contract harness; neither of those libraries is modified.

## Dataset

The default public workload contains 5,000 assets:

| Group | Assets | Purpose |
|---|---:|---|
| Country-balanced GeoNames cities | 3,000 | Broad geographic and naming coverage |
| Repeated coordinates and nearby clusters | 1,000 | Typical trip/home density and repeated polygon lookups |
| Airport points and nearby offsets | 250 | Geometry containment versus fallback; compare airport matching on/off |
| Territories, small states, reported issues, and nearby offsets | 250 | Small polygons, identity mapping, city-profile choices, and tolerance |
| Uniform-area global samples | 500 | Oceans, remote land, poles, and negative/no-match cases |

The generator may append up to 2,000 real GPS samples read from production, for 7,000 total assets. Only latitude/longitude is persisted from those records: no production IDs, filenames, timestamps, people, or media files.

All generated datasets, private coordinates, accounts, API keys, and upload checkpoints remain in the primary checkout's ignored `.local/geographic-workload/`. Do not commit or share that directory. Preserve it together with its named Docker volumes. Public GeoNames city data is [CC BY 4.0](https://download.geonames.org/export/dump/readme.txt); the dataset summary records its URL, SHA-256, retrieval/generation time, and [GeoNames attribution](https://www.geonames.org).

Issue-derived cases include:

- [#2](https://github.com/immich-reversegeo/immich-reversegeo/issues/2): Munich/Salo district choices and Lyon-Bron airport versus commune
- [#10](https://github.com/immich-reversegeo/immich-reversegeo/issues/10): Chokoloskee and Florida wetlands/rural fallbacks
- [#16](https://github.com/immich-reversegeo/immich-reversegeo/issues/16) and [#17](https://github.com/immich-reversegeo/immich-reversegeo/issues/17): Farsta and Skogås label choices
- [#19](https://github.com/immich-reversegeo/immich-reversegeo/issues/19): Hong Kong, covered by the territory fixture catalog
- [#5](https://github.com/immich-reversegeo/immich-reversegeo/issues/5): Vatican City, using the GeoNames capital point rather than downloading the attached personal photo
- [Immich #31157](https://github.com/immich-app/immich/issues/31157): Rod an der Weil versus a neighboring municipality

`required-country` applies to maintained territory/control anchors. Other city names, GeoNames country labels, offsets, and issue reports are references for manual review, not ground truth. Global random points are not assumed to be oceans. Disputed areas and exact city labels require an explicit policy decision, not a blanket pass/fail comparison against another geocoder.

## Run

Requires local Docker, PowerShell 7, Git, and .NET. Ports bind to loopback only:

- Immich: `http://127.0.0.1:18283`
- PostgreSQL: `127.0.0.1:18433`, database `immich`, user `postgres`

```powershell
# Optional: save a read-only production key in .local/geographic-workload/production-api-key.txt.
# Save its base URL in .local/geographic-workload/production-source.json:
# {"baseUrl":"http://your-immich-server:2283"}
# The importer currently supports the verified Immich 3.3.1 API.
# It uses asset.read and bounded random metadata searches; it never mutates production.
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Import
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Generate
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Seed
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action StartApp
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Report
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Reset
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action StopApp
pwsh -NoProfile -File tests/GeographicWorkload/Run-GeographicWorkload.ps1 -Action Stop
```

`Seed` verifies a dedicated database marker before account creation or writes. It uploads unique generated files through the actual Immich API, checkpoints every upload, waits for metadata extraction, then updates only saved fixture IDs. Tied `createdAt` values exercise keyset pagination. Re-run Seed after an interrupted upload to resume. The manifest becomes immutable once seeding starts.

The local login email is `geographic-workload@immich-reversegeo.test`; its password is in `account.json`. PostgreSQL credentials are in `bulk.env`. Do not copy production credentials into the local database settings. Immich machine learning and its built-in reverse geocoding are disabled for this synthetic stack.

`StopApp` stops only the recorded dedicated ReverseGeo process after checking its command line. `Stop` stops the dedicated Docker stack and preserves its volumes.

`Reset` clears location names only for checkpointed fixture IDs, preserving GPS. For a repeated ReverseGeo pass, also clear skipped-asset tracking in the dedicated ReverseGeo data directory. Never point a production ReverseGeo process at this test library accidentally.

## Processing and measurements

`StartApp` starts a separate ReverseGeo UI at `http://127.0.0.1:18222`, pointed only at the bulk-test database. It uses `reversegeo-config/` and `reversegeo-data/` inside the local state directory, with automatic scheduling disabled on first setup. Use Lookup first on the labeled issue fixtures and compare airport matching and GADM settings before running the full pass.

For each run, record:

- exact app revision, source data release, settings, and dataset SHA-256
- normal/low-memory mode and worker/batch limits
- elapsed time, processed/skipped/error counts, peak RSS/container memory, live managed heap, and memory after 1/5/15 idle minutes
- cold caches versus warm caches, and fresh process versus repeated processing in the same process
- output differences on required-country cases and a reviewed subset of city/reference cases

Start with a few known countries before the full worldwide pass. First-time administrative cache creation across hundreds of countries can consume substantial disk/network capacity. Seeding the library does not download those caches or prove that every location name is correct. Normal CI compiles/tests code; it does not run this workload against production or launch these stacks.

# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

## Unreleased

- Fixed scheduler startup failures for cron expressions whose next run is beyond the timer-supported delay range. Scheduled waits now use cancellable intervals of at most one day and recheck the UTC deadline before starting processing.
- Added country- and optional state-scoped city name replacements, with shared final-stage matching in processing and Lookup, state-specific precedence, duplicate validation and persistent configuration.

- Added `DB_HOSTNAME` support for shared Immich environment settings, retaining nonblank `DB_HOST` precedence and the `database` default when both values are unset or blank.
- Display dashboard and log timestamps in the browser's timezone. Preserve structured UTC log timestamps and include dates in UTC log downloads.

## 2026-08-23

- Fixed repeated `System.OutOfMemoryException` container kills during geocoding. Bundled country containment now resolves in two passes: `PreparedGeometry.Covers` first, with the boundary tolerance applied only when nothing contains the point. Previously every candidate that failed `Covers` fell through to `Geometry.Distance`, and because country bounding boxes overstate their footprint (antimeridian crossings, distant overseas territories), most lookups ran a full brute-force `DistanceOp` against several complete country boundaries.
- Replaced the `Geometry.Distance` boundary tolerance with a small rectangle intersection in the bundled country lookup, `OvertureDataAccess.TryGeometryContains`, and `GadmDivisionsService`. NTS `DistanceOp` computes an exact minimum, so it cannot short-circuit at the tolerance, and it copies every boundary ring into a fresh `Coordinate[]` per call — large arrays landing on the non-compacted large object heap. Rectangle intersection reuses the prepared geometry index instead. Measured against the bundled dataset, a Berlin lookup drops from 2.71 ms and 2,596 KB allocated to 0.02 ms and 2.2 KB.
- Added `mem_limit: 2g` to both Compose files to set a total container memory budget. The app retains the .NET 10 defaults: Server GC with background collection and Dynamic Adaptation To Application Sizes (DATAS). No explicit GC overrides are required. Large country-cache downloads and high parallelism may require a higher container limit.
- Exact geometry containment now always outranks a within-tolerance neighbour in bundled country selection. Both previously set `GeometryContainsPoint`, so a neighbouring country with a tighter bounding box could win over a true containment.
- Added `BundledCountryLookupTests`, which builds a synthetic two-country database so the containment behaviour is covered without the LFS-backed bundled dataset, including an allocation guard against regressing to full-boundary distance scans.

## 2026-04-12

- Added optional GADM administrative-area support with per-country on-demand downloads, local SQLite cache export, Kosovo code mapping, and curated split-territory fallback families.
- Split cache ownership into source-specific services for Overture and GADM, with a shared administrative-area resolver used by background processing.
- Added processing settings for enabling GADM, preferring GADM over cached Overture divisions, and enabling GADM territory fallback packages.
- Updated Lookup to show GADM diagnostics, cache status, source comparison, and live lookup progress while cache downloads and queries run.
- Added GADM cache management to the Data area, including a merged sortable/filterable administrative cache table with source, country, row count, version/release, size, downloaded time, delete, and re-download actions.
- Added GADM-specific unit and integration test coverage in a dedicated `ImmichReverseGeo.Gadm.Tests` project, plus a heavyweight all-country GADM import test marked as `Integration` and `Performance`.
- Added public Data Sources documentation covering Overture, GADM, live Overture Places diagnostics, source purpose, storage behavior, and license constraints.
- Renamed the app UI entry from Reset Geo Data to Reset Immich Geo Data to make the database impact clearer.

## 2026-04-03

- Added the City Resolver page for reviewing bundled defaults, changing the global profile, and setting country-specific city resolver overrides.
- Added bundled city resolver profile defaults plus configuration and processing support for applying user overrides on top of bundled country profiles.

## 2026-04-01

- Added a processing setting to disable airport infrastructure lookup when you prefer administrative city names.
- Added the Reset Geo Data page for clearing reverse geo `city`, `state`, and `country` values in Immich by all assets, selected asset GUIDs, or matching location values before reprocessing.

## 2026-03-29

- Initial Version.

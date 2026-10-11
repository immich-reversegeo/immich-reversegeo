# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

## Unreleased

- Refresh bundled airport infrastructure from Overture 2026-03-18.0 to 2026-09-23.1. Preserve the existing schema and airport resolution policy.

- Expanded the bundled Overture country artifact to include `dependency` boundaries and canonical Alpha-2, Alpha-3, and display-name identities.
- Fixed offline country detection for Hong Kong and added validated distinct-territory coverage for Macao, Greenland, the Faroe Islands, the Crown Dependencies, selected US and UK territories, Aruba, Curaçao, the Åland Islands, Réunion, French Polynesia, and New Caledonia.
- Preserve older bundled/custom databases without Alpha-3 columns through catalog-based inference; keep malformed modern identities visible instead of substituting an overlapping sovereign. Return the catalog's canonical Alpha-3 as the cache key after validation. No database migration is performed.
- Prefer supported dependency boundaries over overlapping sovereign countries in bundled country lookup.
- Add territory-resolution and downstream Overture/GADM routing tests, plus country-index size, memory, initialization, and warm-lookup performance budgets.

- Route HKG/MAC GADM territory fallbacks to a shared CHN cache without standalone package requests. Preserve resolved country identity and require named geometry matches in processing and Lookup for these shared-dataset results, excluding GADM's deeper NA placeholder subdivisions.

- Query the bundled country spatial index using the existing tolerance envelope so nearby points outside source bounding boxes can resolve. Preserve prepared-geometry exact matching before tolerance and actual bounding-box containment ranking.

- Make equal Overture division candidates deterministic: rank known administrative levels before missing levels and use ordinal source IDs for otherwise equal candidates. Preserve containment, subtype, territorial, and bounding-box-area priorities.

- Return structured bundled-country lookup outcomes and source IDs; distinguish spatial misses from unresolved identities in Lookup and processing progress. Preserve the shipped database and existing spatial selection.

- Added explicit Dashboard processing eligibility and links to preview/reset workflows.
- Added read-only, cancellable location previews by asset GUID, existing city/state/country, or whole library, with keyset pagination, explicit selections retained across pages, per-asset failure reporting, and current-versus-proposed comparison. Cancelled previews can resume after the last checked asset.
- Shared final administrative/airport/fallback resolution between preview and background processing; preview does not write Immich locations or alter skipped assets.

- Enhance the country exporter to include dependencies, canonical catalog identities, and source traceability. Validate generated artifacts and shared coordinate coverage before replacement, and add a read-only validation mode. This tooling change does not replace shipped country data or change runtime resolution.

- Use the web project Version as the release version source and stamp app artifacts with the source SHA, include bundled runtime geodata, validate package contents and SQLite headers, emit SHA-256 checksums and signed provenance through official GitHub actions, and enforce strict release docs builds.
- Add a portable country identity catalog with explicit display names, Alpha-2/Alpha-3 mappings, and source aliases. CountryCodeService uses the catalog for country options; retain legacy flat mapping-file support. Geographic boundaries and containment selection are unchanged.

- Match Nearby Places include/exclude terms at snake-case category boundaries in both SQL and diagnostics, preventing `port` from matching `sports` or `transportation` while retaining compound types such as `art_museum` and `train_station`.

- Follow the system colour preference on the Zensical website until a theme is chosen, improve light/dark link and button contrast, and use solid theme-aware cards and visible keyboard focus.
- Reworked optional live Places lookup as Nearby Places investigation: bounded distance-ordered suggestions and an excluded preview, with shared eligibility diagnostics, existence-confidence wording, and reliable JSON source attribution. Filter useful candidates and the true search radius before applying result limits; keep the airport and administrative processing paths unchanged.

- Include Overture source aliases CP, XE, XS, and XJ in their canonical FRA, BES, and SJM administrative caches. Store the exported source-code set in cache metadata and refresh only affected older caches; retain ordinary-country caches and keep the old file until a replacement succeeds.

- Updated live Overture Places queries to use `taxonomy.primary` with `basic_category` fallback after removal of `categories` in schema v2.
- Centralized STAC release discovery for live lookups, country cache exports, and bundled-data exports; reject malformed, future, and expired release paths instead of using a fixed February release.
- Added optional division-owner IDs and default hierarchy data to new administrative caches. Existing cache schemas remain readable; adding hierarchy metadata does not force a migration or refresh.
- Shared hierarchy-aware administrative selection between processing and Lookup, preserving city profile ordering and using geometry-matched areas to recover parent cities and consistent states.
- Added regression coverage for Czech/Slovak locality-to-localadmin and Vietnamese microhood-to-neighborhood changes, plus focused live-release checks.

- Fixed scheduler startup failures for cron expressions whose next run is beyond the timer-supported delay range. Scheduled waits now use cancellable intervals of at most one day and recheck the UTC deadline before starting processing.
- Publish full Docker version tags on GitHub release publication, promote the built digest to `latest` only for GitHub's designated latest stable release through a serialized queue, and retain `edge`, SHA tags, and the explicit manual promotion option. Validate prerelease identifiers including consecutive and trailing hyphens.
- Publish master Docker images through a reusable workflow only after CI build, test, and Docker build checks succeed for the same commit. Each successful master push publishes edge and SHA tags.

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

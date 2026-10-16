# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

## Unreleased

- Serialize the full Overture/GADM country cache download, export, validation, and replacement lifecycle through one shared gate in every memory mode. Keep low-memory query limits separate, make queued builds cancellable, and defer failed initial/incompatible builds for five minutes without retrying per asset. Preserve compatible Overture refresh fallback and show explicit queued/deferred status.

- Build localized Overture parent hierarchies once per division before joining polygon payloads, and stream the staged country export into SQLite. Preserve staged download progress, cancellation, hierarchy ordering, language fallbacks, and areas without hierarchies. Add constrained-memory Parquet and production-exporter regression tests.

- Keep Lookup's write eligibility aligned with processing after mandatory Overture cache preparation fails: show an incomplete result instead of a writable country-name city fallback. Add desktop/mobile browser E2E coverage of language settings, the production Parquet-to-SQLite exporter, correction previews, PostgreSQL writes, cache reuse, failed upgrades, and retry.

- Add `processing.locationNames` with English (existing default), source-primary, and preferred-language policies. Retain primary/common names in Overture cache format 2 and parent hierarchies; choose labels at lookup time across Lookup, previews, and processing. Localize available country names only for the matched country identity. Preserve GADM, bundled-airport, and live Places naming. Older administrative caches rebuild on demand once; switching language afterward reuses the cache and does not rewrite existing Immich metadata.

## 0.2.0 — 2026-10-14

- Automatically refresh outdated Overture administrative caches to the latest discovered release on use. Preserve a compatible previous cache if refresh fails, retry after one hour, and share release discovery across a processing pass or location scan.
- Stage Overture cache preparation through filtered division areas and referenced hierarchy parents before the final SQLite export; expose query phases and exported row counts without treating query estimates as whole-cache completion.
- Repair Docker latest-release promotion and add digest-checked recovery for an already-published stable image without rebuilding.

- Refresh public screenshots from the current UI and document the scan/review/apply quick-start, cache progress, low-memory troubleshooting, result filters, and recovery workflow. Keep the user-facing release summary aligned.
- Improve responsive navigation and location-review layouts with sticky selection actions, keyboard focus on confirmation and return, saved-scan/history filters before pagination, and guarded settings saves. Settings displays UTC schedules, a local next-run preview, and effective processing limits.
- Show whole-scan checked totals, active-time estimates, and exact character-level differences in live and saved reviews; distinguish whitespace, missing values, and empty strings.
- Prefer exact Overture administrative geometry matches over tolerance-only candidates, preserving tolerance as a fallback. Prefer English hierarchy-parent names when available; existing administrative caches can be re-downloaded to use updated names.
- Expose source/country cache-preparation stages and elapsed time, GADM byte-transfer progress, estimated DuckDB Overture query progress, and cache-building row counts. Query percentages do not represent byte transfer or whole-cache completion.
- Add unauthenticated `POST /api/processing/run` with immediate `202 accepted` or `409 busy` responses, shared run/maintenance exclusion, and no queued duplicate pass. Keep deployment access private, as with the web UI.
- Add a default-on Follow logs control and let the log panel fill available page height. Preserve filtered recent-log downloads and structured UTC export timestamps; clarify browser connection recovery and small browser favicons.

- Persist scan checkpoints and resumable status with atomic row saves, restore original selection/settings and progress from Saved Scans, protect concurrent continuation with run ownership tokens, and skip already-recorded asset IDs when resuming. Migrate older reports without losing snapshots; recover interrupted owners on startup.
- Surface automatic difference-limit pauses with an amber status notice and a sticky review banner containing the pause count and Continue Scan action; clear the pause indication when scanning resumes.
- Default live and saved reviews to differences-only within the selected status filter, filter before pagination, and render photo-preview components only for rows with proposed differences.
- Keep live and saved scan reviews actionable after apply: default to unattempted differences, page results by journal status before limiting, refresh and refill after writes, and reject repeated confirmations of handled snapshots. Preserve applied, skipped, pending, uncertain, and undone history through explicit result filters.
- Expand preview into Location Management with compatible legacy routes, exact-snapshot selected apply, per-asset PostgreSQL row locks and stale-value guards, an independent SQLite write-ahead journal, resumable pending work, and metadata-revision-guarded undo. Undo selections are queued atomically; history exposes pending undo and ignores stale navigation loads. Section tabs indicate the current page and change details use separate result cards. Uncertain interrupted writes require review. See [user-facing release notes](docs/website/changelog.md).

## 0.1.0 — 2026-10-12

First versioned release; earlier development milestones remain listed below.

- Reduce bundled country-index memory with streamed geometry loading, disk-backed low-memory lookup, and idle index release (five minutes by default). Add an opt-in bounded country-boundary cache with geometry-size budgeting; low-memory mode takes precedence. These settings do not cap total process memory.

- Add an opt-in point-only airport fallback within 100 m when no administrative city/county resolves. Keep mapped boundary matches preferred and suppress same-name point records when a known boundary is available.

- Require valid Polygon/MultiPolygon coverage for airport boundary matches in bundled and live lookups. By default exclude point-only and non-containing airports, allow a 5 m locally scaled boundary tolerance, and share final location policy with Lookup. Expose boundary availability in diagnostics.

- Add county as the last bundled USA city subtype preference, preserving existing city subtype ordering and smallest-area tie breaking. Explicit user profiles retain precedence.

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

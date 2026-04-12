# Changelog

Technical release notes for Immich ReverseGeo live here.

For a shorter user-facing summary, see [docs/website/changelog.md](./docs/website/changelog.md).

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

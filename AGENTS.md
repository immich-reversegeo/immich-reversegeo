# AGENTS.md

## Project Overview

**Immich ReverseGeo** is a self-hosted companion service (ASP.NET Core + Blazor Server) that improves Immich location metadata. It reads GPS-tagged assets from the Immich PostgreSQL database, resolves country and administrative areas from Overture Maps data, uses bundled Overture airport infrastructure where helpful, and writes city/state/country back to the `asset_exif` table.

## Repo Conventions

- In user-facing copy, use the product name **Immich ReverseGeo**. Keep lowercase `immich-reversegeo` only for technical slugs: URLs, repo names, Docker tags, image names.
- The service defaults to port `8080` in Docker and `5122` for local dev.
- The Docker image name is `immich-reversegeo` (or `ghcr.io/immich-reversegeo/immich-reversegeo`).
- Keep public docs and README copy concise and plain. Avoid hype, filler, and over-marketing language.
- Write public docs for end users and self-hosters, not for contributors or maintainers.
- Prefer plain-language explanations over internal data-model terms. Introduce terms like `division_area`, `subtype`, or `admin_level` only when they are necessary, and explain them in user-facing language.
- Public docs should explain user goals, limits, and recommended workflow first. Technical implementation detail should only appear when it helps the reader make a decision.
- When documenting advanced features, include what the feature can fix, what it cannot fix, and how to validate behavior with the app before changing settings.
- Prefer concrete troubleshooting steps over abstract descriptions. If the recommended workflow is “use Lookup first, inspect the result, then decide,” say that directly.
- Public docs use **Zensical** with the existing `mkdocs.yml` compatibility path, not a separate static-site generator setup. Main public routes should live under the website docs.
- Public website content lives under `docs/website/`. Maintainer-only docs live under `docs/maintainer/`.
- Keep the README product-first. Public setup and product documentation should usually live in `docs/website/` or `CONTRIBUTING.md`, not in the README.
- The target audience for the public website is end users and self-hosters, not contributors. Public setup docs should be Docker-first unless there is a strong reason not to.
- Local developer setup belongs in `CONTRIBUTING.md` or maintainer/developer docs, not in public Getting Started pages.
- When a change affects user-visible behavior, setup, settings, processing expectations, or other public-facing workflows, update the relevant docs in the same change unless the deferral is explicit.
- Keep a technical changelog in `CHANGELOG.md` and a user-facing release summary in `docs/website/changelog.md`. When one is updated for a release, keep the other in sync and link between them.
- Config lives under `/config` at runtime; geodata and caches live under `/data`. These are separate volumes so config (secrets) and data (downloadable/regenerable) are mounted independently.
- Put non-source local output under `_out/` or `localdata/` — both are gitignored. Do not commit them.
- Keep `.planning/` local-only and gitignored. Do not commit planning or spec work.
- Treat untracked local tooling folders such as `.claude/`, `.superpowers/`, `.playwright-mcp/`, `.vs/`, and `localdata/` carefully. Do not commit or clean them up unless explicitly requested.
- Prefer [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) for commit messages. Common types in this repo include `feat`, `fix`, `docs`, `test`, `ci`, `chore`, and `cleanup`.

---

## Code Style

- **Always use braces** for `if`, `else`, `foreach`, `while`, `for` — even single-statement bodies. No braceless one-liners.
- Allman style: opening brace on its own line for method/class bodies; same line is acceptable for short inline catch blocks (`catch (X) { return; }`).
- Expression-body members (`=>`) are fine for trivial one-liners (properties, delegates, simple methods).

## Tech Stack

.NET 10, ASP.NET Core, Blazor Server (Interactive), Npgsql, NetTopologySuite, DuckDB, MSTest SDK on Microsoft.Testing.Platform

---

## Commands

```bash
# Run locally → http://localhost:5122
npm run start

# Stop the running dev app before running tests (Windows locks the exe)
# Run normal tests (excludes Integration and Performance)
npm run test

# Run integration tests explicitly
npm run test:integration

# Build docs / exports
npm run docs:build
npm run export:airports
npm run export:country-divisions

# Build Docker image
docker build -t immich-reversegeo .

# Deploy (add to existing Immich compose)
docker compose up -d
```

Test notes:
- This repo uses MSTest SDK on Microsoft.Testing.Platform v2 via `global.json`.
- Prefer `npm run test` / `npm run test:integration` for normal runs.
- If you call `dotnet test` directly, use `--project <path-to-csproj>` with this repo's MTP setup.
- The root `.runsettings` excludes `Integration` and `Performance` by default; use `integration.runsettings` for explicit integration runs while still excluding `Performance`.
- Run `git lfs pull` before tests or exports that read bundled databases; LFS pointer files produce SQLite 'file is not a database' errors.
- Avoid concurrent test/build runs against the same output directory on Windows; running test executables can also lock rebuild outputs.

---

## Key Architecture

```
src/ImmichReverseGeo.Web/
  Components/
    Layout/          MainLayout.razor, NavMenu.razor
    Pages/           Dashboard.razor, Settings.razor, Data.razor, Lookup.razor, LocationPreview.razor, Logs.razor
  Services/
    ConfigService              Load/save AppConfig; read DB credentials from env vars
    AdministrativeAreaResolverService Shared source-ordering policy for Overture and optional GADM admin results
    CountryCodeService         CountryIdentityCatalog-backed codes, names, and country list helpers
    AssetLocationResolverService Shared final admin/airport/fallback resolution for processing and previews
    ImmichDbRepository         Npgsql queries against Immich PostgreSQL (tables: asset, asset_exif)
    ProcessingBackgroundService Hosted service; runs scheduled and on-demand processing
    ProcessingState            Singleton in-memory run state (counts, log ring buffer, OnChanged event)
    SkippedAssetsRepository    SQLite tracking of assets with no resolvable location
  wwwroot/
    app.css          All styles — dark indigo-night theme. No Bootstrap. Do NOT add scoped .razor.css styles.
    app.js           JS helpers (log download via base64)

src/ImmichReverseGeo.Overture/
  Services/          Overture divisions/places/infrastructure logic and lookup services
  Models/            Overture result/diagnostic records
src/ImmichReverseGeo.Gadm/
  Services/          GADM download/cache/export/lookup logic and country fallback catalog
  Models/            GADM result/diagnostic records
src/ImmichReverseGeo.Legacy/
  Services/          Legacy reference implementations retained out of the active runtime
  data/              Legacy reference data kept out of the active runtime
                     Do not treat this project as part of the active production path unless explicitly reintroduced.
tests/ImmichReverseGeo.Tests/            Web/app tests
tests/ImmichReverseGeo.Overture.Tests/   Overture-specific tests
tests/ImmichReverseGeo.Gadm.Tests/       GADM-specific tests
docs/website/                         Public end-user docs
docs/maintainer/                      Maintainer-only docs
src/ImmichReverseGeo.Web/Dockerfile
                     Multi-stage runtime image with bundled Overture data
docker-compose.yml   Reference snippet; joins immich_default network
```

---

## Key Patterns & Gotchas

**Data directory:** In `Development`, `dataDir` defaults to `./localdata` (relative to CWD). In production it's `/data`. Override with `DATA_DIR` env var. The `./localdata/` folder is gitignored.

**DI lifetime rules (important):** `ProcessingBackgroundService` must be registered as **both** `AddSingleton<T>()` **and** `AddHostedService(sp => sp.GetRequiredService<T>())`. Using only `AddHostedService<T>()` registers the instance solely as `IHostedService`, which breaks concrete-type injection into Blazor pages.

**Bundled Overture data:** Country detection uses the bundled `overture-country-divisions.db`. Airport fallback uses the bundled `overture-airports.db`.

**On-demand Overture caches:** Per-country `division_area` SQLite caches are downloaded on demand under `/data/overture-divisions/{ISO3}.db`.

**Optional GADM caches:** Per-country GADM SQLite caches are downloaded on demand under `/data/gadm-divisions/{ISO3}.db` when GADM administrative areas are enabled or used from Lookup. GADM is non-commercial; keep that license constraint visible in public docs and settings copy.

**DuckDB Azure on Linux:** For Overture Azure blob access in Linux containers, DuckDB's Azure extension should explicitly run `SET azure_transport_option_type='curl'` after `LOAD azure`. This was the real fix for the `Problem with the SSL CA cert (path? access rights?)` error in the local Docker image. Keep that bootstrap centralized and do not remove it casually during refactors.

**Country identity and administrative sources:** Use `CountryIdentityCatalog` for canonical codes, display names, and source aliases. Keep the resolved country identity separate from the administrative dataset; a shared or related country package must not relabel the country written to Immich.

**Bundled country resolver:** Normal mode caches an in-memory spatial index and prepared country/dependency geometries. It releases the index after the configured idle timeout (default five minutes; zero keeps it cached), allowing active lookups to finish. Low-memory mode streams matching polygon parts using a regenerable disk index under `/data`. Preserve exact point coverage, including boundaries, before tolerance and reuse the existing deterministic parent candidate ordering. Keep normal mode's prepared-geometry rectangle intersection instead of full-boundary `Geometry.Distance`. Restart after replacing bundled data while normal mode's index is retained.

**Country artifact compatibility:** Databases without an `alpha3` column infer recognized codes through the catalog without file changes. If the column exists, missing/conflicting values on the selected candidate are identity errors. Rank recognized candidates before validating stored Alpha-3; do not replace a winning known territory with its sovereign solely to hide a bad stored identity. Use the canonical catalog Alpha-3 as the cache key.

**Country artifact validation:** Exporter checks cover schema, identities, geometry, bounds, and exact coverage by the expected identity. They do not prove which overlapping candidate wins; retain runtime territory/mainland and routing tests using `CountryResolutionFixtureCatalog`. Reproduce with an explicit release and ignored output first; keep the shipped artifact until validation succeeds. Compare logical rows and geometry bytes when only timestamps differ. See [Overture divisions maintainer notes](docs/maintainer/OVERTURE_DIVISIONS.md) for resource budgets and benchmark methodology.

**Allocation regressions:** Preserve the prepared-geometry allocation regression and validate meaningful memory limits when changing country lookup. Keep allocation fixes separate from dependency updates or GC tuning; runtime overrides should be backed by reproduced evidence.

**Overture source aliases:** `OvertureCountrySourceCodes` supplies all source codes for a canonical cache, including BQ/XE/XS, FR/CP, and SJ/XJ. Preserve `_meta.sourceCountries` validation for affected caches, ordinary-country cache reuse, and replacement only after a new export succeeds.

**GADM shared datasets:** With processing territory fallbacks enabled, HKG/MAC use one CHN administrative cache. Lookup also uses this shared source for investigation. Preserve HKG/MAC country identity, skip country-level and unnamed `NA` rows before decoding geometry, and require named administrative geometry matches. Preserve existing behavior for other countries and when processing territory fallbacks are disabled.

**Data upgrades and retries:** New country coverage does not guarantee city/state coverage or automatically rewrite existing Immich names. Use Lookup first, clear previously skipped assets to retry old country misses, and use Preview Location Corrections before resetting existing names. Preview shares final resolution with processing without writing Immich metadata or changing skipped-asset tracking.

**Cache download synchronization:** Source-specific Overture and GADM cache services each use shared per-country in-flight task maps so concurrent requests for the same country await the same download/export instead of racing ahead.

**City fallback:** Processing and Lookup should stay aligned. Airport infrastructure should override the administrative city only when the airport geometry actually matches the point; otherwise the admin city stays preferred and the airport remains only a fallback when no city was resolved.

**Schedule UX:** The Settings page uses `ScheduleEditorState` for the user-friendly schedule presets. Keep cron parsing/building logic there, not inline in Razor components.

**Scheduled runs:** Scheduled triggers must not start a new run while another pass is already active. Preserve the `_runLock` / `MarkPending()` behavior in `ProcessingBackgroundService`. Use `ScheduledRunWaiter` for distant cron dates rather than one unbounded `Task.Delay`; retain cancellation and `TimeProvider` coverage.

**Activity labels under parallel processing:** Use the scoped activity API on `ProcessingState` for long-running shared work like country-cache downloads. Direct set/clear calls will get stomped by concurrent assets.

**ProcessingState.MarkPending():** Both `TriggerRunAsync` (manual run) **and** the scheduled run path in `ExecuteAsync` must call `state.MarkPending()` immediately after acquiring `_runLock`. Without it there is a window where the lock is held but `IsRunning` is false, causing silent button failures.

**Blazor "Connecting…" lag:** If `_circuitReady` stays false for more than ~3 seconds, suspect something blocking startup (e.g. synchronous DI factory in `AddSingleton`). Move it to `Task.Run` and await in `ExecuteAsync`.

**Immich DB tables:** `asset` and `asset_exif`. The join column is `"assetId"` (camelCase, quoted). Repository queries must use `asset_exif`, not the shorter `exif`.

**CSS:** All styles in `wwwroot/app.css`. Component `.razor.css` files are intentionally empty — do not add scoped styles there. Classes follow the lounge pattern: `settings-card`, `form-group`, `form-control`, `btn btn-primary`, `btn btn-secondary`, `alert alert-success/error`.

**Config persistence:** `AppConfig` is stored at `/config/settings.json` (or `./localdata/settings.json` in dev). DB credentials are always read from environment variables, never from `settings.json`.

**Test framework:** MSTest SDK on Microsoft.Testing.Platform v2. This repo uses the `MSTest.Sdk` project SDK plus `global.json` with `test.runner = Microsoft.Testing.Platform` for the .NET 10 test path. The repo includes a root `.runsettings` that excludes `Integration` and `Performance` tests from normal default runs, plus `integration.runsettings` for explicit integration runs while still excluding `Performance`. When invoking `dotnet test` directly, prefer `dotnet test --project <csproj>`. Regular CI-style test runs should skip those categories unless the task explicitly needs them.

---

## Data Directory Layout

```
/data/                         (DATA_DIR env var, defaults to /data in prod or ./localdata in dev)
  overture-divisions/
    {ISO3}.db                  Overture per-country division cache (downloaded on demand)
  gadm-divisions/
    {SOURCE_ISO3}.db           Optional GADM source cache (CHN.db is shared by HKG/MAC fallbacks)
  skipped.db                   SQLite: asset IDs with no resolvable location
  location-previews.db         SQLite: saved preview selections, settings, and result snapshots

/config/                       (CONFIG_DIR env var, defaults to /config)
  settings.json                AppConfig (schedule and processing settings)
```

---

## Environment Variables (Docker)

| Variable | Default | Purpose |
|---|---|---|
| `DB_HOST` | `database` | Immich PostgreSQL hostname |
| `DB_PORT` | `5432` | PostgreSQL port |
| `DB_USERNAME` | — | PostgreSQL user |
| `DB_PASSWORD` | — | PostgreSQL password |
| `DB_DATABASE_NAME` | `immich` | Database name |
| `IMMICH_URL` | — | Optional server URL for photo thumbnails |
| `IMMICH_WEB_URL` | `IMMICH_URL` | Browser URL for original-photo links |
| `IMMICH_API_KEY` | — | Optional thumbnail key with asset.view permission; environment only |
| `DATA_DIR` | `/data` | Geodata and cache files |
| `CONFIG_DIR` | `/config` | settings.json |

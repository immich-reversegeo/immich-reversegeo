# AGENTS.md

## Project Overview

**immich-reversegeo** is a self-hosted companion service (ASP.NET Core + Blazor Server) that improves Immich location metadata. It reads GPS-tagged assets from the Immich PostgreSQL database, resolves country and administrative areas from Overture Maps data, uses bundled Overture airport infrastructure where helpful, and writes city/state/country back to the `asset_exif` table.

## Repo Conventions

- In user-facing copy, use the product name **Immich ReverseGeo**. Keep lowercase `immich-reversegeo` only for technical slugs: URLs, repo names, Docker tags, image names.
- The service defaults to port `8080` in Docker and `5122` for local dev.
- The Docker image name is `immich-reversegeo` (or `ghcr.io/immich-reversegeo/immich-reversegeo`).
- Keep public docs and README copy concise and plain. Avoid hype, filler, and over-marketing language.
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
# Run all tests
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

---

## Key Architecture

```
src/ImmichReverseGeo.Web/
  Components/
    Layout/          MainLayout.razor, NavMenu.razor
    Pages/           Dashboard.razor, Settings.razor, Data.razor, Logs.razor
  Services/
    ConfigService              Load/save AppConfig; read DB credentials from env vars
    DataCacheService           On-demand Overture per-country division cache download and ISO mapping
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
src/ImmichReverseGeo.Legacy/
  Services/          Legacy reference implementations retained out of the active runtime
  data/              Legacy reference data kept out of the active runtime
                     Do not treat this project as part of the active production path unless explicitly reintroduced.
tests/ImmichReverseGeo.Tests/            Web/app tests
tests/ImmichReverseGeo.Overture.Tests/   Overture-specific tests
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

**DuckDB Azure on Linux:** For Overture Azure blob access in Linux containers, DuckDB's Azure extension should explicitly run `SET azure_transport_option_type='curl'` after `LOAD azure`. This was the real fix for the `Problem with the SSL CA cert (path? access rights?)` error in the local Docker image. Keep that bootstrap centralized and do not remove it casually during refactors.

**Bundled country resolver:** `OvertureDivisionsService` keeps an in-memory spatial index and prepared geometries for bundled country polygons. If country behavior looks inconsistent, suspect stale app process state or stale bundled DB output first.

**Cache download synchronization:** `DataCacheService` uses a shared per-country in-flight task map so concurrent requests for the same country await the same download/export instead of racing ahead.

**City fallback:** Processing and Lookup should stay aligned. Airport infrastructure should override the administrative city only when the airport geometry actually matches the point; otherwise the admin city stays preferred and the airport remains only a fallback when no city was resolved.

**Schedule UX:** The Settings page uses `ScheduleEditorState` for the user-friendly schedule presets. Keep cron parsing/building logic there, not inline in Razor components.

**Scheduled runs:** Scheduled triggers must not start a new run while another pass is already active. Preserve the `_runLock` / `MarkPending()` behavior in `ProcessingBackgroundService`.

**Activity labels under parallel processing:** Use the scoped activity API on `ProcessingState` for long-running shared work like country-cache downloads. Direct set/clear calls will get stomped by concurrent assets.

**ProcessingState.MarkPending():** Both `TriggerRunAsync` (manual run) **and** the scheduled run path in `ExecuteAsync` must call `state.MarkPending()` immediately after acquiring `_runLock`. Without it there is a window where the lock is held but `IsRunning` is false, causing silent button failures.

**Blazor "Connecting…" lag:** If `_circuitReady` stays false for more than ~3 seconds, suspect something blocking startup (e.g. synchronous DI factory in `AddSingleton`). Move it to `Task.Run` and await in `ExecuteAsync`.

**Immich DB tables:** `asset` and `asset_exif`. The join column is `"assetId"` (camelCase, quoted). All three repository queries must use `asset_exif`, not the shorter `exif`.

**CSS:** All styles in `wwwroot/app.css`. Component `.razor.css` files are intentionally empty — do not add scoped styles there. Classes follow the lounge pattern: `settings-card`, `form-group`, `form-control`, `btn btn-primary`, `btn btn-secondary`, `alert alert-success/error`.

**Config persistence:** `AppConfig` is stored at `/config/settings.json` (or `./localdata/settings.json` in dev). DB credentials are always read from environment variables, never from `settings.json`.

**Test framework:** MSTest SDK on Microsoft.Testing.Platform. This repo uses the `MSTest.Sdk` project SDK plus `global.json` with `test.runner = Microsoft.Testing.Platform` for the .NET 10 test path. The repo also includes a root `.runsettings` that excludes `Integration` and `Performance` tests from normal default runs. Regular CI-style test runs should skip those categories unless the task explicitly needs them.

---

## Data Directory Layout

```
/data/                         (DATA_DIR env var, defaults to /data in prod or ./localdata in dev)
  overture-divisions/
    {ISO3}.db                  Overture per-country division cache (downloaded on demand)
  skipped.db                   SQLite: asset IDs with no resolvable location

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
| `DATA_DIR` | `/data` | Geodata and cache files |
| `CONFIG_DIR` | `/config` | settings.json |

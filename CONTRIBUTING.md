# Contributing

Thanks for helping improve Immich ReverseGeo.

## Before You Start

- Open an issue for bugs or feature ideas when possible.
  Use [GitHub Issues](https://github.com/immich-reversegeo/immich-reversegeo/issues).
- Keep changes focused. Small, clear pull requests are easier to review and ship.
- If your change affects user-facing behavior, update the docs too.

## Local Setup

### App

```bash
dotnet restore
npm run start
npm run test
```

The local web app runs on `http://localhost:5122`.

Development defaults:

- data dir: `./localdata`
- bundled data dir: `./bundled-data`
- config dir: `./localdata`

### Common Task Scripts

This repo includes a lightweight [`package.json`](./package.json) as a task runner, even though the app itself is not a Node project.

Useful commands:

```bash
npm run build
npm run test
npm run test:integration
npm run docker:build
npm run docker:up
npm run docker:down
npm run docs:serve
npm run docs:build
npm run export:airports
npm run export:country-divisions
```

The Docker task scripts use [`src/ImmichReverseGeo.Web/Dockerfile`](./src/ImmichReverseGeo.Web/Dockerfile) and the standalone local override file [`docker-compose.local.yml`](./docker-compose.local.yml), so local contributor runs do not depend on the external Immich Docker network from the main end-user [`docker-compose.yml`](./docker-compose.yml).
`npm run docker:build` and `npm run docker:up` intentionally use a no-cache rebuild path for local troubleshooting so container behavior tracks the current source as closely as possible.

If Overture downloads fail in Linux containers with a DuckDB Azure SSL certificate error, check that the shared DuckDB bootstrap still sets `SET azure_transport_option_type='curl'` after `LOAD azure`. That was the confirmed fix for the current Docker/Linux Azure transport issue.

The test projects use `MSTest.Sdk` on Microsoft.Testing.Platform for the .NET 10 test path. The repo-level runner selection lives in [`global.json`](./global.json).

For Visual Studio and other tooling that honors repo-level run settings, [`.runsettings`](./.runsettings) excludes `Integration` and `Performance` tests from normal default runs.

### Browser regression tests

The frontend suite checks selection and exact-name confirmation, keyboard focus, saved-history filtering, connection recovery, and long-table layout in Chromium at desktop and 390px phone widths.

```bash
npm ci
npx playwright install chromium
npm run test:frontend
```

Use Node.js 24 and the .NET 10 SDK, and hydrate Git LFS data first. On Linux, use `npx playwright install --with-deps chromium` if browser dependencies are missing. Stop local app processes before building or testing on Windows.

The suite creates fresh SQLite fixtures under `_out/frontend-tests/runs/`, disables scheduling, clears Immich photo connection variables, and overrides database credentials with dummy values pointing to loopback port 1. It starts its own app on `http://127.0.0.1:5265` and refuses to reuse an existing server. Tests review confirmations without performing writes; no live Immich database is needed.

The HTML report is under `_out/frontend-tests/report/`; failures retain screenshots and traces under `_out/frontend-tests/results/`. CI runs the same suite before Docker validation and image publication. These checks cover the UI with saved fixtures; the separate integration suite verifies live database contracts.

### Environment

For local Visual Studio or `dotnet run` usage, the app reads real process environment variables for database access:

```env
DB_HOSTNAME=...
DB_PORT=5432
DB_USERNAME=...
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

Existing `DB_HOST` settings remain supported and take precedence over `DB_HOSTNAME` when nonblank. If neither has a nonblank value, the host defaults to `database`.

The app does not parse `.env` directly. In Docker setups, Compose is expected to expose those values as real environment variables to the running container.

## Data and Safety

- This project writes location data back into Immich's PostgreSQL database.
- The Data page can also clear existing Immich `city`, `state`, and `country` values before a rerun.
- Contributors testing against real data should treat database backups as mandatory before large processing runs or bulk clears.
  Use the official Immich guide:
  [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## Pull Requests

- Describe what changed and why.
- Link related issues.
- Include screenshots for UI changes when helpful.
- Call out follow-up work or known limitations.

### City Resolver defaults

If you want to improve the bundled city resolver defaults for everyone, please keep the change evidence-driven.

The bundled catalog lives in:

- [`src/ImmichReverseGeo.Web/bundled-data/defaults/city-resolver-profiles.json`](./src/ImmichReverseGeo.Web/bundled-data/defaults/city-resolver-profiles.json)

Recommended workflow:

1. Reproduce the problem in the Lookup page with real coordinates.
2. Confirm which division candidates are actually present.
3. Verify that the desired result is achievable by subtype order or broader-vs-tighter tie-break behavior.
4. Update the bundled profile only after that is clear.

Please include in the PR description:

- the country ISO3 code
- one or more example coordinates
- current result
- expected result
- why the new profile is a better default
- any limits or known tradeoffs you noticed

If the desired place is not in the candidate list, that usually means the fix belongs in upstream data or a different resolver step, not in the bundled city resolver profile.

## Commits

- Prefer [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/) where practical.
- Common types in this repo include `feat`, `fix`, `docs`, `test`, `ci`, `chore`, and `cleanup`.
- Keep commit subjects short and descriptive.

## Style Notes

- Follow the existing structure and naming in each project.
- Keep generated build output out of version control.
- Do not commit secrets, `.env` values, or machine-specific local config.
- Keep user-facing documentation aligned with runtime behavior.

## Docs

The documentation site is built with Zensical using the existing [`mkdocs.yml`](./mkdocs.yml) compatibility path. Public website content lives under [`docs/website/`](./docs/website/). Generated output goes to [`_out/website/`](./_out/website/).

Use a repository-local environment so local previews and CI use the same pinned Zensical version. From the repository root on Windows:

```powershell
py -m venv _out/docs-venv
_out/docs-venv/Scripts/python -m pip install -r docs/website/requirements.txt
_out/docs-venv/Scripts/python -m zensical serve
_out/docs-venv/Scripts/python -m zensical build --clean --strict
```

On Linux or macOS, create the environment with `python3` and use `_out/docs-venv/bin/python` for the remaining commands. With the environment activated, `npm run docs:serve` and `npm run docs:build` use its Python runtime.

Build from the repository root: Compose snippets come from the checked-out source, including in PR previews. Strict validation rejects broken internal links and anchors. When upgrading Zensical, update the pinned requirement and verify a clean strict build.

If you add or rename public docs pages, update [`mkdocs.yml`](./mkdocs.yml) too.

Reader features use content tabs, collapsible details, and selectively enlarged screenshots. Mark app screenshots with `.doc-screenshot .on-glb`; leave logos outside the lightbox. Keep screenshots free of credentials and personal library data.

Public pages should have concise `description` front matter for link previews. The `social` and `llmstxt` features are provided by Zensical; generated cards and Markdown exports stay under `_out/website`. Add new guides to the `llmstxt.sections` list in `mkdocs.yml` when they should be exported and offer Copy as Markdown.

For automated checks against real pinned Immich database schemas, run `npm run test:database` with Docker running. Testcontainers creates and removes isolated synthetic Immich stacks; CI runs the same suite separately from the fast tests. See [Live Immich database checks](./docs/maintainer/immich-db-testing.md) for details and the persistent Compose workflow for manual inspection.

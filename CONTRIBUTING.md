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

### Environment

For local Visual Studio or `dotnet run` usage, the app reads real process environment variables for database access:

```env
DB_HOST=...
DB_PORT=5432
DB_USERNAME=...
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

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

To build or preview the docs locally, install the Python packages first:

```bash
py -m pip install -r docs/website/requirements.txt
```

Then use one of these:

```bash
py -m zensical serve
py -m zensical build
```

If you add or rename public docs pages, update [`mkdocs.yml`](./mkdocs.yml) too.

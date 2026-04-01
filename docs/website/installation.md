---
icon: material/docker
---

# Installation

## Docker

If you already run immich with Docker Compose, the simplest setup is to add one service to that existing compose file.

!!! warning "Database backup strongly recommended"
    This service updates immich metadata in the database.

    Before first use, and especially before using any bulk-clear action from the Data page, create a proper immich database backup.

    Official guide:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## Preferred setup

- Add the service to your existing Immich `docker-compose.yml`.
- Reuse the same `.env` file that already contains your Immich database settings.
- Keep `/config` and `/data` persisted with Docker volumes.
- No extra Docker network configuration is needed when the service lives in the same compose project as Immich.

Reference file:
[docker-compose.yml](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/docker-compose.yml)

Copy/paste snippet:

```yaml title="docker-compose.yml"
--8<-- "https://raw.githubusercontent.com/immich-reversegeo/immich-reversegeo/master/docker-compose.yml"
```

This service expects:

- the same database connection values Immich already uses
- to run in the same compose project and Docker network as Immich
- a persistent `/config` volume for settings
- a persistent `/data` volume for downloaded Overture data and runtime state
- a free host port for the published web UI, with `8080` as the default example

Typical variables come from the shared `.env` file:

```env
DB_HOST=database
DB_PORT=5432
DB_USERNAME=postgres
DB_PASSWORD=...
DB_DATABASE_NAME=immich
DATA_DIR=/data
CONFIG_DIR=/config
```

Then start the stack:

```bash
docker compose up -d
```

## Runtime notes

- Built-in data covers country matching and airport matching.
- More detailed country data is downloaded only when needed.
- The app needs internet access the first time it downloads data for a new country.
- Large countries can use hundreds of megabytes each under `/data`.

## After install

- Use [Configuration](./configuration.md) for database and processing settings.
- Use [Using the App](./using-the-app.md) for manual runs, coordinate testing, and reset tools.

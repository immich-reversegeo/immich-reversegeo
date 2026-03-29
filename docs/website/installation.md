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
- Mount `/config` and `/data` so settings and downloaded country data persist.

Reference file:
[docker-compose.yml](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/docker-compose.yml)

Copy/paste snippet:

```yaml title="docker-compose.yml"
--8<-- "https://raw.githubusercontent.com/immich-reversegeo/immich-reversegeo/master/docker-compose.yml"
```

This service expects:

- the same database connection values Immich already uses
- access to the same Docker network as Immich
- a persistent `/config` mount for settings
- a persistent `/data` mount for downloaded Overture data and runtime state

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

## Separate compose file

If you prefer, you can also run Immich ReverseGeo from a separate compose file instead of editing the main Immich one.

In that case, make sure it still:

- joins the same Docker network as Immich
- uses the same database environment values
- mounts persistent `/config` and `/data` volumes

The same reference file above can also be used as a starting point for that setup.

## Runtime notes

- Built-in data covers country matching and airport matching.
- More detailed country data is downloaded only when needed.
- The app needs internet access the first time it downloads data for a new country.
- Large countries can use hundreds of megabytes each under `/data`.

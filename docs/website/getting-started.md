---
icon: material/rocket-launch-outline
---

# Getting Started

This is the fastest end-user path to a first working setup with Docker.

Use Immich ReverseGeo when you want more accurate or more useful location names in immich than the default built-in reverse-geocoding results are giving you.

What is different here is that Immich ReverseGeo uses better location data and more careful matching than immich's built-in reverse geocoding. That usually gives better results for things like coastlines, islands, airport areas, and other places where the default result feels too broad or inaccurate.

!!! danger "Disable immich's built-in reverse geocoding first"
    Immich ReverseGeo and immich's own reverse geocoding both write location fields.

    If both are enabled at the same time, they can overwrite each other's values and cause inconsistent results.

    Turn off immich's built-in reverse geocoding before you start processing with Immich ReverseGeo.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Back up immich before processing or clearing location data"
    Immich ReverseGeo writes `city`, `state`, and `country` values back into immich's database.

    Before you run this against real data, or before you use the Data page to clear existing location fields, create a proper immich database backup first.

    Follow the official guide:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## 1. Make sure you can reach the immich database

Immich ReverseGeo needs direct access to the same database your immich instance uses.

In Docker setups, that usually means:

- joining the same Docker network as immich
- passing the immich database connection values through to this container

Typical variables:

```env
DB_HOST=database
DB_PORT=5432
DB_USERNAME=...
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

## 2. Start the container

Use Docker with persistent mounts for:

- `/config` for settings
- `/data` for downloaded country data

You can either:

- add the `immich-reversegeo` service to your existing Immich compose file

```bash
docker compose up -d
```

## 3. Open the UI

Default container URL:

```text
http://localhost:8080
```

## 4. Test one coordinate

Use the Lookup page to confirm the basics before a full run:

<div class="step-grid">
  <div class="step-card">
    <h3>Country matching</h3>
    <p>Make sure the country is detected correctly for a coordinate you know well.</p>
  </div>
  <div class="step-card">
    <h3>State and city matching</h3>
    <p>Check that the result is reasonably precise and not too broad or generic.</p>
  </div>
  <div class="step-card">
    <h3>Airport areas</h3>
    <p>If you have airport photos, confirm they resolve the way you would expect.</p>
  </div>
</div>

## 5. Run a small processing pass

Use a conservative batch size first and confirm the resulting location names look right in immich before scaling up.

If you want to start from a clean slate, the Data page includes an action that clears existing `city`, `state`, and `country` values in immich before reprocessing.

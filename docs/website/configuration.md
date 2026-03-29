---
icon: material/tune-variant
---

# Configuration

<div class="section-intro">
The Settings page is intentionally small. Most users only need to check the database connection, pick a schedule, and tune how aggressively processing should run.
</div>

## Database settings

Database values are environment-backed and shown as read-only in the UI.

The app reads:

- `DB_HOST`
- `DB_PORT`
- `DB_USERNAME`
- `DB_PASSWORD`
- `DB_DATABASE_NAME`

These values are required because the app reads and updates immich data directly.

<div class="step-grid">
  <div class="step-card">
    <h3>Read-only by design</h3>
    <p>The database values shown in the UI come from the running environment and are there as a sanity check, not for editing.</p>
  </div>
  <div class="step-card">
    <h3>Keep them in Docker or your host environment</h3>
    <p>Set the database values where you launch the app, then restart it if you change them.</p>
  </div>
</div>

## Processing settings

The Settings page lets you control:

- automatic processing on or off
- a user-friendly schedule preset:
  - every hour
  - every few minutes
  - every few hours
  - every day
  - once a week
  - advanced custom cron when needed
- batch size
- batch delay
- max parallelism
- verbose logging

The UI shows a human-readable summary of the selected schedule and the resulting cron expression.

Important behavior:

- scheduled runs do not start a second pass if one is already in progress
- manual runs from the dashboard still work independently of the automatic schedule
- custom cron is only needed for advanced cases; most users should stay on the preset schedule options

<div class="feature-grid">
  <div class="card">
    <h3>Schedule</h3>
    <p>Choose when the app should run automatically, from simple presets up to a custom cron schedule.</p>
  </div>
  <div class="card">
    <h3>Batch size</h3>
    <p>Controls how many photos are processed at a time before the next pause or write cycle.</p>
  </div>
  <div class="card">
    <h3>Parallelism</h3>
    <p>Controls how much work the app does at once. Higher values can be faster, but they also put more load on the system and database.</p>
  </div>
</div>

## Database connection details

The database section in Settings is read-only and mainly there as a sanity check.

- values are taken from the running process environment
- they are not stored in `settings.json`

## Data layout

Runtime data goes under `/data` in production or `./localdata` in development.

Config goes under `/config` in production.

## Operational notes

<div class="feature-grid">
  <div class="card">
    <h3>Turn off immich’s built-in reverse geocoding</h3>
    <p>Only one tool should be updating location names. See immich’s <a href="https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings">Reverse Geocoding Settings</a>.</p>
  </div>
  <div class="card">
    <h3>Bulk clear is a real change</h3>
    <p>The Data page can clear existing country, state, and city values before a rerun, so a database backup is strongly recommended first.</p>
  </div>
  <div class="card">
    <h3>Country downloads take space</h3>
    <p>The app needs internet access when a country is downloaded for the first time, and those downloads can grow over time.</p>
  </div>
</div>

!!! danger "Do not run two reverse-geocoders against the same immich library"
    Immich ReverseGeo should be the only tool updating your immich location fields.

    Turn off immich's built-in reverse geocoding before using this app, otherwise the two systems can step on each other's results.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Clearing location data is a real metadata change"
    The Data page does not just reset this app's local state. It can clear existing immich location fields in the database.

    Take a database backup before using it:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

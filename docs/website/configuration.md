---
icon: material/tune-variant
---

# Configuration

<div class="section-intro">
The Settings page is intentionally small. Most users only need to check the database connection, pick a schedule, and tune how aggressively processing should run. Country-specific city matching now lives on its own City Resolver page.
</div>

For manual runs, coordinate testing, and reset tools, see [Using the App](./using-the-app.md).

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

- whether processing runs automatically
- when it runs, using simple presets or a custom cron expression
- batch size, delay, and parallelism
- whether airport infrastructure can override the city name
- whether every asset is written to the log

Most users should stay on the preset schedule options. Manual runs from the dashboard still work even when automatic scheduling is disabled.

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
  <div class="card">
    <h3>Airport matching</h3>
    <p>Leave it on if airport names are useful to you. Turn it off if you prefer commune or city names for photos taken on airport grounds.</p>
  </div>
</div>

## City Resolver

The City Resolver page lets you adjust how the app picks a city name when Overture returns several possible matches.

It gives you:

- bundled default rules that ship with the app
- an optional global override
- country-specific overrides
- a searchable country picker
- simple up/down controls to change the order of preferred place types from the official Overture list
- a choice between:
  - prefer the tighter match
  - prefer the broader match

This keeps the main Settings page simple while still giving you a way to fix countries where the default city result is not what you want.

### What it actually changes

The City Resolver only affects the `city` value.

It is useful when the app finds the right general area, but chooses the wrong name for your taste. For example:

- it picks a district instead of the wider city
- it picks a very small local area instead of the municipality
- it picks something broader than you want

### What it does not change

The City Resolver does not download better data or invent missing places.

It will not help if:

- the place you want is not in the Lookup results at all
- the airport name is winning and airport matching is still turned on
- the country match is wrong

So before changing anything, use Lookup first and make sure the place you want is actually in the returned data.

### Recommended workflow

1. Open the Lookup page and test a coordinate that gave you a bad city result.
2. Look at `Resolved City`, `Raw Best Area`, and the candidate list.
3. If the place you want is present, decide what to change:
   - if the wrong kind of place won, change the preferred order
   - if two similar places are competing, try broader or tighter matching
   - if an airport name is winning, turn off airport matching first
4. Add a country override only after you know the data supports the result you want.
5. Run Lookup again and confirm the final output before processing your library.

### About `admin_level`

Lookup shows `admin_level` because it can sometimes help explain why a result was chosen, but it is not a setting you edit directly.

That is because:

- many results do not have a useful `admin_level`
- the place type is usually more important than the number
- `admin_level` alone does not solve every case

So the controls on the City Resolver page stay simple:

- preferred place type order
- broader or tighter matching

### Bundled defaults vs your overrides

The app ships with built-in default rules. Your own settings sit on top of them:

- bundled global default
- bundled country-specific default, if present
- your optional global override
- your optional country override

That means you only need to change the countries you care about.

### Want to improve the bundled defaults for everyone?

If you find a country setting that clearly works better, you can open a pull request.

The bundled defaults currently live in:

- `src/ImmichReverseGeo.Web/bundled-data/defaults/city-resolver-profiles.json`

A good pull request should include:

- the country ISO3 code
- one or more example coordinates
- the old result
- the new expected result
- a short explanation of why the new default is better
- Lookup evidence showing that the desired place is really in the returned data

See the contributor notes in [`CONTRIBUTING.md`](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CONTRIBUTING.md#city-resolver-defaults).

## Database connection details

The database section in Settings is read-only and mainly there as a sanity check.

- values are taken from the running process environment
- they are not stored in `settings.json`

## Data layout

Runtime data goes under `/data`.

Config goes under `/config` in production.

## Operational notes

<div class="feature-grid">
  <div class="card">
    <h3>Turn off immich’s built-in reverse geocoding</h3>
    <p>Only one tool should be updating location names. See immich’s <a href="https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings">Reverse Geocoding Settings</a>.</p>
  </div>
  <div class="card">
    <h3>Geo resets are a real change</h3>
    <p>The Reset Geo Data page under Data can reset reverse geo country, state, and city values for all assets, pasted asset GUIDs, or a selected location value before a rerun, so a database backup is strongly recommended first.</p>
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
    The Reset Geo Data page does not just reset this app's local state. It can clear existing immich reverse geo `city`, `state`, and `country` fields in the database.

    It does not touch any other immich metadata.

    Take a database backup before using it:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

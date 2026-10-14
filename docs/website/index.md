---
description: "Improve Immich location names with a self-hosted companion. Preview known coordinates before processing your library."
title: Immich ReverseGeo
icon: material/home-outline
hide:
  - pageTitle
---

<style>
  .md-typeset h1,
  .md-content__button {
    display: none;
  }
</style>

![Immich ReverseGeo](assets/images/logo-horizontal.svg){ style="max-width: 34rem; width: 100%; margin: 0 0 1rem 0;" }

<div class="hero-lead">
Immich ReverseGeo is a self-hosted companion for <a href="https://immich.app">immich</a> that improves location names when the built-in results feel too broad, too generic, or simply wrong for your library.
</div>

<div class="hero-actions">
  <a class="md-button md-button--primary" href="./getting-started">Get started</a>
  <a class="md-button" href="./installation">Installation</a>
  <a class="md-button" href="https://github.com/immich-reversegeo/immich-reversegeo">GitHub</a>
</div>

## Check a coordinate before processing

Open **Lookup**, paste coordinates from a photo you know, and inspect the country, state, city, and matching sources before running against your library. Lookup previews location names without updating Immich assets; it may download country data when needed.

![Coordinate Lookup showing the proposed city, state, and country for Zurich](assets/screenshots/lookup.png){ .doc-screenshot .on-glb }

## What it can help with

- Missing or overly broad location names on photos that already have GPS coordinates.
- Matching around coastlines, islands, administrative boundaries, and airport areas.
- Repeatable processing with locally cached geographic data.

Results depend on the available source data. It cannot add missing GPS coordinates or guarantee a better name for every point. Compare known locations in Lookup first, then choose the settings that fit your library.

To correct existing names, use **Location Management** to scan your photos, review proposed differences, and apply only the changes you select. Back up the database before applying. Saved scans let you continue unfinished work; Change History records applied names and supports undo when the metadata has not changed. See [Correct existing location names](./getting-started.md#correct-existing-location-names).

See [Using the App](./using-the-app.md) for the workflow and [Data Sources](./data-sources.md) for coverage and licence limits.

## Before You Run It On Real Data

- It needs direct access to your immich database.
- immich's own reverse geocoding should be turned off first.
- It writes back to immich metadata.
- It can clear existing immich location values from the Data page.
- It needs internet access the first time it downloads extra data for a country.
- Large country downloads can consume hundreds of megabytes each.

!!! danger "Turn off immich's built-in reverse geocoding"
    Immich ReverseGeo writes `city`, `state`, and `country` back into immich.

    If immich's own reverse geocoding remains enabled, both systems can overwrite the same fields and step on each other's results.

    Disable immich's built-in reverse geocoding before running Immich ReverseGeo.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Back up your immich database first"
    Immich ReverseGeo reads from and writes to immich's database.

    Before running processing on real assets, and before clearing existing location fields, create a proper immich backup first:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## Quick Links

<div class="quick-grid">
  <a class="quick-link" href="./getting-started">Getting Started</a>
  <a class="quick-link" href="./installation">Installation</a>
  <a class="quick-link" href="./configuration">Configuration</a>
  <a class="quick-link" href="./using-the-app">Using the App</a>
  <a class="quick-link" href="./data-sources">Data Sources</a>
  <a class="quick-link" href="./architecture">Architecture</a>
  <a class="quick-link" href="./changelog">Changelog</a>
  <a class="quick-link" href="./troubleshooting">Troubleshooting</a>
</div>

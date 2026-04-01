---
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

## Why People Use It

<div class="highlight-grid">
  <div class="card">
    <h3>Better place names</h3>
    <p>Improves country, state, and city names when immich’s built-in result is too rough for the photo.</p>
  </div>
  <div class="card">
    <h3>Tricky places handled better</h3>
    <p>Often works better around coastlines, islands, border areas, and other places where simple matching falls short.</p>
  </div>
  <div class="card">
    <h3>Smarter airport matching</h3>
    <p>Can recognize airport areas more accurately, so airport photos can resolve to the airport itself when that is the better fit.</p>
  </div>
</div>

## Good Fits

<div class="section-intro">
Immich ReverseGeo is a strong fit if your photos already have GPS coordinates and you want cleaner, more useful location names in a self-hosted setup.
</div>

<div class="feature-grid">
  <div class="card">
    <h3>Large libraries</h3>
    <p>Useful when many assets already have coordinates but the place names are missing or low quality.</p>
  </div>
  <div class="card">
    <h3>Self-hosted workflows</h3>
    <p>Designed for repeatable local processing instead of depending on a third-party geocoding API at run time.</p>
  </div>
  <div class="card">
    <h3>Careful rollouts</h3>
    <p>Lets you test individual coordinates first, then process the whole library once the results look right.</p>
  </div>
</div>

## Features

<div class="feature-grid">
  <div class="card">
    <h3>immich integration</h3>
    <p>Reads your existing photo coordinates and writes improved location names back into immich.</p>
  </div>
  <div class="card">
    <h3>Better matching</h3>
    <p>Uses built-in location data plus extra country downloads when needed.</p>
  </div>
  <div class="card">
    <h3>Web UI</h3>
    <p>Change settings, test coordinates, manage downloads, and run processing manually.</p>
  </div>
  <div class="card">
    <h3>Preview before processing</h3>
    <p>See why a specific coordinate resolves the way it does before you process your full library.</p>
  </div>
</div>

## Architecture Overview

<div class="feature-grid">
  <div class="card">
    <h3>immich database</h3>
    <p>Photo coordinates are read from here, and improved location names are written back here.</p>
  </div>
  <div class="card">
    <h3>Immich ReverseGeo app</h3>
    <p>The web app handles settings, processing, lookups, and download management.</p>
  </div>
  <div class="card">
    <h3>Built-in data</h3>
    <p>Used for country matching and airport matching right away.</p>
  </div>
  <div class="card">
    <h3>Downloaded data</h3>
    <p>Extra country data is downloaded when needed for better state and city matching.</p>
  </div>
</div>

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
  <a class="quick-link" href="./architecture">Architecture</a>
  <a class="quick-link" href="./changelog">Changelog</a>
  <a class="quick-link" href="./troubleshooting">Troubleshooting</a>
</div>

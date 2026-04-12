---
icon: material/compass-outline
---

# Architecture

<div class="section-intro">
You do not need to understand the internals to use Immich ReverseGeo, but the overall shape is simple: the app reads coordinates from immich, matches them against built-in and downloaded location data, and writes the final place names back.
</div>

For a plain-language explanation of the active geographic data sources, see [Data Sources](./data-sources.md).

## Main components

<div class="feature-grid">
  <div class="card">
    <h3>Web app</h3>
    <p>The web UI handles settings, manual processing, download management, lookups, and logs.</p>
  </div>
  <div class="card">
    <h3>Built-in data</h3>
    <p>Country matching and airport matching are available right away from data shipped with the app.</p>
  </div>
  <div class="card">
    <h3>Downloaded country data</h3>
    <p>Extra per-country data is downloaded when needed so state and city matching can be more precise.</p>
  </div>
</div>

### Processing pipeline

The background processor:

1. reads unprocessed assets from immich
2. resolves bundled country
3. resolves administrative areas from cached Overture divisions and optionally cached GADM country packages
4. optionally queries bundled airport infrastructure
5. writes city/state/country back to immich when a complete result is available

### Active data sources

<div class="feature-grid">
  <div class="card">
    <h3>Built in</h3>
    <p>Country matching data and airport matching data are included in the app image.</p>
  </div>
  <div class="card">
    <h3>Downloaded on demand</h3>
    <p>Country-specific Overture and optional GADM data is downloaded locally when needed for better state and city matching.</p>
  </div>
  <div class="card">
    <h3>Optional live lookup help</h3>
    <p>The Lookup page can also show live Overture Places diagnostics when you want to inspect a point more deeply.</p>
  </div>
</div>

### Legacy project

Older geoBoundaries, static-airport, and Foursquare code is preserved in the legacy project but is no longer part of the active runtime path.

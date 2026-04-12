---
icon: material/database-outline
---

# Data Sources

This page explains the geographic data sources Immich ReverseGeo uses today, where they come from, why the app uses them, and what license limits matter in practice.

Use this page when you want to answer questions like:

- what data is built into the app image
- what data is downloaded later on demand
- why one source is used for country matching while another is used for state or city matching
- whether a source is suitable for your setup

## At a glance

| Source | How the app uses it | Built in or downloaded | License notes |
|---|---|---|---|
| [Built-in Overture country data](#built-in-overture-country-data) | Country matching | Built in | ODbL |
| [Downloaded Overture administrative caches](#downloaded-overture-administrative-caches) | State matching, city matching | Downloaded per country on demand | ODbL |
| [Built-in airport data](#built-in-airport-data) | Airport-area matching | Built in | ODbL |
| [Optional live Overture Places diagnostics](#optional-live-overture-places-diagnostics) | Extra Lookup diagnostics only | Live query during Lookup when enabled | Multi-license dataset, varies by source |
| [Optional GADM administrative data](#optional-gadm-administrative-data) | Optional administrative fallback or preferred admin source | Downloaded per country on demand | Non-commercial use only |

## Built-in Overture country data

Immich ReverseGeo ships with a small bundled Overture country cache.

The app uses it for:

- the first country match for a coordinate
- deciding which country-specific admin cache should be loaded next
- letting Lookup work immediately without waiting for the first country-level download

Why this source is used:

- it is open and easy to bundle
- it gives consistent country polygons for the active runtime
- it avoids a live network dependency for the first step of every lookup

What it does not do by itself:

- it is not the main state and city source for the final result
- it can still miss some edge-case territories, so country detection is not perfect everywhere

Origin:

- Overture Maps `divisions` theme, especially `division_area`

License:

- ODbL for the divisions theme

Official references:

- [Overture Divisions Guide](https://docs.overturemaps.org/guides/divisions/)
- [Overture Attribution and Licensing](https://docs.overturemaps.org/attribution/)

## Downloaded Overture administrative caches

When the app needs better state and city matching, it downloads a per-country Overture administrative cache into `/data/overture-divisions/{ISO3}.db`.

The app uses this source for:

- normal administrative area matching during processing
- the default state and city result in Lookup
- the baseline admin result even when optional GADM support is enabled

Why this source is used:

- it is a strong open default
- it is shaped for containment checks like reverse geocoding
- per-country caching keeps local storage more manageable than downloading the whole world

What it does not do:

- it does not guarantee that every municipality or territory is represented the way you expect
- it does not guarantee current legal or official naming in every country

Origin:

- Overture Maps `divisions` theme

License:

- ODbL for the divisions theme

Official references:

- [Overture Divisions Guide](https://docs.overturemaps.org/guides/divisions/)
- [Overture Attribution and Licensing](https://docs.overturemaps.org/attribution/)

## Built-in airport data

Immich ReverseGeo also ships with a bundled airport-focused extract built from Overture data.

The app uses it for:

- recognizing airport grounds more accurately than a normal city or commune lookup
- optionally overriding the city name when the point is clearly inside an airport geometry
- helping airport photos resolve to the airport itself when that is the more useful label

Why this source is used:

- airport photos are a common case where plain admin boundaries feel wrong
- keeping the airport extract bundled avoids another first-run download for this feature

What it does not do:

- it is not a general-purpose places database
- it only helps when the coordinate actually matches airport-related geometry

Origin:

- exported from Overture transportation data into a bundled airport cache used by the app

License:

- ODbL for the transportation theme

Official references:

- [Overture Transportation Guide](https://docs.overturemaps.org/guides/transportation/)
- [Overture Attribution and Licensing](https://docs.overturemaps.org/attribution/)

## Optional live Overture Places diagnostics

The Lookup page can optionally run a live Overture Places search. This is for diagnostics, not for normal background processing.

The app uses it for:

- helping you inspect whether a nearby place or point of interest exists in Overture
- debugging difficult coordinates where the administrative result is not enough

Why this source is optional:

- it is slower than bundled or cached local sources
- it depends on live access to Overture-hosted public data
- it is useful for investigation, but not required for normal processing

What it does not do:

- it is not the default source for the city written back to Immich
- it is not required for normal country, state, or city processing

Origin:

- Overture Maps `places` theme

License:

- Overture Places is a multi-license dataset
- license terms depend on the source of each place record
- current source licenses listed by Overture include CC0-1.0, Apache-2.0, and CDLA-Permissive-2.0

Official references:

- [Overture Places Guide](https://docs.overturemaps.org/guides/places/)
- [Overture Attribution and Licensing](https://docs.overturemaps.org/attribution/)

## Optional GADM administrative data

GADM is an optional source for administrative boundaries.

When enabled, the app downloads one or more country GeoPackages and builds local caches under `/data/gadm-divisions/{ISO3}.db`.

The app uses it for:

- optional state and city matching in Lookup
- optional fallback or preferred admin matching during processing
- some cases where a traditional admin-boundary dataset gives a better municipality or state result than Overture

Why this source is optional instead of the default:

- its license is much more restrictive than Overture
- it can involve large country downloads
- it still depends on the bundled Overture country match to decide which country cache to try first

What it can fix:

- some cases where Overture admin naming or municipality choice is not ideal
- some split-territory cases when the detected country is expanded through the app's GADM fallback family rules

What it cannot fix:

- cases where the initial bundled country lookup finds no country at all
- all territory or sovereignty edge cases
- commercial-use licensing concerns

Origin:

- GADM country packages in GeoPackage format

License:

- GADM says the data is for academic and other non-commercial use
- redistribution or commercial use is not allowed without prior permission

Practical meaning for this project:

- fine for non-commercial use
- not a clean fit for commercial use
- still worth testing with Lookup before enabling it for full-library processing

Official references:

- [GADM About](https://gadm.org/about.html)
- [GADM Data](https://gadm.org/data.html)
- [GADM License](https://gadm.org/license.html)

## Why there is more than one source

No single source is best at everything.

Immich ReverseGeo mixes sources on purpose:

- bundled Overture country data gives a fast local first step
- downloaded Overture admin data is the open default for state and city matching
- bundled airport data helps where an airport name is more useful than the surrounding municipality
- optional GADM can improve some admin-boundary cases
- optional live Overture Places helps you inspect difficult coordinates before changing settings

That also means there is no promise that every source will agree with every other source for every point.

The recommended workflow is:

1. test a coordinate in Lookup
2. compare the returned sources
3. decide whether you want default Overture behavior, airport matching, or optional GADM for that use case

## Storage and network behavior

- built-in Overture country and airport data ships inside the app image
- downloaded Overture and GADM country caches are stored under `/data`
- the first lookup or processing pass for a new country may take longer because the cache must be created locally
- larger countries can use hundreds of megabytes of local storage per cached country

## Notes

This page is a practical summary, not legal advice.

If license terms are important for your deployment, check the official source pages above before enabling or redistributing any optional data path.

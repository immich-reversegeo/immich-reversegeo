---
icon: material/history
---

# Changelog

This is the public-facing release summary for Immich ReverseGeo.

Technical implementation notes live in [CHANGELOG.md](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md).

## Unreleased

- Fixed the app stopping while waiting for an infrequent custom schedule, such as an annual run.
- Add city name replacements in City Resolver to correct known location names for your library. Scope them by country and optional state, then check the final result in Lookup before reprocessing affected assets.

- You can now reuse Immich's `DB_HOSTNAME` setting to connect to an external database. Existing `DB_HOST` settings continue to work and take precedence when nonblank.
- Dashboard and Logs now show dates and times in your browser's timezone. Downloaded logs remain in UTC with full dates.

## 2026-08-23

This release fixes the app running out of memory and restarting during processing.

- Fixed the container growing in memory until it was killed, then restarting mid-run. Country matching was doing far more geometry work than it needed to on almost every photo. See [issue #13](https://github.com/immich-reversegeo/immich-reversegeo/issues/13).
- Country matching is also much faster. On typical European coordinates a country lookup went from roughly 2.5 ms to under 0.1 ms.
- The [installation snippet](./installation.md#docker) now sets a `mem_limit` of 2 GB. Existing installations can add this to their Compose file to limit total container memory. No extra runtime memory-tuning settings are required. Larger country caches or higher parallelism may need a higher limit. See [The container keeps restarting or gets killed](./troubleshooting.md#the-container-keeps-restarting-or-gets-killed).
- In rare cases near a border, a photo that sits exactly inside one country but within about 16 m of a neighbour now always resolves to the country it is actually in. Previously the neighbour could win.

## 2026-04-12

This release focuses on better administrative area matching and clearer data management.

- Optional GADM support can now be enabled as an extra source for state and city matching. Use [Lookup](./using-the-app.md#lookup) first to compare Overture and GADM results, then enable it for processing if it improves your library. See [GADM administrative areas](./configuration.md#gadm-administrative-areas).
- Lookup now shows what it is doing while it runs, including when it is downloading or waiting for country data.
- The Data area now has one administrative cache table for downloaded Overture and GADM data. It shows the data source, country, row count, version, size, download time, and cache actions. See [Data tools](./using-the-app.md#data-tools).
- Split-territory GADM fallbacks can help in places where one country match may need related packages, such as Denmark with Greenland and the Faroe Islands, or the UK with Jersey, Guernsey, and the Isle of Man.
- A new [Data Sources](./data-sources.md) page explains where the geographic data comes from, what each source is used for, what is built in or downloaded, and the important license notes.
- The reset page is now called **Reset Immich Geo Data** so it is clearer that it changes Immich reverse geo fields, not just this app's local cache.

## 2026-04-03

- Added the City Resolver page so you can see the bundled defaults, change the global city matching behavior, and add per-country overrides when a country needs different city-like divisions.
- Added curated bundled country defaults for places where the normal city matching order needs a different preference.

## 2026-04-01

- Added an option to turn off airport matching if you prefer normal city or commune names.
- Added the Reset Immich Geo Data page so you can clear reverse geo `city`, `state`, and `country` values before rerunning processing.

## 2026-03-29

- Initial Version.

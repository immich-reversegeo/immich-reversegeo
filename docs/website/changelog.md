---
icon: material/history
---

# Changelog

This is the public-facing release summary for Immich ReverseGeo.

Technical implementation notes live in [CHANGELOG.md](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md).

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

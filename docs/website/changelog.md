---
description: "User-facing release notes for Immich ReverseGeo."
icon: material/history
---

# Changelog

This is the public-facing release summary for Immich ReverseGeo.

Technical implementation notes live in [CHANGELOG.md](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md).

## Unreleased

- Use county names as a fallback for US photos where Overture has no preferred city-like match, instead of falling back directly to a state or a nearby airport.

- Refresh the built-in airport data. Use Lookup to compare airport matches before reprocessing existing photos.

- Country detection now keeps supported territories as their own country identity instead of replacing them with the administering country.
- Fixed the reported Hong Kong coordinates and expanded built-in coverage for Macao, Greenland, the Faroe Islands, Jersey, Guernsey, the Isle of Man, Puerto Rico, Guam, the U.S. Virgin Islands, Bermuda, Gibraltar, the Cayman Islands, the British Virgin Islands, Aruba, Curaçao, the Åland Islands, Réunion, French Polynesia, and New Caledonia.
- Older custom country databases remain readable without modification. Incorrect identities in newer-format databases produce a clear mapping error.
- Country detection still works offline from bundled data. Use [Lookup](./using-the-app.md#lookup) to verify a coordinate and inspect the later Overture and optional GADM results before processing your library.

- Optional GADM lookup can now use Hong Kong and Macao administrative data from its China download, preserving each territory's country name. Processing uses the related/shared package setting.

- Apply the existing small country-boundary tolerance to points just outside a country polygon's bounding box, while keeping exact country matches preferred.

- Keep overlapping area choices consistent when otherwise equal matches arrive in a different order.

- Lookup now explains whether no country boundary matched or a matched boundary has an unresolved country code.

- The Dashboard now explains which photos a run processes and how to correct existing names.
- Added **Preview Location Corrections** under Data. Compare existing and proposed names before resetting selected photos. Preview leaves Immich data unchanged; see [Using the App](./using-the-app.md#preview-location-corrections).

- Added checks for future bundled country-data updates. The shipped geographic data and country-matching behavior are unchanged by this tooling update.

- Include version details, integrity checksums, and signed build provenance with downloadable release packages, along with the bundled geographic data needed by the app.
- Country selection lists now use bundled names consistently across platforms. This change does not add geographic coverage or change how coordinates match a country.

- Nearby Places filters now avoid unrelated sports and transport businesses that previously matched part of a category name.

- Make the website easier to read in light and dark mode. Follow your system theme by default and remember your choice when you switch modes.
- Lookup now shows Nearby Places as an investigation tool, with several suggestions, distances, categories, sources, and reasons for inclusion or exclusion. Nearby places do not change the location written to Immich.

- Improved downloaded administrative coverage for Saba, Sint Eustatius, Clipperton Island, and Jan Mayen. Affected older caches refresh automatically when needed; other country caches remain usable.

- Restored optional live Places lookup compatibility with recent Overture releases.
- New Overture country downloads can use parent areas to improve city matching and keep city/state results consistent. Existing caches remain usable; re-download a country cache and test known coordinates in Lookup to use the improvement.
- Overture download failures now report release-discovery problems instead of trying an expired data release.

- Fixed the app stopping while waiting for an infrequent custom schedule, such as an annual run.
- Docker releases now have version tags for controlled upgrades. GitHub's designated latest stable release updates `latest`; older release retries and prereleases do not automatically change it. `edge` continues to follow development builds.
- Development images now publish only after their build and automated checks pass.

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

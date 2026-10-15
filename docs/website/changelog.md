---
description: "User-facing release notes for Immich ReverseGeo."
icon: material/history
---

# Changelog

This is the public-facing release summary for Immich ReverseGeo.

Technical implementation notes live in [CHANGELOG.md](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md).

## Unreleased

- Reduce memory use while building Overture country caches, including the first processing pass or a cache refresh. Existing caches and location matching rules stay the same.

- Lookup now reports an incomplete result when a required country cache cannot be prepared, instead of suggesting that a country-only fallback would be written. Retry after resolving the cache error.

- Choose English, local/source names, or a preferred language in Settings. Lookup, correction previews, and processing use available Overture translations, including parent-area names. Older country caches rebuild once to retain alternate names. Existing Immich names stay unchanged until you apply reviewed changes or reprocess them. See [Location name language](configuration.md#location-name-language).

## 0.2.0 — 2026-10-14

This release adds selected location corrections and guarded undo, resumable scans, clearer reviews, and improved administrative matching.

- Outdated Overture country caches refresh automatically when used. If an update fails, the app keeps a compatible previous cache and retries later. Check known coordinates in [Lookup](./using-the-app.md#lookup) before applying changed names.
- Overture cache preparation now shows separate query and cache-building stages. A query percentage describes that stage, not the entire download.
- Fixed stable Docker image promotion and added recovery for an already-built release image.

- Updated the [app guide](./using-the-app.md) with current screenshots and the [quick-start correction workflow](./getting-started.md#correct-existing-location-names). Added help for slow cache preparation, memory limits, and handled scan differences.
- Location reviews keep selection actions within reach and move keyboard focus into and back from confirmation. Saved Scans and Change History can filter unfinished or pending work across all pages. Navigation and comparison tables work on smaller screens.
- Settings shows schedules in UTC, previews the next run in your browser's timezone, validates changes before saving, and displays effective processing limits. The save bar stays available while scrolling.
- Scans show checked totals and estimated remaining processing time. Highlighted characters make spelling, punctuation, and invisible differences visible in both existing and proposed names.
- Exact administrative boundary matches now take priority over neighbouring tolerance-only matches. Overture parent names prefer English when available; re-download an older country cache and check Lookup before applying corrections.
- Country-cache preparation shows its current stage and elapsed time, with file-transfer or estimated query progress when available. Query progress is not a percentage of the whole cache; see [cache preparation](./using-the-app.md#scan-progress-and-country-caches).
- Scripts can start a processing pass with `POST /api/processing/run`. The endpoint needs no API key, so keep it private like the web UI. An accepted response does not mean processing succeeded; see [scripted runs](./using-the-app.md#start-a-run-from-a-script).
- Logs has a **Follow logs** toggle and a panel that fills the available page height. Connection recovery explains how to continue saved scans and check interrupted changes. Browser favicons are clearer at small sizes.

- Continue unfinished scans from **Saved Scans** after closing the page or restarting the app. The app restores the original options, keeps earlier results, and resumes after the last saved asset. Saved reports also show pause status and recorded progress.
- Scans that reach their difference limit now show a clear **Paused for review** notice and a **Continue Scan** button that stays visible while reviewing results.
- **Show only different values** is enabled by default in live and saved scans. Unchanged results no longer load photo previews, even when you choose to show them.
- Applying a batch now clears it from **Ready to apply** so you can review the next differences. Use **Applied**, **Needs attention**, or **All results** to inspect saved outcomes without applying the same snapshot again.
- Added **Location Management**: scan and review names, apply selected corrections from live or saved scans, and use Change History to inspect results or undo unchanged applied metadata. Section tabs highlight the current page; history shows clear result counts and remaining undo work. Previous preview links still work. See [Using the App](./using-the-app.md#location-management) and the [technical changelog](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md).

## 0.1.0 — 2026-10-12

This first versioned release brings location previews, improved country and city matching, refreshed airport data, and lower retained country-boundary memory. Earlier development milestones remain listed below.

- Country lookup uses less memory, with a low-memory mode for smaller hosts and automatic release of idle country boundaries after five minutes. An optional bounded cache can retain recently used boundaries. These settings do not cap total app memory; see [memory settings](./configuration.md#low-memory-mode).

- Optionally fill a missing city with a point-only airport within 100 m. The new setting defaults off and never replaces a resolved city or county.

- By default, airport names now require a mapped boundary matching the photo coordinates, with a 5 m tolerance for minor GPS or mapping differences. Nearby airports outside that tolerance are excluded. Point-only airports can fill a missing city only when the optional 100 m fallback is enabled; use Lookup to inspect excluded airport candidates.

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

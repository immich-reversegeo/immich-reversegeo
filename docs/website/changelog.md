---
description: "What's new in Immich ReverseGeo."
icon: material/history
---

# Changelog

New features, fixes, and upgrade notes for Immich ReverseGeo. See the [technical changelog](https://github.com/immich-reversegeo/immich-reversegeo/blob/master/CHANGELOG.md) for implementation details.

## Unreleased

- **Choose your location-name language.** Use English, local names, or a preferred language in Settings. Lookup, correction previews, and processing use the translations available in Overture. See [language settings](./configuration.md#location-name-language).
- **More reliable country-data preparation.** Downloads use less memory and disk space, and countries are prepared one at a time. Failed builds wait five minutes before retrying.
- **Clearer Lookup errors.** If required country data cannot be prepared, Lookup shows an incomplete result so you can resolve the error before applying changes.

**After upgrading:** older Overture country data rebuilds automatically when next needed, so the first lookup or scan may take longer. Switching languages afterward needs no new download. Existing Immich names change only when you apply corrections or reprocess photos.

## 0.2.0 — 2026-10-14

Review and correct existing location names with more control.

- **Location Management:** scan your library, compare proposed names, and apply selected corrections. Use Change History to review changes and undo them when the applied values have not changed since. See [the app guide](./using-the-app.md#location-management).
- **Resume unfinished scans** from Saved Scans after closing the page or restarting the app. Scans that pause for review have a visible Continue Scan button.
- **Easier reviews:** show differences by default, highlight small spelling changes, and filter results that still need attention. Reviews and navigation also work better on smaller screens.
- **Automatic country-data updates:** outdated Overture downloads refresh when needed. If an update fails, a compatible previous download stays available. Progress shows the current preparation step.
- **Clearer settings and logs:** preview the next scheduled run in your timezone, keep the save controls within reach, and choose whether logs follow new messages.
- **Start processing from a script** using the [processing endpoint](./using-the-app.md#start-a-run-from-a-script). Keep access private, like the web UI.

Location matching near administrative boundaries has also improved. Check known coordinates in [Lookup](./using-the-app.md#lookup) before applying updated names.

## 0.1.0 — 2026-10-12

The first versioned release adds location previews and improves country, city, and airport matching.

- **Preview location corrections** before changing your photos. Compare current and proposed city, state, and country names.
- **Lower country-lookup memory use**, with a low-memory mode for smaller hosts. See [memory settings](./configuration.md#low-memory-mode) for the limits of these options.
- **Better airport matching:** airport names normally require a matching mapped boundary. An optional nearby-airport fallback can fill a missing city; it is off by default. Built-in airport data has also been refreshed.
- **Better city and territory coverage:** US county names can fill missing cities, and supported territories such as Hong Kong, Macao, and Greenland keep their own country names. Optional GADM lookup also improves coverage for Hong Kong and Macao.
- **More useful Lookup results:** inspect nearby places and clearer matching explanations. Nearby Places suggestions do not change the names written to Immich.
- **Easier self-hosting:** versioned Docker tags support controlled upgrades, and database connections can reuse Immich's `DB_HOSTNAME` setting. Dashboard and log times follow your browser's timezone.

Newly downloaded country data can improve city and state names. To use those improvements with an older download, re-download it from Data and check familiar coordinates in Lookup before reprocessing photos.

## 2026-08-23

- Fixed excessive memory growth that could restart the app during processing, and made country lookup faster.
- Improved country selection for photos near borders.
- Added a 2 GB container memory limit to the example Docker setup. Larger country downloads may need more memory; see [memory troubleshooting](./troubleshooting.md#the-container-keeps-restarting-or-gets-killed).

## 2026-04-12

- Added optional **GADM** data for state and city matching, including support for some related territories. Compare results in Lookup before enabling it for processing. GADM has a non-commercial license; see [GADM settings](./configuration.md#gadm-administrative-areas).
- Added a shared Data view for managing Overture and GADM downloads, with clearer download progress.
- Added a [Data Sources](./data-sources.md) guide explaining coverage and licenses.
- Renamed the reset page to **Reset Immich Geo Data** to make its effect clearer.

## 2026-04-03

- Added **City Resolver** settings to adjust city matching globally or for individual countries, with built-in defaults for supported countries.

## 2026-04-01

- Added an option to turn off airport matching.
- Added **Reset Immich Geo Data** to clear location names before reprocessing photos.

## 2026-03-29

- Initial release.

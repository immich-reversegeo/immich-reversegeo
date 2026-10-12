---
description: "Use the Dashboard, Lookup, data maintenance tools, and logs in Immich ReverseGeo."
icon: material/application-outline
---

# Using the App

This page covers the day-to-day UI for Immich ReverseGeo after setup is complete.

For install-time settings such as database values, schedules, and processing limits, see [Configuration](./configuration.md).
For a source-by-source explanation of the geographic data behind the app, see [Data Sources](./data-sources.md).

The sidebar keeps the main tools available and includes **Help & setup** for the public guides. Browser tab titles identify the current tool.

## Dashboard

Use `Run Now` on the Dashboard to start a manual processing pass immediately.

Runs process active assets with GPS coordinates only when **both city and country are missing**. Existing names are kept, even if they are incorrect. Assets on the skip list are excluded until you clear that list under Data. The unprocessed count includes eligible assets that may still be on the skip list.

To correct existing names, use [Preview Location Corrections](#preview-location-corrections) first, reset only the assets you want to correct, then select `Run Now`.

- it works even if automatic scheduling is turned off
- it uses your current Settings values for batch size, delay, parallelism, and airport matching
- the Dashboard shows live progress, recent activity, and the last completed run
- `Cancel` stops the current run

Processing selects GPS-tagged assets only when both city and country are missing (stored as `NULL`). To correct existing location names, back up the database and use a targeted reset from [Data tools](#data-tools) before reprocessing.

Run completion and activity timestamps use your browser's local timezone, shown beside each time. This does not change the processing schedule.

## Lookup

Use the Lookup page when you want to test a coordinate before running a full processing pass.

- paste or type coordinates to see how the resolver behaves for that point
- it shows the country match, cached administrative area match, optional airport match, and final values that would be written
- `Include bundled airport infrastructure lookup` lets you test with or without airport matching for that lookup
- `Include nearby places` adds a slower live Overture search for investigation. It shows nearby suggestions and a small sample of excluded places; it does not change the location written to Immich
- `Prefer cached GADM administrative areas` switches the administrative area part of the lookup to an experimental on-demand GADM cache for that country; country detection still starts with the bundled Overture country data

![Coordinate Lookup input](assets/screenshots/lookup.png){ .doc-screenshot .on-glb }

Click the screenshot to enlarge it. Lookup previews do not update Immich assets.

### Country lookup failures

Lookup distinguishes a point outside the bundled country coverage from a matched boundary whose country code cannot be resolved. If the country code cannot be resolved, check that the bundled country database and country catalog belong to the same app release. A missing boundary match can require newer geographic data; changing city settings will not add country coverage.

## Data tools

### Preview Location Corrections

Open **Data → Preview Location Corrections** to compare existing city, state, and country names with proposed values from your current processing settings.

1. Choose pasted asset GUIDs, an exact existing city/state/country name, or the whole library.
2. Start the preview. It checks up to 100 assets per page and may download country caches. Every scope includes only assets with both GPS coordinates. It does not write to Immich or change the skip list.
3. Review the existing and proposed values. Use **Show only different values** to focus on changes. Different does not necessarily mean better: test unfamiliar results in Lookup using the coordinates shown in the table.
4. Select only the assets you want to correct. The GUID list includes your selections across reviewed pages. Copy those GUIDs and take an [Immich database backup](https://docs.immich.app/administration/backup-and-restore/) before resetting them under **Reset Immich Geo Data → Reset Single Item(s)**.
5. Select **Run Now** on the Dashboard to process the reset assets.

In live and saved previews, changed characters are highlighted in both the existing and proposed names. The comparison is exact, so punctuation, capitalization, and spaces can mark a name as different even when it refers to the same place. Changed spaces appear as `␠`; other invisible characters use symbols or Unicode codes. Hover over a highlight to see its character codes. An empty value appears as `(empty)` and a missing value as `—`.

**Preview Next 100** replaces the current results, so select the assets you want to retain first. Selected GUIDs are kept across pages; **Start Preview** clears the selection. After cancellation, you can review completed results and continue after the last checked asset. If cancellation happens before any results are available, start a new preview.

**Continuous scan** advances through all matching assets automatically. Set **Pause after this many new differences** to stop for review after a chosen number of changed results; use **0** to scan to the end. **Continue Scan** resumes after the last saved asset and pauses after the next group of differences. Unresolved results and errors do not count as differences. The live table shows only the current batch of up to 100 results; the saved report contains every completed result.

Keep the preview page open while scanning. Navigating away, reloading, or closing the browser stops the scan when the page disconnects. It is not a background job. Saved results remain available, but resuming a scan requires the original page to stay open; otherwise start a new preview.

Choose **Order by → Nearby coordinates** to group photos into quarter-degree map tiles, helping nearby lookups reuse cached data. This can reduce switching between country caches, particularly with the bounded country cache, but each uncached country still needs preparing. Sorting can increase database query time for large libraries; use **Asset GUID** order if loading matching assets takes too long. Start a new preview to change the order. Both orders support continuing after the last checked asset on cancellation.

While preview runs, the page shows the current asset, lookup step or country-cache wait, and elapsed time. The progress bar counts checked assets, not downloaded data. Preparing a country for the first time can take several minutes; later lookups reuse its cache. To investigate a long wait, open **Logs** from the preview page. Preview events include a short identifier so you can follow one preview's start, cache waits, failures, cancellation, and page summaries. These events also appear in the container logs. Successful cached lookups are summarized rather than logged for every asset.

Settings are loaded once when you start the preview; start again after changing settings. Results may change if settings, source data, or coordinates change before processing.

Photo thumbnails and **Open in Immich** links are available in live previews and saved reports when the optional [Immich connection](./configuration.md#photo-previews-and-immich-links) is configured. Thumbnails load on demand; they are not part of the saved snapshot and may become unavailable if an asset is deleted or access changes.

#### Saved previews

Each completed asset result is saved automatically under **Data → Saved Previews**, including results checked before you cancel. Open a report later to compare the saved existing and proposed names, filter to differences, and copy GUIDs you have reviewed across pages. Viewing a saved report does not run new lookups or change Immich. Selections made with checkboxes are kept only while that report is open; the saved results remain available after a reload or app restart.

Reports store the selection, processing settings, asset GUIDs, GPS coordinates, names, and lookup failures in `location-previews.db` under your `/data` volume. Preserve that volume to keep reports across container replacements. Reports stay until you delete them from the report page; deleting a report removes only its local snapshot. A report marked **More assets available**, **Paused**, **Cancelled**, or **Interrupted** covers only the assets already checked. After restarting, start a new preview to check any remaining assets.

Saved values may be out of date if Immich metadata, coordinates, settings, or source data changed afterward. Run a new preview before deciding which assets to reset. If saving fails, the preview stops with an error; results already saved are retained.

Deleted assets, GUIDs without an Immich metadata row, and assets missing either latitude or longitude are excluded from every preview query, including pasted GUIDs. Unresolved locations and lookup failures are shown separately and cannot be selected for correction. Preview can help find inconsistent names; it cannot recover missing GPS coordinates or guarantee that a proposed name is correct.

### Maintenance actions

The Data area contains maintenance tools that change downloaded caches or Immich reverse geo values.

| Page or action | What it does |
|---|---|
| `Preview Location Corrections` | Compares existing and proposed names in pages of 100 without changing Immich. |
| `Clear Skip List` | Removes permanently skipped assets so they can be retried on the next run. |
| `Reset All Data` | Clears Immich reverse geo `city`, `state`, and `country` values for all matching assets and clears the skip list. |
| `Reset Single Item(s)` | Clears reverse geo values only for the pasted asset GUIDs and removes those assets from the skip list. |
| `Reset Specific Locations` | Finds assets by an existing city, state, or country value and clears all three reverse geo fields for those matching assets. |
| `Delete Overture cache` | Removes one downloaded Overture country cache file. |
| `Re-download Overture cache` | Replaces one downloaded Overture country cache with a fresh copy. |
| `Delete All Overture Divisions` | Removes every downloaded country cache so they will be fetched again on demand later. |
| `Delete GADM cache` | Removes one downloaded GADM country cache file. |
| `Re-download GADM cache` | Replaces one downloaded GADM country cache with a fresh copy. |
| `Delete All GADM Caches` | Removes every downloaded GADM cache so they will be fetched again on demand later. |

The Reset Immich Geo Data page only clears reverse geo values in `asset_exif`. It does not touch any other Immich metadata.

## Logs

Use the Logs page when you want to inspect recent activity outside the Dashboard summary.

- filter the in-app log view to all messages, warnings, or errors
- download the current filtered view as `immich-reversegeo.log`

On-screen timestamps use your browser's timezone. Downloaded logs use full UTC timestamps with dates so you can compare activity across systems. While the interactive page is connecting, timestamps are shown in UTC.

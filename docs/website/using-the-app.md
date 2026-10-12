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

To correct existing names, open [Location Management](#location-management), scan and review the proposed names, then apply only the differences you select. You can still reset selected assets and reprocess them if you prefer.

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

<a id="preview-location-corrections"></a>

### Location Management

Open **Data → Location Management → Scan & Review** to compare existing city, state, and country names with proposed values from your current processing settings. Scanning is read-only; applying selected changes is a separate confirmed action. Existing preview URLs and saved reports remain available.

1. Choose pasted asset GUIDs, an exact existing city/state/country name, or the whole library.
2. Start the scan. It checks up to 100 assets per page and may download country caches. Every scope includes only assets with both GPS coordinates. It does not write to Immich or change the skip list.
3. Review the existing and proposed values. Use **Show only different values** to focus on changes. Different does not necessarily mean better: test unfamiliar results in Lookup using the coordinates shown in the table.
4. Select only the assets you want to correct. Selections are kept across review pages. Open **Review Selected Changes**, inspect the exact existing and proposed names, and take an [Immich database backup](https://docs.immich.app/administration/backup-and-restore/).
5. Acknowledge the confirmation and choose **Confirm Apply**. The app writes the saved proposed names directly; no reset or processing run is needed. Each operation supports up to 1,000 selected differences. Its results appear beside the assets and in **Change History**.

In live and saved scans, changed characters are highlighted in both the existing and proposed names. The comparison is exact, so punctuation, capitalization, and spaces can mark a name as different even when it refers to the same place. Changed spaces appear as `␠`; other invisible characters use symbols or Unicode codes. Hover over a highlight to see its character codes. An empty value appears as `(empty)` and a missing value as `—`.

**Show only different values** keeps differences from every checked batch available while scanning continues. Browse them with **Previous 100 Differences** and **Next 100 Differences** when the scan is paused or complete. Uncheck the filter to inspect all results from the current batch. **Scan Next 100** replaces only the full-results batch; selected GUIDs are kept across review pages. **Start Scan** clears the selection and starts a new differences list. After cancellation, you can review completed results and continue after the last checked asset. If cancellation happens before any results are available, start a new scan.

**Continuous scan** advances through all matching assets automatically. Set **Pause after this many new differences** to stop for review after a chosen number of changed results; use **0** to scan to the end. **Continue Scan** resumes after the last saved asset and pauses after the next group of differences. Unresolved results and errors do not count as differences. The full-results table shows the current batch of up to 100 results. The differences view shows a review page of up to 100 differences accumulated across the scan; the saved report contains every completed result.

Keep the scan page open while scanning. Navigating away, reloading, or closing the browser stops the scan when the page disconnects. It is not a background job. Saved results remain available, but resuming a scan requires the original page to stay open; otherwise start a new scan.

Choose **Order by → Nearby coordinates** to group photos into quarter-degree map tiles, helping nearby lookups reuse cached data. This can reduce switching between country caches, particularly with the bounded country cache, but each uncached country still needs preparing. Sorting can increase database query time for large libraries; use **Asset GUID** order if loading matching assets takes too long. Start a new scan to change the order. Both orders support continuing after the last checked asset on cancellation.

While a scan runs, the page shows the current asset, lookup step or country-cache wait, and elapsed time. The progress bar counts checked assets, not downloaded data. Preparing a country for the first time can take several minutes; later lookups reuse its cache. To investigate a long wait, open **Logs** from the scan page. Scan events include a short identifier so you can follow one preview's start, cache waits, failures, cancellation, and page summaries. These events also appear in the container logs. Successful cached lookups are summarized rather than logged for every asset.

A scan counts all matching assets with both GPS coordinates when you start and shows the total checked and percentage across manual pages and continuous scans. Estimated remaining processing time appears after five checked assets and excludes review pauses. Country-cache downloads can change the estimate. Use **Scan Next 100** or **Continue Scan** to resume after a pause. If assets are added, deleted, or edited during the preview, the starting total may no longer match the final count.

Settings are loaded once when you start the scan; start again after changing settings. Results may change if settings, source data, or coordinates change before processing.

Photo thumbnails and **Open in Immich** links are available in live scans and saved reports when the optional [Immich connection](./configuration.md#photo-previews-and-immich-links) is configured. Thumbnails load on demand; they are not part of the saved snapshot and may become unavailable if an asset is deleted or access changes.

#### Saved scans

Each completed asset result is saved automatically under **Location Management → Saved Scans**, including results checked before you cancel. Open a report later to compare the saved existing and proposed names, filter to differences, and copy GUIDs you have reviewed across pages. Viewing a saved report does not run new lookups or change Immich. Selections made with checkboxes are kept only while that report is open; the saved results remain available after a reload or app restart.

Reports store the selection, processing settings, asset GUIDs, GPS coordinates, names, and lookup failures in `location-previews.db` under your `/data` volume. Preserve that volume to keep reports across container replacements. Reports stay until you delete them from the report page; deleting a report removes only its local snapshot. A report marked **More assets available**, **Paused**, **Cancelled**, or **Interrupted** covers only the assets already checked. After restarting, start a new scan to check any remaining assets.

Saved values may be out of date if Immich metadata, coordinates, settings, or source data changed afterward. Run a new scan to check current proposals, or review the saved proposals before applying; changed names and coordinates are skipped. If saving fails, the scan stops with an error; results already saved are retained.

Deleted assets, GUIDs without an Immich metadata row, and assets missing either latitude or longitude are excluded from every scan query, including pasted GUIDs. Unresolved locations and lookup failures are shown separately and cannot be selected for correction. Scanning can help find inconsistent names; it cannot recover missing GPS coordinates or guarantee that a proposed name is correct.

#### Applying saved proposals

You can apply selected differences from a live scan or a saved scan. The app uses the exact saved proposed names, without running another lookup. It compares the current city, state, country, and GPS coordinates with the reviewed snapshot before each write. Assets with changed values, deleted assets, and missing metadata rows are skipped. Unresolved and failed lookup results cannot be applied. Punctuation-only changes are still differences; review them before selecting.

Applying changes updates only city, state, and country. GPS coordinates and other metadata are preserved. Refresh the asset in Immich to see its new names. Scheduled metadata extraction or other apps can subsequently replace those names; run a new scan if the current results differ.

#### Change history, undo, and interruption

**Location Management → Change History** records each selected operation and its per-asset outcomes. Open an operation to see its saved previous names, exact proposals, timestamps, and applied, skipped, pending, or **Needs review** results. Select applied rows, review the previous names to restore, and confirm **Undo**. Undo requires the current applied names, GPS coordinates, and metadata version to match; any later metadata edit is skipped, even if its location names are identical.

The history overview shows pending apply and undo work separately. If any selected change is no longer eligible for undo, the app queues none of that selection; refresh the results and review your selection again.

Cancel or leave the page to stop remaining work. A write already underway may complete. Pending items have not been attempted and can be resumed from Change History after confirmation. Applied items are not retried. If the app loses the connection or stops between a database write and saving its outcome, the item is marked **Needs review**. Check that asset in Immich and run a new scan before making another change; uncertain items are never retried or undone automatically.

Previous names and operation results are saved in `location-changes.db` under `/data`, separately from saved scans. Preserve this volume to retain history and undo information. Deleting a saved scan does not delete its change history. The journal contains location metadata; protect it along with your other app data. It does not replace an Immich database backup, and undo cannot restore deleted assets or override later edits.

### Maintenance actions

The Data area contains maintenance tools that change downloaded caches or Immich reverse geo values.

| Page or action | What it does |
|---|---|
| `Location Management` | Scan and review names, apply selected differences, and inspect or undo changes. |
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

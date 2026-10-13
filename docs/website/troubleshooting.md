---
description: "Troubleshoot database access, location matching, cache downloads, and settings problems."
icon: material/wrench-outline
---

# Troubleshooting

<div class="section-intro">
Most issues come down to one of three things: the app cannot see the right database values, the app is still downloading country data, or the running process has stale state after a change.
</div>

## The app shows default DB values instead of my real ones

The app reads real process environment variables. If you changed Windows user or system environment variables after Visual Studio or your shell was already open, restart that process.

## Country lookup says no match

Check:

- the bundled `overture-country-divisions.db` file exists in the bundled data directory inside the app image
- the running app was restarted after data or code changes
- the coordinate actually has latitude and longitude in immich

## A country cache download is slow

The first lookup for a new country can take longer because the per-country Overture division cache must be created locally.

Large countries can also produce much larger cache files than small ones.

<div class="feature-grid">
  <div class="card">
    <h3>First run is slower</h3>
    <p>The app has to download and prepare country data the first time you hit a country it has not seen before.</p>
  </div>
  <div class="card">
    <h3>Bigger countries take longer</h3>
    <p>Large countries usually mean larger downloads and more time spent preparing the local data.</p>
  </div>
</div>

## Processing seems slower than expected

Things that affect throughput:

- batch size
- max parallelism
- database latency to the immich PostgreSQL instance
- first-time per-country cache creation

<div class="step-grid">
  <div class="step-card">
    <h3>Start with the easy checks</h3>
    <p>If processing feels slow, first check whether the app is still downloading country data for the first time.</p>
  </div>
  <div class="step-card">
    <h3>Then tune settings</h3>
    <p>Batch size and max parallelism usually make the biggest difference once the needed country data is already present.</p>
  </div>
</div>

## The container keeps restarting or gets killed

This usually means the container ran out of memory.

Check whether the restarts line up with processing runs:

```bash
docker inspect --format '{{.RestartCount}} {{.State.OOMKilled}}' immich-reversegeo
```

If `OOMKilled` is `true`, or the logs end with `System.OutOfMemoryException`, raise the memory
ceiling. The compose snippet in [Installation](./installation.md#docker) sets `mem_limit: 2g`,
as a starting memory budget. If your Compose file has no `mem_limit`, add one to set a total
container memory budget that the .NET runtime can take into account. Large country caches or
higher parallelism may require a higher limit.

Lowering the limit is not a fix. The bundled country boundary data alone needs several hundred
megabytes once loaded, so a limit below that makes the container fail straight away.

Reducing **max parallelism** in processing settings also lowers peak memory, at the cost of
throughput.

## I want to rerun everything from scratch

The Data page can clear existing `city`, `state`, and `country` values in immich.

See [Using the App](./using-the-app.md) for the reset options and what each one does.

Because this is a destructive write operation against immich metadata, back up your database first:

- [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## Settings cannot be read or saved

If logs report that `settings.json` cannot be read, scheduled processing pauses and retries once a minute. The file is left unchanged; the app does not substitute default settings for a damaged file.

Check that the `/config` volume is writable and restore `settings.json` from a known-good backup if its contents are damaged. Keep a copy of the damaged file while investigating. After restoring it, reload the Settings page and check the values before starting a run.

If a save fails, the previous settings file remains available. Check volume permissions and free disk space, then retry the save.

## Connection interrupted or session expired

The app tries to reconnect after a temporary connection failure. If the server no longer has your session, the dialog asks you to **Reload page** and warns that unsaved changes may be lost. Reload starts a new session; it does not recover unsaved form values.

If retrying does not work, check that the container is running and your private connection to it is available, then reload.

Disconnecting stops page-owned scans and remaining apply/undo work. A write already underway may finish. After reconnecting:

- Open **Location Management → Saved Scans** and choose **Continue scan** on an unfinished report. Checked results are kept; continuation uses the original scan settings. A completed scan needs no continuation.
- Open **Change History** to check apply/undo outcomes. Review pending work before resuming it. For **Needs review**, inspect the affected asset in Immich and run a new scan before another change; uncertain writes are not retried automatically.
- Check the **Dashboard** for background processing status. Reconnecting does not start another processing pass.

Reloading clears live checkbox selections and unsaved form edits. Reopen the saved report and select results again; saved reports and change history remain available as long as the `/data` volume is preserved.

## Overture release discovery fails

Check that the app can reach `https://stac.overturemaps.org/catalog.json`, then retry the lookup or country download. Overture keeps public releases for only 60 days, so the app does not fall back to a fixed older version. Existing downloaded country caches and built-in data remain usable without release discovery. Optional live Places diagnostics still need internet access.

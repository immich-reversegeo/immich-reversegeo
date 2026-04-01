---
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

## I want to rerun everything from scratch

The Data page can clear existing `city`, `state`, and `country` values in immich.

See [Using the App](./using-the-app.md) for the reset options and what each one does.

Because this is a destructive write operation against immich metadata, back up your database first:

- [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

---
description: "Set up Immich ReverseGeo with Docker, check a coordinate, and prepare your first processing run."
icon: material/rocket-launch-outline
---

# Getting Started

This is the fastest end-user path to a first working setup with Docker.

Use Immich ReverseGeo when you want more accurate or more useful location names in immich than the default built-in reverse-geocoding results are giving you.

What is different here is that Immich ReverseGeo uses better location data and more careful matching than immich's built-in reverse geocoding. That usually gives better results for things like coastlines, islands, airport areas, and other places where the default result feels too broad or inaccurate.

!!! danger "Disable immich's built-in reverse geocoding first"
    Immich ReverseGeo and immich's own reverse geocoding both write location fields.

    If both are enabled at the same time, they can overwrite each other's values and cause inconsistent results.

    Turn off immich's built-in reverse geocoding before you start processing with Immich ReverseGeo.
    See immich's official docs:
    [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/)
    and
    [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).

!!! warning "Back up immich before processing or clearing location data"
    Immich ReverseGeo writes `city`, `state`, and `country` values back into immich's database.

    Before processing, applying location corrections, or clearing existing location fields, create a proper immich database backup first.

    Follow the official guide:
    [Backup and Restore | Immich](https://docs.immich.app/administration/backup-and-restore/)

## 1. Make sure you can reach the immich database

Immich ReverseGeo needs direct access to the same database your immich instance uses.

In Docker setups, that usually means:

- joining the same Docker network as immich
- passing the immich database connection values through to this container

Typical variables:

```env
DB_HOSTNAME=database
DB_PORT=5432
DB_USERNAME=...
DB_PASSWORD=...
DB_DATABASE_NAME=immich
```

Existing `DB_HOST` settings remain supported and take precedence over `DB_HOSTNAME` when nonblank. If neither has a nonblank value, the host defaults to `database`.

## 2. Start the container

Use Docker with persistent mounts for:

- `/config` for settings
- `/data` for downloaded country data

Add the service to your existing Immich Compose file. See [Installation](./installation.md) for the snippet or a separate-stack setup.

```bash
docker compose up -d immich-reversegeo
```

## 3. Open the UI

Default container URL:

```text
http://localhost:8080
```

??? info "Which computer does localhost refer to?"
    `localhost` means the computer running your browser. If Docker runs on another machine, use a private connection to that machine, such as the SSH tunnel below.

If port `8080` is already in use on your host, change the left side of the compose port mapping and open that port instead.

If the container runs on a remote server, do not expose the UI publicly. Docker-published ports can bypass host firewall rules such as UFW. For a VPS, bind the published port to localhost and use SSH local port forwarding, a VPN, or a trusted reverse proxy with authentication:

```bash
ssh -N -L 8080:localhost:8080 user@host
```

## 4. Test one coordinate

Use the Lookup page to confirm the basics before a full run:

<div class="step-grid">
  <div class="step-card">
    <h3>Country matching</h3>
    <p>Make sure the country is detected correctly for a coordinate you know well.</p>
  </div>
  <div class="step-card">
    <h3>State and city matching</h3>
    <p>Check that the result is reasonably precise and not too broad or generic.</p>
  </div>
  <div class="step-card">
    <h3>Airport areas</h3>
    <p>If you have airport photos, confirm they resolve the way you would expect.</p>
  </div>
</div>

## 5. Start manual processing

After checking known coordinates in Lookup, use `Run Now` from the Dashboard for your first manual pass. A smaller batch size reduces work per batch; it does not limit the total number of assets in the run. Inspect the resulting location names in Immich and use `Cancel` if you need to stop processing.

Processing selects GPS-tagged assets only when both the city and country are missing (stored as `NULL` in the database). Assets with an existing city or country are not selected, even if the other value is missing.

### Correct existing location names

Use **Scan & Review Existing Names** on the Dashboard to open Location Management. Scanning compares existing names with proposed names without changing Immich.

1. Start with a few asset GUIDs or one existing location before scanning the whole library.
2. Review the differences and use **Inspect in Lookup** to check unfamiliar results. A spelling or punctuation difference does not necessarily mean a better location.
3. Select the differences you want, open **Review Selected Changes**, and check the exact names. After taking a database backup, acknowledge the confirmation and choose **Confirm Apply**.
4. Refresh those assets in Immich to check the result. Applying writes the reviewed names directly; no reset or processing run is needed.

You can reopen completed results in **Saved Scans** and inspect applied changes or review an undo in **Change History**. See [Location Management](./using-the-app.md#location-management) for continuation, filters, and undo limits.

For the advanced reset-and-reprocess workflow, use **Reset Selected Assets** or **Reset by Existing Location** on **Reset Immich Geo Data**, then select **Run Now**. Reset clears all three location fields—city, state, and country—for the selected assets. See [Maintenance actions](./using-the-app.md#maintenance-actions) before confirming.

For more on the UI after setup, see [Using the App](./using-the-app.md).

# Immich ReverseGeo

Immich ReverseGeo is a self-hosted companion service for [immich](https://immich.app) that improves the accuracy and usefulness of reverse-geocoded location names for photo assets.

It is built for people who already have GPS coordinates on their assets and want a local, repeatable way to write better `city`, `state`, and `country` values back into immich than the built-in basic reverse-geocoding flow typically provides.

Immich ReverseGeo is an independent project and is not affiliated with immich. immich is a great product, and this project is built to work alongside it.

## What It Does

- Reads unprocessed immich assets that already have latitude and longitude
- Resolves better country, state, and city names
- Uses built-in airport data to improve results around airports and terminals
- Writes improved location names back into immich
- Includes a local web UI for setup, lookups, downloads, and operations

## Important Notes

- Immich ReverseGeo needs direct access to the same PostgreSQL database your immich instance uses.
- Disable immich's own reverse geocoding before using Immich ReverseGeo, otherwise both systems can overwrite the same location fields and fight each other. immich docs:
  [Reverse Geocoding](https://docs.immich.app/features/reverse-geocoding/) and
  [Reverse Geocoding Settings](https://docs.immich.app/administration/system-settings/#reverse-geocoding-settings).
- It writes location fields back into immich, so you should take a database backup first.
- The Data page includes an option to clear existing `city`, `state`, and `country` values in immich before reprocessing.
- The app needs internet access the first time it downloads extra location data for a country.
- Large downloaded country data can take a lot of disk space. Bigger countries can approach `~500 MB` each, and multiple countries can grow into many gigabytes on disk.
- If you rebuild or switch containers and see antiforgery or key-ring errors in the browser, restart with the same persisted `/config` volume and reload the page. You may need to clear old browser cookies once after changing setups.

## Documentation

- Installation and usage docs: [immich-reversegeo.github.io](https://immich-reversegeo.github.io/)
- Contributor workflows: [CONTRIBUTING.md](./CONTRIBUTING.md)

## Docker

The app expects:

- database connection values from environment variables
- persistent app data under `/data`
- persistent settings under `/config`

End users should be able to start the published image with:

```bash
docker compose up -d
```

using the provided `docker-compose.yml` and the environment values required for their immich setup.

See the official installation guide: [immich-reversegeo.github.io/installation](https://immich-reversegeo.github.io/installation/).

## Contributing

Contributor and local-development workflows live in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Changelog

Technical release notes live in [CHANGELOG.md](./CHANGELOG.md).

## License

Immich ReverseGeo is licensed under the GNU Affero General Public License v3.0.

See [LICENSE](./LICENSE).

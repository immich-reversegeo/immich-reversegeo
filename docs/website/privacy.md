---
description: "How Immich ReverseGeo handles library metadata, local settings, and geographic data downloads."
icon: material/shield-lock-outline
---

# Privacy

Immich ReverseGeo is a self-hosted project intended to run in your own environment.

Operationally:

- asset coordinates are read from your immich database
- resolved place names are written back to your immich database
- bundled and cached data is stored locally on your system
- preview reports save asset GUIDs, coordinates, existing and proposed names, and processing settings locally in your `/data` volume; they remain until you delete the saved report

The app UI uses local system fonts and does not request fonts from an external provider.

If photo previews are configured, Immich ReverseGeo fetches thumbnails from your Immich server when you view preview results. The API key stays on the server; thumbnail requests are limited to assets in saved preview reports. Image files are not added to the saved reports.

The optional Nearby Places investigation tool on the Lookup page may contact Overture-hosted public data sources when enabled.

You are responsible for how and where you deploy the service.

#!/usr/bin/env bash
# scripts/download-data.sh
# Downloads ADM0 world boundaries and OurAirports for local development.
# Docker builds do this inline; run this script for non-Docker dev.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA_DIR="$SCRIPT_DIR/../data"

echo "Downloading Natural Earth 10m admin-0 countries (~25 MB)..."
mkdir -p "$DATA_DIR/adm0"
curl -L -o "$DATA_DIR/adm0/ne_10m_admin_0_countries.geojson" \
  "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_10m_admin_0_countries.geojson"

echo "Downloading OurAirports airports.csv..."
mkdir -p "$DATA_DIR/airports"
curl -L -o "$DATA_DIR/airports/airports.csv" \
  "https://davidmegginson.github.io/ourairports-data/airports.csv"

echo "Done. Files written to $DATA_DIR"

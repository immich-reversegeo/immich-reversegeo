# DuckDB.NET cache-builder comparison

This opt-in harness calls the production `EnsureDataAsync` pipeline: Parquet staging, streamed names/polygon import, cache validation, and atomic publication. It does not modify Immich or use production settings.

Use a dedicated checkout. Publish the harness first with the three production DuckDB.NET package references at the baseline version, then publish again after changing them to the candidate version. Both binaries must contain the same builder and harness source; keep separate output directories.

```powershell
dotnet publish tests/CacheBuilderBenchmark -c Release -o _out/duckdb-benchmark/baseline --self-contained false /p:UseAppHost=false
# Update package references, then:
dotnet publish tests/CacheBuilderBenchmark -c Release -o _out/duckdb-benchmark/candidate --self-contained false /p:UseAppHost=false

# Download the same pinned public inputs once. This is outside the timings.
dotnet _out/duckdb-benchmark/baseline/CacheBuilderBenchmark.dll snapshot _out/duckdb-benchmark/inputs FRA
dotnet _out/duckdb-benchmark/baseline/CacheBuilderBenchmark.dll snapshot _out/duckdb-benchmark/inputs USA

pwsh -NoProfile -File tests/CacheBuilderBenchmark/Run-Comparison.ps1 `
  -Inputs _out/duckdb-benchmark/inputs `
  -Baseline _out/duckdb-benchmark/baseline/CacheBuilderBenchmark.dll `
  -Candidate _out/duckdb-benchmark/candidate/CacheBuilderBenchmark.dll `
  -Results _out/duckdb-benchmark/results
```

The input snapshot preserves the country-filtered source columns consumed by the real builder from Overture release `2026-09-23.1`, including the production source-country aliases. SHA-256 hashes are printed when downloaded. Use a fresh input directory if a snapshot fails partway through.

Each build has a 1 GiB DuckDB budget and four threads, runs in a fresh process, and writes a fresh cache. Round zero warms filesystem/extension caches and is excluded; three measured rounds alternate version order. Run without concurrent builds or tests. Budget several GB of disk space. Caches and raw results stay under ignored `_out/`.

`verify` compares both directions of every polygon and normalized-name row, including geometry bytes, hierarchy JSON, and translations. Download timestamps are intentionally excluded. The summary reports medians. Full-build timing includes staging and validation; `importMs` samples the names/polygon import phases every 5 ms and is approximate. Allocation totals include harness overhead; peak working set is a whole-process high-water mark, not the DuckDB memory limit. These measurements do not predict network download time or steady-state SQLite lookup performance.

To check Linux compatibility, run the published candidate in a .NET 10 ASP.NET runtime container with the checkout mounted at `/work`:

```sh
dotnet /work/_out/duckdb-benchmark/candidate/CacheBuilderBenchmark.dll compatibility
```

This loads the native engine and spatial extension through the production bootstrap, checks Linux curl transport, and reads one row from public Azure Parquet data. `build` can also run against the shared snapshots in that container to validate the complete Linux exporter. No performance comparison between Windows and Linux is implied.

# DuckDB country-cache benchmark

This harness compares the old combined country query with the staged export used by the app. It runs in fresh Linux containers using the same .NET runtime base and DuckDB package as the app. It never connects to Immich or uses the running app's volumes.

Run from the repository root with Docker Desktop in Linux mode:

```powershell
# Fast, deterministic pipeline and output-validation smoke test (no Overture reads).
./tests/OvertureCacheBenchmark/Run-Benchmark.ps1 -Fixture

# Pin the release and country for a real Azure comparison.
./tests/OvertureCacheBenchmark/Run-Benchmark.ps1 -Release 2026-09-23.1 -Country ES

# Repeat in reversed order on alternating runs to expose ordering/network bias.
./tests/OvertureCacheBenchmark/Run-Benchmark.ps1 -Release 2026-09-23.1 -Country ES -Repetitions 2 -SkipBuild

# Verify the existing low-memory budget without applying tuning overrides.
./tests/OvertureCacheBenchmark/Run-Benchmark.ps1 -Fixture -LowMemory -Variants original-default,phased-default -SkipBuild
```

The default matrix changes one setting at a time after the phased baseline:

| Variant | Query | Azure buffer | DuckDB threads |
|---|---|---|---|
| `original-default` | Original combined SQL | Installed default | Installed default |
| `original-threads8` (optional) | Original combined SQL | Installed default | 8 |
| `phased-default` | Staged SQL | Installed default | Installed default |
| `phased-buffer32` | Staged SQL | 32 MiB | Installed default |
| `phased-threads8` | Staged SQL | Installed default | 8 |
| `phased-buffer32-threads8` (optional) | Staged SQL | 32 MiB | 8 |

The Azure chunk size and transfer concurrency remain at the installed defaults. The harness records effective settings; do not infer them from online documentation. DuckDB 1.5.6 in our image currently reports 8 MiB for both the Azure buffer and chunk size. With those equal, a larger concurrency limit alone does not cause parallel buffer filling. A 32 MiB buffer allows multiple 8 MiB chunks to be fetched concurrently, at a higher memory cost.

Every run has a fresh DuckDB instance/container, a two-CPU quota, and a 2 GiB container limit by default. Adjust `-CpuCount`, `-MemoryMiB`, and `-TimeoutSeconds` explicitly when comparing hosts. Shared infrastructure, OS caches, network caches, and server throttling are not reset. Record repeated runs before changing runtime defaults. Keep the Linux Azure `curl` transport.

Outputs are under `_out/duckdb-cache-benchmark/<run>/` and include:

- `comparison.json` and per-run `summary.json`: query wall time, phase times, effective settings, process peak working set, cgroup peak memory, row counts, and a logical SHA-256 fingerprint.
- `phase-*-plan.txt`: `EXPLAIN ANALYZE` plans with `azure_http_stats=true`, including available Azure request and transfer statistics.
- `query-result.parquet`: retained output for inspection, plus deterministic inputs in fixture runs.
- Console logs, including native query estimates during long phases.

The original and phased variants export identical projected columns to Parquet. The comparison validates all field values, geometry bytes, hierarchy JSON, and duplicate rows with sorted per-row hashes. It fails on a row-count or fingerprint mismatch. It measures query/export work, not SQLite cache writes, asset processing, or thumbnail rendering. The logical validation pass is outside `querySeconds`; setup and validation are included in `totalSeconds`. Phase times are wall times and should not be confused with summed parallel operator CPU times.

`EXPLAIN ANALYZE` runs the export itself once; it does not execute a second remote export for profiling. Profiling and writing the inspection Parquet add overhead to both variants. Fixtures establish correctness and container compatibility, not network speed. Results from a developer's Docker host do not establish performance on the production NAS.

The normal app stages country names, boundary rows, and parent hierarchies as Parquet under a unique `/data/duckdb-cache-work/<id>/` directory. These files allow distinct query phases without retaining the full intermediate dataset in application memory. They are cleaned up after success, failure, and cancellation; a killed container can leave its directory behind. Parquet groups are limited to 2,048 rows to limit writer buffering. This adds temporary disk I/O and requires spare disk space. Benchmark the complete export before claiming an improvement.


## Initial Linux trial (2026-10-14)

Release `2026-09-23.1`, Spain (`ES`), two-CPU quota, 2 GiB container limit, DuckDB 1.5.6, one run per variant:

| Variant | Query/export time | Result |
|---|---:|---|
| Original combined SQL, defaults | Timed out at 1,200 s | No completed comparison output |
| Phased, defaults (2 threads, 8 MiB buffer) | 149.45 s | 29,667 rows |
| Phased, 32 MiB buffer | 156.40 s | 29,667 rows |
| Phased, 8 threads | 66.28 s | 29,667 rows |

All completed phased runs had logical SHA-256 `111F92576B6BF253EF6C3F31D0FF2EA29B407087CC10DF1E27C30D971ABFA291`. Normal and low-memory Linux fixtures matched the original SQL. The production app image also completed a fresh Spain export: its SQLite cache passed integrity checks and matched all 15 columns of the phased Parquet result, including geometry bytes and hierarchy JSON. Its staging directory was empty afterward. The UI showed phase transitions and a separate percentage for SQLite rows written.

These are single developer-host trials, not NAS performance predictions. The initial baseline may have overlapped the NAS's own downloads, and shared network/server caches were not controlled. Its timeout does not establish a precise speedup factor. A larger buffer showed no benefit here; more threads warrant repeated testing on the target host. Runtime thread, Azure buffer, and low-memory defaults remain unchanged. The app offers an optional **DuckDB threads** setting (0 means automatic); low-memory mode always uses one thread.

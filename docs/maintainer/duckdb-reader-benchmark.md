# DuckDB.NET 1.5.6.1 builder comparison

Measured on 2026-10-25. This is an implementation-level dependency update; no material user-facing performance improvement was demonstrated.

## Method

- Same production builder and benchmark source; only DuckDB.NET.Data.Full changes from 1.5.6 to 1.5.6.1. Both report native DuckDB v1.5.6.
- Windows x64, .NET 10.0.13, Release binaries (built with the installed .NET 11.0.100-rc.2 SDK). Four DuckDB threads, configured 1 GiB DuckDB budget.
- Public Overture release 2026-09-23.1. Capture the required source columns once, using production country filtering, including France's source aliases. Both versions read exactly the same local Parquet files. Network transfer is excluded.
- Fresh process and output directory for every build; full EnsureDataAsync staging, streamed import, validation, and publication. One unmeasured warmup per version/country; three measured rounds with alternating version order. No other task builds/tests ran during measurement.
- Compare every polygon/name row in both directions after every pair. Geometry bytes, hierarchy JSON and translations must match; download timestamps are excluded.

See the [harness instructions](../../tests/CacheBuilderBenchmark/README.md) and comparison script to repeat the measurement. Raw result JSON, caches, and downloaded inputs stay under ignored `_out/duckdb-benchmark/`.

## Results

| Country | Version | Full build median (range), s | Import median, s | Managed allocations, MiB | Process peak RSS, MiB |
|---|---|---:|---:|---:|---:|
| FRA | 1.5.6 | 6.521 (6.086-6.537) | 5.033 | 1249.8 | 705.9 |
| FRA | 1.5.6.1 | 6.926 (6.309-8.155) | 5.634 | 1249.8 | 711.1 |
| USA | 1.5.6 | 4.701 (4.223-4.820) | 3.856 | 718.7 | 437.5 |
| USA | 1.5.6.1 | 4.688 (4.530-6.362) | 3.766 | 718.5 | 441.7 |

France's full-build median was 6.2% slower, while USA was 0.3% faster. Three samples and the observed variability do not establish a general speedup or a consistent regression. Allocation differences were below 0.1%, and there was no meaningful peak-RAM reduction. Do not use the library's reader microbenchmarks as an app-wide performance claim.

Import time samples phases 3-4 at 5 ms intervals and is approximate. Allocations include harness overhead. RSS is a whole-process high-water mark, not the DuckDB budget. Cached administrative lookups use SQLite and are outside this comparison.

Every comparison passed with 57,178 polygon / 57,176 name rows for France and 60,264 polygon / 60,065 name rows for USA. SQLite file sizes can vary slightly with parallel row insertion order even when all logical rows are identical.

## Input provenance

| Country | Source | Bytes | SHA-256 |
|---|---|---:|---|
| FRA | division | 38699078 | `8382e1be3359ed12ef970286ab551893d1e27f75b3f1934d7f711ff61802fd7a` |
| FRA | division_area | 461724766 | `a36b3a7d46ec8b7962e9d1e73054d67bb869a6d0477028ed262f97e14496d76b` |
| USA | division | 23398344 | `6fd01f7b76d5929228f2f9210126a7a7137a47134e848987312a5f6f24349a4d` |
| USA | division_area | 229765248 | `d9b7a2a764b7a129020bb67b0ba8b1efdfd4bd4c8301abec16ce60bc6ce20963` |

## Compatibility

- 644 normal tests passed, including actual-exporter constrained-memory and language fallback regressions.
- 7 browser workflows passed, covering desktop/mobile language settings, preview/processing agreement, and old-cache failure/retry recovery.
- Production Linux x64 Docker image built successfully and served an HTTP 200 app page on an isolated localhost port.
- Native loading, spatial functions, Linux curl transport, and a real public Azure Parquet read passed inside the non-root chiseled production image.
- Cache format remains 3. No cache rebuild or settings change is required by the provider upgrade.
- Complete France and USA builders also passed in the non-root production Linux x64 image, using the same snapshots and 1 GiB/four-thread configuration. All polygon/name rows matched the Windows baseline. Windows bind-mount I/O makes these Linux smoke-run timings unsuitable for performance comparison. ARM64 runtime behavior was not exercised.

The public release summary intentionally has no new performance claim for this internal dependency update. The complete implementation note remains in the [technical changelog](../../CHANGELOG.md).

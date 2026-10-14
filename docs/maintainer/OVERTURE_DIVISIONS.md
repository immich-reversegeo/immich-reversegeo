# Overture Divisions Notes

Use `division_area` for containment.

- `division_area` is the reliable source for point-in-polygon checks and any claim that a coordinate is "in" a city, neighborhood, region, or country.
- `division` is point-based. It is useful for labels, hierarchy, and diagnostics, but it is not a reliable containment source.
- `division_area.division_id` links an area back to its parent `division`, but the relationship is not guaranteed to be 1:1.
- A `division` can exist without any `division_area`.
- A `division` can also have multiple `division_area` rows.

## Practical rule

- Keep admin resolution based on containing `division_area` rows.
- Do not let a bare `division` point override a containing `division_area` result.

## Bundled country artifact

Country bootstrap exports Overture `country` and `dependency` `division_area` rows directly. The exporter applies the shared canonical ISO catalog and rejects the artifact unless every mandatory territory and parent-country fixture has exact coverage from its expected canonical identity. Runtime regression tests separately verify which overlapping candidate wins.

Pinned release: `2026-08-19.0` (previous bundled release: `2026-03-18.0`). This is a data refresh as well as added dependency coverage; all 378 prior country IDs remain, but 281 country WKB blobs differ.

| Measurement | Previous artifact | Current artifact | Change |
|---|---:|---:|---:|
| Rows | 378 | 483 | +27.8% |
| Source identities | 219 | 272 | +24.2% |
| Database size | 162,414,592 bytes | 167,931,904 bytes | +3.40% |
| WKB size | 161,847,272 bytes | 167,239,276 bytes | +3.33% |
| Retained managed memory | 543,809,040 bytes | 559,270,232 bytes | +2.84% |
| Cold initialization | 2,315.74 ms | 2,198.54 ms | Similar in this run |
| Warm lookup average | 0.05318 ms | 0.05150 ms | Similar in this run |
| Warm allocation per lookup | 2,383 bytes | 2,385 bytes | Similar in this run |

The current artifact contains 105 dependency rows across 53 additional source identities. Validation recognized 248 standard identities. Both artifacts were measured with the current Release resolver on Windows x64 and .NET 10.0.12, in separate processes, with 100 warm-up and 10,000 measured Berlin lookups. Retained managed memory is the full-GC heap delta for the country index, not total container memory. These are single-run timings and are not evidence of a speed improvement; both remain around 0.05 ms and 2.4 KB per lookup.

Older geographic databases without an `alpha3` column use the bundled catalog to resolve existing country codes without rewriting the database. If the column exists, a missing or conflicting value remains an identity error. Known territory identities are ranked before their stored Alpha-3 is checked, so broken territory data reports an error rather than silently returning an overlapping sovereign.

Equal spatial candidates use deterministic ordering. Explicit administrative levels precede missing levels; stable IDs break otherwise equal ties. Dependencies receive priority over sovereign countries only in bundled country lookup.

Release budgets enforced by the performance test are no more than 5% file/WKB growth, no more than 5x WKB size in retained managed memory, cold initialization under 30 seconds, and warm lookup average under 250 ms.

Regenerate with `npm run export:country-divisions`. The exporter pins the requested release in `_meta`, validates the complete fixture catalog before replacing the existing file, and prints row, identity, WKB, and file-size totals.

## Example: neighborhood-style names

If a place such as `Kreuzberg` exists only as a `division` label with parent `Berlin`, but has no linked `division_area`, the app should keep the containing area-based result such as `Berlin`.

Only switch to the more specific neighborhood-style name when Overture provides a real containing `division_area` for it, or when another trusted boundary source is introduced.

## Country database export and validation

The maintainer exporter includes both Overture `country` and `dependency` boundaries, writes canonical names and Alpha-2/Alpha-3 codes from the bundled catalog, and retains source names/codes for traceability. Unknown source identities fail the export unless explicitly classified as non-ISO. Unmapped non-ISO codes retain their source identity; only catalog aliases may change a source code into a canonical identity.

Generate a candidate into ignored output rather than overwriting the shipped database during review:

```powershell
dotnet run --project src/ImmichReverseGeo.Tools.OvertureExport -- --dataset country-divisions --release 2026-08-19.0 --output _out/country-export/country.db
```

The exporter validates required columns, identities, valid polygon boundaries, source bounds, release metadata, and exact coverage for the shared territory and mainland coordinates before replacing the requested output. On failure the previous output remains in place and the temporary export is removed.

Recheck an existing candidate without network access or file changes:

```powershell
dotnet run --project src/ImmichReverseGeo.Tools.OvertureExport -- --dataset country-divisions --validate-only --output _out/country-export/country.db
```

Validation checks that the expected identity has exact boundary coverage at each coordinate. It does not choose the runtime winner when boundaries overlap, or validate the runtime resolver's tolerance and routing behavior. Those belong in resolver tests. The existing shipped database predates the new export schema and is not expected to pass this validator. Shipping a replacement is a separate change.

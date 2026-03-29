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

## Example: neighborhood-style names

If a place such as `Kreuzberg` exists only as a `division` label with parent `Berlin`, but has no linked `division_area`, the app should keep the containing area-based result such as `Berlin`.

Only switch to the more specific neighborhood-style name when Overture provides a real containing `division_area` for it, or when another trusted boundary source is introduced.

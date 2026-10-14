# Swiss boundary regression fixtures

`SwissBoundaryFixtures.json` contains Overture Maps division areas from release
`2026-09-23.1`, downloaded through the Linux Docker app on 2026-10-13.
It covers the reported Aarau (47.3773, 8.0298) and Baden (47.446267, 8.289111)
coordinates. The current Swiss municipal boundary API independently identified
Aarau (BFS 4001) and Baden (BFS 4021), both in Aargau.

Polygons are clipped to a rectangle extending 0.002 degrees around each point.
Original IDs, bounding rectangles, names and hierarchy information are retained.
Clipping preserves the exact and tolerance results at the test coordinates;
these excerpts are not general-purpose municipal boundaries.

Both neighbouring municipalities are outside exact containment but intersect
the app's tolerance rectangle. Aarau and Baden contain their respective points
exactly. This dataset also retains the inconsistent Olten county boundary rather
than correcting source data within the fixture.

Source: https://overturemapswestus2.blob.core.windows.net/release/2026-09-23.1/theme=divisions/

Overture Maps divisions are licensed under ODbL. See
https://docs.overturemaps.org/attribution/ and the repository's bundled
`OVERTURE-ATTRIBUTION.txt` for source attribution, including OpenStreetMap
contributors. These clipped excerpts remain under the same licence.

namespace ImmichReverseGeo.Tests.Fixtures;

/// <summary>
/// Minimal GeoJSON fixtures for unit tests — simplified bounding polygons, not real shapes.
/// CHE covers roughly 6.0–10.5 E, 45.8–47.8 N.
/// AUT covers roughly 9.5–17.2 E, 46.4–49.0 N.
/// Sea point: (0, 0) — middle of the Atlantic, no ADM0 match.
/// </summary>
public static class MiniGeoJson
{
    public const string Adm0 = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "ISO_A3": "CHE", "ADM0_A3": "CHE", "NAME": "Switzerland" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[6.0,45.8],[10.5,45.8],[10.5,47.8],[6.0,47.8],[6.0,45.8]]]
              }
            },
            {
              "type": "Feature",
              "properties": { "ISO_A3": "AUT", "ADM0_A3": "AUT", "NAME": "Austria" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[9.5,46.4],[17.2,46.4],[17.2,49.0],[9.5,49.0],[9.5,46.4]]]
              }
            }
          ]
        }
        """;

    // ADM1 for Switzerland — one canton
    public const string CheAdm1 = """
        {
          "type": "FeatureCollection",
          "features": [
            {
              "type": "Feature",
              "properties": { "shapeISO": "CHE-ZH", "shapeName": "Zurich" },
              "geometry": {
                "type": "Polygon",
                "coordinates": [[[8.4,47.2],[8.9,47.2],[8.9,47.7],[8.4,47.7],[8.4,47.2]]]
              }
            }
          ]
        }
        """;
}

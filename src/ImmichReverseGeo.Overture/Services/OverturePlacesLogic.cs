using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class OverturePlacesLogic
{
    public const string LatestCatalogUrl = "https://stac.overturemaps.org/catalog.json";
    public const string PlacesReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=places/type=place/*.parquet";
    public const string InfrastructureReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=base/type=infrastructure/*.parquet";
    public const string DocumentedFallbackRelease = "2026-02-18.0";
    public const double SearchRadiusMetres = 2_500;
    public const double InfrastructureSearchRadiusMetres = 8_000;
    public const int QueryLimit = 80;
    public const double MinimumInterestingConfidence = 0.90;

    private static readonly string[] IncludedCategoryTerms =
    [
        "airport",
        "station",
        "terminal",
        "museum",
        "gallery",
        "theater",
        "theatre",
        "monument",
        "landmark",
        "historic",
        "castle",
        "church",
        "cathedral",
        "mosque",
        "temple",
        "synagogue",
        "shrine",
        "park",
        "beach",
        "island",
        "mountain",
        "marina",
        "harbor",
        "harbour",
        "port",
        "ferry",
        "bridge",
        "square",
        "plaza",
        "neighborhood",
        "neighbourhood",
        "district",
        "zoo",
        "aquarium",
        "stadium",
        "arena",
        "convention",
        "library",
        "university",
        "government",
        "tourism",
        "attraction",
        "scenic"
    ];

    private static readonly string[] ExcludedCategoryTerms =
    [
        "restaurant",
        "cafe",
        "coffee",
        "bar",
        "pub",
        "nightlife",
        "shop",
        "retail",
        "store",
        "mall",
        "supermarket",
        "grocery",
        "convenience",
        "pharmacy",
        "bank",
        "atm",
        "fuel",
        "gas_station",
        "parking",
        "hotel",
        "motel",
        "lodging",
        "clinic",
        "hospital",
        "office",
        "industrial",
        "warehouse",
        "residential",
        "apartment",
        "fitness",
        "beauty",
        "laundry",
        "car_wash",
        "auto_repair",
        "taxiway",
        "runway",
        "airport_gate",
        "gate",
        "platform",
        "track",
        "bus_stop"
    ];

    public static string BuildQuery(
        double lat,
        double lon,
        double minLat,
        double maxLat,
        double minLon,
        double maxLon,
        string? alpha2,
        string releaseUrl)
    {
        var countryClause = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"  AND lower(addresses[1].country) = '{alpha2.ToLowerInvariant()}'\n";

        return $"""
            SELECT
                id,
                COALESCE(names.common['en'], names.primary) AS name,
                categories.primary AS primary_category,
                basic_category,
                confidence,
                ST_Y(geometry) AS latitude,
                ST_X(geometry) AS longitude,
                operating_status,
                bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)} AS bbox_contains_point,
                CAST(sources AS VARCHAR) AS sources_json
            FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
            WHERE names.primary IS NOT NULL
              AND bbox.xmin BETWEEN {minLon.ToString(CultureInfo.InvariantCulture)} AND {maxLon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymin BETWEEN {minLat.ToString(CultureInfo.InvariantCulture)} AND {maxLat.ToString(CultureInfo.InvariantCulture)}
            {countryClause}LIMIT {QueryLimit}
            """;
    }

    public static string BuildInfrastructureQuery(
        double lat,
        double lon,
        double minLat,
        double maxLat,
        double minLon,
        double maxLon,
        string releaseUrl)
    {
        return $"""
            SELECT
                id,
                COALESCE(names.common['en'], names.primary, id) AS name,
                type AS feature_type,
                subtype,
                class,
                ST_Y(ST_CENTROID(geometry)) AS centroid_latitude,
                ST_X(ST_CENTROID(geometry)) AS centroid_longitude,
                bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)} AS bbox_contains_point,
                ST_Intersects(geometry, ST_Point({lon.ToString(CultureInfo.InvariantCulture)}, {lat.ToString(CultureInfo.InvariantCulture)})) AS geometry_contains_point,
                CAST(sources AS VARCHAR) AS sources_json
            FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
            WHERE bbox.xmax >= {minLon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.xmin <= {maxLon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymax >= {minLat.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymin <= {maxLat.ToString(CultureInfo.InvariantCulture)}
              AND subtype = 'airport'
              AND (
                    COALESCE(class, '') = 'airport'
                    OR COALESCE(class, '') LIKE '%_airport'
                  )
              AND subtype NOT IN ('airport_gate', 'taxiway', 'runway', 'apron')
              AND COALESCE(class, '') NOT IN ('airport_gate', 'taxiway', 'runway', 'apron')
            LIMIT {QueryLimit}
            """;
    }

    public static bool ShouldPreferCandidate(OverturePlaceResult candidate, OverturePlaceResult currentBest)
    {
        var candidateActive = IsPreferredStatus(candidate.OperatingStatus);
        var currentBestActive = IsPreferredStatus(currentBest.OperatingStatus);
        if (candidateActive && !currentBestActive)
        {
            return true;
        }

        if (!candidateActive && currentBestActive)
        {
            return false;
        }

        if (candidate.BoundingBoxContainsPoint && !currentBest.BoundingBoxContainsPoint)
        {
            return true;
        }

        if (!candidate.BoundingBoxContainsPoint && currentBest.BoundingBoxContainsPoint)
        {
            return false;
        }

        return candidate.DistanceMetres < currentBest.DistanceMetres;
    }

    public static bool IsInterestingPhotoPlace(OverturePlaceResult candidate)
    {
        if (!IsPreferredStatus(candidate.OperatingStatus))
        {
            return false;
        }

        if (candidate.Confidence < MinimumInterestingConfidence)
        {
            return false;
        }

        var categoryText = $"{candidate.Category} {candidate.BasicCategory}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(categoryText))
        {
            return false;
        }

        var hasIncludedTerm = IncludedCategoryTerms.Any(categoryText.Contains);
        if (!hasIncludedTerm)
        {
            return false;
        }

        var hasExcludedTerm = ExcludedCategoryTerms.Any(categoryText.Contains);
        return !hasExcludedTerm;
    }

    public static bool ShouldPreferInfrastructureCandidate(
        OvertureInfrastructureResult candidate,
        OvertureInfrastructureResult currentBest)
    {
        var candidateClassRank = GetInfrastructureClassRank(candidate.SubType, candidate.ClassName);
        var currentBestClassRank = GetInfrastructureClassRank(currentBest.SubType, currentBest.ClassName);

        if (candidateClassRank < currentBestClassRank)
        {
            return true;
        }

        if (candidateClassRank > currentBestClassRank)
        {
            return false;
        }

        if (candidate.GeometryContainsPoint && !currentBest.GeometryContainsPoint)
        {
            return true;
        }

        if (!candidate.GeometryContainsPoint && currentBest.GeometryContainsPoint)
        {
            return false;
        }

        if (candidate.BoundingBoxContainsPoint && !currentBest.BoundingBoxContainsPoint)
        {
            return true;
        }

        if (!candidate.BoundingBoxContainsPoint && currentBest.BoundingBoxContainsPoint)
        {
            return false;
        }

        return candidate.DistanceMetres < currentBest.DistanceMetres;
    }

    public static int GetInfrastructureClassRank(string? subType, string? className)
    {
        if (!string.Equals(subType, "airport", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        return className?.ToLowerInvariant() switch
        {
            "international_airport" => 0,
            "regional_airport" => 1,
            "municipal_airport" => 2,
            "airport" => 3,
            "private_airport" => 4,
            "airfield" => 5,
            "seaplane_airport" => 6,
            "heliport" => 7,
            _ => 50
        };
    }

    public static IReadOnlyList<string> ParseSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("dataset", out var datasetEl))
                {
                    var dataset = datasetEl.GetString();
                    if (!string.IsNullOrWhiteSpace(dataset))
                    {
                        results.Add(dataset);
                    }
                }
                else if (item.TryGetProperty("property", out var propertyEl))
                {
                    var property = propertyEl.GetString();
                    if (!string.IsNullOrWhiteSpace(property))
                    {
                        results.Add(property);
                    }
                }
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static bool IsPreferredStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status.Equals("active", StringComparison.OrdinalIgnoreCase)
        || status.Equals("open", StringComparison.OrdinalIgnoreCase);

    public static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadius * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

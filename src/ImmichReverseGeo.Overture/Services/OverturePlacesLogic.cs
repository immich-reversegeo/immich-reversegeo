using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class OverturePlacesLogic
{
    public const string LatestCatalogUrl = OvertureReleaseCatalog.CatalogUrl;
    public const string PlacesReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=places/type=place/*.parquet";
    public const string InfrastructureReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=base/type=infrastructure/*.parquet";
    public const double SearchRadiusMetres = 2_500;
    public const double InfrastructureSearchRadiusMetres = 8_000;
    public const int QueryLimit = 80;
    public const int ExcludedPreviewLimit = 20;
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

    // Preserve useful-place filtering across documented category renames, not broad ancestor labels.
    // https://docs.overturemaps.org/taxonomy/2026-09-23.0/taxonomy_to_legacy_categories.csv
    private static readonly IReadOnlyDictionary<string, string> CategoryFilterAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anglican_or_episcopal_place_of_worship"] = "anglican_church",
            ["baptist_place_of_worship"] = "baptist_church",
            ["roman_catholic_place_of_worship"] = "catholic_church",
            ["christian_place_of_worship"] = "church_cathedral",
            ["protestant_place_of_worship"] = "evangelical_church",
            ["pentecostal_place_of_worship"] = "pentecostal_church",
            ["buddhist_place_of_worship"] = "buddhist_temple",
            ["hindu_place_of_worship"] = "hindu_temple",
            ["muslim_place_of_worship"] = "mosque",
            ["sikh_place_of_worship"] = "sikh_temple",
            ["jewish_place_of_worship"] = "synagogue",
            ["place_of_worship"] = "temple",
            ["performing_arts_venue"] = "theater",
            ["government_office"] = "government_services",
            ["school_district_office"] = "school_district_offices",
            ["shopping"] = "retail",
            ["non_alcoholic_beverage_venue"] = "beverage_store",
            ["reproductive_perinatal_and_womens_care"] = "womens_health_clinic",
            ["b2b_storage"] = "warehouse",
            ["b2b_storage_and_warehouses"] = "warehouse",
        };

    private static string NormalizeFilterCategory(string? category)
    {
        var normalized = category?.Trim().ToLowerInvariant() ?? string.Empty;
        return CategoryFilterAliases.TryGetValue(normalized, out var alias) ? alias : normalized;
    }

    private static string BuildFilterCategorySql(string column)
    {
        var cases = string.Join(" ", CategoryFilterAliases.Select(pair => $"WHEN '{pair.Key}' THEN '{pair.Value}'"));
        return $"CASE lower(trim(COALESCE({column}, ''))) {cases} ELSE lower(trim(COALESCE({column}, ''))) END";
    }

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

        var latSql = lat.ToString(CultureInfo.InvariantCulture);
        var lonSql = lon.ToString(CultureInfo.InvariantCulture);
        var included = string.Join(" OR ", IncludedCategoryTerms.Select(term => $"contains(category_text, '_{term}_')"));
        var excluded = string.Join(" OR ", ExcludedCategoryTerms.Select(term => $"contains(category_text, '_{term}_')"));
        return $"""
            WITH nearby AS (
                SELECT
                    id,
                    COALESCE(names.common['en'], names.primary) AS name,
                    COALESCE(taxonomy.primary, basic_category) AS primary_category,
                    basic_category,
                    confidence,
                    ST_Y(geometry) AS latitude,
                    ST_X(geometry) AS longitude,
                    operating_status,
                    false AS bbox_contains_point,
                    to_json(sources) AS sources_json
                FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
                WHERE names.primary IS NOT NULL
                  AND bbox.xmin BETWEEN {minLon.ToString(CultureInfo.InvariantCulture)} AND {maxLon.ToString(CultureInfo.InvariantCulture)}
                  AND bbox.ymin BETWEEN {minLat.ToString(CultureInfo.InvariantCulture)} AND {maxLat.ToString(CultureInfo.InvariantCulture)}
                {countryClause}
            ), measured AS (
                SELECT *,
                    6371000 * 2 * asin(sqrt(least(1.0, greatest(0.0,
                        pow(sin(radians(latitude - {latSql}) / 2), 2)
                        + cos(radians({latSql})) * cos(radians(latitude))
                        * pow(sin(radians(longitude - {lonSql}) / 2), 2))))) AS distance_metres,
                    '_' || ({BuildFilterCategorySql("primary_category")}) || '_ _' || ({BuildFilterCategorySql("basic_category")}) || '_' AS category_text
                FROM nearby
            ), classified AS (
                SELECT *, CASE
                    WHEN COALESCE(trim(operating_status), '') <> '' AND lower(trim(operating_status)) NOT IN ('active', 'open')
                        THEN 'Excluded: place is not operating or its status is unsupported.'
                    WHEN confidence IS NULL THEN 'Excluded: existence confidence is unknown.'
                    WHEN NOT (confidence >= {MinimumInterestingConfidence.ToString(CultureInfo.InvariantCulture)} AND confidence <= 1)
                        THEN 'Excluded: existence confidence is below 0.90 or invalid.'
                    WHEN NOT ({included}) THEN 'Excluded: category is outside the useful-place filter.'
                    WHEN {excluded} THEN 'Excluded: category is routine business or transport detail.'
                    ELSE NULL END AS exclusion_reason
                FROM measured
                WHERE distance_metres <= {SearchRadiusMetres.ToString(CultureInfo.InvariantCulture)}
            ), suggestions AS (
                SELECT * FROM classified WHERE exclusion_reason IS NULL
                ORDER BY distance_metres, id LIMIT {QueryLimit}
            ), excluded_preview AS (
                SELECT * FROM classified WHERE exclusion_reason IS NOT NULL
                ORDER BY distance_metres, id LIMIT {ExcludedPreviewLimit}
            ), combined AS (
                SELECT * FROM suggestions UNION ALL SELECT * FROM excluded_preview
            )
            SELECT * FROM combined
            ORDER BY exclusion_reason IS NOT NULL, distance_metres, id
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
                ST_AsWKB(geometry) AS geom_wkb,
                to_json(sources) AS sources_json
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
            ORDER BY (ST_GeometryType(geometry) IN ('POLYGON', 'MULTIPOLYGON')
                AND ST_Covers(geometry, ST_Point({lon.ToString(CultureInfo.InvariantCulture)}, {lat.ToString(CultureInfo.InvariantCulture)}))) DESC,
                ST_Distance(geometry, ST_Point({lon.ToString(CultureInfo.InvariantCulture)}, {lat.ToString(CultureInfo.InvariantCulture)})) ASC, id
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

        return candidate.DistanceMetres < currentBest.DistanceMetres
            || (candidate.DistanceMetres == currentBest.DistanceMetres
                && string.CompareOrdinal(candidate.Id, currentBest.Id) < 0);
    }

    public static bool IsInterestingPhotoPlace(OverturePlaceResult candidate) => GetExclusionReason(candidate) is null;

    public static string? GetExclusionReason(OverturePlaceResult candidate)
    {
        if (!IsPreferredStatus(candidate.OperatingStatus))
        {
            return "Excluded: place is not operating or its status is unsupported.";
        }

        if (!candidate.ConfidenceKnown)
        {
            return "Excluded: existence confidence is unknown.";
        }

        if (!double.IsFinite(candidate.Confidence) || candidate.Confidence < MinimumInterestingConfidence || candidate.Confidence > 1)
        {
            return "Excluded: existence confidence is below 0.90 or invalid.";
        }

        // Overture category identifiers use snake_case; pad each identifier to match complete terms.
        var categoryText = $"_{NormalizeFilterCategory(candidate.Category)}_ _{NormalizeFilterCategory(candidate.BasicCategory)}_";
        if (!IncludedCategoryTerms.Any(term => categoryText.Contains($"_{term}_", StringComparison.Ordinal)))
        {
            return "Excluded: category is outside the useful-place filter.";
        }

        return ExcludedCategoryTerms.Any(term => categoryText.Contains($"_{term}_", StringComparison.Ordinal))
            ? "Excluded: category is routine business or transport detail."
            : null;
    }

    public static OvertureLookupDiagnostics BuildPlaceDiagnostics(
        IEnumerable<OverturePlaceResult> places, string? release, string? countryFilter)
    {
        var classified = places
            .Where(p => double.IsFinite(p.DistanceMetres) && p.DistanceMetres >= 0 && p.DistanceMetres <= SearchRadiusMetres)
            .Select(p => (Place: p, Reason: GetExclusionReason(p)))
            .OrderBy(p => p.Place.DistanceMetres)
            .ThenBy(p => p.Place.Id, StringComparer.Ordinal)
            .ToList();
        var included = classified.Where(p => p.Reason is null).Take(QueryLimit).ToList();
        var excluded = classified.Where(p => p.Reason is not null).Take(ExcludedPreviewLimit);
        var best = included.FirstOrDefault().Place;
        var candidates = included.Concat(excluded).Select(p => new OvertureCandidateDiagnostic(
            p.Place.Id, p.Place.Name, p.Place.Category, p.Place.BasicCategory, p.Place.Confidence,
            p.Place.OperatingStatus, p.Place.DistanceMetres, p.Place.BoundingBoxContainsPoint, p.Place.Sources,
            p.Reason is null && p.Place.Id == best?.Id,
            p.Reason ?? "Included: useful place type, sufficient existence confidence, and no known closure.",
            p.Reason is null, p.Place.ConfidenceKnown)).ToList();
        return new OvertureLookupDiagnostics(best, candidates, release, countryFilter);
    }

    public static bool ShouldPreferInfrastructureCandidate(
        OvertureInfrastructureResult candidate,
        OvertureInfrastructureResult currentBest)
    {
        if (!candidate.IsBoundaryMatch)
        {
            return false;
        }

        if (!currentBest.IsBoundaryMatch)
        {
            return true;
        }

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
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var dataset = ReadSourceString(item, "dataset");
                var provider = ReadSourceString(item, "provider");
                var resource = ReadSourceString(item, "resource");
                var label = dataset ?? (provider is not null && resource is not null
                    ? $"{provider}/{resource}"
                    : provider ?? resource);
                if (label is not null)
                {
                    results.Add(label);
                }
            }

            return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadSourceString(JsonElement source, string property)
    {
        if (source.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    public static bool IsPreferredStatus(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || status.Trim().Equals("active", StringComparison.OrdinalIgnoreCase)
        || status.Trim().Equals("open", StringComparison.OrdinalIgnoreCase);

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

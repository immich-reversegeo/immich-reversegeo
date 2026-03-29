using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureDivisionsLogic
{
    public const string DivisionAreaReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=divisions/type=division_area/*.parquet";
    public const int QueryLimit = 200;

    public static string BuildDivisionAreaQuery(double lat, double lon, string? alpha2, string releaseUrl)
    {
        var countryClause = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"  AND lower(country) = '{alpha2.ToLowerInvariant()}'\n";

        return $"""
            SELECT
                id,
                COALESCE(names.common['en'], names.primary, id) AS name,
                subtype,
                class,
                admin_level,
                country,
                COALESCE(is_land, true) AS is_land,
                COALESCE(is_territorial, false) AS is_territorial,
                bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
                    AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)} AS bbox_contains_point,
                ST_Intersects(geometry, ST_Point({lon.ToString(CultureInfo.InvariantCulture)}, {lat.ToString(CultureInfo.InvariantCulture)})) AS geometry_contains_point,
                ABS((bbox.xmax - bbox.xmin) * (bbox.ymax - bbox.ymin)) AS bbox_area
            FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
            WHERE names.primary IS NOT NULL
              AND bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)}
            {countryClause}LIMIT {QueryLimit}
            """;
    }

    public static bool ShouldPreferDivisionCandidate(
        OvertureDivisionResult candidate,
        OvertureDivisionResult currentBest)
    {
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

        var candidateSubtypeRank = GetSubtypeRank(candidate.SubType);
        var currentBestSubtypeRank = GetSubtypeRank(currentBest.SubType);
        if (candidateSubtypeRank < currentBestSubtypeRank)
        {
            return true;
        }

        if (candidateSubtypeRank > currentBestSubtypeRank)
        {
            return false;
        }

        if (candidate.AdminLevel.HasValue && currentBest.AdminLevel.HasValue)
        {
            if (candidate.AdminLevel.Value < currentBest.AdminLevel.Value)
            {
                return true;
            }

            if (candidate.AdminLevel.Value > currentBest.AdminLevel.Value)
            {
                return false;
            }
        }

        if (candidate.IsTerritorial != currentBest.IsTerritorial)
        {
            return candidate.IsTerritorial;
        }

        return candidate.BoundingBoxArea < currentBest.BoundingBoxArea;
    }

    public static int GetSubtypeRank(string? subtype) =>
        subtype?.ToLowerInvariant() switch
        {
            "microhood" => 0,
            "neighborhood" => 1,
            "macrohood" => 2,
            "borough" => 3,
            "locality" => 4,
            "localadmin" => 5,
            "county" => 6,
            "macrocounty" => 7,
            "region" => 8,
            "macroregion" => 9,
            "dependency" => 10,
            "country" => 11,
            _ => 50
        };

    public static string? SelectStateName(IEnumerable<OvertureDivisionCandidateDiagnostic> candidates)
    {
        return SelectPreferredName(candidates, ["region", "macroregion", "county", "macrocounty", "dependency"]);
    }

    public static string? SelectCityName(IEnumerable<OvertureDivisionCandidateDiagnostic> candidates)
    {
        return SelectPreferredName(candidates, ["locality", "borough", "localadmin", "macrohood", "neighborhood", "microhood"]);
    }

    private static string? SelectPreferredName(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        IReadOnlyList<string> preferredSubtypes)
    {
        var applicable = candidates
            .Where(c => c.BoundingBoxContainsPoint)
            .Where(c => preferredSubtypes.Contains(c.SubType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (applicable.Count == 0)
        {
            return null;
        }

        var geometries = applicable.Where(c => c.GeometryContainsPoint).ToList();
        var pool = geometries.Count > 0 ? geometries : applicable;

        return pool
            .OrderBy(c => GetPreferredSubtypeOrder(c.SubType, preferredSubtypes))
            .ThenBy(c => c.AdminLevel ?? int.MaxValue)
            .ThenByDescending(c => c.IsTerritorial)
            .ThenBy(c => c.BoundingBoxArea)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    private static int GetPreferredSubtypeOrder(string? subtype, IReadOnlyList<string> preferredSubtypes)
    {
        for (var i = 0; i < preferredSubtypes.Count; i++)
        {
            if (string.Equals(preferredSubtypes[i], subtype, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}

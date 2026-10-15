using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureDivisionsLogic
{
    private static readonly string[] StateSubtypes = ["region", "macroregion", "county", "macrocounty", "dependency"];

    public const string DivisionAreaReleaseUrlTemplate = "az://overturemapswestus2.blob.core.windows.net/release/{0}/theme=divisions/type=division_area/*.parquet";
    public const int QueryLimit = 200;

    public static string BuildDivisionAreaQuery(double lat, double lon, string? alpha2, string releaseUrl)
    {
        var countryClause = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"  AND {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}\n";

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
                ABS((bbox.xmax - bbox.xmin) * (bbox.ymax - bbox.ymin)) AS bbox_area,
                division_id,
                hierarchy_json,
                to_json(struct_pack("primary" := names.primary, common := names.common)) AS names_json
            FROM {BuildDivisionAreaSource(releaseUrl, alpha2)}
            WHERE names.primary IS NOT NULL
              AND bbox.xmax >= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.xmin <= {lon.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymax >= {lat.ToString(CultureInfo.InvariantCulture)}
              AND bbox.ymin <= {lat.ToString(CultureInfo.InvariantCulture)}
            {countryClause}LIMIT {QueryLimit}
            """;
    }

    // Keep area containment and its default parent hierarchy in the same release.
    public static string BuildDivisionAreaSource(string releaseUrl, string? alpha2)
    {
        var divisionUrl = releaseUrl.Replace("type=division_area/", "type=division/", StringComparison.Ordinal);
        var countryFilter = string.IsNullOrWhiteSpace(alpha2)
            ? string.Empty
            : $"WHERE {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}";
        return $"""
            (
                WITH division_names AS MATERIALIZED (
                    SELECT id, hierarchies, country,
                        struct_pack("primary" := names.primary, common := names.common) AS names,
                        COALESCE(NULLIF(trim(names.common['en']), ''), NULLIF(trim(names.primary), '')) AS name
                    FROM read_parquet('{divisionUrl}', hive_partitioning = 1) {countryFilter}
                )
                SELECT a.*, (
                    SELECT to_json(list(struct_pack(
                        division_id := entry.division_id,
                        subtype := entry.subtype,
                        name := COALESCE(parent.name, entry.name),
                        names := parent.names,
                        country := parent.country
                    ) ORDER BY position))
                    FROM UNNEST(d.hierarchies[1]) WITH ORDINALITY AS ancestors(entry, position)
                    LEFT JOIN division_names parent ON parent.id = entry.division_id
                ) AS hierarchy_json
                FROM (SELECT * FROM read_parquet('{releaseUrl}', hive_partitioning = 1) {countryFilter}) a
                LEFT JOIN division_names d
                    ON a.division_id = d.id
            )
            """;
    }

    public static bool ShouldPreferDivisionCandidate(
        OvertureDivisionResult candidate,
        OvertureDivisionResult currentBest)
    {
        if (candidate.IsExactGeometryMatch != currentBest.IsExactGeometryMatch)
        {
            return candidate.IsExactGeometryMatch;
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

        var adminLevelComparison = (candidate.AdminLevel ?? int.MaxValue)
            .CompareTo(currentBest.AdminLevel ?? int.MaxValue);
        if (adminLevelComparison != 0)
        {
            return adminLevelComparison < 0;
        }

        if (candidate.IsTerritorial != currentBest.IsTerritorial)
        {
            return candidate.IsTerritorial;
        }

        var areaComparison = candidate.BoundingBoxArea.CompareTo(currentBest.BoundingBoxArea);
        if (areaComparison != 0)
        {
            return areaComparison < 0;
        }

        return string.Compare(candidate.Id, currentBest.Id, StringComparison.Ordinal) < 0;
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
        return SelectPreferredCandidate(candidates, StateSubtypes)?.Name;
    }

    public static string? SelectCityName(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile = null) => SelectCityCandidate(candidates.ToList(), resolverProfile)?.Name;

    public static OvertureAdministrativeResult SelectAdministrativeAreas(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile = null)
    {
        var areas = candidates.ToList();
        var city = SelectCityCandidate(areas, resolverProfile);
        var hierarchy = city is { GeometryContainsPoint: true } ? ParseHierarchy(city) : [];
        foreach (var subtype in StateSubtypes)
        {
            // The city itself is not its parent state, including county-like city profiles.
            var ancestor = hierarchy.FirstOrDefault(h => h.SubType.Equals(subtype, StringComparison.OrdinalIgnoreCase)
                && h.DivisionId != city?.DivisionId);
            if (ancestor is not null)
            {
                // Prefer the existing localized polygon name when the matching parent is present.
                var parent = areas.FirstOrDefault(c => c.DivisionId == ancestor.DivisionId
                    && c.GeometryContainsPoint && c.Country == city?.Country);
                return new OvertureAdministrativeResult(parent?.Name ?? ancestor.Name, city?.Name);
            }
        }

        return new OvertureAdministrativeResult(hierarchy.Count > 0 ? null : SelectStateName(areas), city?.Name);
    }

    public static IReadOnlyList<OvertureDivisionAncestor> ParseHierarchy(OvertureDivisionCandidateDiagnostic candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.DivisionId) || string.IsNullOrWhiteSpace(candidate.HierarchyJson))
        {
            return [];
        }

        try
        {
            using var json = JsonDocument.Parse(candidate.HierarchyJson);
            var ancestors = new List<OvertureDivisionAncestor>();
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var id = entry.GetProperty("division_id").GetString();
                var name = entry.GetProperty("name").GetString();
                var subtype = entry.GetProperty("subtype").GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(subtype)
                    || ancestors.Any(h => h.DivisionId == id))
                {
                    return [];
                }

                ancestors.Add(new OvertureDivisionAncestor(id, name, subtype,
                    entry.TryGetProperty("country", out var country) && country.ValueKind == JsonValueKind.String
                        ? country.GetString() : null));
            }

            // Only accept a chain ending at this area owner; never borrow an unrelated hierarchy.
            return ancestors.Count > 0
                && ancestors[0].SubType.Equals("country", StringComparison.OrdinalIgnoreCase)
                && ancestors[^1].DivisionId == candidate.DivisionId ? ancestors : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }

    private static OvertureDivisionCandidateDiagnostic? SelectCityCandidate(
        List<OvertureDivisionCandidateDiagnostic> candidates,
        CityResolverProfile? resolverProfile)
    {
        var profile = (resolverProfile ?? new CityResolverProfile()).Clone().Normalize();
        var selected = SelectPreferredCandidate(candidates, profile.PreferredSubtypes, profile.TieBreakMode);
        var selectedRank = selected is not { GeometryContainsPoint: true }
            ? int.MaxValue
            : GetPreferredSubtypeOrder(selected.SubType, profile.PreferredSubtypes);
        if (selectedRank == 0 && selected!.IsExactGeometryMatch)
        {
            return selected;
        }

        var promoted = new List<OvertureDivisionCandidateDiagnostic>();
        foreach (var candidate in candidates.Where(c => c.GeometryContainsPoint && c.BoundingBoxContainsPoint))
        {
            if (selected is { IsExactGeometryMatch: true } && !candidate.IsExactGeometryMatch)
            {
                continue;
            }

            var hierarchy = ParseHierarchy(candidate);
            for (var i = 0; i < hierarchy.Count - 1; i++)
            {
                var ancestor = hierarchy[i];
                var ancestorRank = GetPreferredSubtypeOrder(ancestor.SubType, profile.PreferredSubtypes);
                if (ancestorRank == int.MaxValue || (ancestorRank >= selectedRank
                    && !(candidate.IsExactGeometryMatch && selected is { IsExactGeometryMatch: false })))
                {
                    continue;
                }

                promoted.Add(candidate with
                {
                    Name = ancestor.Name,
                    SubType = ancestor.SubType,
                    AdminLevel = null,
                    DivisionId = ancestor.DivisionId,
                    HierarchyJson = JsonSerializer.Serialize(hierarchy.Take(i + 1).Select(h => new
                    {
                        division_id = h.DivisionId,
                        name = h.Name,
                        subtype = h.SubType
                    }))
                });
            }
        }

        if (selected is not null)
        {
            promoted.Add(selected);
        }

        return SelectPreferredCandidate(promoted, profile.PreferredSubtypes, profile.TieBreakMode);
    }

    private static OvertureDivisionCandidateDiagnostic? SelectPreferredCandidate(
        IEnumerable<OvertureDivisionCandidateDiagnostic> candidates,
        IReadOnlyList<string> preferredSubtypes,
        string tieBreakMode = CityResolverTieBreakModes.SmallestArea)
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
        var exactGeometries = geometries.Where(c => c.IsExactGeometryMatch).ToList();
        var pool = exactGeometries.Count > 0 ? exactGeometries : geometries.Count > 0 ? geometries : applicable;

        var ordered = pool
            .OrderBy(c => GetPreferredSubtypeOrder(c.SubType, preferredSubtypes))
            .ThenBy(c => c.AdminLevel ?? int.MaxValue)
            .ThenByDescending(c => c.IsTerritorial);

        ordered = string.Equals(tieBreakMode, CityResolverTieBreakModes.LargestArea, StringComparison.OrdinalIgnoreCase)
            ? ordered.ThenByDescending(c => c.BoundingBoxArea)
            : ordered.ThenBy(c => c.BoundingBoxArea);

        return ordered.FirstOrDefault();
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

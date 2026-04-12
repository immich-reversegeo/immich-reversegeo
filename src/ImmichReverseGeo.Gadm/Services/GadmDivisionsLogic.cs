using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Gadm.Models;

namespace ImmichReverseGeo.Gadm.Services;

public static class GadmDivisionsLogic
{
    public const string DatasetVersion = "4.1";
    public const string CountryGeoPackageUrlTemplate = "https://geodata.ucdavis.edu/gadm/gadm4.1/gpkg/gadm41_{0}.gpkg";

    public static string BuildCountryGeoPackageUrl(string iso3)
    {
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            CountryGeoPackageUrlTemplate,
            iso3.ToUpperInvariant());
    }

    public static bool ShouldPreferDivisionCandidate(
        GadmDivisionResult candidate,
        GadmDivisionResult currentBest)
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

        if (candidate.AdminLevel > currentBest.AdminLevel)
        {
            return true;
        }

        if (candidate.AdminLevel < currentBest.AdminLevel)
        {
            return false;
        }

        return candidate.BoundingBoxArea < currentBest.BoundingBoxArea;
    }

    public static string? SelectStateName(IEnumerable<GadmDivisionCandidateDiagnostic> candidates)
    {
        var pool = GetContainingCandidates(candidates)
            .Where(candidate => candidate.AdminLevel > 0)
            .ToList();
        if (pool.Count == 0)
        {
            return null;
        }

        var level = pool.Min(candidate => candidate.AdminLevel);
        return pool
            .Where(candidate => candidate.AdminLevel == level)
            .OrderBy(candidate => candidate.BoundingBoxArea)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    public static string? SelectCityName(IEnumerable<GadmDivisionCandidateDiagnostic> candidates)
    {
        var pool = GetContainingCandidates(candidates)
            .Where(candidate => candidate.AdminLevel >= 2)
            .Where(candidate => !IsStateLikeType(candidate.EnglishType))
            .ToList();

        if (pool.Count == 0)
        {
            pool = GetContainingCandidates(candidates)
                .Where(candidate => candidate.AdminLevel >= 2)
                .ToList();
        }

        if (pool.Count == 0)
        {
            return null;
        }

        return pool
            .OrderByDescending(candidate => candidate.AdminLevel)
            .ThenBy(candidate => GetCityTypeRank(candidate.EnglishType))
            .ThenBy(candidate => candidate.BoundingBoxArea)
            .Select(candidate => candidate.Name)
            .FirstOrDefault();
    }

    private static List<GadmDivisionCandidateDiagnostic> GetContainingCandidates(IEnumerable<GadmDivisionCandidateDiagnostic> candidates)
    {
        var applicable = candidates
            .Where(candidate => candidate.BoundingBoxContainsPoint)
            .ToList();
        if (applicable.Count == 0)
        {
            return [];
        }

        var geometryMatches = applicable
            .Where(candidate => candidate.GeometryContainsPoint)
            .ToList();
        return geometryMatches.Count > 0 ? geometryMatches : applicable;
    }

    private static bool IsStateLikeType(string? englishType)
    {
        if (string.IsNullOrWhiteSpace(englishType))
        {
            return false;
        }

        return englishType.Contains("state", StringComparison.OrdinalIgnoreCase)
               || englishType.Contains("province", StringComparison.OrdinalIgnoreCase)
               || englishType.Contains("region", StringComparison.OrdinalIgnoreCase)
               || englishType.Contains("governorate", StringComparison.OrdinalIgnoreCase)
               || englishType.Contains("department", StringComparison.OrdinalIgnoreCase)
               || englishType.Contains("prefecture", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetCityTypeRank(string? englishType)
    {
        if (string.IsNullOrWhiteSpace(englishType))
        {
            return 50;
        }

        return englishType.ToLowerInvariant() switch
        {
            var value when value.Contains("city", StringComparison.Ordinal) => 0,
            var value when value.Contains("municip", StringComparison.Ordinal) => 1,
            var value when value.Contains("town", StringComparison.Ordinal) => 2,
            var value when value.Contains("commune", StringComparison.Ordinal) => 3,
            var value when value.Contains("district", StringComparison.Ordinal) => 4,
            var value when value.Contains("borough", StringComparison.Ordinal) => 5,
            var value when value.Contains("village", StringComparison.Ordinal) => 6,
            _ => 20
        };
    }
}

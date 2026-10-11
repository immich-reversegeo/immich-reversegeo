using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Core.Models;

public sealed class CityNameOverride
{
    public string CountryIso3 { get; set; } = string.Empty;
    public string OriginalCity { get; set; } = string.Empty;
    public string? State { get; set; }
    public string OverrideCity { get; set; } = string.Empty;
}

public sealed record CityNameOverrideResolution(GeoResult GeoResult, CityNameOverride? Rule);

public static class CityNameOverrides
{
    public static List<CityNameOverride> NormalizeAndValidate(IEnumerable<CityNameOverride>? rules)
    {
        var normalized = new List<CityNameOverride>();
        foreach (var rule in rules ?? [])
        {
            if (rule is null)
            {
                throw new ArgumentException("City name replacement rules cannot be empty.");
            }

            var candidate = new CityNameOverride
            {
                CountryIso3 = rule.CountryIso3?.Trim().ToUpperInvariant() ?? string.Empty,
                OriginalCity = rule.OriginalCity?.Trim() ?? string.Empty,
                State = string.IsNullOrWhiteSpace(rule.State) ? null : rule.State.Trim(),
                OverrideCity = rule.OverrideCity?.Trim() ?? string.Empty
            };
            if (candidate.CountryIso3.Length != 3 || candidate.CountryIso3.Any(c => c < 'A' || c > 'Z'))
            {
                throw new ArgumentException("Choose a country for each city name replacement.");
            }
            if (candidate.OriginalCity.Length == 0 || candidate.OverrideCity.Length == 0)
            {
                throw new ArgumentException("Each city name replacement needs an original city and a replacement city.");
            }
            if (normalized.Any(existing => Equal(existing.CountryIso3, candidate.CountryIso3)
                && Equal(existing.OriginalCity, candidate.OriginalCity)
                && Equal(existing.State, candidate.State)))
            {
                throw new ArgumentException($"Duplicate city name replacement for {candidate.OriginalCity} in {candidate.CountryIso3}. Use one rule per country, state and original city.");
            }

            normalized.Add(candidate);
        }

        return normalized;
    }

    public static CityNameOverrideResolution Resolve(GeoResult geo, string? iso3, CityResolverConfig config)
    {
        if (geo.City is null || iso3 is null)
        {
            return new CityNameOverrideResolution(geo, null);
        }

        var rule = config.CityNameOverrides
            .Where(candidate => Equal(candidate.CountryIso3, iso3) && Equal(candidate.OriginalCity, geo.City))
            .Where(candidate => candidate.State is null || Equal(candidate.State, geo.State))
            .OrderByDescending(candidate => candidate.State is not null)
            .FirstOrDefault();

        return rule is null
            ? new CityNameOverrideResolution(geo, null)
            : new CityNameOverrideResolution(geo with { City = rule.OverrideCity }, rule);
    }

    private static bool Equal(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Core.Models;

public class AppConfig
{
    public ScheduleConfig Schedule { get; set; } = new();
    public ProcessingConfig Processing { get; set; } = new();
}

public class ScheduleConfig
{
    public string Cron { get; set; } = "0 * * * *";
    public bool Enabled { get; set; } = true;
}

public class ProcessingConfig
{
    public int BatchSize { get; set; } = 50;
    public int BatchDelayMs { get; set; } = 100;
    public int MaxDegreeOfParallelism { get; set; } = 4;
    public bool UseAirportInfrastructure { get; set; } = true;
    public CityResolverConfig CityResolver { get; set; } = new();
    public bool VerboseLogging { get; set; } = false;
}

public class CityResolverConfig
{
    public CityResolverProfile DefaultProfile { get; set; } = CityResolverProfile.CreateEmpty();
    public Dictionary<string, CityResolverProfile> CountryOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class CityResolverProfileCatalog
{
    public CityResolverProfile DefaultProfile { get; set; } = new();
    public Dictionary<string, CityResolverProfile> CountryOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public CityResolverProfile GetProfile(CityResolverConfig? overrides, string? iso3)
    {
        var effective = DefaultProfile.Clone();
        if (!string.IsNullOrWhiteSpace(iso3)
            && CountryOverrides.TryGetValue(iso3.ToUpperInvariant(), out var bundledCountryProfile))
        {
            effective = effective.ApplyOverride(bundledCountryProfile);
        }

        if (overrides is not null)
        {
            effective = effective.ApplyOverride(overrides.DefaultProfile);
            if (!string.IsNullOrWhiteSpace(iso3)
                && overrides.CountryOverrides.TryGetValue(iso3.ToUpperInvariant(), out var overrideCountryProfile))
            {
                effective = effective.ApplyOverride(overrideCountryProfile);
            }
        }

        return effective.Normalize();
    }
}

public class CityResolverProfile
{
    public List<string> PreferredSubtypes { get; set; } =
    [
        "locality",
        "borough",
        "localadmin",
        "macrohood",
        "neighborhood",
        "microhood"
    ];

    public string TieBreakMode { get; set; } = CityResolverTieBreakModes.SmallestArea;

    public static CityResolverProfile CreateEmpty()
    {
        return new CityResolverProfile
        {
            PreferredSubtypes = [],
            TieBreakMode = string.Empty
        };
    }

    public CityResolverProfile Clone()
    {
        return new CityResolverProfile
        {
            PreferredSubtypes = [.. PreferredSubtypes],
            TieBreakMode = TieBreakMode
        };
    }

    public CityResolverProfile ApplyOverride(CityResolverProfile profileOverride)
    {
        var merged = Clone();
        if (profileOverride.PreferredSubtypes.Count > 0)
        {
            merged.PreferredSubtypes = [.. profileOverride.PreferredSubtypes];
        }

        if (!string.IsNullOrWhiteSpace(profileOverride.TieBreakMode))
        {
            merged.TieBreakMode = profileOverride.TieBreakMode;
        }

        return merged;
    }

    public CityResolverProfile Normalize()
    {
        PreferredSubtypes = PreferredSubtypes
            .Where(subtype => !string.IsNullOrWhiteSpace(subtype))
            .Select(subtype => subtype.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (PreferredSubtypes.Count == 0)
        {
            PreferredSubtypes =
            [
                "locality",
                "borough",
                "localadmin",
                "macrohood",
                "neighborhood",
                "microhood"
            ];
        }

        TieBreakMode = string.Equals(TieBreakMode, CityResolverTieBreakModes.LargestArea, StringComparison.OrdinalIgnoreCase)
            ? CityResolverTieBreakModes.LargestArea
            : CityResolverTieBreakModes.SmallestArea;

        return this;
    }
}

public static class CityResolverTieBreakModes
{
    public const string SmallestArea = "smallest-area";
    public const string LargestArea = "largest-area";
}

public record StorageOptions(string DataDir, string BundledDataDir);

public record DbSettings(
    string Host,
    int Port,
    string Username,
    string Password,
    string Database);

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class CountryCodeService
{
    private readonly IReadOnlyDictionary<string, string> _iso3ToAlpha2;
    private readonly IReadOnlyDictionary<string, string> _alpha2ToIso3;

    public CountryCodeService(ILogger<CountryCodeService> logger, StorageOptions dirs)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("CountryCodeService: loading ISO 3166 mappings");
        _iso3ToAlpha2 = LoadIso3166(dirs.BundledDataDir);
        _alpha2ToIso3 = BuildReverseIsoMap(_iso3ToAlpha2);
        logger.LogInformation(
            "CountryCodeService: ISO 3166 mappings loaded ({Count} entries) in {Elapsed}ms",
            _iso3ToAlpha2.Count,
            sw.ElapsedMilliseconds);
    }

    public CountryCodeService(string bundledDataDir)
    {
        _iso3ToAlpha2 = LoadIso3166(bundledDataDir);
        _alpha2ToIso3 = BuildReverseIsoMap(_iso3ToAlpha2);
    }

    public static CountryCodeService CreateForTest(string bundledDataDir = "data")
    {
        return new CountryCodeService(bundledDataDir);
    }

    public string? Iso3ToAlpha2(string iso3)
    {
        return _iso3ToAlpha2.TryGetValue(iso3, out var alpha2) ? alpha2 : null;
    }

    public string? Alpha2ToIso3(string alpha2)
    {
        return _alpha2ToIso3.TryGetValue(alpha2.ToUpperInvariant(), out var iso3) ? iso3 : null;
    }

    public IReadOnlyList<string> GetKnownIso3Codes()
    {
        return _iso3ToAlpha2.Keys
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<KnownCountryOption> GetKnownCountries()
    {
        return _iso3ToAlpha2
            .Select(kvp =>
            {
                var iso3 = kvp.Key.ToUpperInvariant();
                var alpha2 = kvp.Value.ToUpperInvariant();
                var displayName = alpha2;

                try
                {
                    displayName = new RegionInfo(alpha2).EnglishName;
                }
                catch
                {
                }

                return new KnownCountryOption(iso3, alpha2, displayName);
            })
            .OrderBy(country => country.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyDictionary<string, string> LoadIso3166(string bundledDataDir)
    {
        var path = Path.Combine(bundledDataDir, "iso3166.json");
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    }

    private static IReadOnlyDictionary<string, string> BuildReverseIsoMap(IReadOnlyDictionary<string, string> iso3ToAlpha2)
    {
        return iso3ToAlpha2
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .GroupBy(kvp => kvp.Value.ToUpperInvariant(), kvp => kvp.Key.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }
}

public record KnownCountryOption(string Iso3, string Alpha2, string DisplayName)
{
    public string Label => $"{DisplayName} ({Iso3})";
}

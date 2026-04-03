using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class CityResolverProfileCatalogService
{
    private readonly ILogger<CityResolverProfileCatalogService> _logger;
    private readonly string _catalogPath;
    private readonly object _lock = new();
    private CityResolverProfileCatalog? _cachedCatalog;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CityResolverProfileCatalogService(
        ILogger<CityResolverProfileCatalogService> logger,
        StorageOptions storageOptions)
    {
        _logger = logger;
        _catalogPath = Path.Combine(storageOptions.BundledDataDir, "defaults", "city-resolver-profiles.json");
    }

    public CityResolverProfile GetProfile(CityResolverConfig? overrides, string? iso3)
    {
        var catalog = GetCatalog();
        return catalog.GetProfile(overrides, iso3);
    }

    private CityResolverProfileCatalog GetCatalog()
    {
        if (_cachedCatalog is not null)
        {
            return _cachedCatalog;
        }

        lock (_lock)
        {
            if (_cachedCatalog is not null)
            {
                return _cachedCatalog;
            }

            _cachedCatalog = LoadCatalog();
            return _cachedCatalog;
        }
    }

    private CityResolverProfileCatalog LoadCatalog()
    {
        if (!File.Exists(_catalogPath))
        {
            _logger.LogWarning("City resolver profile catalog not found at {Path}, using in-code fallback defaults.", _catalogPath);
            return new CityResolverProfileCatalog();
        }

        try
        {
            var json = File.ReadAllText(_catalogPath);
            var catalog = JsonSerializer.Deserialize<CityResolverProfileCatalog>(json, Json) ?? new CityResolverProfileCatalog();
            catalog.DefaultProfile.Normalize();

            var normalizedOverrides = new Dictionary<string, CityResolverProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (iso3, profile) in catalog.CountryOverrides)
            {
                if (string.IsNullOrWhiteSpace(iso3))
                {
                    continue;
                }

                normalizedOverrides[iso3.ToUpperInvariant()] = profile.Normalize();
            }

            catalog.CountryOverrides = normalizedOverrides;
            return catalog;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load city resolver profile catalog from {Path}, using in-code fallback defaults.", _catalogPath);
            return new CityResolverProfileCatalog();
        }
    }
}

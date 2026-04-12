using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class AdministrativeAreaResolverService
{
    private readonly ILogger<AdministrativeAreaResolverService> _logger;
    private readonly CityResolverProfileCatalogService _cityResolverCatalog;
    private readonly OvertureDivisionsService _overtureDivisions;
    private readonly OvertureDivisionCacheService _overtureCache;
    private readonly GadmDivisionsService _gadmDivisions;
    private readonly GadmDivisionCacheService _gadmCache;

    public AdministrativeAreaResolverService(
        ILogger<AdministrativeAreaResolverService> logger,
        CityResolverProfileCatalogService cityResolverCatalog,
        OvertureDivisionsService overtureDivisions,
        OvertureDivisionCacheService overtureCache,
        GadmDivisionsService gadmDivisions,
        GadmDivisionCacheService gadmCache)
    {
        _logger = logger;
        _cityResolverCatalog = cityResolverCatalog;
        _overtureDivisions = overtureDivisions;
        _overtureCache = overtureCache;
        _gadmDivisions = gadmDivisions;
        _gadmCache = gadmCache;
    }

    public async Task<AdministrativeAreaResolution?> ResolveAsync(
        double lat,
        double lon,
        ProcessingConfig config,
        IAdministrativeAreaResolutionProgress? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Checking bundled Overture country coverage...");
        var (iso3, countryName, alpha2) = await _overtureDivisions.FindBundledCountryAsync(lat, lon, ct);
        if (iso3 is null || countryName is null)
        {
            progress?.Report("Bundled Overture country lookup found no match.");
            return null;
        }

        progress?.Report($"Country resolved as {countryName} ({iso3}).");
        var cityResolverProfile = _cityResolverCatalog.GetProfile(config.CityResolver, iso3);
        OvertureAdministrativeResult? overtureResult = null;
        GadmAdministrativeResult? gadmResult = null;

        if (!config.UseGadmAdministrativeAreas || !config.PreferGadmAdministrativeAreas)
        {
            overtureResult = await ResolveOvertureAsync(lat, lon, alpha2, iso3, cityResolverProfile, progress, ct);
        }

        if (config.UseGadmAdministrativeAreas)
        {
            gadmResult = await ResolveGadmAsync(lat, lon, iso3, config.UseGadmTerritoryFallbacks, progress, ct);
        }

        if (config.UseGadmAdministrativeAreas && config.PreferGadmAdministrativeAreas)
        {
            overtureResult ??= await ResolveOvertureAsync(lat, lon, alpha2, iso3, cityResolverProfile, progress, ct);
        }

        var finalState = SelectPreferredValue(
            config,
            gadmResult?.State,
            overtureResult?.State);
        var finalCity = SelectPreferredValue(
            config,
            gadmResult?.City,
            overtureResult?.City);

        return new AdministrativeAreaResolution(
            iso3,
            alpha2,
            countryName,
            new GeoResult(countryName, finalState, finalCity),
            overtureResult,
            gadmResult);
    }

    private async Task<OvertureAdministrativeResult?> ResolveOvertureAsync(
        double lat,
        double lon,
        string? alpha2,
        string iso3,
        CityResolverProfile cityResolverProfile,
        IAdministrativeAreaResolutionProgress? progress,
        CancellationToken ct)
    {
        var (downloadTask, ensureResult) = _overtureCache.GetOrStartDownload(iso3, ct);
        if (ensureResult == OvertureDivisionEnsureResult.StartedDownload)
        {
            _logger.LogInformation("Preparing Overture divisions cache for {ISO3}", iso3);
        }

        using (BeginCacheActivity(progress, GetOvertureCacheActivityMessage(iso3, ensureResult)))
        {
            ReportCacheEvent(progress, GetOvertureCacheLogMessage(iso3, ensureResult));
            await downloadTask.WaitAsync(ct);
        }

        ReportCacheEvent(progress, $"Overture administrative cache ready for {iso3}.");
        progress?.Report("Querying cached Overture administrative areas...");
        return await _overtureDivisions.ResolveAdministrativeGeoAsync(
            lat,
            lon,
            alpha2,
            iso3,
            cityResolverProfile,
            ct);
    }

    private async Task<GadmAdministrativeResult?> ResolveGadmAsync(
        double lat,
        double lon,
        string iso3,
        bool useTerritoryFallbacks,
        IAdministrativeAreaResolutionProgress? progress,
        CancellationToken ct)
    {
        var candidateCodes = useTerritoryFallbacks
            ? GadmCountryFallbackCatalog.ExpandCandidateCodes(iso3)
            : [iso3];

        progress?.Report($"Preparing GADM administrative caches for {string.Join(", ", candidateCodes)}...");
        var readyCodes = new List<string>();
        foreach (var code in candidateCodes)
        {
            try
            {
                var (downloadTask, ensureResult) = _gadmCache.GetOrStartDownload(code, ct);
                using (BeginCacheActivity(progress, GetGadmCacheActivityMessage(code, ensureResult)))
                {
                    ReportCacheEvent(progress, GetGadmCacheLogMessage(code, ensureResult));
                    await downloadTask.WaitAsync(ct);
                }

                if (_gadmCache.HasData(code))
                {
                    readyCodes.Add(code);
                    ReportCacheEvent(progress, $"GADM administrative cache ready for {code}.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GADM cache unavailable for {ISO3}", code);
                ReportCacheEvent(progress, $"GADM administrative cache unavailable for {code}: {ex.Message}");
            }
        }

        if (readyCodes.Count == 0)
        {
            progress?.Report("No GADM administrative caches are available for this lookup.");
            return null;
        }

        progress?.Report($"Querying cached GADM administrative areas across {string.Join(", ", readyCodes)}...");
        var diagnostics = await _gadmDivisions.FindContainingDivisionAreasAsync(lat, lon, readyCodes, ct);
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            return null;
        }

        return new GadmAdministrativeResult(
            GadmDivisionsLogic.SelectStateName(diagnostics.Candidates),
            GadmDivisionsLogic.SelectCityName(diagnostics.Candidates));
    }

    private static string? SelectPreferredValue(
        ProcessingConfig config,
        string? gadmValue,
        string? overtureValue)
    {
        if (!config.UseGadmAdministrativeAreas)
        {
            return overtureValue;
        }

        if (config.PreferGadmAdministrativeAreas)
        {
            return gadmValue ?? overtureValue;
        }

        return overtureValue ?? gadmValue;
    }

    private static IDisposable? BeginCacheActivity(
        IAdministrativeAreaResolutionProgress? progress,
        string? activity)
    {
        if (progress is null || string.IsNullOrWhiteSpace(activity))
        {
            return null;
        }

        return progress.BeginActivity(activity);
    }

    private static void ReportCacheEvent(
        IAdministrativeAreaResolutionProgress? progress,
        string message)
    {
        progress?.Report(message);
    }

    private static string? GetOvertureCacheActivityMessage(string iso3, OvertureDivisionEnsureResult result)
    {
        return result switch
        {
            OvertureDivisionEnsureResult.StartedDownload => $"Downloading Overture administrative cache for {iso3}...",
            OvertureDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for Overture administrative cache for {iso3}...",
            _ => null
        };
    }

    private static string GetOvertureCacheLogMessage(string iso3, OvertureDivisionEnsureResult result)
    {
        return result switch
        {
            OvertureDivisionEnsureResult.StartedDownload => $"Starting Overture administrative cache download for {iso3}.",
            OvertureDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for in-flight Overture administrative cache download for {iso3}.",
            _ => $"Overture administrative cache already ready for {iso3}."
        };
    }

    private static string? GetGadmCacheActivityMessage(string iso3, GadmDivisionEnsureResult result)
    {
        return result switch
        {
            GadmDivisionEnsureResult.StartedDownload => $"Downloading GADM administrative cache for {iso3}...",
            GadmDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for GADM administrative cache for {iso3}...",
            _ => null
        };
    }

    private static string GetGadmCacheLogMessage(string iso3, GadmDivisionEnsureResult result)
    {
        return result switch
        {
            GadmDivisionEnsureResult.StartedDownload => $"Starting GADM administrative cache download for {iso3}.",
            GadmDivisionEnsureResult.AwaitedExistingDownload => $"Waiting for in-flight GADM administrative cache download for {iso3}.",
            _ => $"GADM administrative cache already ready for {iso3}."
        };
    }
}

public record AdministrativeAreaResolution(
    string Iso3,
    string? Alpha2,
    string CountryName,
    GeoResult GeoResult,
    OvertureAdministrativeResult? OvertureResult,
    GadmAdministrativeResult? GadmResult);

public interface IAdministrativeAreaResolutionProgress
{
    IDisposable BeginActivity(string activity);
    void Report(string message);
}

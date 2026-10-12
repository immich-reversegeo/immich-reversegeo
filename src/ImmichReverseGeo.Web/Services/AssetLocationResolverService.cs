using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Web.Services;

/// <summary>The final location policy shared by processing and correction previews.</summary>
public class AssetLocationResolverService(
    AdministrativeAreaResolverService administrativeResolver,
    OverturePlacesService overturePlaces)
{
    public async Task<GeoResult?> ResolveAsync(
        double latitude,
        double longitude,
        ProcessingConfig config,
        IAdministrativeAreaResolutionProgress? progress = null,
        CancellationToken ct = default)
    {
        var admin = await administrativeResolver.ResolveAsync(latitude, longitude, config, progress, ct);
        if (admin is null)
        {
            return null;
        }

        OvertureInfrastructureResult? airport = null;
        if (config.UseAirportInfrastructure)
        {
            progress?.Report("Checking bundled airport infrastructure...");
            var infrastructure = await overturePlaces.FindNearestInfrastructureWithDiagnosticsAsync(
                latitude, longitude, admin.Iso3, ct);
            airport = SelectAirport(admin.GeoResult, infrastructure, config.UsePointOnlyAirportFallback);
        }

        return FinalizeLocation(admin.GeoResult, airport, config.UsePointOnlyAirportFallback);
    }

    public const double PointOnlyAirportRadiusMetres = 100;

    public static OvertureInfrastructureResult? SelectAirport(
        GeoResult admin,
        OvertureInfrastructureLookupDiagnostics diagnostics,
        bool allowPointOnlyFallback)
    {
        if (diagnostics.BestMatch?.IsBoundaryMatch == true)
        {
            return diagnostics.BestMatch;
        }

        if (!allowPointOnlyFallback || admin.City is not null || diagnostics.Error is not null)
        {
            return null;
        }

        var point = diagnostics.Candidates
            .Where(c => c.IsPointOnly && !c.HasMappedBoundary
                && double.IsFinite(c.DistanceMetres) && c.DistanceMetres >= 0
                && c.DistanceMetres <= PointOnlyAirportRadiusMetres)
            // A point record must not circumvent a known footprint for the same airport.
            .Where(p => !diagnostics.Candidates.Any(c => c.HasMappedBoundary
                && string.Equals(c.Name.Trim(), p.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.DistanceMetres)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        return point is null ? null : new OvertureInfrastructureResult(
            point.Id, point.Name, point.FeatureType, point.SubType, point.ClassName,
            point.DistanceMetres, point.BoundingBoxContainsPoint, false, point.Sources,
            HasMappedBoundary: false, IsPointOnly: true);
    }

    public static GeoResult FinalizeLocation(GeoResult result, OvertureInfrastructureResult? airport, bool allowPointOnlyFallback = false)
    {
        if (airport?.IsBoundaryMatch == true
            || (allowPointOnlyFallback && result.City is null && airport is { IsPointOnly: true, HasMappedBoundary: false }
                && double.IsFinite(airport.DistanceMetres) && airport.DistanceMetres >= 0
                && airport.DistanceMetres <= PointOnlyAirportRadiusMetres))
        {
            result = result with { City = airport.Name };
        }

        return result.WithFallbackCity();
    }
}

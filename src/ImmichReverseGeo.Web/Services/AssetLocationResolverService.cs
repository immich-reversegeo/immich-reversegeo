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
            airport = infrastructure.BestMatch;
        }

        return FinalizeLocation(admin.GeoResult, airport);
    }

    public static GeoResult FinalizeLocation(GeoResult result, OvertureInfrastructureResult? airport)
    {
        if (airport?.IsBoundaryMatch == true)
        {
            result = result with { City = airport.Name };
        }

        return result.WithFallbackCity();
    }
}

using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class PointOnlyAirportFallbackTests
{
    private static readonly GeoResult MissingCity = new("Country", "State", null);

    [TestMethod]
    [DataRow(0, true)]
    [DataRow(99.9, true)]
    [DataRow(100, true)]
    [DataRow(100.1, false)]
    [DataRow(-1, false)]
    [DataRow(double.NaN, false)]
    [DataRow(double.PositiveInfinity, false)]
    public void Fallback_RequiresFiniteDistanceWithinOneHundredMetres(double distance, bool eligible)
    {
        var diagnostics = Diagnostics(Point("point", distance));
        var selected = AssetLocationResolverService.SelectAirport(MissingCity, diagnostics, true);
        Assert.AreEqual(eligible, selected is not null);
        Assert.AreEqual(eligible ? "point" : "State",
            AssetLocationResolverService.FinalizeLocation(MissingCity, selected, true).City);
    }

    [TestMethod]
    public void Fallback_IsOffByDefaultAndDoesNotReplaceCityOrCounty()
    {
        var diagnostics = Diagnostics(Point("point", 10));
        Assert.IsNull(AssetLocationResolverService.SelectAirport(MissingCity, diagnostics, false));
        var airport = AssetLocationResolverService.SelectAirport(MissingCity, diagnostics, true)!;
        Assert.AreEqual("State", AssetLocationResolverService.FinalizeLocation(MissingCity, airport).City);
        foreach (var city in new[] { "City", "County" })
        {
            var admin = MissingCity with { City = city };
            Assert.IsNull(AssetLocationResolverService.SelectAirport(admin, diagnostics, true));
            Assert.AreEqual(admin, AssetLocationResolverService.FinalizeLocation(admin, airport, true));
        }
    }

    [TestMethod]
    public void Fallback_UsesNearestPointAndDeterministicIdTieBreak()
    {
        var diagnostics = Diagnostics(Point("far", 90), Point("b", 10), Point("a", 10));
        Assert.AreEqual("a", AssetLocationResolverService.SelectAirport(MissingCity, diagnostics, true)?.Id);
    }

    [TestMethod]
    public void BoundaryMatchWinsOverPointProximity()
    {
        var boundary = new OvertureInfrastructureResult("boundary", "Boundary", "infrastructure", "airport", "airport", 500, true, true, [], true);
        var diagnostics = Diagnostics(Point("point", 0)) with { BestMatch = boundary };
        Assert.AreEqual(boundary, AssetLocationResolverService.SelectAirport(MissingCity, diagnostics, true));
    }

    [TestMethod]
    public void Fallback_CannotBypassKnownBoundaryOrUseUnclassifiedGeometry()
    {
        var point = Point("point", 1) with { Name = " Airport " };
        var outside = Point("polygon", 1000) with { Name = "airport", IsPointOnly = false, HasMappedBoundary = true };
        Assert.IsNull(AssetLocationResolverService.SelectAirport(MissingCity, Diagnostics(point, outside), true));
        Assert.IsNull(AssetLocationResolverService.SelectAirport(MissingCity, Diagnostics(point with { IsPointOnly = false }), true));
        Assert.IsNull(AssetLocationResolverService.SelectAirport(MissingCity, Diagnostics(point) with { Error = "failed" }, true));
    }

    private static OvertureInfrastructureCandidateDiagnostic Point(string id, double distance) =>
        new(id, id, "infrastructure", "airport", "airport", distance, false, false, [], false, "point-only", IsPointOnly: true);

    private static OvertureInfrastructureLookupDiagnostics Diagnostics(params OvertureInfrastructureCandidateDiagnostic[] points) =>
        new(null, points.ToList(), "fixture");
}

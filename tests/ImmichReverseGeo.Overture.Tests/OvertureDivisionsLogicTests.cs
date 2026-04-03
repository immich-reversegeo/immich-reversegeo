using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureDivisionsLogicTests
{
    [TestMethod]
    public void BuildDivisionAreaQuery_WithCountryFilter_EmbedsAlpha2AndDivisionPath()
    {
        var sql = OvertureDivisionsLogic.BuildDivisionAreaQuery(
            lat: 47.4513,
            lon: 8.5574,
            alpha2: "CH",
            releaseUrl: "az://example.test/release/theme=divisions/type=division_area/*.parquet");

        StringAssert.Contains(sql, "read_parquet('az://example.test/release/theme=divisions/type=division_area/*.parquet'");
        StringAssert.Contains(sql, "lower(country) = 'ch'");
        StringAssert.Contains(sql, "ST_Intersects(geometry, ST_Point(8.5574, 47.4513))");
    }

    [TestMethod]
    public void BuildDivisionAreaQuery_WithoutCountryFilter_DoesNotEmbedCountryClause()
    {
        var sql = OvertureDivisionsLogic.BuildDivisionAreaQuery(
            lat: 47.4513,
            lon: 8.5574,
            alpha2: null,
            releaseUrl: "az://example.test/release/theme=divisions/type=division_area/*.parquet");

        Assert.IsFalse(sql.Contains("lower(country)", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersGeometryContainment()
    {
        var geometry = CreateCandidate("locality", geometryContainsPoint: true, bboxArea: 0.50);
        var bboxOnly = CreateCandidate("microhood", geometryContainsPoint: false, bboxArea: 0.01);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(geometry, bboxOnly));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(bboxOnly, geometry));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersMoreSpecificSubtypeWhenContainmentMatches()
    {
        var neighborhood = CreateCandidate("neighborhood", geometryContainsPoint: true, bboxArea: 0.20);
        var locality = CreateCandidate("locality", geometryContainsPoint: true, bboxArea: 0.05);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(neighborhood, locality));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(locality, neighborhood));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersTerritorialBeforeAreaTieBreak()
    {
        var nonTerritorial = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.50, isTerritorial: false);
        var territorial = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.01, isTerritorial: true);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(territorial, nonTerritorial));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(nonTerritorial, territorial));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersSmallerAreaAsFinalTieBreak()
    {
        var tighter = CreateCandidate("county", geometryContainsPoint: false, bboxArea: 0.02);
        var broader = CreateCandidate("county", geometryContainsPoint: false, bboxArea: 0.20);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(tighter, broader));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(broader, tighter));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersLowerAdminLevelWithinSameSubtypePool()
    {
        var lower = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.20, adminLevel: 1);
        var higher = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.10, adminLevel: 2);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(lower, higher));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(higher, lower));
    }

    [TestMethod]
    public void SelectStateName_PrefersRegionOverCounty()
    {
        var result = OvertureDivisionsLogic.SelectStateName(
        [
            CreateDiagnostic("county", "Zurich District", bboxArea: 0.05),
            CreateDiagnostic("region", "Canton of Zurich", bboxArea: 0.20)
        ]);

        Assert.AreEqual("Canton of Zurich", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersLocalityOverNeighborhood()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("neighborhood", "Seefeld", bboxArea: 0.01),
            CreateDiagnostic("locality", "Zurich", bboxArea: 0.20)
        ]);

        Assert.AreEqual("Zurich", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersGeometryMatchesWithinSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Zurich", bboxArea: 0.20, geometryContainsPoint: false),
            CreateDiagnostic("locality", "Zurich-Flughafen", bboxArea: 0.05, geometryContainsPoint: true)
        ]);

        Assert.AreEqual("Zurich-Flughafen", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersTerritorialWithinSameSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Water Label", bboxArea: 0.01, geometryContainsPoint: true, isTerritorial: false),
            CreateDiagnostic("locality", "Real Administrative Area", bboxArea: 0.02, geometryContainsPoint: true, isTerritorial: true)
        ]);

        Assert.AreEqual("Real Administrative Area", result);
    }

    [TestMethod]
    public void SelectCityName_UsesConfiguredSubtypeOrder()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Chassieu", bboxArea: 0.00217),
            CreateDiagnostic("localadmin", "Lyon", bboxArea: 0.39414)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["localadmin", "locality"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Lyon", result);
    }

    [TestMethod]
    public void SelectCityName_UsesLargestAreaTieBreakWithinSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Armfelt", bboxArea: 0.000239),
            CreateDiagnostic("locality", "Salo", bboxArea: 0.60364)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["locality", "localadmin"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Salo", result);
    }

    [TestMethod]
    public void SelectCityName_UsesConfiguredCountyPreference()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Altstadt-Lehel", bboxArea: 0.000856),
            CreateDiagnostic("county", "Munich", bboxArea: 0.067538, adminLevel: 2)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["county", "locality", "localadmin"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Munich", result);
    }

    [TestMethod]
    public void SelectStateName_PrefersLowerAdminLevelWithinSameSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectStateName(
        [
            CreateDiagnostic("region", "Lower Priority Region", bboxArea: 0.01, adminLevel: 2),
            CreateDiagnostic("region", "Preferred Region", bboxArea: 0.20, adminLevel: 1)
        ]);

        Assert.AreEqual("Preferred Region", result);
    }

    private static OvertureDivisionResult CreateCandidate(
        string subtype,
        bool geometryContainsPoint,
        double bboxArea,
        bool isTerritorial = false,
        int? adminLevel = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Candidate",
            SubType: subtype,
            ClassName: "land",
            AdminLevel: adminLevel,
            Country: "CH",
            IsLand: true,
            IsTerritorial: isTerritorial,
            BoundingBoxContainsPoint: true,
            GeometryContainsPoint: geometryContainsPoint,
            BoundingBoxArea: bboxArea);

    private static OvertureDivisionCandidateDiagnostic CreateDiagnostic(
        string subtype,
        string name,
        double bboxArea,
        bool geometryContainsPoint = true,
        bool isTerritorial = false,
        int? adminLevel = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            SubType: subtype,
            ClassName: "land",
            AdminLevel: adminLevel,
            Country: "CH",
            IsLand: true,
            IsTerritorial: isTerritorial,
            BoundingBoxContainsPoint: true,
            GeometryContainsPoint: geometryContainsPoint,
            BoundingBoxArea: bboxArea,
            Selected: false,
            Decision: "test");
}

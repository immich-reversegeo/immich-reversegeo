using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OverturePlacesLogicTests
{
    [TestMethod]
    public void BuildQuery_WithCountryFilter_EmbedsAlpha2AndReleaseUrl()
    {
        var sql = OverturePlacesLogic.BuildQuery(
            lat: 47.45,
            lon: 8.55,
            minLat: 47.40,
            maxLat: 47.50,
            minLon: 8.50,
            maxLon: 8.60,
            alpha2: "CH",
            releaseUrl: "https://example.test/release/theme=places/type=place/*");

        StringAssert.Contains(sql, "read_parquet('https://example.test/release/theme=places/type=place/*'");
        StringAssert.Contains(sql, "lower(addresses[1].country) = 'ch'");
        StringAssert.Contains(sql, "bbox.xmin BETWEEN 8.5 AND 8.6");
        StringAssert.Contains(sql, "bbox.ymin BETWEEN 47.4 AND 47.5");
    }

    [TestMethod]
    public void BuildQuery_WithoutCountryFilter_DoesNotEmbedCountryClause()
    {
        var sql = OverturePlacesLogic.BuildQuery(
            lat: 47.45,
            lon: 8.55,
            minLat: 47.40,
            maxLat: 47.50,
            minLon: 8.50,
            maxLon: 8.60,
            alpha2: null,
            releaseUrl: "https://example.test/release/theme=places/type=place/*");

        Assert.IsFalse(sql.Contains("addresses[1].country", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ParseSources_DeduplicatesAndReturnsDatasets()
    {
        var sources = OverturePlacesLogic.ParseSources("""
            [
              { "dataset": "meta" },
              { "dataset": "foursquare" },
              { "dataset": "meta" }
            ]
            """);

        CollectionAssert.AreEqual(new[] { "meta", "foursquare" }, sources.ToArray());
    }

    [TestMethod]
    public void ParseSources_InvalidJson_ReturnsEmpty()
    {
        var sources = OverturePlacesLogic.ParseSources("not json");

        Assert.AreEqual(0, sources.Count);
    }

    [TestMethod]
    public void ShouldPreferCandidate_PrefersActiveOverClosed()
    {
        var active = CreateCandidate("active", confidence: 0.30, distanceMetres: 600);
        var closed = CreateCandidate("closed", confidence: 0.95, distanceMetres: 10);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(active, closed));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(closed, active));
    }

    [TestMethod]
    public void ShouldPreferCandidate_PrefersBoundingBoxContainmentBeforeDistance()
    {
        var bboxWinner = CreateCandidate("active", confidence: 0.92, distanceMetres: 800, bboxContainsPoint: true);
        var closer = CreateCandidate("active", confidence: 0.99, distanceMetres: 50, bboxContainsPoint: false);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(bboxWinner, closer));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(closer, bboxWinner));
    }

    [TestMethod]
    public void ShouldPreferCandidate_UsesDistanceWhenStatusAndContainmentMatch()
    {
        var closer = CreateCandidate("active", confidence: 0.82, distanceMetres: 120);
        var farther = CreateCandidate("active", confidence: 0.80, distanceMetres: 400);

        Assert.IsTrue(OverturePlacesLogic.ShouldPreferCandidate(closer, farther));
        Assert.IsFalse(OverturePlacesLogic.ShouldPreferCandidate(farther, closer));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_AcceptsHighConfidenceMuseum()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Kunsthaus Zurich",
            Category: "museum",
            BasicCategory: "arts_and_entertainment",
            Confidence: 0.96,
            OperatingStatus: "active",
            DistanceMetres: 150,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsLowConfidenceCandidate()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Scenic Lookout",
            Category: "tourist_attraction",
            BasicCategory: "attractions",
            Confidence: 0.72,
            OperatingStatus: "active",
            DistanceMetres: 150,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsRoutineRetail()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Corner Shop",
            Category: "convenience_store",
            BasicCategory: "retail",
            Confidence: 0.98,
            OperatingStatus: "active",
            DistanceMetres: 25,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    [TestMethod]
    public void IsInterestingPhotoPlace_RejectsTransportNoise()
    {
        var candidate = new OverturePlaceResult(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Gate A12",
            Category: "airport_gate",
            BasicCategory: "transportation",
            Confidence: 0.99,
            OperatingStatus: "active",
            DistanceMetres: 40,
            BoundingBoxContainsPoint: true,
            Sources: ["overture"]);

        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(candidate));
    }

    private static OverturePlaceResult CreateCandidate(string? status, double confidence, double distanceMetres, bool bboxContainsPoint = false) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: "Candidate",
            Category: "airport",
            BasicCategory: "transportation",
            Confidence: confidence,
            OperatingStatus: status,
            DistanceMetres: distanceMetres,
            BoundingBoxContainsPoint: bboxContainsPoint,
            Sources: ["overture"]);
}

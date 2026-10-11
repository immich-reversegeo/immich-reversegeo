using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationPreviewTests
{
    [TestMethod]
    public void Selection_InvalidGuidRejectsWholeSelection()
    {
        Assert.Throws<ArgumentException>(() => LocationPreviewSelection.Create(
            LocationPreviewScope.AssetIds, $"{Guid.NewGuid()}, invalid"));
    }

    [TestMethod]
    public void Selection_DeduplicatesGuidSeparators()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var selection = LocationPreviewSelection.Create(
            LocationPreviewScope.AssetIds, $"{first};\n{second}\t{first}");
        CollectionAssert.AreEquivalent(new[] { first, second }, selection.AssetIds);
    }

    [TestMethod]
    public void Selection_EmptyAndInvalidScopesDoNotBecomeWholeLibrary()
    {
        Assert.Throws<ArgumentException>(() => LocationPreviewSelection.Create(LocationPreviewScope.AssetIds, " , ; "));
        Assert.Throws<ArgumentException>(() => LocationPreviewSelection.Create(LocationPreviewScope.City, " "));
        Assert.Throws<ArgumentException>(() => LocationPreviewSelection.Create((LocationPreviewScope)999, ""));
    }

    [TestMethod]
    public void Selection_LocationValuePreservesExactName()
    {
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.City, "L'Haÿ-les-Roses");
        Assert.AreEqual("L'Haÿ-les-Roses", selection.Value);
    }

    [TestMethod]
    public void Comparison_NoMatchOrErrorDoesNotSuggestClearingExistingLocation()
    {
        var asset = Asset(new GeoResult("France", "Region", "City"));
        Assert.IsFalse(new LocationPreviewRow(asset, null).HasChange);
        Assert.IsFalse(new LocationPreviewRow(asset, new GeoResult(null, null, null)).HasChange);
        Assert.IsFalse(new LocationPreviewRow(asset, new GeoResult("France", "Region", "Other"), "Failure").HasChange);
        Assert.IsFalse(new LocationPreviewRow(asset, new GeoResult("France", null, null)).HasChange);
    }

    [TestMethod]
    public void Comparison_DetectsStateAndCountryChangesWithSameCity()
    {
        var asset = Asset(new GeoResult("France", "Region", "City"));
        Assert.IsTrue(new LocationPreviewRow(asset, new GeoResult("France", "Other region", "City")).HasChange);
        Assert.IsTrue(new LocationPreviewRow(asset, new GeoResult("Monaco", "Region", "City")).HasChange);
        Assert.IsFalse(new LocationPreviewRow(asset, new GeoResult("France", "Region", "City")).HasChange);
    }

    [TestMethod]
    public void FinalLocation_NearbyAirportDoesNotReplaceAdministrativeCity()
    {
        var existing = new GeoResult("France", "Region", "Commune");
        var final = AssetLocationResolverService.FinalizeLocation(existing, Airport(containsPoint: false));
        Assert.AreEqual(existing, final);
    }

    [TestMethod]
    public void FinalLocation_ContainingAirportOverridesCity()
    {
        var final = AssetLocationResolverService.FinalizeLocation(
            new GeoResult("France", "Region", "Commune"), Airport(containsPoint: true));
        Assert.AreEqual("Airport", final.City);
    }

    [TestMethod]
    public void FinalLocation_MissingCityRejectsNearbyAirportAndUsesStateThenCountry()
    {
        var admin = new GeoResult("France", "Region", null);
        Assert.AreEqual("Region", AssetLocationResolverService.FinalizeLocation(admin, Airport(false)).City);
        Assert.AreEqual("Region", AssetLocationResolverService.FinalizeLocation(admin, null).City);
        Assert.AreEqual("Monaco", AssetLocationResolverService.FinalizeLocation(new GeoResult("Monaco", null, null), null).City);
    }

    [TestMethod]
    public void FinalLocation_UnboundedAirportCannotOverrideEvenWithContainmentFlag()
    {
        var admin = new GeoResult("France", "Region", "City");
        var unbounded = Airport(true) with { HasMappedBoundary = false };
        Assert.AreEqual(admin, AssetLocationResolverService.FinalizeLocation(admin, unbounded));
        Assert.AreEqual("Region", AssetLocationResolverService.FinalizeLocation(admin with { City = null }, unbounded).City);
    }

    private static LocationPreviewAsset Asset(GeoResult existing) => new(Guid.NewGuid(), 48.0, 2.0, existing);

    [TestMethod]
    public void Review_KeepsOnlyExplicitResolvableChangesAcrossPages()
    {
        var review = new LocationPreviewReview();
        var original = new GeoResult("France", "Region", "City");
        var firstPage = new LocationPreviewRow(Asset(original), new GeoResult("France", "Region", "New city"));
        var secondPage = new LocationPreviewRow(Asset(original), new GeoResult("France", "Other region", "City"));
        Assert.AreEqual(0, review.Count);
        review.Select(firstPage, true);
        review.Select(secondPage, true);
        review.Select(firstPage, true);
        review.Select(new LocationPreviewRow(Asset(original), original), true);
        review.Select(new LocationPreviewRow(Asset(original), null), true);
        review.Select(new LocationPreviewRow(Asset(original), new GeoResult("France", "Region", "New city"), "Failure"), true);
        Assert.AreEqual(2, review.Count);
        CollectionAssert.AreEquivalent(new[] { firstPage.Asset.Id, secondPage.Asset.Id }, review.SelectedIds.ToArray());
        review.Select(firstPage, false);
        Assert.IsFalse(review.IsSelected(firstPage.Asset.Id));
        Assert.IsTrue(review.IsSelected(secondPage.Asset.Id));
        review.Clear();
        Assert.AreEqual(0, review.Count);
    }

    [TestMethod]
    public void Review_MissingGpsCannotBeSelectedEvenWithAProposedValue()
    {
        var review = new LocationPreviewReview();
        var asset = new LocationPreviewAsset(Guid.NewGuid(), null, 2, new GeoResult("France", null, "City"));
        var row = new LocationPreviewRow(asset, new GeoResult("France", null, "Other city"));
        Assert.IsFalse(row.HasChange);
        review.Select(row, true);
        Assert.AreEqual(0, review.Count);
    }

    [TestMethod]
    public void Selection_RejectsMoreThanOneThousandUniqueGuids()
    {
        var input = string.Join(",", Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => LocationPreviewSelection.Create(LocationPreviewScope.AssetIds, input));
    }

    private static OvertureInfrastructureResult Airport(bool containsPoint) =>
        new("airport", "Airport", "airport", null, null, 100, true, containsPoint, [], HasMappedBoundary: true);
}

using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingPipelineTests
{
    [TestMethod]
    public void GeoResult_NoCountry_HasMatchFalse()
    {
        var result = new GeoResult(Country: null, State: null, City: null);
        Assert.IsFalse(result.HasMatch);
    }

    [TestMethod]
    public void GeoResult_CountryOnly_HasMatchTrue()
    {
        var result = new GeoResult(Country: "Switzerland", State: null, City: null);
        Assert.IsTrue(result.HasMatch);
    }

    [TestMethod]
    public void GeoResult_WithFallbackCity_UsesStateBeforeCountry()
    {
        var result = new GeoResult(Country: "Switzerland", State: "Zurich", City: null);

        var finalized = result.WithFallbackCity();

        Assert.AreEqual("Zurich", finalized.City);
    }

    [TestMethod]
    public void GeoResult_WithFallbackCity_UsesCountryWhenNoStateOrCity()
    {
        var result = new GeoResult(Country: "Vatican City", State: null, City: null);

        var finalized = result.WithFallbackCity();

        Assert.AreEqual("Vatican City", finalized.City);
    }

    [TestMethod]
    public void ProcessingState_IncrementProcessed_UpdatesCounter()
    {
        var s = new ProcessingState();
        s.StartRun(10);
        s.IncrementProcessed();
        s.IncrementProcessed();
        Assert.AreEqual(2, s.ProcessedThisRun);
    }

    [TestMethod]
    public void ProcessingState_OnChanged_Fires()
    {
        var s = new ProcessingState();
        int callCount = 0;
        s.OnChanged += () => callCount++;
        s.StartRun(5);
        s.IncrementProcessed();
        Assert.IsTrue(callCount >= 2);
    }

    [TestMethod]
    public void AssetCursor_Initial_HasEpochAndEmptyGuid()
    {
        Assert.AreEqual(DateTime.UnixEpoch, AssetCursor.Initial.CreatedAt);
        Assert.AreEqual(Guid.Empty, AssetCursor.Initial.Id);
    }
}

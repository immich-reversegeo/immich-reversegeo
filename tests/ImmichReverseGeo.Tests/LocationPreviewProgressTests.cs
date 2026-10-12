using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationPreviewProgressTests
{
    [TestMethod]
    public void CacheWait_ReportsCountryAndLogsStartAndEndOnlyOnce()
    {
        var stages = new List<string>();
        var events = new List<string>();
        var progress = new LocationPreviewProgress(stages.Add, events.Add);
        var activity = progress.BeginActivity("Downloading Overture administrative cache for ESP...");
        Assert.AreEqual("Downloading Overture administrative cache for ESP...", stages.Single());
        Assert.AreEqual(stages.Single(), events.Single());

        activity.Dispose();
        activity.Dispose();
        Assert.HasCount(2, events);
        StringAssert.Contains(events[1], "Cache wait ended after");
        StringAssert.Contains(events[1], "ESP");
    }

    [TestMethod]
    public void CachedLookups_UpdateStageWithoutFillingLogs()
    {
        var stages = new List<string>();
        var events = new List<string>();
        var progress = new LocationPreviewProgress(stages.Add, events.Add);
        for (var asset = 0; asset < 100; asset++)
        {
            progress.Report("Overture administrative cache already ready for CHE.");
            progress.Report("Querying cached Overture administrative areas...");
        }
        Assert.HasCount(200, stages);
        Assert.HasCount(0, events);
    }

    [TestMethod]
    public void UnavailableCache_IsLoggedAsAWarning()
    {
        var events = new List<string>();
        var progress = new LocationPreviewProgress(_ => { }, events.Add);
        progress.Report("GADM administrative cache unavailable for ESP: download failed.");
        StringAssert.StartsWith(events.Single(), "[WARN]");
        StringAssert.Contains(events.Single(), "ESP");
    }
}

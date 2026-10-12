using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationPreviewScanEstimateTests
{
    [TestMethod]
    public void Progress_EstimateUsesCumulativeActiveTimeAcrossPagesAndResume()
    {
        var progress = new LocationPreviewScanEstimate(200);
        Assert.IsNull(progress.EstimatedRemaining);
        for (var i = 1; i <= 100; i++)
        {
            progress.RecordChecked(TimeSpan.FromSeconds(i * 2));
        }

        Assert.AreEqual(50.0, progress.Percentage);
        Assert.AreEqual(TimeSpan.FromSeconds(200), progress.EstimatedRemaining);
        // Review time is excluded: resuming supplies accumulated active time only.
        progress.RecordChecked(TimeSpan.FromSeconds(202));
        Assert.AreEqual(101L, progress.Checked);
        Assert.AreEqual(TimeSpan.FromSeconds(198), progress.EstimatedRemaining);
        for (var i = 102; i <= 200; i++)
        {
            progress.RecordChecked(TimeSpan.FromSeconds(i * 2));
        }

        Assert.AreEqual(100.0, progress.Percentage);
        Assert.AreEqual(TimeSpan.Zero, progress.EstimatedRemaining);
    }

    [TestMethod]
    public void Progress_WaitsForSampleAndHandlesEmptyOrGrowingSelection()
    {
        var empty = new LocationPreviewScanEstimate(0);
        Assert.AreEqual(100.0, empty.Percentage);
        Assert.AreEqual(TimeSpan.Zero, empty.EstimatedRemaining);
        var progress = new LocationPreviewScanEstimate(10);
        for (var i = 1; i <= 4; i++)
        {
            progress.RecordChecked(TimeSpan.FromSeconds(i));
            Assert.IsNull(progress.EstimatedRemaining);
        }

        progress.RecordChecked(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TimeSpan.FromSeconds(5), progress.EstimatedRemaining);
        progress.UpdateElapsed(TimeSpan.FromSeconds(10));
        Assert.AreEqual(TimeSpan.FromSeconds(10), progress.EstimatedRemaining);
        for (var i = 6; i <= 11; i++)
        {
            progress.RecordChecked(TimeSpan.FromSeconds(i * 2));
        }

        Assert.AreEqual(11L, progress.Checked);
        Assert.AreEqual(100.0, progress.Percentage);
        Assert.AreEqual(TimeSpan.Zero, progress.EstimatedRemaining);
    }

}

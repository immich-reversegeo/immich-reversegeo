using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CacheDownloadProgressTests
{
    [TestMethod]
    [DataRow(null, null)]
    [DataRow(0L, null)]
    [DataRow(200L, 50d)]
    [DataRow(50L, 100d)]
    public void TransferPercentage_RequiresKnownPositiveTotal_AndStaysWithinBounds(long? total, double? expected)
    {
        var progress = new CacheDownloadProgress("GADM", "CHE", DateTimeOffset.UtcNow,
            "Downloading GADM file", 100, total);
        Assert.AreEqual(expected, progress.TransferPercentage);
    }

    [TestMethod]
    public async Task SharedDownload_ReportsWaitingForSlot_AndClearsProgressOnCancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        using var occupiedSlot = await policy.EnterAsync();
        using var cancellation = new CancellationTokenSource();
        var cache = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, directory, policy);
        try
        {
            var first = cache.GetOrStartDownload("CHE", cancellation.Token);
            var second = cache.GetOrStartDownload("CHE");
            Assert.AreSame(first.Task, second.Task);
            Assert.AreEqual(GadmDivisionEnsureResult.AwaitedExistingDownload, second.Result);
            var snapshot = cache.GetActiveDownloads();
            Assert.HasCount(1, snapshot);
            Assert.AreEqual("CHE", snapshot[0].Iso3);
            Assert.AreEqual("GADM", snapshot[0].Source);
            Assert.AreEqual("Waiting for a data processing slot", snapshot[0].Stage);
            Assert.IsNull(snapshot[0].TransferPercentage);

            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await first.Task);
            Assert.HasCount(0, cache.GetActiveDownloads());
            Assert.HasCount(1, snapshot);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

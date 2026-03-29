using System.Diagnostics;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
[TestCategory("Performance")]
public class OverturePerformanceTests
{
    [TestMethod]
    public async Task BundledCountryLookup_WarmQueriesStayReasonablyFast()
    {
        var sourceDb = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db"));

        if (!File.Exists(sourceDb))
        {
            Assert.Inconclusive($"Bundled country divisions DB not found at {sourceDb}");
            return;
        }

        var root = CreateTempRoot();
        try
        {
            var defaultsDir = Path.Combine(root, "defaults");
            Directory.CreateDirectory(defaultsDir);
            File.Copy(sourceDb, Path.Combine(defaultsDir, "overture-country-divisions.db"), overwrite: true);

            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                root,
                root,
                static alpha2 => alpha2.ToUpperInvariant() switch
                {
                    "DE" => "DEU",
                    _ => null
                });

            _ = await service.FindBundledCountryAsync(52.5200, 13.4050);

            var sw = Stopwatch.StartNew();
            const int iterations = 25;
            for (var i = 0; i < iterations; i++)
            {
                var result = await service.FindBundledCountryAsync(52.5200, 13.4050);
                Assert.AreEqual("DEU", result.Iso3);
            }

            sw.Stop();
            var averageMs = sw.Elapsed.TotalMilliseconds / iterations;
            TestContext?.WriteLine($"Bundled country lookup average: {averageMs:0.00} ms over {iterations} iterations");
            Assert.IsTrue(averageMs < 250, $"Expected bundled country lookup average to stay below 250 ms, but got {averageMs:0.00} ms.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task CachedDivisionLookup_OnLargeCountryCacheStaysReasonablyFast()
    {
        var sourceDb = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "ImmichReverseGeo.Web", "localdata", "overture-divisions", "DEU.db"));

        if (!File.Exists(sourceDb))
        {
            Assert.Inconclusive($"Large cached DEU divisions DB not found at {sourceDb}");
            return;
        }

        var root = CreateTempRoot();
        try
        {
            var cacheDir = Path.Combine(root, "overture-divisions");
            Directory.CreateDirectory(cacheDir);
            File.Copy(sourceDb, Path.Combine(cacheDir, "DEU.db"), overwrite: true);

            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                root,
                root,
                static alpha2 => alpha2.ToUpperInvariant() switch
                {
                    "DE" => "DEU",
                    _ => null
                });

            _ = await service.FindContainingDivisionAreasAsync(52.5200, 13.4050, "DE", "DEU");

            var sw = Stopwatch.StartNew();
            const int iterations = 20;
            for (var i = 0; i < iterations; i++)
            {
                var diagnostics = await service.FindContainingDivisionAreasAsync(52.5200, 13.4050, "DE", "DEU");
                Assert.IsTrue(diagnostics.Candidates.Count > 0);
            }

            sw.Stop();
            var averageMs = sw.Elapsed.TotalMilliseconds / iterations;
            TestContext?.WriteLine($"Cached DEU division lookup average: {averageMs:0.00} ms over {iterations} iterations");
            Assert.IsTrue(averageMs < 350, $"Expected cached division lookup average to stay below 350 ms, but got {averageMs:0.00} ms.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    public TestContext? TestContext { get; set; }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-perf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

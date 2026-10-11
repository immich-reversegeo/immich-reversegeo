using System.Diagnostics;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
[TestCategory("Performance")]
public class OverturePerformanceTests
{
    [TestMethod]
    public async Task BundledCountryLookup_WarmQueriesStayReasonablyFast()
    {
        var artifactOverride = Environment.GetEnvironmentVariable("COUNTRY_ARTIFACT_PATH");
        var sourceDb = artifactOverride
            ?? Path.GetFullPath(Path.Combine(
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

            var catalog = CountryIdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"));
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                root,
                root,
                alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
            var coldStart = Stopwatch.StartNew();
            var firstResult = await service.FindBundledCountryAsync(52.5200, 13.4050);
            coldStart.Stop();
            var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
            var retainedMemoryDelta = Math.Max(0, memoryAfter - memoryBefore);
            AssertPerformanceLookupResult(firstResult);

            var sw = Stopwatch.StartNew();
            const int iterations = 25;
            for (var i = 0; i < iterations; i++)
            {
                var result = await service.FindBundledCountryAsync(52.5200, 13.4050);
                AssertPerformanceLookupResult(result);
            }

            sw.Stop();
            var averageMs = sw.Elapsed.TotalMilliseconds / iterations;
            var fileSize = new FileInfo(sourceDb).Length;
            var wkbBytes = ReadTotalWkbBytes(sourceDb);
            TestContext?.WriteLine($"Bundled country file: {fileSize:N0} bytes; WKB: {wkbBytes:N0} bytes");
            TestContext?.WriteLine($"Cold initialization: {coldStart.Elapsed.TotalMilliseconds:0.00} ms; retained managed memory delta: {retainedMemoryDelta:N0} bytes");
            TestContext?.WriteLine($"Bundled country lookup average: {averageMs:0.00} ms over {iterations} iterations");

            const long previousFileSize = 162_414_592;
            const long previousWkbBytes = 161_847_272;
            Assert.IsTrue(fileSize <= previousFileSize * 1.05, $"Bundled country file grew beyond the 5% release budget: {fileSize:N0} bytes.");
            Assert.IsTrue(wkbBytes <= previousWkbBytes * 1.05, $"Bundled country WKB grew beyond the 5% release budget: {wkbBytes:N0} bytes.");
            Assert.IsTrue(
                retainedMemoryDelta <= wkbBytes * 5,
                $"Country index retained more than the 5x-WKB managed-memory budget: {retainedMemoryDelta:N0} bytes for {wkbBytes:N0} WKB bytes.");
            Assert.IsTrue(coldStart.Elapsed < TimeSpan.FromSeconds(30), $"Country index cold initialization exceeded 30 seconds: {coldStart.Elapsed}.");
            Assert.IsTrue(averageMs < 250, $"Expected bundled country lookup average to stay below 250 ms, but got {averageMs:0.00} ms.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void AssertPerformanceLookupResult(BundledCountryLookupResult result)
    {
        Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status);
        Assert.AreEqual("DEU", result.Iso3);
        Assert.AreEqual("DE", result.Alpha2);
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

    private static long ReadTotalWkbBytes(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(length(geom_wkb)), 0) FROM division_area";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

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

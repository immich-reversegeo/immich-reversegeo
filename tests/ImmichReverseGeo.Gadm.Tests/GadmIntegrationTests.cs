using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
[TestCategory("Integration")]
public class GadmIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    public async Task EnsureData_Liechtenstein_DownloadsAndResolvesCandidates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var cache = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                new StorageOptions(tempDir, "data"));

            await cache.EnsureDataAsync("LIE");

            var dbPath = Path.Combine(tempDir, "gadm-divisions", "LIE.db");
            Assert.IsTrue(File.Exists(dbPath), $"Expected GADM cache file at {dbPath}");
            Assert.IsTrue(cache.HasData("LIE"), "Expected GADM cache to report ready after download.");

            var service = new GadmDivisionsService(
                NullLogger<GadmDivisionsService>.Instance,
                tempDir);

            var diagnostics = await service.FindContainingDivisionAreasAsync(47.1410, 9.5209, "LIE");

            Assert.IsNull(diagnostics.Error, $"Expected GADM lookup to succeed, but got: {diagnostics.Error}");
            Assert.IsNotNull(diagnostics.BestMatch, "Expected a containing GADM area near Vaduz.");
            Assert.IsTrue(diagnostics.Candidates.Count > 0, "Expected at least one GADM candidate near Vaduz.");
            Assert.AreEqual(GadmDivisionsLogic.DatasetVersion, diagnostics.Version);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Performance")]
    public async Task EnsureData_AllKnownIso3Countries_DownloadsAndBuildsCaches()
    {
        var iso3Codes = await GetGadmSupportedAppCodesAsync();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var failures = new List<string>();

        try
        {
            var cache = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                new StorageOptions(tempDir, "data"));

            foreach (var iso3 in iso3Codes)
            {
                try
                {
                    TestContext.WriteLine($"Downloading and importing GADM for {iso3}...");
                    await cache.EnsureDataAsync(iso3);

                    var dbPath = Path.Combine(tempDir, "gadm-divisions", $"{iso3}.db");
                    if (!File.Exists(dbPath) || !cache.HasData(iso3))
                    {
                        failures.Add($"{iso3}: cache file missing or empty after import");
                        continue;
                    }

                    cache.DeleteFile(iso3);
                    SqliteConnection.ClearAllPools();
                }
                catch (Exception ex)
                {
                    failures.Add($"{iso3}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                Assert.Fail("GADM full-country import failures:\n" + string.Join("\n", failures));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> GetGadmSupportedAppCodesAsync()
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        var html = await http.GetStringAsync("https://gadm.org/download_country.html");
        var matches = System.Text.RegularExpressions.Regex.Matches(html, "value=\"([A-Z]{3})_");

        return matches
            .Select(match => match.Groups[1].Value)
            .Select(GadmCountryCodeMapper.ToAppCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

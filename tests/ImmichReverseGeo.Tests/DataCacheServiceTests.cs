using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class DataCacheServiceTests
{
    [TestMethod]
    public void Iso3ToAlpha2_CHE_ReturnsCH()
    {
        var service = DataCacheService.CreateForTest(NullLogger<DataCacheService>.Instance);
        Assert.AreEqual("CH", service.Iso3ToAlpha2("CHE"));
    }

    [TestMethod]
    public void Iso3ToAlpha2_Unknown_ReturnsNull()
    {
        var service = DataCacheService.CreateForTest(NullLogger<DataCacheService>.Instance);
        Assert.IsNull(service.Iso3ToAlpha2("XYZ"));
    }

    [TestMethod]
    public void Alpha2ToIso3_VA_ReturnsVAT()
    {
        var service = DataCacheService.CreateForTest(NullLogger<DataCacheService>.Instance);
        Assert.AreEqual("VAT", service.Alpha2ToIso3("VA"));
    }

    [TestMethod]
    public void BundledCountryCodes_AreMappedOrExplicitlyNonIso()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dbPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db");
        var isoPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");

        Assert.IsTrue(File.Exists(dbPath), $"Bundled country divisions DB not found at {dbPath}");
        Assert.IsTrue(File.Exists(isoPath), $"ISO mapping file not found at {isoPath}");

        var iso3ToAlpha2 = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(isoPath))
            ?? throw new InvalidOperationException("Failed to parse iso3166.json");
        var mappedAlpha2 = iso3ToAlpha2.Values
            .Select(value => value.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var explicitlyNonIso = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XA", "XB", "XC", "XD", "XG", "XH", "XI", "XL", "XM", "XN", "XO",
            "XP", "XQ", "XR", "XT", "XU", "XW", "XX", "XY", "XZ"
        };

        var missing = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT country FROM division_area WHERE country IS NOT NULL AND TRIM(country) <> ''";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var alpha2 = reader.GetString(0).ToUpperInvariant();
            if (!mappedAlpha2.Contains(alpha2) && !explicitlyNonIso.Contains(alpha2))
            {
                missing.Add(alpha2);
            }
        }

        CollectionAssert.AreEquivalent(Array.Empty<string>(), missing);
    }

    [TestMethod]
    public void GetOvertureDivisionStatus_WithValidDb_ReturnsRowCountAndRelease()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "overture-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO division_area VALUES ('1', 'Zurich');
                INSERT INTO _meta VALUES ('downloadedAt', '2026-03-27T12:00:00Z');
                INSERT INTO _meta VALUES ('release', '2026-03-18.0');
                ";
            cmd.ExecuteNonQuery();
        }

        try
        {
            var svc = new DataCacheService_TestAccessor(NullLogger<DataCacheService>.Instance, dataDir: tempDir);
            var status = svc.GetOvertureDivisionStatus();

            Assert.IsTrue(status.ContainsKey("CHE"));
            Assert.AreEqual(1L, status["CHE"].RowCount);
            Assert.AreEqual("2026-03-18.0", status["CHE"].Release);
            Assert.IsNotNull(status["CHE"].DownloadedAt);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void DeleteOvertureDivisionFile_RemovesDbAndTempFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "overture-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");
        var tmpPath = Path.Combine(dbDir, "CHE.abc.tmp");
        File.WriteAllText(dbPath, "db");
        File.WriteAllText(tmpPath, "tmp");

        try
        {
            var svc = new DataCacheService_TestAccessor(NullLogger<DataCacheService>.Instance, dataDir: tempDir);
            svc.DeleteOvertureDivisionFile("CHE");

            Assert.IsFalse(File.Exists(dbPath));
            Assert.IsFalse(File.Exists(tmpPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}

internal class DataCacheService_TestAccessor : DataCacheService
{
    public DataCacheService_TestAccessor(ILogger<DataCacheService> logger, string dataDir)
        : base(logger, dataDir)
    {
    }
}

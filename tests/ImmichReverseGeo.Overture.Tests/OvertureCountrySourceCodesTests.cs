using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCountrySourceCodesTests
{
    [TestMethod]
    [DataRow("BQ", "BQ,XE,XS")]
    [DataRow("FR", "FR,CP")]
    [DataRow("SJ", "SJ,XJ")]
    [DataRow("ch", "CH")]
    public void ExportFilter_SelectsCanonicalAndAliasRowsOnly(string alpha2, string expected)
    {
        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        using var query = conn.CreateCommand();
        query.CommandText = $"""
            SELECT upper(country) FROM (VALUES ('bq'), ('xe'), ('xs'), ('fr'), ('cp'), ('sj'), ('xj'), ('ch'), ('nl'), ('xx')) AS areas(country)
            WHERE {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}
            ORDER BY country
            """;
        using var reader = query.ExecuteReader();
        var actual = new List<string>();
        while (reader.Read())
        {
            actual.Add(reader.GetString(0));
        }

        CollectionAssert.AreEquivalent(expected.Split(','), actual.ToArray());
    }

    [TestMethod]
    [DataRow("BQ' OR true --")]
    [DataRow("")]
    [DataRow("12")]
    public void ExportFilter_RejectsInvalidCountryCodes(string alpha2)
    {
        Assert.ThrowsExactly<ArgumentException>(() => OvertureCountrySourceCodes.BuildCountryFilter(alpha2));
    }

    [TestMethod]
    [DataRow("BES", "BQ")]
    [DataRow("FRA", "FR")]
    [DataRow("SJM", "SJ")]
    public void ExistingCacheWithoutAliasCoverage_IsNotReadyEvenAfterStatusRead(string iso3, string alpha2)
    {
        WithCache(iso3, null, (service, path) =>
        {
            Assert.AreEqual(1L, service.GetStatus()[iso3].RowCount);
            Assert.IsFalse(service.HasData(iso3));
            Assert.IsTrue(File.Exists(path), "An incomplete cache must remain available until its replacement succeeds.");
        }, alpha2);
    }

    [TestMethod]
    public void CacheWithOutdatedAliasCoverage_IsNotReady()
    {
        WithCache("BES", "BQ,XE", (service, _) => Assert.IsFalse(service.HasData("BES")), "BQ");
    }

    [TestMethod]
    public void CurrentFormatOrdinaryCountryCache_RemainsReadyWithoutCoverageMetadata()
    {
        WithCache("CHE", null, (service, _) =>
        {
            Assert.IsTrue(service.HasData("CHE"));
            service.EnsureDataAsync("CHE").GetAwaiter().GetResult();
            Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, service.GetOrStartDownload("CHE").Result);
        }, "CH");
    }

    [TestMethod]
    public void CompleteAliasCache_IsReusedWithoutDownloading()
    {
        WithCache("BES", "BQ,XE,XS", (service, _) =>
        {
            service.GetStatus();
            Assert.IsTrue(service.HasData("BES"));
            service.EnsureDataAsync("BES").GetAwaiter().GetResult();
            var (task, result) = service.GetOrStartDownload("BES");
            Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, result);
            Assert.IsTrue(task.IsCompletedSuccessfully);
        }, "BQ");
    }

    private static void WithCache(string iso3, string? coverage, Action<OvertureDivisionCacheService, string> check, string alpha2)
    {
        var root = Path.Combine(Path.GetTempPath(), $"reversegeo-alias-tests-{Guid.NewGuid():N}");
        var dir = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{iso3}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                conn.Open();
                using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE division_area (id TEXT PRIMARY KEY);
                    INSERT INTO division_area VALUES ('fixture');
                    CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO _meta VALUES ('downloadedAt', '2026-09-23T00:00:00Z');
                    INSERT INTO _meta VALUES ('release', '2026-09-23.1');
                    """;
                create.ExecuteNonQuery();
                using (var format = conn.CreateCommand())
                {
                    format.CommandText = "INSERT INTO _meta VALUES ('cacheFormatVersion', $version)";
                    format.Parameters.AddWithValue("$version", OvertureDivisionCacheService.CacheFormatVersion);
                    format.ExecuteNonQuery();
                }
                if (coverage is not null)
                {
                    using var meta = conn.CreateCommand();
                    meta.CommandText = "INSERT INTO _meta VALUES ('sourceCountries', $coverage)";
                    meta.Parameters.AddWithValue("$coverage", coverage);
                    meta.ExecuteNonQuery();
                }
            }

            var service = new FixtureCacheService(root, alpha2);
            check(service, path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private sealed class FixtureCacheService(string root, string alpha2)
        : OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => alpha2)
    {
        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2026-09-23.1";
    }
}

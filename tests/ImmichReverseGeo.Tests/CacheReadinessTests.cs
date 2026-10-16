using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CacheReadinessTests
{
    [TestMethod]
    [DataRow(false, false, "delete")]
    [DataRow(false, false, "empty")]
    [DataRow(false, false, "corrupt")]
    [DataRow(false, true, "delete")]
    [DataRow(false, true, "empty")]
    [DataRow(false, true, "corrupt")]
    [DataRow(true, false, "delete")]
    [DataRow(true, false, "empty")]
    [DataRow(true, false, "corrupt")]
    [DataRow(true, true, "delete")]
    [DataRow(true, true, "empty")]
    [DataRow(true, true, "corrupt")]
    public void ChangedCache_IsRevalidatedAndCanRecover(bool gadm, bool inventoryFirst, string change)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, gadm ? "gadm-divisions" : "overture-divisions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "CHE.db");
        var backup = Path.Combine(root, "backup.db");
        try
        {
            Seed(path, gadm);
            File.Copy(path, backup);
            var overture = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "CH");
            var gadmService = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root);
            if (inventoryFirst)
            {
                Assert.AreEqual(1L, gadm ? gadmService.GetStatus()["CHE"].RowCount : overture.GetStatus()["CHE"].RowCount);
            }
            bool HasData() => gadm ? gadmService.HasData("CHE") : overture.HasData("CHE");
            Assert.IsTrue(HasData());
            Assert.IsTrue(HasData());
            if (change == "delete")
            {
                File.Delete(path);
            }
            else if (change == "empty")
            {
                Execute(path, $"DELETE FROM {(gadm ? "gadm_area" : "division_area")}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            }
            else
            {
                File.WriteAllText(path, "Not a database");
            }
            Assert.IsFalse(HasData(), "Remembered readiness must not hide a missing or invalid file.");
            Assert.IsFalse(HasData());
            File.Copy(backup, path, overwrite: true);
            Assert.IsTrue(HasData(), "The same service instance should recognize a restored cache.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void OvertureReplacement_RechecksCacheFormatAndAliasCoverage()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        var path = Path.Combine(root, "overture-divisions", "BES.db");
        try
        {
            Seed(path, false);
            Execute(path, $"INSERT INTO _meta VALUES ('sourceCountries', '{OvertureCountrySourceCodes.GetCoverageSignature("BQ")}')");
            var service = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "BQ");
            Assert.IsTrue(service.HasData("BES"));
            Execute(path, "UPDATE _meta SET value='old' WHERE key='cacheFormatVersion'");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Assert.IsFalse(service.HasData("BES"));
            Execute(path, $"UPDATE _meta SET value='{OvertureDivisionCacheService.CacheFormatVersion}' WHERE key='cacheFormatVersion'; UPDATE _meta SET value='BQ' WHERE key='sourceCountries'");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            Assert.IsFalse(service.HasData("BES"));
            Execute(path, $"UPDATE _meta SET value='{OvertureCountrySourceCodes.GetCoverageSignature("BQ")}' WHERE key='sourceCountries'");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(6));
            Assert.IsTrue(service.HasData("BES"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    [Timeout(5000)]
    [DataRow(false)]
    [DataRow(true)]
    public void UnchangedReadyCache_DoesNotOpenSqliteAgain(bool gadm)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, gadm ? "gadm-divisions" : "overture-divisions"));
        var path = Path.Combine(root, gadm ? "gadm-divisions" : "overture-divisions", "CHE.db");
        try
        {
            Seed(path, gadm);
            var overture = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "CH");
            var gadmService = new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root);
            bool HasData() => gadm ? gadmService.HasData("CHE") : overture.HasData("CHE");
            Assert.IsTrue(HasData());
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            // An exclusive SQLite transaction blocks another connection's row validation.
            // With unchanged file metadata, readiness should require no database read.
            command.CommandText = "BEGIN EXCLUSIVE";
            command.ExecuteNonQuery();
            try
            {
                Assert.IsTrue(HasData());
            }
            finally
            {
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static void Seed(string path, bool gadm)
    {
        Execute(path, $"""
            CREATE TABLE {(gadm ? "gadm_area" : "division_area")} (id TEXT PRIMARY KEY);
            INSERT INTO {(gadm ? "gadm_area" : "division_area")} VALUES ('row');
            CREATE TABLE IF NOT EXISTS division_name (id TEXT PRIMARY KEY, country TEXT, names_json TEXT); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('release', '2026-09-23.1');
            INSERT INTO _meta VALUES ('cacheFormatVersion', '{OvertureDivisionCacheService.CacheFormatVersion}');
            """);
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

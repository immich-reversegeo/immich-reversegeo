using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class OvertureDivisionCacheServiceTests
{
    [TestMethod]
    public void GetStatus_WithValidDb_ReturnsRowCountAndRelease()
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
            var svc = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH");
            var status = svc.GetStatus();

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
    public void DeleteFile_RemovesDbAndTempFiles()
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
            var svc = new OvertureDivisionCacheService(
                NullLogger<OvertureDivisionCacheService>.Instance,
                tempDir,
                _ => "CH");
            svc.DeleteFile("CHE");

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

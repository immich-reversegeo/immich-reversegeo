using ImmichReverseGeo.Gadm.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public class GadmDivisionCacheServiceTests
{
    [TestMethod]
    public void GetStatus_ReadsCachedDbMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "gadm-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");

        try
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE gadm_area (id TEXT PRIMARY KEY);
                    CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    INSERT INTO gadm_area (id) VALUES ('row-1');
                    INSERT INTO gadm_area (id) VALUES ('row-2');
                    INSERT INTO _meta (key, value) VALUES ('downloadedAt', '2026-04-05T12:34:56Z');
                    INSERT INTO _meta (key, value) VALUES ('version', '4.1');
                    """;
                cmd.ExecuteNonQuery();
            }

            var svc = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir);

            var status = svc.GetStatus();

            Assert.AreEqual(1, status.Count);
            Assert.IsTrue(status.ContainsKey("CHE"));
            Assert.AreEqual(2, status["CHE"].RowCount);
            Assert.AreEqual("4.1", status["CHE"].Version);
            Assert.IsTrue(status["CHE"].FileSizeBytes > 0);
            Assert.AreEqual(DateTime.Parse("2026-04-05T12:34:56Z", null, System.Globalization.DateTimeStyles.RoundtripKind), status["CHE"].DownloadedAt);
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
    public void DeleteFile_RemovesDbAndTempFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var dbDir = Path.Combine(tempDir, "gadm-divisions");
        Directory.CreateDirectory(dbDir);
        var dbPath = Path.Combine(dbDir, "CHE.db");
        var tmpPath = Path.Combine(dbDir, "CHE.abc.tmp");
        File.WriteAllText(dbPath, "db");
        File.WriteAllText(tmpPath, "tmp");

        try
        {
            var svc = new GadmDivisionCacheService(
                NullLogger<GadmDivisionCacheService>.Instance,
                tempDir);
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

using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class LowMemoryTests
{
    [TestMethod]
    public async Task CancelledRemoteLookup_PropagatesCancellationInsteadOfReportingNoMatch()
    {
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        using var slot = policy.Enter();
        using var cts = new CancellationTokenSource();
        var service = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, Path.GetTempPath(), Path.GetTempPath(), policy);
        var lookup = service.FindNearestPlaceWithDiagnosticsAsync(47, 8, "CH", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void DuckDbBudget_AppliesLimitsAndReleasesSlotOnDisposalAndFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        try
        {
            using (var work = new DuckDbWorkScope(policy, root))
            {
                using var cmd = work.Connection.CreateCommand();
                cmd.CommandText = "SELECT current_setting('threads')";
                Assert.AreEqual(1L, Convert.ToInt64(cmd.ExecuteScalar()));
                cmd.CommandText = "SELECT current_setting('memory_limit')";
                Assert.AreEqual("256.0 MiB", cmd.ExecuteScalar()?.ToString());
                cmd.CommandText = "SELECT current_setting('temp_directory')";
                var spillPath = cmd.ExecuteScalar()!.ToString()!;
                Assert.IsTrue(Path.GetFullPath(spillPath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar));
            }

            Assert.AreEqual(0, Directory.GetDirectories(Path.Combine(root, "duckdb-tmp")).Length);
            var blocker = Path.Combine(root, "file");
            File.WriteAllText(blocker, "blocks directory creation");
            Assert.Throws<IOException>(() => new DuckDbWorkScope(policy, blocker));
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var next = new DuckDbWorkScope(policy, root, ct.Token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CountryLookup_LowMemoryPreservesContainmentAndReleasesWarmIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var defaults = Path.Combine(root, "defaults");
        Directory.CreateDirectory(defaults);
        var path = Path.Combine(defaults, "overture-country-divisions.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE division_area (
                        id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                        country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    CREATE TABLE _meta (key TEXT, value TEXT);
                    INSERT INTO _meta VALUES ('release', 'test');
                    """;
                cmd.ExecuteNonQuery();
                cmd.CommandText = """
                    INSERT INTO division_area VALUES
                    ('1', 'Test country', 'country', NULL, 0, 'CH', 1, 1, $geom, -10, -10, 10, 10)
                    """;
                var geometry = new WKTReader().Read("POLYGON((-10 -10,10 -10,10 10,-10 10,-10 -10),(-1 -1,-1 1,1 1,1 -1,-1 -1))");
                cmd.Parameters.AddWithValue("$geom", new WKBWriter().Write(geometry));
                cmd.ExecuteNonQuery();
            }

            var policy = new MemoryResourcePolicy();
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root, policy);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE", policy);
            var normal = await service.FindBundledCountryAsync(5, 5);
            Assert.AreEqual("CHE", normal.Iso3);
            Assert.IsNull((await service.FindBundledCountryAsync(0, 0)).Iso3, "A bounding box alone must not match a polygon hole.");

            policy.LowMemoryMode = true;
            Assert.AreEqual(normal, await service.FindBundledCountryAsync(5, 5));
            Assert.IsNull((await service.FindBundledCountryAsync(0, 0)).Iso3);
            Assert.IsNull((await service.FindBundledCountryAsync(20, 20)).Iso3);

            // Replacing the source after enabling low-memory mode must affect lookup.
            // Disabling it must not resurrect the earlier retained index.
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM division_area";
                cmd.ExecuteNonQuery();
            }
            Assert.IsNull((await service.FindBundledCountryAsync(5, 5)).Iso3);
            policy.LowMemoryMode = false;
            Assert.IsNull((await service.FindBundledCountryAsync(5, 5)).Iso3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

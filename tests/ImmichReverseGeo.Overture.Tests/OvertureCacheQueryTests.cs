using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCacheQueryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public void StagedQuery_PreservesRowsGeometryAndHierarchy_AndCleansDisk(bool missingParent, bool lowMemory)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cache-phases-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        Directory.CreateDirectory(Path.Combine(root, "type=division_area"));
        var stages = new List<int>();
        try
        {
            using var connection = new DuckDBConnection("Data Source=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            if (lowMemory)
            {
                command.CommandText = "SET memory_limit='256MiB'; SET threads=1";
                command.ExecuteNonQuery();
            }
            command.CommandText = """
                CREATE MACRO ST_AsWKB(x) AS x;
                CREATE TABLE divisions AS
                SELECT 'area' AS id, 'AE' AS country,
                    {'primary': 'local area', 'common': MAP {'en': 'English area'}} AS names,
                    [[{'division_id': 'city', 'subtype': 'locality', 'name': 'local city'},
                      {'division_id': 'area', 'subtype': 'macrohood', 'name': 'local area'}]] AS hierarchies;
                INSERT INTO divisions VALUES ('unused', 'AE', {'primary': 'unused', 'common': MAP {'en': 'unused'}}, [[{'division_id': 'unused', 'subtype': 'locality', 'name': 'unused'}]]);
                INSERT INTO divisions VALUES ('city', 'AE', {'primary': 'local city', 'common': MAP {'en': 'English city'}}, NULL);
                CREATE TABLE areas AS
                SELECT 'polygon1' AS id, names, 'macrohood' AS subtype, 'land' AS class, 8 AS admin_level,
                    country, true AS is_land, false AS is_territorial, from_hex('01020304') AS geometry,
                    {'xmin': 1.0, 'ymin': 2.0, 'xmax': 3.0, 'ymax': 4.0} AS bbox, id AS division_id
                FROM divisions WHERE id='area';
                INSERT INTO areas SELECT 'polygon2', names, subtype, class, admin_level, country,
                    NULL, NULL, geometry, bbox, division_id FROM areas;
                INSERT INTO areas SELECT 'excluded', names, subtype, class, admin_level, 'CH',
                    is_land, is_territorial, geometry, bbox, division_id FROM areas LIMIT 1;
                """;
            command.ExecuteNonQuery();
            if (missingParent)
            {
                command.CommandText = "DELETE FROM divisions WHERE id='city'";
                command.ExecuteNonQuery();
            }
            var url = root.Replace('\\', '/');
            command.CommandText = $"COPY divisions TO '{url}/type=division/rows.parquet' (FORMAT PARQUET); COPY areas TO '{url}/type=division_area/rows.parquet' (FORMAT PARQUET);";
            command.ExecuteNonQuery();
            var areaUrl = url + "/type=division_area/*.parquet";
            var original = OvertureCacheQuery.BuildAreaProjection(OvertureDivisionsLogic.BuildDivisionAreaSource(areaUrl, "AE"), true)
                + " WHERE " + OvertureCountrySourceCodes.BuildCountryFilter("AE");
            using (var query = new OvertureCacheQuery(connection, root, NullLogger.Instance,
                (phase, _, snapshot) => { if (snapshot is null) { stages.Add(phase); } }))
            {
                Assert.AreEqual(2L, query.Prepare(areaUrl, "AE"));
                var files = Directory.GetFiles(Path.Combine(root, "duckdb-cache-work"), "*.parquet", SearchOption.AllDirectories)
                    .Select(Path.GetFileName).Order().ToArray();
                CollectionAssert.AreEqual(new[] { "areas.parquet", "names.parquet" }, files);
                command.CommandText = $"SELECT count(*) FROM ({query.BuildNamesQuery()})";
                Assert.AreEqual(missingParent ? 1L : 2L, command.ExecuteScalar(),
                    "Shared references must be unique; parent-only divisions retained; unrelated hierarchies excluded.");
                command.CommandText = $"SELECT count(*) FROM ((SELECT * EXCLUDE (hierarchy_json) FROM ({original})) EXCEPT ALL (SELECT * EXCLUDE (hierarchy_json) FROM ({query.BuildResultQuery()})))";
                Assert.AreEqual(0L, command.ExecuteScalar());
                command.CommandText = $"SELECT count(*) FROM ((SELECT * EXCLUDE (hierarchy_json) FROM ({query.BuildResultQuery()})) EXCEPT ALL (SELECT * EXCLUDE (hierarchy_json) FROM ({original})))";
                Assert.AreEqual(0L, command.ExecuteScalar());
                command.CommandText = $"SELECT hierarchy_json FROM ({query.BuildResultQuery()}) LIMIT 1";
                StringAssert.Contains(command.ExecuteScalar()!.ToString()!, "local city");
                command.CommandText = $"SELECT names_json FROM ({query.BuildNamesQuery()}) WHERE id='city'";
                var parentNames = command.ExecuteScalar()?.ToString();
                if (missingParent)
                {
                    Assert.IsNull(parentNames);
                }
                else
                {
                    StringAssert.Contains(parentNames!, "English city");
                }
            }
            CollectionAssert.AreEqual(new[] { 1, 2 }, stages);
            Assert.HasCount(0, Directory.GetDirectories(Path.Combine(root, "duckdb-cache-work")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void FailedBoundaryRead_RemovesEarlierStagedNames()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cache-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            var nameFile = Path.Combine(root, "type=division/rows.parquet").Replace('\\', '/');
            command.CommandText = $"COPY (SELECT 'x' AS id, 'AE' AS country, {{'primary': 'x', 'common': MAP {{'en': 'x'}}}} AS names, [[{{'division_id': 'x', 'subtype': 'locality', 'name': 'x'}}]] AS hierarchies) TO '{nameFile}' (FORMAT PARQUET)";
            command.ExecuteNonQuery();
            using (var query = new OvertureCacheQuery(connection, root, NullLogger.Instance, (_, _, _) => { }))
            {
                Assert.Throws<DuckDBException>(() => query.Prepare(Path.Combine(root, "type=division_area/*.parquet").Replace('\\', '/'), "AE"));
            }
            Assert.HasCount(0, Directory.GetDirectories(Path.Combine(root, "duckdb-cache-work")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CancelledPreparation_CleansItsStagingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cache-cancel-{Guid.NewGuid():N}");
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (var query = new OvertureCacheQuery(connection, root, NullLogger.Instance, (_, _, _) => { }))
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => query.Prepare("unused/type=division_area/*.parquet", "AE", cancellation.Token));
            }
            Assert.HasCount(0, Directory.GetDirectories(Path.Combine(root, "duckdb-cache-work")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using System.Text.Json;
using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureHierarchyMemoryTests
{
    [TestMethod]
    public void CacheExport_StreamsEveryPolygonAndPreservesMissingHierarchies()
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-export-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        Directory.CreateDirectory(Path.Combine(root, "type=division_area"));
        var source = root.Replace('\\', '/').Replace("'", "''");
        var geometry = new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0,1 0,1 1,0 1,0 0))"));
        try
        {
            using (var connection = new DuckDBConnection("DataSource=:memory:"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE polygon (geometry BLOB); INSERT INTO polygon VALUES ($geometry);";
                command.Parameters.Add(new DuckDBParameter("geometry", geometry));
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = $$$"""
                    CREATE TABLE divisions AS SELECT 'd' || i AS id, 'DE' AS country,
                        {'primary': 'local' || i, 'common': MAP {'en': 'English' || i}} AS names,
                        [[{'division_id': 'd' || i, 'subtype': 'locality', 'name': 'local' || i}]] AS hierarchies
                        FROM range(3) t(i);
                    COPY divisions TO '{{{source}}}/type=division/rows.parquet' (FORMAT PARQUET);
                    COPY (SELECT 'a' || i AS id, 'd' || (i % 4) AS division_id, 'DE' AS country,
                        {'primary': 'area' || i, 'common': MAP {'en': 'Area' || i}} AS names,
                        'locality' AS subtype, NULL::VARCHAR AS class, 8 AS admin_level,
                        true AS is_land, false AS is_territorial, geometry,
                        {'xmin': 0::DOUBLE, 'ymin': 0::DOUBLE, 'xmax': 1::DOUBLE, 'ymax': 1::DOUBLE} AS bbox
                        FROM range(4096) t(i), polygon)
                        TO '{{{source}}}/type=division_area/rows.parquet' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);
                    """;
                command.ExecuteNonQuery();
            }

            var cache = Path.Combine(root, "cache.db");
            Assert.AreEqual(4096L, new FixtureExporter(root).Export(cache));
            using var sqlite = new SqliteConnection($"Data Source={cache};Mode=ReadOnly;Pooling=false");
            sqlite.Open();
            using var check = sqlite.CreateCommand();
            check.CommandText = "SELECT count(*) FROM division_area";
            Assert.AreEqual(4096L, check.ExecuteScalar());
            check.CommandText = "SELECT count(*) FROM division_area WHERE hierarchy_json IS NULL";
            Assert.AreEqual(1024L, check.ExecuteScalar());
            check.CommandText = "SELECT name, geom_wkb, hierarchy_json FROM division_area WHERE id='a0'";
            using var reader = check.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("Area0", reader.GetString(0));
            CollectionAssert.AreEqual(geometry, (byte[])reader.GetValue(1));
            using var hierarchy = JsonDocument.Parse(reader.GetString(2));
            Assert.AreEqual("English0", hierarchy.RootElement[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "128MiB")]
    [DataRow(true, "256MiB")]
    public void LargeAreaPayloads_PreserveSharedHierarchiesWithinQueryBudget(bool uniquePayloads, string budget)
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-hierarchy-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        Directory.CreateDirectory(Path.Combine(root, "type=division_area"));
        var source = root.Replace('\\', '/').Replace("'", "''");
        var rowCount = uniquePayloads ? 100000 : 20000;
        var payloadSize = uniquePayloads ? 4096 : 16384;
        var payload = uniquePayloads ? "repeat('x', 4088) || lpad(i::VARCHAR, 8, '0')" : "repeat('x', 16384)";
        try
        {
            // Multiple areas per division, with more total payload than the query budget.
            // Prepare in a separate instance so setup allocations cannot affect the budget.
            using (var setupConnection = new DuckDBConnection("DataSource=:memory:"))
            {
                setupConnection.Open();
                using var setup = setupConnection.CreateCommand();
                setup.CommandText = $$$"""
                    CREATE TABLE divisions AS SELECT 'd' || i AS id, 'DE' AS country,
                        {'primary': 'local' || i, 'common': MAP {'en': 'English' || i}} AS names,
                        [[{'division_id': 'd' || i, 'subtype': 'locality', 'name': 'local' || i}]] AS hierarchies
                        FROM range(10000) t(i);
                    COPY divisions TO '{{{source}}}/type=division/rows.parquet' (FORMAT PARQUET);
                    COPY (SELECT 'a' || i AS id, 'd' || (i % 10000) AS division_id, 'DE' AS country,
                        {{{payload}}} AS geometry FROM range({{{rowCount}}}) t(i))
                        TO '{{{source}}}/type=division_area/rows.parquet' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);
                    """;
                setup.ExecuteNonQuery();
            }

            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SET memory_limit='{budget}'; SET threads=4; SET temp_directory='{source}/spill';";
            command.ExecuteNonQuery();
            command.UseStreamingMode = true;
            command.CommandText = "SELECT id, geometry, hierarchy_json FROM " +
                OvertureDivisionsLogic.BuildDivisionAreaSource(source + "/type=division_area/*.parquet", "DE");
            using var reader = command.ExecuteReader();
            var ids = new HashSet<int>();
            while (reader.Read())
            {
                var id = int.Parse(reader.GetString(0).AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(ids.Add(id), "Every polygon must appear exactly once.");
                var geometry = reader.GetString(1);
                Assert.AreEqual(payloadSize, geometry.Length);
                if (uniquePayloads)
                {
                    Assert.AreEqual(id.ToString("D8", System.Globalization.CultureInfo.InvariantCulture), geometry[^8..]);
                }
                using var hierarchy = JsonDocument.Parse(reader.GetString(2));
                Assert.AreEqual(1, hierarchy.RootElement.GetArrayLength());
                var ancestor = hierarchy.RootElement[0];
                Assert.AreEqual("d" + (id % 10000), ancestor.GetProperty("division_id").GetString());
                Assert.AreEqual("English" + (id % 10000), ancestor.GetProperty("name").GetString());
                Assert.AreEqual("local" + (id % 10000), ancestor.GetProperty("names").GetProperty("primary").GetString());
            }
            Assert.AreEqual(rowCount, ids.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FixtureExporter : OvertureDivisionCacheService
    {
        private readonly string _root;

        public FixtureExporter(string root) : base(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "DE")
        {
            _root = root;
        }

        public long Export(string path) => base.ExportOvertureDivisions(path, "DE", "DEU", "test", CancellationToken.None);

        protected override string GetDivisionAreaReleaseUrl(string release) =>
            Path.Combine(_root, "type=division_area", "*.parquet").Replace('\\', '/');

        protected override void PrepareExportConnection(DuckDBConnection connection)
        {
            // Fixtures already contain WKB, so exercise the production exporter without downloading extensions.
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE MACRO ST_AsWKB(geometry) AS geometry; SET memory_limit='128MiB'; SET threads=4;";
            command.ExecuteNonQuery();
        }
    }
}

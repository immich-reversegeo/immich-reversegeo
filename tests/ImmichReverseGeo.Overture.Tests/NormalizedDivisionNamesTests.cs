using System.Security.Cryptography;
using System.Text.Json;
using ImmichReverseGeo.Overture.Models;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class NormalizedDivisionNamesTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionBuilder_MultilingualHierarchiesFit256MiB_AndSwitchLanguagesWithoutRebuild(bool lowMemory)
    {
        var root = Path.Combine(Path.GetTempPath(), "normalized-division-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        Directory.CreateDirectory(Path.Combine(root, "type=division_area"));
        var source = root.Replace('\\', '/').Replace("'", "''");
        try
        {
            using (var setup = new DuckDBConnection("DataSource=:memory:"))
            {
                setup.Open();
                using var command = setup.CreateCommand();
                command.CommandText = $$$"""
                    CREATE TABLE divisions AS SELECT 'd' || i AS id, 'FR' AS country,
                        {'primary': 'Local' || i, 'common': map_concat(MAP {'en': 'English' || i, 'fr': 'Français' || i},
                            map_from_entries(list_transform(range(16), j ->
                                {'key': 'l' || j, 'value': 'Translation' || i || '-' || j || repeat('x', 64)})))} AS names,
                        [list_concat(list_transform(range(9), j -> {'division_id': 'd' || j,
                            'subtype': CASE WHEN j=0 THEN 'country' ELSE 'region' END, 'name': 'Parent' || j}),
                            [{'division_id': 'd' || i, 'subtype': 'locality', 'name': 'Local' || i}])] AS hierarchies
                        FROM range(10000) t(i);
                    INSERT INTO divisions SELECT 'unused', 'FR', names,
                        [[{'division_id': 'unused', 'subtype': 'locality', 'name': 'Unused'}]]
                        FROM divisions LIMIT 1;
                    COPY divisions TO '{{{source}}}/type=division/rows.parquet' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);
                    COPY (SELECT id || '-area' AS id, country, names, id AS division_id, 'locality' AS subtype,
                        NULL::VARCHAR AS class, 8 AS admin_level, true AS is_land, false AS is_territorial,
                        from_hex('0103000000010000000500000000000000000000000000000000000000000000000000F03F0000000000000000000000000000F03F000000000000F03F0000000000000000000000000000F03F00000000000000000000000000000000') AS geometry,
                        {'xmin': CASE WHEN id='d9999' THEN 0 ELSE 10 END::DOUBLE, 'ymin': 0::DOUBLE,
                         'xmax': CASE WHEN id='d9999' THEN 1 ELSE 11 END::DOUBLE, 'ymax': 1::DOUBLE} AS bbox
                        FROM divisions WHERE id NOT IN ('unused', 'd8')) TO '{{{source}}}/type=division_area/rows.parquet' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);
                    """;
                command.ExecuteNonQuery();
            }

            var exporter = new FixtureCache(root, lowMemory);
            await exporter.EnsureDataAsync("FRA");
            Assert.AreEqual("256.0 MiB", exporter.MemoryLimit);
            Assert.AreEqual(lowMemory ? 1L : 4L, exporter.Threads);
            var cache = Path.Combine(root, "overture-divisions", "FRA.db");
            Assert.IsTrue(exporter.HasData("FRA"), "Validate and publish the actual production cache.");
            using (var connection = new SqliteConnection($"Data Source={cache};Mode=ReadOnly;Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM division_name";
                Assert.AreEqual(10000L, command.ExecuteScalar());
                command.CommandText = "SELECT count(*) FROM division_name WHERE id='unused'";
                Assert.AreEqual(0L, command.ExecuteScalar(), "Do not import an unrelated division's self-referencing hierarchy.");
                command.CommandText = "SELECT count(*) FROM division_name WHERE id='d8'";
                Assert.AreEqual(1L, command.ExecuteScalar(), "Retain referenced parents even when they have no polygon.");
                command.CommandText = "SELECT count(*) FROM division_area";
                Assert.AreEqual(9999L, command.ExecuteScalar());
                command.CommandText = "SELECT hierarchy_json FROM division_area WHERE id='d9999-area'";
                using var hierarchy = JsonDocument.Parse((string)command.ExecuteScalar()!);
                Assert.AreEqual(10, hierarchy.RootElement.GetArrayLength());
                foreach (var entry in hierarchy.RootElement.EnumerateArray())
                {
                    Assert.IsFalse(entry.TryGetProperty("names", out _), "Hierarchies must reference names instead of duplicating maps.");
                }
            }

            var before = SHA256.HashData(File.ReadAllBytes(cache));
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "FRA");
            foreach (var (mode, language, city, state, country) in new[]
            {
                ("english", "en", "English9999", "English1", (string?)null),
                ("local", "en", "Local9999", "Local1", "Local0"),
                ("preferred", "fr-CA", "Français9999", "Français1", "Français0"),
                ("preferred", "ja", "Local9999", "Local1", "Local0")
            })
            {
                var result = await service.ResolveAdministrativeGeoAsync(0.5, 0.5, "FR", "FRA",
                    locationNames: new LocationNameOptions { Mode = mode, PreferredLanguage = language });
                Assert.IsNotNull(result);
                Assert.AreEqual(city, result.City);
                Assert.AreEqual(state, result.State);
                Assert.AreEqual(country, result.Country);
            }
            CollectionAssert.AreEqual(before, SHA256.HashData(File.ReadAllBytes(cache)));
            Assert.AreEqual(1, exporter.Exports);
            // Missing references retain source fallback names; another country's record
            // must not supply a localized country label for the resolved FR identity.
            using (var connection = new SqliteConnection($"Data Source={cache};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM division_name WHERE id='d1'; UPDATE division_name SET country='DE' WHERE id='d0';";
                command.ExecuteNonQuery();
            }
            var fallback = await service.ResolveAdministrativeGeoAsync(0.5, 0.5, "FR", "FRA",
                locationNames: new LocationNameOptions { Mode = "local" });
            Assert.IsNotNull(fallback);
            Assert.AreEqual("Parent1", fallback.State);
            Assert.AreEqual("Local9999", fallback.City);
            Assert.IsNull(fallback.Country);
            Assert.HasCount(0, Directory.GetDirectories(Path.Combine(root, "duckdb-cache-work")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void NormalizedParent_ResolvesNamesWhenHierarchyHasNoSourceLabel(string? sourceLabel)
    {
        var candidate = new OvertureDivisionCandidateDiagnostic("area", "Area", "neighborhood", null, null,
            "DE", true, true, true, true, 1, true, "test", "owner",
            JsonSerializer.Serialize(new[]
            {
                new { division_id = "country", subtype = "country", name = (string?)"Germany" },
                new { division_id = "state", subtype = "region", name = sourceLabel },
                new { division_id = "owner", subtype = "neighborhood", name = (string?)"Area" }
            }));
        var names = new Dictionary<string, (string? NamesJson, string? Country)>
        {
            ["state"] = ("""{"primary":"Bayern","common":{"en":"Bavaria","fr":"Bavière"}}""", "DE")
        };
        var localized = OvertureNameLocalizer.Localize(new OvertureDivisionLookupDiagnostics(null, [candidate], "test"),
            new LocationNameOptions { Mode = "preferred", PreferredLanguage = "fr" }, names);
        var ancestors = OvertureDivisionsLogic.ParseHierarchy(localized.Candidates.Single());
        Assert.AreEqual("Bavière", ancestors.Single(entry => entry.DivisionId == "state").Name);
    }

    private sealed class FixtureCache : OvertureDivisionCacheService
    {
        private readonly string _root;
        private readonly bool _lowMemory;
        public string? MemoryLimit;
        public long Threads;
        public int Exports;

        public FixtureCache(string root, bool lowMemory) : base(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "FR",
            new MemoryResourcePolicy { LowMemoryMode = lowMemory })
        {
            _root = root;
            _lowMemory = lowMemory;
        }
        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2026-09-23.1";
        protected override string GetDivisionAreaReleaseUrl(string release) => Path.Combine(_root, "type=division_area", "*.parquet").Replace('\\', '/');

        protected override void PrepareExportConnection(DuckDBConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE MACRO ST_AsWKB(x) AS x; SET memory_limit='256MiB'; SET threads={(_lowMemory ? 1 : 4)};";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT current_setting('memory_limit')";
            MemoryLimit = (string)command.ExecuteScalar()!;
            command.CommandText = "SELECT current_setting('threads')";
            Threads = Convert.ToInt64(command.ExecuteScalar());
        }

        protected override long ExportOvertureDivisions(string path, string alpha2, string iso3, string release, CancellationToken ct)
        {
            Exports++;
            return base.ExportOvertureDivisions(path, alpha2, iso3, release, ct);
        }
    }
}

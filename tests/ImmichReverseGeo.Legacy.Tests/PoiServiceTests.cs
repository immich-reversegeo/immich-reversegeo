using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Legacy.Models;
using ImmichReverseGeo.Legacy.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Legacy.Tests;

[TestClass]
public class PoiServiceTests
{
    private static readonly WKBWriter WkbWriter = new();
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private void CreateTestDb(string iso3)
    {
        var poiDir = Path.Combine(_tempDir, "poi");
        Directory.CreateDirectory(poiDir);
        var airportGeom = WkbWriter.Write(new Point(8.5492, 47.4647) { SRID = 4326 });
        using var conn = new SqliteConnection($"Data Source={Path.Combine(poiDir, $"{iso3}.db")};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE poi (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                latitude REAL NOT NULL, longitude REAL NOT NULL,
                primary_category_id TEXT NOT NULL,
                geom_wkb BLOB NULL,
                bbox_xmin REAL NULL,
                bbox_ymin REAL NULL,
                bbox_xmax REAL NULL,
                bbox_ymax REAL NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z');
            INSERT INTO poi VALUES
                ('1', 'Zurich Airport', 47.4647, 8.5492, '4bf58dd8d48988d1ed931735', $geom1, 8.5000, 47.4000, 8.6000, 47.5000),
                ('2', 'Some Cafe',      47.3769, 8.5417, 'some-other-id', NULL, NULL, NULL, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("$geom1", airportGeom);
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public async Task FindNearestPoi_CloseToAirport_ReturnsAirport()
    {
        CreateTestDb("CHE");
        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = ["4bf58dd8d48988d1ed931735"];

        var tierMap = new Dictionary<string, CategoryTier>
        {
            ["4bf58dd8d48988d1ed931735"] = CategoryTier.MajorTransport
        };

        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);
        var result = await svc.FindNearestPoiAsync(47.4700, 8.5500, "CHE");

        Assert.IsNotNull(result);
        Assert.AreEqual("Zurich Airport", result.Name);
        Assert.AreEqual(CategoryTier.MajorTransport, result.Tier);
        Assert.IsTrue(result.DistanceMetres > 0);
        Assert.IsTrue(result.DistanceMetres < 1000);
    }

    [TestMethod]
    public async Task FindNearestPoi_NoPoi_ReturnsNull()
    {
        CreateTestDb("CHE");
        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = ["4bf58dd8d48988d1ed931735"];
        var tierMap = new Dictionary<string, CategoryTier>
        {
            ["4bf58dd8d48988d1ed931735"] = CategoryTier.MajorTransport
        };
        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);

        var result = await svc.FindNearestPoiAsync(45.9, 6.1, "CHE");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindNearestPoi_TierPriority_LowerTierWinsOverCloser()
    {
        const string stationCatId = "stationcat001";
        const string cafeCatId = "cafecat000001";

        var poiDir = Path.Combine(_tempDir, "poi");
        Directory.CreateDirectory(poiDir);
        using var conn = new SqliteConnection($"Data Source={Path.Combine(poiDir, "CHE.db")}");
        conn.Open();
        var stationGeom = WkbWriter.Write(new Point(8.5402, 47.3779) { SRID = 4326 });
        var cafeGeom = WkbWriter.Write(new Point(8.5417, 47.3769) { SRID = 4326 });
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE poi (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                latitude REAL NOT NULL, longitude REAL NOT NULL,
                primary_category_id TEXT NOT NULL,
                geom_wkb BLOB NULL,
                bbox_xmin REAL NULL,
                bbox_ymin REAL NULL,
                bbox_xmax REAL NULL,
                bbox_ymax REAL NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z');
            INSERT INTO poi VALUES
                ('A', 'Zurich HB Station', 47.3779, 8.5402, '{stationCatId}', $geomA, NULL, NULL, NULL, NULL),
                ('B', 'Some Cafe',         47.3769, 8.5417, '{cafeCatId}', $geomB, NULL, NULL, NULL, NULL);
            """;
        cmd.Parameters.AddWithValue("$geomA", stationGeom);
        cmd.Parameters.AddWithValue("$geomB", cafeGeom);
        cmd.ExecuteNonQuery();
        conn.Close();
        SqliteConnection.ClearAllPools();

        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = [stationCatId, cafeCatId];

        var tierMap = new Dictionary<string, CategoryTier>
        {
            [stationCatId] = CategoryTier.LocalTransport,
            [cafeCatId]    = CategoryTier.Default
        };

        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);
        var result = await svc.FindNearestPoiAsync(47.3769, 8.5417, "CHE");

        Assert.IsNotNull(result);
        Assert.AreEqual("Zurich HB Station", result.Name);
        Assert.AreEqual(CategoryTier.LocalTransport, result.Tier);
    }

    [TestMethod]
    public async Task FindNearestPoi_InvalidCategoryId_ReturnsNull()
    {
        CreateTestDb("CHE");

        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = ["'; DROP TABLE poi; --"];

        var tierMap = new Dictionary<string, CategoryTier>();
        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);

        var result = await svc.FindNearestPoiAsync(47.4700, 8.5500, "CHE");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindNearestPoi_NoDbFileForCountry_ReturnsNull()
    {
        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = ["4bf58dd8d48988d1ed931735"];
        var tierMap = new Dictionary<string, CategoryTier>();
        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);

        var result = await svc.FindNearestPoiAsync(47.4700, 8.5500, "XYZ");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindNearestPoi_LegacyAirportId_IsUpgradedToAirport()
    {
        CreateTestDb("CHE");
        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = ["4bf58dd8d48988d1f0931735"];

        var tierMap = new Dictionary<string, CategoryTier>
        {
            ["4bf58dd8d48988d1ed931735"] = CategoryTier.MajorTransport
        };

        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);
        var result = await svc.FindNearestPoiAsync(47.4700, 8.5500, "CHE");

        Assert.IsNotNull(result);
        Assert.AreEqual("Zurich Airport", result.Name);
        Assert.AreEqual(CategoryTier.MajorTransport, result.Tier);
    }

    [TestMethod]
    public async Task FindNearestPoi_GeometryContainment_BeatsBboxOnlyWithinSameTier()
    {
        const string districtA = "districtA";
        const string districtB = "districtB";

        var poiDir = Path.Combine(_tempDir, "poi");
        Directory.CreateDirectory(poiDir);
        using var conn = new SqliteConnection($"Data Source={Path.Combine(poiDir, "CHE.db")}");
        conn.Open();

        var exactGeom = WkbWriter.Write(new Point(8.5417, 47.3769) { SRID = 4326 });

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE poi (
                id TEXT PRIMARY KEY, name TEXT NOT NULL,
                latitude REAL NOT NULL, longitude REAL NOT NULL,
                primary_category_id TEXT NOT NULL,
                geom_wkb BLOB NULL,
                bbox_xmin REAL NULL,
                bbox_ymin REAL NULL,
                bbox_xmax REAL NULL,
                bbox_ymax REAL NULL
            );
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('downloadedAt', '2026-01-01T00:00:00Z');
            INSERT INTO poi VALUES
                ('A', 'BBox Match', 47.3768, 8.5416, '{districtA}', NULL, 8.5400, 47.3760, 8.5430, 47.3780),
                ('B', 'Geometry Match', 47.3772, 8.5421, '{districtB}', $geomB, 8.5300, 47.3600, 8.5500, 47.3900);
            """;
        cmd.Parameters.AddWithValue("$geomB", exactGeom);
        cmd.ExecuteNonQuery();

        var cfg = new LegacyPoiConfig();
        cfg.CategoryAllowlist = [districtA, districtB];
        var tierMap = new Dictionary<string, CategoryTier>
        {
            [districtA] = CategoryTier.Districts,
            [districtB] = CategoryTier.Districts
        };

        var svc = PoiService.CreateForTest(NullLogger<PoiService>.Instance, cfg, tierMap, dataDir: _tempDir);
        var result = await svc.FindNearestPoiAsync(47.3769, 8.5417, "CHE");

        Assert.IsNotNull(result);
        Assert.AreEqual("Geometry Match", result.Name);
        Assert.IsTrue(result.ContainsPoint);
        Assert.AreEqual(2, result.ContainmentRank);
    }
}

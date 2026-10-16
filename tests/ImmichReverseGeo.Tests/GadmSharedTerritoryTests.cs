using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class GadmSharedTerritoryTests
{
    [TestMethod]
    [DataRow("HKG", true, true, true)]
    [DataRow("MAC", true, true, true)]
    [DataRow("HKG", true, true, false)]
    [DataRow("MAC", true, true, false)]
    [DataRow("HKG", false, true, true)]
    [DataRow("MAC", false, true, true)]
    [DataRow("HKG", true, false, true)]
    [DataRow("MAC", true, false, true)]
    public async Task SharedDataset_PreservesIdentityAndRequiresNamedGeometry(
        string iso3, bool useFallbacks, bool geometryMatches, bool preferGadm)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            var lat = iso3 == "HKG" ? 22.2812 : 22.1987;
            var lon = iso3 == "HKG" ? 114.1719 : 113.5439;
            var alpha2 = iso3 == "HKG" ? "HK" : "MO";
            var country = iso3 == "HKG" ? "Hong Kong" : "Macao";
            SeedCountry(root, alpha2, country, lon, lat);
            SeedOverture(root, iso3);
            SeedGadm(root, "CHN", lon, lat, geometryMatches);
            SeedGadm(root, iso3, 1000, 1000, true);
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var resolver = new AdministrativeAreaResolverService(
                NullLogger<AdministrativeAreaResolverService>.Instance,
                new CityResolverProfileCatalogService(
                    NullLogger<CityResolverProfileCatalogService>.Instance, new StorageOptions(root, root)),
                new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => iso3),
                new FixtureCacheService(root, _ => alpha2),
                new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root),
                new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root));
            var progress = new RecordingProgress();
            var result = await resolver.ResolveAsync(lat, lon, new ProcessingConfig
            {
                UseGadmAdministrativeAreas = true,
                UseGadmTerritoryFallbacks = useFallbacks,
                PreferGadmAdministrativeAreas = preferGadm
            }, progress);

            Assert.IsNotNull(result);
            Assert.AreEqual(iso3, result.Iso3);
            Assert.AreEqual(alpha2, result.Alpha2);
            Assert.AreEqual(country, result.GeoResult.Country);
            if (useFallbacks && geometryMatches)
            {
                Assert.AreEqual("Shared state", result.GeoResult.State);
                Assert.AreEqual("Shared city", result.GeoResult.City);
            }
            else
            {
                Assert.IsNull(result.GadmResult);
                Assert.IsNull(result.GeoResult.State);
                Assert.IsNull(result.GeoResult.City);
            }

            Assert.AreEqual(useFallbacks, progress.Messages.Any(m => m == "GADM administrative cache ready for CHN."));
            Assert.AreEqual(!useFallbacks, progress.Messages.Any(m => m == $"GADM administrative cache ready for {iso3}."));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void SeedCountry(string root, string alpha2, string name, double lon, double lat)
    {
        using var connection = Open(Path.Combine(root, "defaults", "overture-country-divisions.db"));
        using var command = connection.CreateCommand();
        command.CommandText = DivisionSchema + """
            INSERT INTO division_area VALUES (
                'country', $name, 'country', 'land', 0, $code, 1, 0,
                $geom, $xmin, $ymin, $xmax, $ymax);
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$code", alpha2);
        AddGeometry(command, Box(lon, lat));
        command.ExecuteNonQuery();
    }

    private static void SeedOverture(string root, string iso3)
    {
        using var connection = Open(Path.Combine(root, "overture-divisions", $"{iso3}.db"));
        using var command = connection.CreateCommand();
        command.CommandText = DivisionSchema + """
            CREATE TABLE IF NOT EXISTS division_name (id TEXT PRIMARY KEY, country TEXT, names_json TEXT); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
            INSERT INTO _meta VALUES ('release', 'test');
            INSERT INTO division_area VALUES (
                'ready', 'Ready', 'region', 'land', 1, NULL, 1, 0, NULL,
                1000, 1000, 1001, 1001);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO _meta VALUES ('cacheFormatVersion', $version)";
        command.Parameters.AddWithValue("$version", OvertureDivisionCacheService.CacheFormatVersion);
        command.ExecuteNonQuery();
    }

    private static void SeedGadm(string root, string code, double lon, double lat, bool geometryMatches)
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var geometry = geometryMatches ? Box(lon, lat) : factory.CreatePolygon([
            new Coordinate(lon - 0.8, lat - 0.8), new Coordinate(lon + 0.2, lat - 0.8),
            new Coordinate(lon - 0.8, lat + 0.2), new Coordinate(lon - 0.8, lat - 0.8)
        ]);
        using var connection = Open(Path.Combine(root, "gadm-divisions", $"{code}.db"));
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
            INSERT INTO _meta VALUES ('version', 'test');
            CREATE TABLE gadm_area (
                id TEXT PRIMARY KEY, name TEXT, english_type TEXT, local_type TEXT,
                admin_level INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
            INSERT INTO gadm_area VALUES (
                'state', 'Shared state', 'Province', NULL, 1, $geom, $xmin, $ymin, $xmax, $ymax);
            INSERT INTO gadm_area VALUES (
                'city', 'Shared city', 'District', NULL, 2, $geom, $xmin, $ymin, $xmax, $ymax);
            INSERT INTO gadm_area VALUES (
                'placeholder', 'NA', 'NA', NULL, 3, $geom, $xmin, $ymin, $xmax, $ymax);
            """;
        AddGeometry(command, geometry);
        command.ExecuteNonQuery();
        // A matching source-country polygon alone must not count as admin coverage.
        command.CommandText = """
            INSERT INTO gadm_area VALUES (
                'country', 'China', 'Country', NULL, 0, $geom, $xmin, $ymin, $xmax, $ymax);
            """;
        command.Parameters.Clear();
        AddGeometry(command, Box(lon, lat));
        command.ExecuteNonQuery();
    }

    private static Polygon Box(double lon, double lat) =>
        new GeometryFactory(new PrecisionModel(), 4326).CreatePolygon([
            new Coordinate(lon - 0.01, lat - 0.01), new Coordinate(lon + 0.01, lat - 0.01),
            new Coordinate(lon + 0.01, lat + 0.01), new Coordinate(lon - 0.01, lat + 0.01),
            new Coordinate(lon - 0.01, lat - 0.01)
        ]);

    private static void AddGeometry(SqliteCommand command, Geometry geometry)
    {
        var bounds = geometry.EnvelopeInternal;
        command.Parameters.AddWithValue("$geom", new WKBWriter().Write(geometry));
        command.Parameters.AddWithValue("$xmin", bounds.MinX);
        command.Parameters.AddWithValue("$ymin", bounds.MinY);
        command.Parameters.AddWithValue("$xmax", bounds.MaxX);
        command.Parameters.AddWithValue("$ymax", bounds.MaxY);
    }

    private static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        return connection;
    }

    private const string DivisionSchema = """
        CREATE TABLE division_area (
            id TEXT PRIMARY KEY, name TEXT, subtype TEXT, class_name TEXT,
            admin_level INTEGER, country TEXT, is_land INTEGER, is_territorial INTEGER,
            geom_wkb BLOB, bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
        """;

    private sealed class FixtureCacheService(string root, Func<string, string?> codes)
        : OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, codes)
    {
        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "test";
    }

    private sealed class RecordingProgress : IAdministrativeAreaResolutionProgress
    {
        public List<string> Messages { get; } = [];
        public void Report(string message) => Messages.Add(message);
        public IDisposable BeginActivity(string activity) => new Activity();
    }

    private sealed class Activity : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

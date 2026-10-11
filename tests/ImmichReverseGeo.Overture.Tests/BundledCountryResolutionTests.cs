using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class BundledCountryResolutionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MandatoryTerritoryFixtures_ResolveToCanonicalIdentities(bool lowMemory)
    {
        var catalog = LoadIdentityCatalog();
        using var testData = CreateBundledTestData();
        var service = CreateService(testData.Root, catalog, lowMemory);

        foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories)
        {
            string? sourceId = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await service.FindBundledCountryAsync(fixture.Latitude, fixture.Longitude);

                Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status, fixture.Label);
                Assert.AreEqual(fixture.DisplayName, result.CountryName, fixture.Label);
                Assert.AreEqual(fixture.Alpha3, result.Iso3, fixture.Label);
                Assert.AreEqual(fixture.Alpha2, result.Alpha2, fixture.Label);
                Assert.AreNotEqual(fixture.AdministeringSovereignAlpha2, result.Alpha2, fixture.Label);
                sourceId ??= result.SourceId;
                Assert.AreEqual(sourceId, result.SourceId, $"{fixture.Label} returned a different source row on attempt {attempt + 1}.");
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ParentCountryControls_RetainSovereignIdentities(bool lowMemory)
    {
        var catalog = LoadIdentityCatalog();
        using var testData = CreateBundledTestData();
        var service = CreateService(testData.Root, catalog, lowMemory);

        foreach (var fixture in CountryResolutionFixtureCatalog.ParentCountryControls)
        {
            var result = await service.FindBundledCountryAsync(fixture.Latitude, fixture.Longitude);

            Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status, fixture.Label);
            Assert.AreEqual(fixture.DisplayName, result.CountryName, fixture.Label);
            Assert.AreEqual(fixture.Alpha3, result.Iso3, fixture.Label);
            Assert.AreEqual(fixture.Alpha2, result.Alpha2, fixture.Label);
        }
    }

    [TestMethod]
    [DataRow(17.6350, -63.2320, "BQ", "BES")]
    [DataRow(17.4890, -62.9730, "BQ", "BES")]
    [DataRow(70.9900, -8.5300, "SJ", "SJM")]
    [DataRow(10.3000, -109.2200, "FR", "FRA")]
    public async Task SourceOnlyDependencyCodes_ResolveToUsableCanonicalIdentities(
        double latitude,
        double longitude,
        string expectedAlpha2,
        string expectedAlpha3)
    {
        var catalog = LoadIdentityCatalog();
        using var testData = CreateBundledTestData();
        var service = CreateService(testData.Root, catalog);

        var result = await service.FindBundledCountryAsync(latitude, longitude);

        Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status);
        Assert.AreEqual(expectedAlpha2, result.Alpha2);
        Assert.AreEqual(expectedAlpha3, result.Iso3);
    }

    [TestMethod]
    public async Task MissingBundledArtifact_ReturnsSpatialNoMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = CreateService(root, LoadIdentityCatalog());

            var result = await service.FindBundledCountryAsync(0, 0);

            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, result.Status);
            StringAssert.Contains(result.FailureReason, "unavailable");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MatchedGeometryWithoutCanonicalMapping_ReturnsIdentityMappingFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            CreateSingleCountryDatabase(
                Path.Combine(root, "defaults", "overture-country-divisions.db"));
            var service = CreateService(root, LoadIdentityCatalog());

            var result = await service.FindBundledCountryAsync(0.5, 0.5);

            Assert.AreEqual(BundledCountryLookupStatus.IdentityMappingFailure, result.Status);
            Assert.AreEqual("ZZ", result.Alpha2);
            StringAssert.Contains(result.FailureReason, "inconsistent Alpha-3 mapping");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void BundledArtifact_PassesCanonicalIdentityAndFixtureValidation()
    {
        var statistics = BundledCountryArtifactValidator.Validate(
            GetBundledArtifactPath(),
            LoadIdentityCatalog());

        Assert.AreEqual("2026-08-19.0", statistics.Release);
        Assert.IsTrue(statistics.RowCount > 378);
        Assert.IsTrue(statistics.StandardIdentityCount >= 239);
        Assert.IsTrue(statistics.WkbBytes > 0);
    }

    [TestMethod]
    public async Task OlderArtifactWithoutAlpha3Column_UsesCatalogWithoutRewritingDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        var path = Path.Combine(root, "defaults", "overture-country-divisions.db");
        try
        {
            CreateSingleCountryDatabase(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    ALTER TABLE division_area DROP COLUMN alpha3;
                    UPDATE division_area SET country = 'HK', name = 'Hong Kong';
                    """;
                command.ExecuteNonQuery();
            }

            var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
            var result = await CreateService(root, LoadIdentityCatalog()).FindBundledCountryAsync(0.5, 0.5);
            Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status);
            Assert.AreEqual("HKG", result.Iso3);
            Assert.AreEqual("HK", result.Alpha2);
            CollectionAssert.AreEqual(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("HKG")]
    [DataRow("hkg")]
    public async Task ModernArtifactWithEquivalentAlpha3_ReturnsCanonicalCacheKey(string alpha3)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            var path = Path.Combine(root, "defaults", "overture-country-divisions.db");
            CreateSingleCountryDatabase(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE division_area SET country = 'HK', alpha3 = $alpha3";
                command.Parameters.AddWithValue("$alpha3", alpha3);
                command.ExecuteNonQuery();
            }

            var result = await CreateService(root, LoadIdentityCatalog()).FindBundledCountryAsync(0.5, 0.5);
            Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status);
            Assert.AreEqual("HKG", result.Iso3);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("CHN")]
    public async Task ModernArtifactWithMissingOrInconsistentAlpha3_RemainsAnIdentityError(string? alpha3)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            var path = Path.Combine(root, "defaults", "overture-country-divisions.db");
            CreateSingleCountryDatabase(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE division_area SET country = 'HK', alpha3 = $alpha3";
                command.Parameters.AddWithValue("$alpha3", alpha3 ?? (object)DBNull.Value);
                command.ExecuteNonQuery();
            }

            var result = await CreateService(root, LoadIdentityCatalog()).FindBundledCountryAsync(0.5, 0.5);
            Assert.AreEqual(BundledCountryLookupStatus.IdentityMappingFailure, result.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    [DataRow("HK", null, "HK", BundledCountryLookupStatus.IdentityMappingFailure, false)]
    [DataRow("XA", null, "CN", BundledCountryLookupStatus.Matched, false)]
    [DataRow("HK", null, "HK", BundledCountryLookupStatus.IdentityMappingFailure, true)]
    [DataRow("XA", null, "CN", BundledCountryLookupStatus.Matched, true)]
    public async Task OverlappingSovereign_DoesNotHideBrokenMappedDependency(string sourceCode, string? storedAlpha3, string expectedAlpha2, BundledCountryLookupStatus expectedStatus, bool lowMemory)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            var path = Path.Combine(root, "defaults", "overture-country-divisions.db");
            CreateSingleCountryDatabase(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE division_area SET country = $sourceCode, alpha3 = $storedAlpha3, name = 'Test dependency';
                    INSERT INTO division_area
                    SELECT 'parent-id', 'China', 'country', class_name, admin_level,
                           'CN', 'CHN', is_land, is_territorial, geom_wkb,
                           bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
                    FROM division_area WHERE id = 'test-id';
                    """;
                command.Parameters.AddWithValue("$sourceCode", sourceCode);
                command.Parameters.AddWithValue("$storedAlpha3", storedAlpha3 ?? (object)DBNull.Value);
                command.ExecuteNonQuery();
            }

            var result = await CreateService(root, LoadIdentityCatalog(), lowMemory).FindBundledCountryAsync(0.5, 0.5);
            Assert.AreEqual(expectedStatus, result.Status);
            Assert.AreEqual(expectedAlpha2, result.Alpha2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static OvertureDivisionsService CreateService(string root, CountryIdentityCatalog catalog, bool lowMemory = false)
    {
        var places = new OverturePlacesService(
            NullLogger<OverturePlacesService>.Instance,
            root,
            root);
        return new OvertureDivisionsService(
            NullLogger<OvertureDivisionsService>.Instance,
            places,
            root,
            root,
            alpha2 => catalog.FindByAlpha2(alpha2)?.Alpha3,
            new MemoryResourcePolicy { LowMemoryMode = lowMemory });
    }

    private static CountryIdentityCatalog LoadIdentityCatalog()
    {
        return CountryIdentityCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data", "iso3166.json"));
    }

    private static TemporaryTestData CreateBundledTestData()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        File.Copy(
            GetBundledArtifactPath(),
            Path.Combine(root, "defaults", "overture-country-divisions.db"));
        return new TemporaryTestData(root);
    }

    private static string GetBundledArtifactPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "data", "overture-country-divisions.db");
    }

    private static void CreateSingleCountryDatabase(string path)
    {
        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geometry = geometryFactory.CreatePolygon(
        [
            new Coordinate(0, 0),
            new Coordinate(1, 0),
            new Coordinate(1, 1),
            new Coordinate(0, 1),
            new Coordinate(0, 0)
        ]);
        var wkb = new WKBWriter().Write(geometry);

        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                subtype TEXT NOT NULL,
                class_name TEXT NULL,
                admin_level INTEGER NULL,
                country TEXT NOT NULL,
                alpha3 TEXT NULL,
                is_land INTEGER NOT NULL,
                is_territorial INTEGER NOT NULL,
                geom_wkb BLOB NOT NULL,
                bbox_xmin REAL NOT NULL,
                bbox_ymin REAL NOT NULL,
                bbox_xmax REAL NOT NULL,
                bbox_ymax REAL NOT NULL
            );
            INSERT INTO division_area VALUES (
                'test-id', 'Test Territory', 'dependency', 'land', 1,
                'ZZ', 'ZZZ', 1, 0, $geometry, 0, 0, 1, 1
            );
            """;
        command.Parameters.AddWithValue("$geometry", wkb);
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryTestData(string root) : IDisposable
    {
        public string Root { get; } = root;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch
            {
            }
        }
    }
}

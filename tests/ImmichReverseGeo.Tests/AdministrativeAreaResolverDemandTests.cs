using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

// Adapted from crazz/immich-reversegeo at 31f16bbaa02513c762435af27325caa311af3c5a.
public partial class AdministrativeAreaResolverTerritoryTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CompletePrimary_DoesNotReadBrokenSecondary(bool preferGadm, bool reportProgress)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var unusedSource = preferGadm ? "overture" : "gadm";
            var unusedPath = Path.Combine(root, unusedSource + "-divisions", territory.Alpha3 + ".db");
            await File.WriteAllTextAsync(unusedPath, "Intentionally unreadable secondary cache");
            var progress = new RecordingProgress();
            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, DemandConfig(preferGadm), reportProgress ? progress : null);

            Assert.IsNotNull(result);
            Assert.AreEqual(preferGadm ? "GADM City" : "Overture City", result.GeoResult.City);
            Assert.AreEqual(preferGadm ? "GADM State" : "Overture State", result.GeoResult.State);
            Assert.AreEqual(territory.DisplayName, result.GeoResult.Country);
            if (preferGadm)
            {
                Assert.IsNull(result.OvertureResult);
            }
            else
            {
                Assert.IsNull(result.GadmResult);
            }
            var unusedLabel = preferGadm ? "Overture administrative" : "GADM";
            Assert.IsFalse(progress.Messages.Any(message => message.Contains(unusedLabel, StringComparison.Ordinal)));
            Assert.AreEqual("Intentionally unreadable secondary cache", await File.ReadAllTextAsync(unusedPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, "city")]
    [DataRow(false, "state")]
    [DataRow(false, "both")]
    [DataRow(true, "city")]
    [DataRow(true, "both")]
    public async Task IncompletePrimary_FillsOnlyMissingFields(bool preferGadm, string missing)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var source = preferGadm ? "gadm" : "overture";
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, source + "-divisions", territory.Alpha3 + ".db")};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                // Keep a valid cache, but remove the requested field's geometry from this point.
                command.CommandText = $"UPDATE {(preferGadm ? "gadm_area" : "division_area")} SET bbox_xmin=1000, bbox_xmax=1001 WHERE $missing='both' OR id=$id";
                command.Parameters.AddWithValue("$missing", missing);
                command.Parameters.AddWithValue("$id", source + "-" + missing);
                command.ExecuteNonQuery();
            }
            var progress = new RecordingProgress();
            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, DemandConfig(preferGadm), progress);

            Assert.IsNotNull(result);
            if (!preferGadm && missing == "both")
            {
                Assert.IsNull(result.OvertureResult);
            }
            else
            {
                Assert.IsNotNull(result.OvertureResult);
            }
            if (preferGadm && missing == "both")
            {
                Assert.IsNull(result.GadmResult);
            }
            else
            {
                Assert.IsNotNull(result.GadmResult);
            }
            var primary = preferGadm ? "GADM" : "Overture";
            var secondary = preferGadm ? "Overture" : "GADM";
            Assert.AreEqual((missing is "city" or "both" ? secondary : primary) + " City", result.GeoResult.City);
            Assert.AreEqual((missing is "state" or "both" ? secondary : primary) + " State", result.GeoResult.State);
            Assert.AreEqual(territory.DisplayName, result.GeoResult.Country);
            var primaryStage = preferGadm ? "Querying cached GADM" : "Querying cached Overture";
            var secondaryStage = preferGadm ? "Querying cached Overture" : "Querying cached GADM";
            Assert.IsTrue(progress.Messages.FindIndex(message => message.StartsWith(primaryStage, StringComparison.Ordinal))
                < progress.Messages.FindIndex(message => message.StartsWith(secondaryStage, StringComparison.Ordinal)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisabledGadm_IgnoresStoredPreferenceAndBrokenGadmCache(bool preferGadm)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var path = Path.Combine(root, "gadm-divisions", territory.Alpha3 + ".db");
            await File.WriteAllTextAsync(path, "broken");
            var config = DemandConfig(preferGadm);
            config.UseGadmAdministrativeAreas = false;
            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, config);

            Assert.IsNotNull(result);
            Assert.IsNull(result.GadmResult);
            Assert.AreEqual("Overture City", result.GeoResult.City);
            Assert.AreEqual("Overture State", result.GeoResult.State);
            Assert.AreEqual("broken", await File.ReadAllTextAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelledRequest_DoesNotResolveEitherSource(bool preferGadm)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            var progress = new RecordingProgress();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                    .ResolveAsync(territory.Latitude, territory.Longitude, DemandConfig(preferGadm), progress, cancellation.Token));
            Assert.AreEqual(0, progress.Messages.Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IncompletePrimary_WithNoSecondaryMatch_PreservesExistingFields(bool preferGadm)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            foreach (var source in new[] { "overture", "gadm" })
            {
                using var connection = new SqliteConnection($"Data Source={Path.Combine(root, source + "-divisions", territory.Alpha3 + ".db")};Pooling=false");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"UPDATE {(source == "gadm" ? "gadm_area" : "division_area")} SET bbox_xmin=1000, bbox_xmax=1001 WHERE id=$id OR $secondary";
                command.Parameters.AddWithValue("$id", source + "-city");
                command.Parameters.AddWithValue("$secondary", (source == "gadm") != preferGadm);
                command.ExecuteNonQuery();
            }
            var result = await CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                .ResolveAsync(territory.Latitude, territory.Longitude, DemandConfig(preferGadm));

            Assert.IsNotNull(result);
            Assert.AreEqual(preferGadm ? "GADM State" : "Overture State", result.GeoResult.State);
            Assert.IsNull(result.GeoResult.City);
            Assert.AreEqual(territory.DisplayName, result.GeoResult.Country);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDuringPrimaryLookup_DoesNotStartSecondary(bool preferGadm)
    {
        var root = CreateDemandFixture();
        try
        {
            var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
            using var cancellation = new CancellationTokenSource();
            var progress = new CancellingProgress(cancellation);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateResolver(root, CountryIdentityCatalog.Load(GetIdentityCatalogPath()))
                    .ResolveAsync(territory.Latitude, territory.Longitude, DemandConfig(preferGadm), progress, cancellation.Token));

            var primaryStage = preferGadm ? "Querying cached GADM" : "Querying cached Overture";
            var secondaryLabel = preferGadm ? "Overture administrative" : "GADM";
            Assert.IsTrue(progress.Messages.Any(message => message.StartsWith(primaryStage, StringComparison.Ordinal)));
            Assert.IsFalse(progress.Messages.Any(message => message.Contains(secondaryLabel, StringComparison.Ordinal)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IAdministrativeAreaResolutionProgress
    {
        public List<string> Messages { get; } = [];

        public void Report(string message)
        {
            Messages.Add(message);
            if (message.StartsWith("Querying cached", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        }

        public IDisposable BeginActivity(string activity) => new NoopDisposable();
    }

    private static ProcessingConfig DemandConfig(bool preferGadm) => new()
    {
        UseGadmAdministrativeAreas = true,
        PreferGadmAdministrativeAreas = preferGadm,
        UseGadmTerritoryFallbacks = false
    };

    private static string CreateDemandFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        File.Copy(GetBundledArtifactPath(), Path.Combine(root, "defaults", "overture-country-divisions.db"));
        var territory = CountryResolutionFixtureCatalog.MandatoryTerritories.First();
        CreateDistinctResultCaches(root, territory.Alpha3, territory.Latitude, territory.Longitude, territory.Alpha2);
        return root;
    }

    private static void CreateDistinctResultCaches(string root, string iso3, double lat, double lon, string alpha2)
    {
        var geometry = new WKBWriter().Write(new GeometryFactory().CreatePolygon(
        [
            new Coordinate(lon - 1, lat - 1),
            new Coordinate(lon + 1, lat - 1),
            new Coordinate(lon + 1, lat + 1),
            new Coordinate(lon - 1, lat + 1),
            new Coordinate(lon - 1, lat - 1)
        ]));
        var overtureDirectory = Path.Combine(root, "overture-divisions");
        Directory.CreateDirectory(overtureDirectory);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(overtureDirectory, iso3 + ".db")};Pooling=false"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, subtype TEXT NULL, class_name TEXT NULL, admin_level INTEGER NULL, country TEXT NULL, is_land INTEGER NOT NULL, is_territorial INTEGER NOT NULL, geom_wkb BLOB NOT NULL, bbox_xmin REAL NULL, bbox_ymin REAL NULL, bbox_xmax REAL NULL, bbox_ymax REAL NULL);
                CREATE TABLE IF NOT EXISTS division_name (id TEXT PRIMARY KEY, country TEXT, names_json TEXT); CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO _meta VALUES ('release', 'test');
                INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
                INSERT INTO division_area VALUES ('overture-state', 'Overture State', 'region', 'land', 1, $country, 1, 0, $geometry, $xmin, $ymin, $xmax, $ymax);
                INSERT INTO division_area VALUES ('overture-city', 'Overture City', 'locality', 'land', 2, $country, 1, 0, $geometry, $xmin, $ymin, $xmax, $ymax);
                """;
            command.Parameters.AddWithValue("$country", alpha2);
            command.Parameters.AddWithValue("$geometry", geometry);
            command.Parameters.AddWithValue("$xmin", lon - 1);
            command.Parameters.AddWithValue("$ymin", lat - 1);
            command.Parameters.AddWithValue("$xmax", lon + 1);
            command.Parameters.AddWithValue("$ymax", lat + 1);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO _meta VALUES ('cacheFormatVersion', $version)";
            command.Parameters.AddWithValue("$version", OvertureDivisionCacheService.CacheFormatVersion);
            command.ExecuteNonQuery();
        }

        var gadmDirectory = Path.Combine(root, "gadm-divisions");
        Directory.CreateDirectory(gadmDirectory);
        using var gadmConnection = new SqliteConnection($"Data Source={Path.Combine(gadmDirectory, iso3 + ".db")};Pooling=false");
        gadmConnection.Open();
        using var gadmCommand = gadmConnection.CreateCommand();
        gadmCommand.CommandText = """
            CREATE TABLE gadm_area (id TEXT PRIMARY KEY, name TEXT NOT NULL, english_type TEXT NULL, local_type TEXT NULL, admin_level INTEGER NOT NULL, geom_wkb BLOB NOT NULL, bbox_xmin REAL NOT NULL, bbox_ymin REAL NOT NULL, bbox_xmax REAL NOT NULL, bbox_ymax REAL NOT NULL);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('version', 'test');
            INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
            INSERT INTO gadm_area VALUES ('gadm-state', 'GADM State', 'State', 'State', 1, $geometry, $xmin, $ymin, $xmax, $ymax);
            INSERT INTO gadm_area VALUES ('gadm-city', 'GADM City', 'City', 'City', 2, $geometry, $xmin, $ymin, $xmax, $ymax);
            """;
        gadmCommand.Parameters.AddWithValue("$geometry", geometry);
        gadmCommand.Parameters.AddWithValue("$xmin", lon - 1);
        gadmCommand.Parameters.AddWithValue("$ymin", lat - 1);
        gadmCommand.Parameters.AddWithValue("$xmax", lon + 1);
        gadmCommand.Parameters.AddWithValue("$ymax", lat + 1);
        gadmCommand.ExecuteNonQuery();
    }
}

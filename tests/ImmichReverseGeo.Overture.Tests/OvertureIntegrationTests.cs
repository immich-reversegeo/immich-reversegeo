using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
[TestCategory("Integration")]
public class OvertureIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OverturePlaces_LiveLookup_ZurichAirportArea_ReturnsDiagnostics()
    {
        var service = new OverturePlacesService(
            NullLogger<OverturePlacesService>.Instance,
            Path.GetTempPath(),
            Path.GetTempPath());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await service.FindNearestPlaceWithDiagnosticsAsync(
            47.451320907898726,
            8.557397567083083,
            "CH",
            cts.Token);

        Assert.IsNotNull(result, "The live Overture lookup should return diagnostics.");
        Assert.IsNull(result.Error, $"Expected Overture lookup to succeed, but got: {result.Error}");
        Assert.IsNotNull(result.Release, "The live Overture lookup should resolve a release.");
        Assert.IsTrue(
            result.BestMatch is not null || result.Candidates.Count > 0,
            "Expected at least one live Overture candidate near the Zurich Airport-area coordinate.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OvertureInfrastructure_LiveLookup_ZurichAirportArea_ReturnsDiagnostics()
    {
        var service = new OverturePlacesService(
            NullLogger<OverturePlacesService>.Instance,
            Path.GetTempPath(),
            Path.GetTempPath());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await service.FindNearestInfrastructureWithDiagnosticsAsync(
            47.451320907898726,
            8.557397567083083,
            "CHE",
            cts.Token);

        Assert.IsNotNull(result, "The live Overture infrastructure lookup should return diagnostics.");
        Assert.IsNull(result.Error, $"Expected Overture infrastructure lookup to succeed, but got: {result.Error}");
        Assert.IsNotNull(result.Release, "The live Overture infrastructure lookup should resolve a release.");
        Assert.IsTrue(
            result.BestMatch is not null || result.Candidates.Count > 0,
            "Expected at least one live Overture infrastructure candidate near the Zurich Airport-area coordinate.");
    }

    [TestMethod]
    public async Task OvertureInfrastructure_BundledLookup_ZurichAirportGateArea_ReturnsZurichAirport()
    {
        var sourceDb = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-airports.db"));

        if (!File.Exists(sourceDb))
        {
            Assert.Inconclusive($"Bundled airports DB not found at {sourceDb}");
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "defaults"));
        File.Copy(sourceDb, Path.Combine(tempRoot, "defaults", "overture-airports.db"), overwrite: true);

        try
        {
            var service = new OverturePlacesService(
                NullLogger<OverturePlacesService>.Instance,
                tempRoot,
                tempRoot);

            var result = await service.FindNearestInfrastructureWithDiagnosticsAsync(
                47.460972,
                8.553525,
                "CHE");

            Assert.IsNotNull(result, "The bundled airport lookup should return diagnostics.");
            Assert.IsNull(result.Error, $"Expected bundled airport lookup to succeed, but got: {result.Error}");
            Assert.IsNotNull(result.BestMatch, "Expected a bundled airport best match for the Zurich coordinate.");
            Assert.AreEqual("Zürich Airport", result.BestMatch.Name);
            Assert.AreEqual("airport", result.BestMatch.SubType);
            Assert.AreEqual("international_airport", result.BestMatch.ClassName);
            Assert.IsTrue(result.BestMatch.BoundingBoxContainsPoint, "Expected the Zurich Airport bbox to contain the point.");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task OvertureDivisions_BundledCountryLookup_ZurichAirportGateArea_ReturnsSwitzerland()
    {
        var sourceDb = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db"));

        if (!File.Exists(sourceDb))
        {
            Assert.Inconclusive($"Bundled country divisions DB not found at {sourceDb}");
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "defaults"));
        File.Copy(sourceDb, Path.Combine(tempRoot, "defaults", "overture-country-divisions.db"), overwrite: true);

        try
        {
            var places = new OverturePlacesService(
                NullLogger<OverturePlacesService>.Instance,
                tempRoot,
                tempRoot);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                tempRoot,
                tempRoot,
                alpha2 => alpha2.ToUpperInvariant() switch
                {
                    "CH" => "CHE",
                    _ => null
                });

            var result = await service.FindBundledCountryAsync(
                47.460972,
                8.553525);

            Assert.AreEqual("CHE", result.Iso3);
            Assert.AreEqual("Switzerland", result.CountryName);
            Assert.AreEqual("CH", result.Alpha2);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task OvertureDivisions_BundledCountryLookup_VaticanCity_ReturnsVaticanCity()
    {
        var sourceDb = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db"));

        if (!File.Exists(sourceDb))
        {
            Assert.Inconclusive($"Bundled country divisions DB not found at {sourceDb}");
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "defaults"));
        File.Copy(sourceDb, Path.Combine(tempRoot, "defaults", "overture-country-divisions.db"), overwrite: true);

        try
        {
            var places = new OverturePlacesService(
                NullLogger<OverturePlacesService>.Instance,
                tempRoot,
                tempRoot);
            var service = new OvertureDivisionsService(
                NullLogger<OvertureDivisionsService>.Instance,
                places,
                tempRoot,
                tempRoot,
                alpha2 => alpha2.ToUpperInvariant() switch
                {
                    "VA" => "VAT",
                    _ => null
                });

            var result = await service.FindBundledCountryAsync(
                41.9060875,
                12.454566944444444);

            Assert.AreEqual("VAT", result.Iso3);
            Assert.AreEqual("Vatican City", result.CountryName);
            Assert.AreEqual("VA", result.Alpha2);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task OvertureInfrastructure_FullAirportExport_ReportsSize()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "overture-airports-full.db");

        try
        {
            var release = await Task.Run(GetLatestOvertureRelease);
            var releaseUrl = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                OverturePlacesLogic.InfrastructureReleaseUrlTemplate,
                release);

            long rowCount = 0;

            await Task.Run(() =>
            {
                using var duck = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:");
                duck.Open();

                using (var ext = duck.CreateCommand())
                {
                    ext.CommandText = """
                        INSTALL httpfs; LOAD httpfs;
                        INSTALL azure; LOAD azure;
                        INSTALL spatial; LOAD spatial;
                        """;
                    ext.ExecuteNonQuery();
                }

                using var query = duck.CreateCommand();
                query.CommandText = $"""
                    SELECT
                        id,
                        COALESCE(names.common['en'], names.primary, id) AS name,
                        type AS feature_type,
                        subtype,
                        class,
                        ST_Y(ST_CENTROID(geometry)) AS latitude,
                        ST_X(ST_CENTROID(geometry)) AS longitude,
                        ST_AsWKB(geometry) AS geom_wkb,
                        bbox.xmin AS bbox_xmin,
                        bbox.ymin AS bbox_ymin,
                        bbox.xmax AS bbox_xmax,
                        bbox.ymax AS bbox_ymax
                    FROM read_parquet('{releaseUrl}', filename = true, hive_partitioning = 1)
                    WHERE subtype = 'airport'
                      AND (
                            COALESCE(class, '') = 'airport'
                            OR COALESCE(class, '') LIKE '%_airport'
                          )
                      AND COALESCE(class, '') NOT IN ('airport_gate', 'taxiway', 'runway', 'apron')
                    """;
                using var reader = query.ExecuteReader();

                using var sqlite = new SqliteConnection($"Data Source={dbPath}");
                sqlite.Open();

                using (var ddl = sqlite.CreateCommand())
                {
                    ddl.CommandText = """
                        CREATE TABLE infrastructure (
                            id TEXT PRIMARY KEY,
                            name TEXT NOT NULL,
                            feature_type TEXT NULL,
                            subtype TEXT NULL,
                            class_name TEXT NULL,
                            latitude REAL NOT NULL,
                            longitude REAL NOT NULL,
                            geom_wkb BLOB NULL,
                            bbox_xmin REAL NULL,
                            bbox_ymin REAL NULL,
                            bbox_xmax REAL NULL,
                            bbox_ymax REAL NULL
                        );
                        CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                        INSERT INTO _meta VALUES ('release', $release);
                        """;
                    ddl.Parameters.AddWithValue("$release", release);
                    ddl.ExecuteNonQuery();
                }

                using var tx = sqlite.BeginTransaction();
                using var insert = sqlite.CreateCommand();
                insert.CommandText = """
                    INSERT INTO infrastructure (
                        id, name, feature_type, subtype, class_name, latitude, longitude,
                        geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
                    ) VALUES (
                        $id, $name, $featureType, $subtype, $className, $lat, $lon,
                        $geom, $xmin, $ymin, $xmax, $ymax
                    )
                    """;
                var pId = insert.Parameters.Add("$id", SqliteType.Text);
                var pName = insert.Parameters.Add("$name", SqliteType.Text);
                var pFeatureType = insert.Parameters.Add("$featureType", SqliteType.Text);
                var pSubtype = insert.Parameters.Add("$subtype", SqliteType.Text);
                var pClassName = insert.Parameters.Add("$className", SqliteType.Text);
                var pLat = insert.Parameters.Add("$lat", SqliteType.Real);
                var pLon = insert.Parameters.Add("$lon", SqliteType.Real);
                var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
                var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
                var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
                var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
                var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

                while (reader.Read())
                {
                    pId.Value = reader.GetString(0);
                    pName.Value = reader.GetString(1);
                    pFeatureType.Value = reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2);
                    pSubtype.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
                    pClassName.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetString(4);
                    pLat.Value = reader.GetDouble(5);
                    pLon.Value = reader.GetDouble(6);
                    pGeom.Value = reader.IsDBNull(7) ? DBNull.Value : ReadBlobValue(reader.GetValue(7));
                    pXMin.Value = reader.IsDBNull(8) ? DBNull.Value : reader.GetDouble(8);
                    pYMin.Value = reader.IsDBNull(9) ? DBNull.Value : reader.GetDouble(9);
                    pXMax.Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetDouble(10);
                    pYMax.Value = reader.IsDBNull(11) ? DBNull.Value : reader.GetDouble(11);
                    insert.ExecuteNonQuery();
                    rowCount++;
                }

                tx.Commit();
            });

            var fileInfo = new FileInfo(dbPath);
            TestContext.WriteLine($"Overture release: {release}");
            TestContext.WriteLine($"Rows exported: {rowCount:N0}");
            TestContext.WriteLine($"SQLite size bytes: {fileInfo.Length:N0}");
            TestContext.WriteLine($"SQLite size MB: {fileInfo.Length / 1024d / 1024d:0.00}");
            TestContext.WriteLine($"Output: {dbPath}");

            Assert.IsTrue(fileInfo.Exists, "Expected the full export SQLite file to be created.");
            Assert.IsTrue(rowCount > 0, "Expected at least one airport row in the full Overture export.");
        }
        finally
        {
            TestContext.AddResultFile(dbPath);
        }
    }

    private static string GetLatestOvertureRelease()
    {
        try
        {
            using var conn = new DuckDB.NET.Data.DuckDBConnection("Data Source=:memory:");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSTALL httpfs; LOAD httpfs;";
            cmd.ExecuteNonQuery();
            using var query = conn.CreateCommand();
            query.CommandText = $"SELECT latest FROM '{OverturePlacesLogic.LatestCatalogUrl}'";
            return query.ExecuteScalar()?.ToString() ?? OverturePlacesLogic.DocumentedFallbackRelease;
        }
        catch
        {
            return OverturePlacesLogic.DocumentedFallbackRelease;
        }
    }

    private static byte[] ReadBlobValue(object value)
    {
        if (value is byte[] bytes)
        {
            return bytes;
        }

        if (value is Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        throw new InvalidCastException($"Unsupported blob value type '{value.GetType().FullName}'.");
    }
}

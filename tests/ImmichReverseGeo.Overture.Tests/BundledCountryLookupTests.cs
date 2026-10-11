using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

/// <summary>
/// Regression tests for the bundled country lookup, built against a synthetic two-country
/// database so they run without the 155 MB LFS-backed bundled dataset.
///
/// The scenario reproduces the shape that caused repeated OOM kills in production: a country
/// whose bounding box spans most of the globe (antimeridian crossings and distant overseas
/// territories do this) is returned by the spatial index for practically every point on earth.
/// The lookup must reject it using the prepared-geometry index alone — never by measuring
/// distance to its full boundary, which copies every ring into a fresh Coordinate[].
/// </summary>
[TestClass]
public class BundledCountryLookupTests
{
    private const double PointLon = 10.0;
    private const double PointLat = 50.0;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PointInsideCompactCountry_IsNotConfusedBySprawlingNeighbour(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root, lowMemory);

            var result = await service.FindBundledCountryAsync(PointLat, PointLon);

            Assert.AreEqual("AAA", result.Iso3, "The point lies inside AA, not the globe-spanning SP.");
            Assert.AreEqual("Aaland", result.CountryName);
            Assert.AreEqual(BundledCountryLookupStatus.Matched, result.Status);
            Assert.AreEqual("aa", result.SourceId);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task PointInsideCompactCountry_DoesNotScanTheSprawlingCountrysBoundary()
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root);

            // Warm up: the first call parses the WKB and builds the prepared-geometry indexes.
            var warmup = await service.FindBundledCountryAsync(PointLat, PointLon);
            Assert.AreEqual("AAA", warmup.Iso3);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10; i++)
            {
                _ = await service.FindBundledCountryAsync(PointLat, PointLon);
            }

            var allocatedPerLookup = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;

            // SP's boundary carries ~50k vertices across two rings. Measuring distance to it
            // copies each ring into a new Coordinate[] — roughly 2.4 MB per lookup, much of it
            // straight onto the large object heap. Containment via the prepared index allocates
            // a few KB and never materialises a coordinate array.
            Assert.IsTrue(
                allocatedPerLookup < 512 * 1024,
                $"Expected a warm lookup to allocate well under 512 KB, but it allocated {allocatedPerLookup:N0} bytes. "
                + "This usually means the lookup fell back to a full-boundary distance scan.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PointJustOutsideBoundary_StillResolvesViaTolerance(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root, lowMemory);

            // Step ~5m beyond the middle of AA's south-west edge, along that edge's normal:
            // outside the boundary, inside the bounding box, within the ~16m tolerance.
            const double offset = 0.00005 / 1.4142135623730951;

            var result = await service.FindBundledCountryAsync(49.5 - offset, 9.5 - offset);

            Assert.AreEqual("AAA", result.Iso3, "Points within the boundary tolerance should still resolve.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(0.00005, "AAA", false)]
    [DataRow(0.00025, null, false)]
    [DataRow(0.00005, "AAA", true)]
    [DataRow(0.00025, null, true)]
    public async Task PointOutsideBoundingBox_ResolvesOnlyWithinTolerance(double offset, string? expectedIso3, bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var result = await CreateService(root, lowMemory).FindBundledCountryAsync(PointLat, 9.0 - offset);

            Assert.AreEqual(expectedIso3, result.Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExactCountryMatch_WinsOverSmallerToleranceOnlyNeighbour(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var nearby = factory.CreatePolygon([
                new Coordinate(8.99985, 49.99994),
                new Coordinate(8.99994, 49.99985),
                new Coordinate(9.00003, 49.99994),
                new Coordinate(8.99994, 50.00003),
                new Coordinate(8.99985, 49.99994)
            ]);
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")}"))
            {
                conn.Open();
                InsertCountry(conn, new WKBWriter(), "nearby", "Nearby", "SP", nearby);
            }

            // AA covers this boundary point exactly. The smaller neighbour's bounding box
            // also contains it, but its geometry only intersects the tolerance rectangle.
            var result = await CreateService(root, lowMemory).FindBundledCountryAsync(PointLat, 9.0);

            Assert.AreEqual("AAA", result.Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PointOutsideEveryBoundary_ReturnsNoMatch(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var service = CreateService(root, lowMemory);

            // Open water: inside SP's globe-spanning bounding box, inside no actual boundary.
            var result = await service.FindBundledCountryAsync(0.0, 0.0);

            Assert.IsNull(result.Iso3);
            Assert.IsNull(result.CountryName);
            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, result.Status);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("ZZ", false)]
    [DataRow(null, false)]
    [DataRow("ZZ", true)]
    [DataRow(null, true)]
    public async Task MatchedBoundaryWithUnresolvedIdentity_ReturnsMappingFailure(string? alpha2, bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE division_area SET country = $country WHERE id = 'aa'";
                cmd.Parameters.AddWithValue("$country", (object?)alpha2 ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            var result = await CreateService(root, lowMemory).FindBundledCountryAsync(PointLat, PointLon);

            Assert.AreEqual(BundledCountryLookupStatus.IdentityMappingFailure, result.Status);
            Assert.IsNull(result.Iso3);
            Assert.AreEqual("Aaland", result.CountryName);
            Assert.AreEqual(alpha2, result.Alpha2);
            Assert.AreEqual("aa", result.SourceId);
            Assert.IsNotNull(result.FailureReason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task MissingBundledDatabase_ReturnsSpatialNoMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = await CreateService(root).FindBundledCountryAsync(PointLat, PointLon);

        Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, result.Status);
        Assert.IsNull(result.SourceId);
        Assert.IsNotNull(result.FailureReason);
    }

    private static OvertureDivisionsService CreateService(string root, bool lowMemory = false)
    {
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
        return new OvertureDivisionsService(
            NullLogger<OvertureDivisionsService>.Instance,
            places,
            root,
            root,
            static alpha2 => alpha2.ToUpperInvariant() switch
            {
                "AA" => "AAA",
                "SP" => "SPP",
                _ => null
            }, new MemoryResourcePolicy { LowMemoryMode = lowMemory });
    }

    /// <summary>
    /// Builds a bundled database holding two countries: a compact square containing the test
    /// point, and a sprawling country made of two dense far-apart blobs whose combined envelope
    /// covers nearly the whole globe while its actual area is nowhere near the test point.
    /// </summary>
    private static string CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "immich-reversegeo-bundled", Guid.NewGuid().ToString("N"));
        var defaultsDir = Path.Combine(root, "defaults");
        Directory.CreateDirectory(defaultsDir);

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var writer = new WKBWriter();

        // A diamond provides points inside its bounding box but outside its boundary.
        var compact = factory.CreatePolygon([
            new Coordinate(9.0, 50.0),
            new Coordinate(10.0, 49.0),
            new Coordinate(11.0, 50.0),
            new Coordinate(10.0, 51.0),
            new Coordinate(9.0, 50.0)
        ]);

        var sprawling = factory.CreateMultiPolygon([
            CreateDenseBlob(factory, -179.4, -84.4, 0.5, 25_000),
            CreateDenseBlob(factory, 179.4, 84.4, 0.5, 25_000)
        ]);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(defaultsDir, "overture-country-divisions.db")}");
        conn.Open();

        using (var schema = conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE division_area (
                    id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    subtype TEXT,
                    class_name TEXT,
                    admin_level INTEGER,
                    country TEXT,
                    is_land INTEGER,
                    is_territorial INTEGER,
                    geom_wkb BLOB,
                    bbox_xmin REAL,
                    bbox_ymin REAL,
                    bbox_xmax REAL,
                    bbox_ymax REAL
                );
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO _meta (key, value) VALUES ('release', 'test');
                """;
            schema.ExecuteNonQuery();
        }

        InsertCountry(conn, writer, "aa", "Aaland", "AA", compact);
        InsertCountry(conn, writer, "sp", "Sprawlia", "SP", sprawling);

        return root;
    }

    private static Polygon CreateDenseBlob(GeometryFactory factory, double centreX, double centreY, double radius, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            ring[i] = new Coordinate(
                centreX + (radius * Math.Cos(angle)),
                centreY + (radius * Math.Sin(angle)));
        }

        ring[vertices] = ring[0].Copy();
        return factory.CreatePolygon(ring);
    }

    private static void InsertCountry(
        SqliteConnection conn,
        WKBWriter writer,
        string id,
        string name,
        string alpha2,
        Geometry geometry)
    {
        var envelope = geometry.EnvelopeInternal;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES
                ($id, $name, 'country', NULL, 2, $country, 1, 0,
                 $wkb, $xmin, $ymin, $xmax, $ymax)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$country", alpha2);
        cmd.Parameters.AddWithValue("$wkb", writer.Write(geometry));
        cmd.Parameters.AddWithValue("$xmin", envelope.MinX);
        cmd.Parameters.AddWithValue("$ymin", envelope.MinY);
        cmd.Parameters.AddWithValue("$xmax", envelope.MaxX);
        cmd.Parameters.AddWithValue("$ymax", envelope.MaxY);
        cmd.ExecuteNonQuery();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}

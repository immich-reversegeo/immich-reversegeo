using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
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
                InsertCountry(conn, new WKBWriter(), "nearby", "Nearby", "SP", "SPP", nearby);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerySmallCountry_PreservesDoublePrecision(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            // These distinct vertices collapse to the same values in single precision.
            var tiny = factory.CreatePolygon([
                new Coordinate(10.0000001, 50.0000001),
                new Coordinate(10.0000002, 50.0000001),
                new Coordinate(10.0000002, 50.0000002),
                new Coordinate(10.0000001, 50.0000002),
                new Coordinate(10.0000001, 50.0000001)
            ]);
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false"))
            {
                conn.Open();
                InsertCountry(conn, new WKBWriter(), "tiny", "Tiny country", "SP", "SPP", tiny);
            }

            var service = CreateService(root, lowMemory);
            var inside = await service.FindBundledCountryAsync(50.00000015, 10.00000015);
            Assert.AreEqual("SPP", inside.Iso3);
            Assert.AreEqual("tiny", inside.SourceId);
            // An exact AA match must still outrank the tiny country's tolerance-only match.
            var outside = await service.FindBundledCountryAsync(50.0000003, 10.0000003);
            Assert.AreEqual("AAA", outside.Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
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

    [TestMethod]
    public async Task IdleCountryIndex_UsesSlidingTimeoutAndRebuildsAfterRelease()
    {
        var root = CreateFixture();
        try
        {
            var clock = new ManualClock();
            var policy = new MemoryResourcePolicy { CountryIndexIdleMinutes = 1 };
            using var service = CreateTimedService(root, policy, clock);
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            RenameFixture(root);
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            clock.Advance(TimeSpan.FromSeconds(61));
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            service.Dispose();
            Assert.IsTrue(clock.Timer!.IsDisposed);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task IdleTimeoutZero_KeepsIndexUntilSettingIsEnabled()
    {
        var root = CreateFixture();
        try
        {
            var clock = new ManualClock();
            var policy = new MemoryResourcePolicy { CountryIndexIdleMinutes = 0 };
            using var service = CreateTimedService(root, policy, clock);
            await service.FindBundledCountryAsync(PointLat, PointLon);
            RenameFixture(root);
            clock.Advance(TimeSpan.FromDays(1));
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            policy.CountryIndexIdleMinutes = 1;
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task IdleTimer_DoesNotEvictDuringAnActiveLookup()
    {
        var root = CreateFixture();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        try
        {
            var pause = 0;
            var clock = new ManualClock();
            var policy = new MemoryResourcePolicy { CountryIndexIdleMinutes = 1 };
            using var service = CreateTimedService(root, policy, clock, code =>
            {
                if (Interlocked.Exchange(ref pause, 0) == 1)
                {
                    entered.Set();
                    Assert.IsTrue(resume.Wait(TimeSpan.FromSeconds(5)));
                }
                return code == "AA" ? "AAA" : "SPP";
            });
            await service.FindBundledCountryAsync(PointLat, PointLon);
            RenameFixture(root);
            pause = 1;
            var pending = Task.Run(() => service.FindBundledCountryAsync(PointLat, PointLon));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            clock.Advance(TimeSpan.FromMinutes(2));
            resume.Set();
            await pending;
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
        }
        finally
        {
            resume.Set();
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task WarmDiskLookup_DoesNotReadDistantPolygonBytes()
    {
        var root = CreateFixture();
        try
        {
            var logger = new RecordingLogger();
            using var service = new OvertureDivisionsService(logger,
                new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), root, root,
                code => code == "AA" ? "AAA" : "SPP", new MemoryResourcePolicy { LowMemoryMode = true });
            await service.FindBundledCountryAsync(PointLat, PointLon);
            Assert.IsNull(logger.Exception, logger.Exception?.ToString());
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10; index++)
            {
                Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(PointLat, PointLon)).Iso3);
            }
            var perLookup = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 10;
            Assert.IsTrue(perLookup < 512 * 1024, $"Warm disk lookup allocated {perLookup:N0} bytes, suggesting it decoded distant polygons.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DiskIndex_RebuildsWhenBundledDataChangesAndWhenDeleted()
    {
        var root = CreateFixture();
        try
        {
            using var service = CreateService(root, lowMemory: true);
            Assert.AreEqual("Aaland", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            RenameFixture(root);
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
            File.Delete(Path.Combine(root, "country-polygon-parts.db"));
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static OvertureDivisionsService CreateTimedService(string root, MemoryResourcePolicy policy, TimeProvider clock, Func<string, string?>? map = null) => new(
        NullLogger<OvertureDivisionsService>.Instance,
        new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), root, root,
        map ?? (code => code == "AA" ? "AAA" : "SPP"), policy, clock);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolygonParts_PreserveOriginalEnvelopeRanking(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var geometry = factory.CreateMultiPolygon([
                CreateDenseBlob(factory, PointLon, PointLat, 0.1, 50),
                CreateDenseBlob(factory, 179, 84, 0.1, 50)
            ]);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false"))
            {
                connection.Open();
                InsertCountry(connection, new WKBWriter(), "overlap", "Overlap", "SP", "SPP", geometry);
            }
            using var service = CreateService(root, lowMemory);
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(PointLat, PointLon)).Iso3,
                "The smaller part must not change the parent country's original spatial ranking.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PolygonParts_PreserveHolesAndBoundaryTolerance(bool lowMemory)
    {
        var root = CreateFixture();
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var shell = factory.CreateLinearRing([new(9,49), new(11,49), new(11,51), new(9,51), new(9,49)]);
            var hole = factory.CreateLinearRing([new(9.9,49.9), new(10.1,49.9), new(10.1,50.1), new(9.9,50.1), new(9.9,49.9)]);
            var geometry = factory.CreateMultiPolygon([factory.CreatePolygon(shell, [hole])]);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE division_area SET geom_wkb=$wkb, bbox_ymin=49, bbox_ymax=51 WHERE id='aa'";
                command.Parameters.AddWithValue("$wkb", new WKBWriter().Write(geometry));
                command.ExecuteNonQuery();
            }
            using var service = CreateService(root, lowMemory);
            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, (await service.FindBundledCountryAsync(50,10)).Status);
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50,10.09995)).Iso3);
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50,10.2)).Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DiskIndex_CancellationReleasesPreparationAndConcurrentLookupsShareIt()
    {
        var root = CreateFixture();
        try
        {
            var policy = new MemoryResourcePolicy { LowMemoryMode = true };
            using var service = CreateTimedService(root, policy, new ManualClock());
            using (var blockedWork = policy.Enter(CancellationToken.None))
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = service.FindBundledCountryAsync(PointLat, PointLon, cancellation.Token);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.FindBundledCountryAsync(PointLat, PointLon)));
            Assert.IsTrue(results.All(result => result.Iso3 == "AAA"));
            Assert.AreEqual(0, Directory.GetFiles(root, "country-polygon-parts.*.tmp").Length);
            using var index = new SqliteConnection($"Data Source={Path.Combine(root, "country-polygon-parts.db")};Pooling=false");
            index.Open();
            using var count = index.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM part";
            Assert.AreEqual(3L, count.ExecuteScalar());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task UnwritableIndexDirectory_FallsBackWithoutChangingCountryResult()
    {
        var root = CreateFixture();
        try
        {
            var unusableDirectory = Path.Combine(root, "not-a-directory");
            File.WriteAllText(unusableDirectory, "fixture");
            using var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance,
                new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), unusableDirectory, root,
                code => code == "AA" ? "AAA" : "SPP", new MemoryResourcePolicy { LowMemoryMode = true });
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(PointLat, PointLon)).Iso3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DiskIndex_IgnoresRowsWithMissingBoundsLikeTheOriginalQuery()
    {
        var root = CreateFixture();
        try
        {
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE division_area SET bbox_xmin=NULL WHERE id='sp'";
                command.ExecuteNonQuery();
            }
            var logger = new RecordingLogger();
            using var service = new OvertureDivisionsService(logger,
                new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), root, root,
                code => code == "AA" ? "AAA" : "SPP", new MemoryResourcePolicy { LowMemoryMode = true });
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(PointLat, PointLon)).Iso3);
            Assert.IsNull(logger.Exception, logger.Exception?.ToString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task PolygonParts_ReadBothByteOrdersAndAdditionalOrdinates(bool bigEndian, bool emitZ, bool emitM)
    {
        var root = CreateFixture();
        try
        {
            var factory = new GeometryFactory(new PrecisionModel(), 4326);
            var shell = factory.CreateLinearRing(new Coordinate[]
            {
                new CoordinateZM(9,49,7,8), new CoordinateZM(11,49,7,8),
                new CoordinateZM(11,51,7,8), new CoordinateZM(9,51,7,8), new CoordinateZM(9,49,7,8)
            });
            var hole = factory.CreateLinearRing(new Coordinate[]
            {
                new CoordinateZM(9.9,49.9,7,8), new CoordinateZM(10.1,49.9,7,8),
                new CoordinateZM(10.1,50.1,7,8), new CoordinateZM(9.9,50.1,7,8), new CoordinateZM(9.9,49.9,7,8)
            });
            var geometry = factory.CreateMultiPolygon([factory.CreatePolygon(shell, [hole])]);
            // With SRID, the writer emits EWKB flags; without it, ISO dimensional type codes.
            var writer = new WKBWriter(bigEndian ? ByteOrder.BigEndian : ByteOrder.LittleEndian,
                handleSRID: bigEndian, emitZ, emitM);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE division_area SET geom_wkb=$wkb, bbox_ymin=49, bbox_ymax=51 WHERE id='aa'";
                command.Parameters.AddWithValue("$wkb", writer.Write(geometry));
                command.ExecuteNonQuery();
            }
            var logger = new RecordingLogger();
            using var service = new OvertureDivisionsService(logger,
                new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), root, root,
                code => code == "AA" ? "AAA" : "SPP", new MemoryResourcePolicy { LowMemoryMode = true });
            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, (await service.FindBundledCountryAsync(50,10)).Status);
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50,10.1)).Iso3, "An exact hole boundary is covered.");
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50,10.09995)).Iso3, "Tolerance includes the nearby hole boundary.");
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50,10.2)).Iso3);
            Assert.IsNull(logger.Exception, logger.Exception?.ToString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DiskIndex_RecoversCorruptCacheAndCoordinatesSeparateServiceInstances()
    {
        var root = CreateFixture();
        try
        {
            File.WriteAllText(Path.Combine(root, "country-polygon-parts.db"), "interrupted or damaged local cache");
            using var first = CreateService(root, lowMemory: true);
            using var second = CreateService(root, lowMemory: true);
            var results = await Task.WhenAll(
                first.FindBundledCountryAsync(PointLat, PointLon),
                second.FindBundledCountryAsync(PointLat, PointLon));
            Assert.IsTrue(results.All(result => result.Iso3 == "AAA"));
            using var index = new SqliteConnection($"Data Source={Path.Combine(root, "country-polygon-parts.db")};Mode=ReadOnly;Pooling=false");
            index.Open();
            using var check = index.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM part";
            Assert.AreEqual(3L, check.ExecuteScalar(), "Both instances must share one complete rebuilt index.");
            Assert.AreEqual(0, Directory.GetFiles(root, "country-polygon-parts.*.tmp").Length);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task NormalIndexLoading_DoesNotAllocateAnEntireWkbCopy()
    {
        var root = CreateFixture();
        try
        {
            long wkbBytes;
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Mode=ReadOnly;Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT SUM(length(geom_wkb)) FROM division_area";
                wkbBytes = (long)command.ExecuteScalar()!;
            }
            using var service = CreateService(root);
            var before = GC.GetAllocatedBytesForCurrentThread();
            // Outside every envelope: load the geometries without lazily building point indexes.
            var result = await service.FindBundledCountryAsync(89, 0);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, result.Status);
            Assert.IsTrue(allocated < wkbBytes + 512 * 1024,
                $"Loading {wkbBytes:N0} WKB bytes allocated {allocated:N0} bytes; a full input copy appears to be retained alongside decoded coordinates.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow(4096L, true)]
    [DataRow(8L * 1024 * 1024, true)]
    [DataRow(4096L, false)]
    [DataRow(8L * 1024 * 1024, false)]
    public async Task ExperimentalCache_PreservesResultsAcrossConcurrencyAndSourceReplacement(long budget, bool prepare)
    {
        var root = CreateFixture();
        try
        {
            using var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance,
                new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root), root, root,
                code => code == "AA" ? "AAA" : "SPP", experimentalCountryCacheBytes: budget, experimentalPrepareCountryParts: prepare);
            var matches = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => service.FindBundledCountryAsync(PointLat, PointLon))));
            Assert.IsTrue(matches.All(result => result.Iso3 == "AAA"));
            Assert.AreEqual(BundledCountryLookupStatus.SpatialNoMatch, (await service.FindBundledCountryAsync(89, 0)).Status);
            Assert.AreEqual("AAA", (await service.FindBundledCountryAsync(50, 11.00005)).Iso3);
            RenameFixture(root);
            Assert.AreEqual("Reloaded", (await service.FindBundledCountryAsync(PointLat, PointLon)).CountryName);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static void RenameFixture(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-country-divisions.db")};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE division_area SET name='Reloaded' WHERE id='aa'";
        command.ExecuteNonQuery();
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(callback, state);
            return Timer;
        }
        public void Advance(TimeSpan time)
        {
            Interlocked.Add(ref _ticks, time.Ticks);
            Timer?.Fire();
        }
    }

    private sealed class RecordingLogger : ILogger<OvertureDivisionsService>
    {
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Exception = exception;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool IsDisposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) => !IsDisposed;
        public void Fire()
        {
            if (!IsDisposed)
            {
                callback(state);
            }
        }
        public void Dispose() => IsDisposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
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
                    alpha3 TEXT,
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

        InsertCountry(conn, writer, "aa", "Aaland", "AA", "AAA", compact);
        InsertCountry(conn, writer, "sp", "Sprawlia", "SP", "SPP", sprawling);

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
        string alpha3,
        Geometry geometry)
    {
        var envelope = geometry.EnvelopeInternal;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO division_area
                (id, name, subtype, class_name, admin_level, country, alpha3, is_land, is_territorial,
                 geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax)
            VALUES
                ($id, $name, 'country', NULL, 2, $country, $alpha3, 1, 0,
                 $wkb, $xmin, $ymin, $xmax, $ymax)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$country", alpha2);
        cmd.Parameters.AddWithValue("$alpha3", alpha3);
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

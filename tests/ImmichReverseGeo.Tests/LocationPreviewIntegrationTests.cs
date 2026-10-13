using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Integration")]
public class LocationPreviewIntegrationTests
{
    [TestMethod]
    public async Task PreviewQueries_PageFilterAndPreserveDatabaseValues()
    {
        var connectionString = Environment.GetEnvironmentVariable("PREVIEW_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set PREVIEW_TEST_DB to an isolated PostgreSQL test database.");
        }

        var schema = $"preview_test_{Guid.NewGuid():N}";
        await using var setup = new NpgsqlConnection(connectionString);
        await setup.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup);
        await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            await using var seed = source.CreateCommand("""
                CREATE TABLE asset (id uuid PRIMARY KEY, "deletedAt" timestamptz);
                CREATE TABLE asset_exif ("assetId" uuid PRIMARY KEY, latitude double precision,
                    longitude double precision, country text, state text, city text);
                INSERT INTO asset SELECT ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid, NULL
                    FROM generate_series(1, 207) AS n;
                UPDATE asset SET "deletedAt" = now() WHERE id = '00000000-0000-0000-0000-000000000206';
                INSERT INTO asset_exif
                    SELECT id, 48 - (row_number() OVER (ORDER BY id) % 7) * 0.1,
                        2 + (row_number() OVER (ORDER BY id) % 11) * 0.1, 'France', 'Region', 'Old city'
                    FROM asset WHERE id <> '00000000-0000-0000-0000-000000000207';
                UPDATE asset_exif SET latitude = NULL, state = NULL, city = ''' OR TRUE --'
                    WHERE "assetId" = '00000000-0000-0000-0000-000000000001';
                UPDATE asset_exif SET longitude = NULL
                    WHERE "assetId" = '00000000-0000-0000-0000-000000000205';
                UPDATE asset_exif SET state = NULL, city = ''' OR TRUE --'
                    WHERE "assetId" = '00000000-0000-0000-0000-000000000002';
                UPDATE asset_exif SET latitude = -0.01, longitude = -0.26
                    WHERE "assetId" = '00000000-0000-0000-0000-000000000200';
                """);
            await seed.ExecuteNonQueryAsync();

            var repository = new ImmichDbRepository(source, NullLogger<ImmichDbRepository>.Instance);
            var all = LocationPreviewSelection.Create(LocationPreviewScope.All, null);
            Assert.AreEqual(203L, await repository.GetLocationPreviewCountAsync(all));
            Assert.AreEqual(203L, await repository.GetLocationPreviewCountAsync(all with { Order = LocationPreviewOrder.NearbyCoordinates }));
            Assert.AreEqual(1L, await repository.GetLocationPreviewCountAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.City, "' OR TRUE --")));
            Assert.AreEqual(202L, await repository.GetLocationPreviewCountAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.State, "Region")));
            Assert.AreEqual(203L, await repository.GetLocationPreviewCountAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.Country, "France")));
            Assert.AreEqual(0L, await repository.GetLocationPreviewCountAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.City, "Missing city")));
            Assert.AreEqual(1L, await repository.GetLocationPreviewCountAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.AssetIds,
                    "00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000205,00000000-0000-0000-0000-000000000206,00000000-0000-0000-0000-000000000207")));
            var first = await repository.GetLocationPreviewBatchAsync(all, null);
            Assert.HasCount(101, first);
            Assert.IsNotNull(first[0].Latitude);
            Assert.IsNull(first[0].Existing.State);
            var second = await repository.GetLocationPreviewBatchAsync(all, LocationPreviewCursor.FromAsset(first[99]));
            Assert.HasCount(101, second);
            Assert.AreEqual(103L, await repository.GetLocationPreviewCountAsync(all, after: LocationPreviewCursor.FromAsset(first[99])));
            var third = await repository.GetLocationPreviewBatchAsync(all, LocationPreviewCursor.FromAsset(second[99]));
            Assert.HasCount(3, third);
            Assert.AreEqual(3L, await repository.GetLocationPreviewCountAsync(all, after: LocationPreviewCursor.FromAsset(second[99])));
            var gpsAssets = first.Take(100).Concat(second.Take(100)).Concat(third).ToArray();
            Assert.HasCount(203, gpsAssets);
            Assert.HasCount(203, gpsAssets.Select(a => a.Id).Distinct().ToArray());
            Assert.IsTrue(gpsAssets.All(asset => asset.Latitude is not null && asset.Longitude is not null));

            var nearby = all with { Order = LocationPreviewOrder.NearbyCoordinates };
            var nearbyFirst = await repository.GetLocationPreviewBatchAsync(nearby, null);
            var nearbySecond = await repository.GetLocationPreviewBatchAsync(nearby, LocationPreviewCursor.FromAsset(nearbyFirst[99]));
            var nearbyThird = await repository.GetLocationPreviewBatchAsync(nearby, LocationPreviewCursor.FromAsset(nearbySecond[99]));
            var nearbyAssets = nearbyFirst.Take(100).Concat(nearbySecond.Take(100)).Concat(nearbyThird).ToArray();
            var expectedOrder = first.Take(100).Concat(second.Take(100)).Concat(third)
                .OrderBy(asset => LocationPreviewCursor.FromAsset(asset).LatitudeTile)
                .ThenBy(asset => LocationPreviewCursor.FromAsset(asset).LongitudeTile)
                .ThenBy(asset => asset.Id).Select(asset => asset.Id).ToArray();
            CollectionAssert.AreEqual(expectedOrder, nearbyAssets.Select(asset => asset.Id).ToArray());
            Assert.HasCount(203, nearbyAssets.Select(asset => asset.Id).Distinct().ToArray());
            Assert.IsTrue(nearbyAssets.All(asset => asset.Latitude is not null && asset.Longitude is not null));

            // Resuming after cancellation partway through a tile must retain every unchecked asset.
            var resumed = await repository.GetLocationPreviewBatchAsync(nearby, LocationPreviewCursor.FromAsset(nearbyFirst[36]));
            CollectionAssert.AreEqual(expectedOrder.Skip(37).Take(101).ToArray(), resumed.Select(asset => asset.Id).ToArray());
            Assert.AreEqual(166L, await repository.GetLocationPreviewCountAsync(nearby, after: LocationPreviewCursor.FromAsset(nearbyFirst[36])));
            Assert.AreEqual(0L, await repository.GetLocationPreviewCountAsync(nearby, after: LocationPreviewCursor.FromAsset(nearbyAssets[^1])));
            Assert.HasCount(0, await repository.GetLocationPreviewBatchAsync(nearby, LocationPreviewCursor.FromAsset(nearbyAssets[^1])));

            var exactCity = await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.City, "' OR TRUE --"), null);
            Assert.HasCount(1, exactCity);
            var byIds = await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.AssetIds,
                    "00000000-0000-0000-0000-000000000001,00000000-0000-0000-0000-000000000002,00000000-0000-0000-0000-000000000205,00000000-0000-0000-0000-000000000206,00000000-0000-0000-0000-000000000207"), null);
            Assert.HasCount(1, byIds);
            Assert.AreEqual(Guid.Parse("00000000-0000-0000-0000-000000000002"), byIds[0].Id);
            Assert.HasCount(101, await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.Country, "France"), null));
            Assert.HasCount(101, await repository.GetLocationPreviewBatchAsync(
                LocationPreviewSelection.Create(LocationPreviewScope.State, "Region"), null));

            // Re-read after all preview scopes: neither values nor row counts were changed.
            await using var check = source.CreateCommand("SELECT count(*) FROM asset_exif WHERE country = 'France' AND (city = 'Old city' OR city = ''' OR TRUE --')");
            Assert.AreEqual(206L, await check.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

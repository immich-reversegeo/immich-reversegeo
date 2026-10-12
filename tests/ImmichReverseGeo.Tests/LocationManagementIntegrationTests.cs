using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Integration")]
public class LocationManagementIntegrationTests
{
    [TestMethod]
    public async Task GuardedWrites_ApplyExactNamesPreserveOtherFieldsAndProtectUndo()
    {
        var connectionString = Environment.GetEnvironmentVariable("PREVIEW_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("Set PREVIEW_TEST_DB to an isolated test database.");
        }
        var schema = $"location_management_{Guid.NewGuid():N}";
        await using var setup = new NpgsqlConnection(connectionString);
        await setup.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", setup);
        await create.ExecuteNonQueryAsync();
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
            var id = Guid.NewGuid();
            await using var seed = source.CreateCommand("""
                CREATE TABLE asset(id uuid PRIMARY KEY,"deletedAt" timestamptz);
                CREATE TABLE asset_exif("assetId" uuid PRIMARY KEY,latitude double precision,longitude double precision,
                    country text,state text,city text,description text);
                INSERT INTO asset VALUES (@id,NULL);
                INSERT INTO asset_exif VALUES (@id,48.1,2.1,NULL,NULL,'Old','untouched');
                """);
            seed.Parameters.AddWithValue("id", id);
            await seed.ExecuteNonQueryAsync();
            var repository = new ImmichDbRepository(source, NullLogger<ImmichDbRepository>.Instance);
            var expected = new LocationPreviewAsset(id, 48.1, 2.1, new(null, null, "Old"));
            var proposed = new GeoResult("France", "Zürich", "O'Malley <test>");
            var concurrent = await Task.WhenAll(repository.TryReplaceLocationAsync(expected, proposed), repository.TryReplaceLocationAsync(expected, proposed));
            Assert.AreEqual(1, concurrent.Count(result => result.Applied));
            var result = concurrent.Single(result => result.Applied);
            Assert.IsTrue(result.Applied);
            Assert.IsFalse(await repository.WriteUnprocessedLocationAsync(
                new(id, 48.1, 2.1, DateTime.UtcNow), new("Overwrite", null, "Must not replace reviewed names")));
            Assert.IsNotNull(result.Revision);
            Assert.IsFalse((await repository.TryReplaceLocationAsync(expected, proposed)).Applied);
            await using var read = source.CreateCommand("SELECT country,state,city,description,latitude,longitude FROM asset_exif");
            await using (var reader = await read.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync());
                Assert.AreEqual("France", reader.GetString(0));
                Assert.AreEqual("Zürich", reader.GetString(1));
                Assert.AreEqual("O'Malley <test>", reader.GetString(2));
                Assert.AreEqual("untouched", reader.GetString(3));
                Assert.AreEqual(48.1, reader.GetDouble(4));
                Assert.AreEqual(2.1, reader.GetDouble(5));
            }
            var applied = expected with
            {
                Existing = proposed
            };
            Assert.IsFalse((await repository.TryReplaceLocationAsync(applied with
            {
                Latitude = 49
            }, expected.Existing, result.Revision)).Applied);
            var undone = await repository.TryReplaceLocationAsync(applied, expected.Existing, result.Revision);
            Assert.IsTrue(undone.Applied);
            await using var makeUnprocessed = source.CreateCommand("UPDATE asset_exif SET city=NULL,country=NULL,state=NULL");
            await makeUnprocessed.ExecuteNonQueryAsync();
            Assert.IsFalse(await repository.WriteUnprocessedLocationAsync(new(id, 49, 2.1, DateTime.UtcNow), proposed));
            Assert.IsTrue(await repository.WriteUnprocessedLocationAsync(new(id, 48.1, 2.1, DateTime.UtcNow), proposed));
            // Reset to the reviewed original for the remaining revision-guard checks.
            await using var restoreOriginal = source.CreateCommand("UPDATE asset_exif SET city='Old',country=NULL,state=NULL");
            await restoreOriginal.ExecuteNonQueryAsync();
            var reapplied = await repository.TryReplaceLocationAsync(expected, proposed);
            await using var laterEdit = source.CreateCommand("UPDATE asset_exif SET description='later edit'");
            await laterEdit.ExecuteNonQueryAsync();
            Assert.IsFalse((await repository.TryReplaceLocationAsync(applied, expected.Existing, reapplied.Revision)).Applied);
            await using var delete = source.CreateCommand("UPDATE asset SET \"deletedAt\"=now()");
            await delete.ExecuteNonQueryAsync();
            Assert.IsFalse((await repository.TryReplaceLocationAsync(applied, expected.Existing)).Applied);
            Assert.IsFalse((await repository.TryReplaceLocationAsync(expected with
            {
                Id = Guid.NewGuid()
            }, proposed)).Applied);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", setup);
            await drop.ExecuteNonQueryAsync();
        }
    }
}

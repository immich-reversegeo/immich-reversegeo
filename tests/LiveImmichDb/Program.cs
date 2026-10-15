using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ImmichReverseGeo.LiveImmichDb;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2)
            {
                throw new ArgumentException("Usage: LiveImmichDb <v31|v32|v331> <fixture-state-directory>");
            }
            var target = args[0] switch
            {
                "v31" => new Target("v31", "3.1.0", 15283, 15431),
                "v32" => new Target("v32", "3.2.0", 16283, 16432),
                "v331" => new Target("v331", "3.3.1", 17283, 17433),
                _ => throw new ArgumentException("Only the dedicated pinned local fixture targets are accepted.")
            };
            await RunAsync(target, Path.GetFullPath(args[1]));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fixture check failed: {ex.Message}");
            return 1;
        }
    }

    internal static async Task RunAsync(Target target, string directory, string? databasePassword = null)
    {
        using var http = new HttpClient
        {
            BaseAddress = new UriBuilder("http", target.Host, target.ApiPort).Uri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var version = await http.GetFromJsonAsync<JsonElement>("/api/server/version");
        var actual = $"{version.GetProperty("major")}.{version.GetProperty("minor")}.{version.GetProperty("patch")}";
        Require(actual == target.Version, "Fixture server version does not match the pinned target.");
        var password = databasePassword ?? File.ReadAllLines(Path.Combine(directory, $"{target.Name}.env"))
            .Single(line => line.StartsWith("DB_PASSWORD=", StringComparison.Ordinal))["DB_PASSWORD=".Length..];
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = target.Host, Port = target.DbPort, Database = "immich", Username = "postgres",
            Password = password, Timeout = 10, Pooling = false
        }.ConnectionString;
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        var marker = await ScalarAsync<string>(connection, "SELECT name FROM immich_reversegeo_fixture.guard");
        Require(marker == (target.GuardName ?? $"immich-reversegeo-db-{target.Name}"), "Database is not the dedicated fixture database; refusing all writes.");

        var statePath = Path.Combine(directory, $"{target.Name}-account.json");
        var state = File.Exists(statePath)
            ? JsonSerializer.Deserialize<FixtureState>(await File.ReadAllTextAsync(statePath), JsonOptions)!
            : new FixtureState();
        await SaveStateAsync();
        using var signup = await http.PostAsJsonAsync("/api/auth/admin-sign-up", new
        {
            email = "fixtures@immich-reversegeo.test", password = state.Password, name = "ReverseGeo database fixtures"
        });
        Require(signup.IsSuccessStatusCode || signup.StatusCode == HttpStatusCode.BadRequest, "Fixture account creation failed.");
        using var login = await http.PostAsJsonAsync("/api/auth/login", new
        {
            email = "fixtures@immich-reversegeo.test", password = state.Password
        });
        Require(login.IsSuccessStatusCode, "Fixture account login failed; preserve and inspect its local state.");
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

        for (var index = state.AssetIds.Count; index < 8; index++)
        {
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(CreatePng(index));
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "assetData", $"reversegeo-fixture-{index}.png");
            form.Add(new StringContent("2020-01-01T00:00:00.000Z"), "fileCreatedAt");
            form.Add(new StringContent("2020-01-01T00:00:00.000Z"), "fileModifiedAt");
            using var uploaded = await http.PostAsync("/api/assets", form);
            Require(uploaded.IsSuccessStatusCode, $"Fixture upload failed: HTTP {(int)uploaded.StatusCode}.");
            var result = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
            state.AssetIds.Add(result.GetProperty("id").GetGuid());
            await SaveStateAsync();
        }
        Require(state.AssetIds.Count == 8 && state.AssetIds.Distinct().Count() == 8, "Fixture IDs must be eight distinct assets.");
        Require(await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM asset") == 8,
            "Unexpected assets in this database; refusing global clear tests.");
        foreach (var id in state.AssetIds)
        {
            Require(await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM asset WHERE id=@id", ("id", id)) == 1,
                "A saved fixture asset is missing; inspect the dedicated stack.");
        }
        if (!state.MetadataReady)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            foreach (var id in state.AssetIds)
            {
                while (await ScalarAsync<long>(connection,
                    "SELECT COUNT(*) FROM asset_exif WHERE \"assetId\"=@id AND \"exifImageWidth\" IS NOT NULL", ("id", id)) == 0)
                {
                    Require(DateTime.UtcNow < deadline, "Timed out waiting for real fixture metadata extraction.");
                    await Task.Delay(500);
                }
            }
            state.MetadataReady = true;
            await SaveStateAsync();
        }

        var ids = state.AssetIds;
        for (var index = 0; index < ids.Count; index++)
        {
            await ExecuteAsync(connection, "INSERT INTO asset_exif (\"assetId\") VALUES (@id) ON CONFLICT DO NOTHING", ("id", ids[index]));
            var createdAt = new DateTime(2020 + index / 2, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await ExecuteAsync(connection, "UPDATE asset SET \"createdAt\"=@created, \"deletedAt\"=NULL WHERE id=@id", ("id", ids[index]), ("created", createdAt));
            await ExecuteAsync(connection, "UPDATE asset_exif SET latitude=47.3769, longitude=8.5417, city=NULL, state=NULL, country=NULL WHERE \"assetId\"=@id", ("id", ids[index]));
        }
        await ExecuteAsync(connection, "UPDATE asset_exif SET city='Existing', state='Zurich', country='Switzerland' WHERE \"assetId\"=@id", ("id", ids[4]));
        await ExecuteAsync(connection, "UPDATE asset_exif SET latitude=NULL, longitude=NULL WHERE \"assetId\"=@id", ("id", ids[5]));
        await ExecuteAsync(connection, "UPDATE asset SET \"deletedAt\"=@deleted WHERE id=@id", ("id", ids[6]), ("deleted", DateTime.UtcNow));
        await ExecuteAsync(connection, "UPDATE asset_exif SET city='Ghost', state='Ghost', country='Ghost' WHERE \"assetId\"=@id", ("id", ids[6]));
        await ExecuteAsync(connection, "DELETE FROM asset_exif WHERE \"assetId\"=@id", ("id", ids[7]));

        var repository = new ImmichDbRepository(dataSource, NullLogger<ImmichDbRepository>.Instance);
        var checks = new List<string>();
        Check(await repository.TestConnectionAsync(), "database connection");
        Check(await repository.GetUnprocessedCountAsync() == 4, "eligibility excludes located, no-GPS, deleted and missing-EXIF assets");
        var first = await repository.GetUnprocessedBatchAsync(AssetCursor.Initial, 2);
        Check(first.Count == 2 && first[0].CreatedAt == first[1].CreatedAt, "first page includes tied timestamps");
        var last = first[^1];
        var second = await repository.GetUnprocessedBatchAsync(new AssetCursor(last.CreatedAt, last.Id), 2);
        var all = first.Concat(second).ToList();
        Check(all.Count == 4 && all.Select(asset => asset.Id).Distinct().Count() == 4
            && all.All(asset => ids.Take(4).Contains(asset.Id)), "keyset pagination has no gaps or duplicates");
        last = second[^1];
        Check((await repository.GetUnprocessedBatchAsync(new AssetCursor(last.CreatedAt, last.Id), 2)).Count == 0, "pagination terminates");

        var untouched = await ExifSnapshotAsync(ids[0]);
        await repository.WriteLocationAsync(ids[0], new GeoResult("Switzerland", "Zürich", "O'Malley"));
        Check(await LocationAsync(ids[0]) == "O'Malley|Zürich|Switzerland", "write preserves Unicode and parameterized apostrophes");
        var afterWrite = await ExifSnapshotAsync(ids[0]);
        if (afterWrite != untouched)
        {
            using var beforeDocument = JsonDocument.Parse(untouched);
            using var afterDocument = JsonDocument.Parse(afterWrite);
            var changedFields = beforeDocument.RootElement.EnumerateObject()
                .Where(property => property.Value.GetRawText() != afterDocument.RootElement.GetProperty(property.Name).GetRawText())
                .Select(property => property.Name);
            Console.WriteLine($"Changed EXIF fields: {string.Join(", ", changedFields)}");
        }
        Check(afterWrite == untouched, "write preserves GPS and unrelated EXIF fields");
        Check(await repository.GetUnprocessedCountAsync() == 3, "written assets leave the processing queue");
        await repository.WriteLocationAsync(ids[0], new GeoResult("Switzerland", null, null));
        Check(await LocationAsync(ids[0]) == "||Switzerland", "nullable state and city round-trip");
        await repository.WriteLocationAsync(ids[0], new GeoResult("Switzerland", "Zürich", "O'Malley"));
        // Location Management writes only reviewed names and keeps an independent undo journal.
        var managementDir = Path.Combine(directory, target.Name + "-location-management");
        var scans = new SavedLocationPreviewRepository(managementDir);
        var journal = new LocationChangeRepository(managementDir);
        var management = new LocationManagementService(scans, journal, repository, NullLogger<LocationManagementService>.Instance);
        var expectedLocation = new GeoResult("Switzerland", "Zürich", "O'Malley");
        var exactProposal = new GeoResult("France", null, "Reviewed city");
        var scanId = await CreateManagementScanAsync();
        var operation = await management.CreateApplyAsync(scanId, [ids[0]]);
        await management.RunAsync(operation, false);
        Check(await LocationAsync(ids[0]) == "Reviewed city||France", "selected apply uses exact reviewed values");
        Check(!await repository.WriteUnprocessedLocationAsync(new(ids[0], 47.3769, 8.5417, DateTime.UtcNow),
            new("Overwrite", null, "Must not overwrite reviewed values")), "processing does not overwrite a selected apply from an earlier batch");
        Check(await ExifSnapshotAsync(ids[0]) == untouched, "selected apply preserves GPS and unrelated metadata");
        var apiAsset = await http.GetFromJsonAsync<JsonElement>($"/api/assets/{ids[0]}");
        Check(apiAsset.GetProperty("exifInfo").GetProperty("city").GetString() == "Reviewed city"
            && apiAsset.GetProperty("exifInfo").GetProperty("country").GetString() == "France", "Immich asset API sees direct applied names");
        using (var search = await http.PostAsJsonAsync("/api/search/metadata", new { city = "Reviewed city", country = "France", size = 10 }))
        {
            Require(search.IsSuccessStatusCode, "Fixture location search failed.");
            var results = await search.Content.ReadFromJsonAsync<JsonElement>();
            Check(results.GetProperty("assets").GetProperty("items").EnumerateArray().Any(asset => asset.GetProperty("id").GetGuid() == ids[0]),
                "Immich metadata search sees directly applied names");
        }
        await journal.QueueUndoAsync(operation, [ids[0]]);
        await management.RunAsync(operation, true);
        Check(await LocationAsync(ids[0]) == "O'Malley|Zürich|Switzerland", "guarded undo restores exact previous names");
        var reapplied = await management.CreateApplyAsync(await CreateManagementScanAsync(), [ids[0]]);
        await management.RunAsync(reapplied, false);
        await repository.WriteLocationAsync(ids[0], exactProposal); // Same text, newer metadata revision.
        await journal.QueueUndoAsync(reapplied, [ids[0]]);
        await management.RunAsync(reapplied, true);
        Check((await journal.GetItemsAsync(reapplied)).Single().UndoStatus == LocationUndoStatus.Skipped,
            "undo does not overwrite a later metadata revision with identical location names");
        var stale = await management.CreateApplyAsync(await CreateManagementScanAsync(), [ids[0]]);
        await management.RunAsync(stale, false);
        Check((await journal.GetItemsAsync(stale)).Single().ApplyStatus == LocationChangeStatus.Skipped, "stale saved scan skips newer names");
        await repository.WriteLocationAsync(ids[0], expectedLocation);
        Check(await ExifSnapshotAsync(ids[0]) == untouched, "apply and undo preserve unrelated metadata throughout");


        var cleared = await repository.ClearLocationDataForAssetsAsync([ids[0], ids[0], Guid.NewGuid()]);
        Check(cleared.Count == 1 && cleared[0] == ids[0] && await LocationAsync(ids[4]) == "Existing|Zurich|Switzerland", "ID reset deduplicates IDs and preserves other assets");
        Check(await ExifSnapshotAsync(ids[0]) == untouched, "ID reset preserves unrelated metadata");
        Check((await repository.ClearLocationDataForAssetsAsync([])).Count == 0, "empty ID reset is harmless");
        foreach (var scope in Enum.GetValues<LocationResetScope>())
        {
            await repository.WriteLocationAsync(ids[0], new GeoResult("Scope country", "Scope state", "Scope city"));
            await repository.WriteLocationAsync(ids[6], new GeoResult("Scope country", "Scope state", "Scope city"));
            var value = scope switch
            {
                LocationResetScope.City => "Scope city",
                LocationResetScope.State => "Scope state",
                _ => "Scope country"
            };
            var options = await repository.GetLocationValueOptionsAsync(scope);
            Check(options.Any(option => option.Value == value && option.AssetCount == 1)
                && options.All(option => option.Value != "Ghost"), $"{scope} options exclude deleted assets");
            Check((await repository.ClearLocationDataByValueAsync(scope, "' OR 1=1 --")).Count == 0, $"{scope} reset parameterizes user input");
            var matches = await repository.ClearLocationDataByValueAsync(scope, value);
            Check(matches.Count == 1 && matches[0] == ids[0] && await LocationAsync(ids[0]) == "||", $"{scope} reset clears all three location fields");
            Check(await LocationAsync(ids[6]) == "Scope city|Scope state|Scope country", $"{scope} reset leaves deleted assets unchanged");
        }
        var beforeClear = await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM asset_exif WHERE city IS NOT NULL OR state IS NOT NULL OR country IS NOT NULL");
        Check(await repository.ClearAllLocationDataAsync() == beforeClear, "global reset row count matches the isolated library");
        Check(await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM asset_exif WHERE city IS NOT NULL OR state IS NOT NULL OR country IS NOT NULL") == 0, "global reset clears active and deleted location data");
        Check(await ExifSnapshotAsync(ids[0]) == untouched, "global reset preserves unrelated metadata");
        Check(await repository.GetUnprocessedCountAsync() == 5, "reset makes previously located GPS assets eligible");

        var report = new { version = target.Version, passed = checks.Count, checks, testedAtUtc = DateTime.UtcNow };
        await File.WriteAllTextAsync(Path.Combine(directory, $"{target.Name}-results.json"), JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"Immich {target.Version}: PASS - {checks.Count} live database checks.");

        Task SaveStateAsync() => File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, JsonOptions));
        async Task<Guid> CreateManagementScanAsync()
        {
            // Each scenario needs an unattempted scan, including the intentionally stale snapshot.
            var id = await scans.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.AssetIds, ids[0].ToString()), new());
            await scans.AppendAsync(id, new(new(ids[0], 47.3769, 8.5417, expectedLocation), exactProposal));
            await scans.SetStatusAsync(id, "Complete");
            return id;
        }
        // Immich triggers maintain revision IDs and timestamps after any EXIF update.
        Task<string> ExifSnapshotAsync(Guid id) => ScalarAsync<string>(connection,
            "SELECT (to_jsonb(e) - ARRAY['city','state','country','updatedAt','updateId'])::text FROM asset_exif e WHERE \"assetId\"=@id", ("id", id));
        Task<string> LocationAsync(Guid id) => ScalarAsync<string>(connection,
            "SELECT concat_ws('|', coalesce(city,''), coalesce(state,''), coalesce(country,'')) FROM asset_exif WHERE \"assetId\"=@id", ("id", id));
        void Check(bool condition, string name)
        {
            Require(condition, name);
            checks.Add(name);
            Console.WriteLine($"PASS {target.Version}: {name}");
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    // Synthetic PNGs exercise real upload/metadata extraction without personal photos.
    private static byte[] CreatePng(int variant)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 24);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 24);
        header[8] = 8;
        header[9] = 2;
        WriteChunk("IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < 24; y++)
            {
                zlib.WriteByte(0);
                for (var x = 0; x < 24; x++)
                {
                    zlib.WriteByte((byte)(x + variant * 16));
                    zlib.WriteByte((byte)(y + variant * 8));
                    zlib.WriteByte((byte)(x + y));
                }
            }
        }
        WriteChunk("IDAT", compressed.ToArray());
        WriteChunk("IEND", []);
        return output.ToArray();

        void WriteChunk(string type, byte[] bytes)
        {
            var buffer = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(buffer, bytes.Length);
            output.Write(buffer);
            var data = Encoding.ASCII.GetBytes(type).Concat(bytes).ToArray();
            output.Write(data);
            uint crc = 0xffffffff;
            foreach (var item in data)
            {
                crc ^= item;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
                }
            }
            BinaryPrimitives.WriteUInt32BigEndian(buffer, crc ^ 0xffffffff);
            output.Write(buffer);
        }
    }

    internal sealed record Target(string Name, string Version, int ApiPort, int DbPort,
        string Host = "127.0.0.1", string? GuardName = null);
    private sealed class FixtureState
    {
        public string Password { get; set; } = Guid.NewGuid().ToString("N") + "Aa1!";
        public List<Guid> AssetIds { get; set; } = [];
        public bool MetadataReady { get; set; }
    }
}

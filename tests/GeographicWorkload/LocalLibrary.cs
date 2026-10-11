using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ImmichReverseGeo.GeographicWorkload;

internal static partial class Program
{
    private static async Task<NpgsqlConnection> OpenLocalAsync(string state)
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(state, "bulk.env"));
        var password = lines.Single(line => line.StartsWith("DB_PASSWORD=", StringComparison.Ordinal))["DB_PASSWORD=".Length..];
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = DbPort, Database = "immich", Username = "postgres", Password = password, Pooling = false
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        try
        {
            using var cmd = new NpgsqlCommand("SELECT name FROM immich_reversegeo_fixture.guard", connection);
            Require((string?)await cmd.ExecuteScalarAsync() == Marker, "Not the dedicated bulk-test database; refusing all writes.");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task SeedAsync(string state)
    {
        var manifestPath = Path.Combine(state, "coordinates.json");
        var manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        var coordinates = JsonSerializer.Deserialize<List<CoordinateCase>>(manifestBytes, Json)!;
        Require(coordinates.Count is >= 1 and <= 20000 && coordinates.All(item => ValidCoordinate(item.Latitude, item.Longitude)), "Invalid or oversized workload manifest.");
        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes));
        await using var connection = await OpenLocalAsync(state);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}"), Timeout = TimeSpan.FromSeconds(60) };
        var version = await http.GetFromJsonAsync<JsonElement>("/api/server/version");
        Require(version.GetProperty("major").GetInt32() == 3 && version.GetProperty("minor").GetInt32() == 3
            && version.GetProperty("patch").GetInt32() == 1, "Bulk-test server is not pinned Immich 3.3.1.");
        var accountPath = Path.Combine(state, "account.json");
        var account = File.Exists(accountPath)
            ? JsonSerializer.Deserialize<Account>(await File.ReadAllTextAsync(accountPath), Json)!
            : new Account { DatasetHash = digest };
        Require(account.DatasetHash == digest, "A seeded dataset is immutable; use a new isolated stack/state for another dataset.");
        await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(account, Json));
        const string email = "geographic-workload@immich-reversegeo.test";
        using var signup = await http.PostAsJsonAsync("/api/auth/admin-sign-up", new { email, password = account.Password, name = "ReverseGeo geographic workload" });
        Require(signup.IsSuccessStatusCode || signup.StatusCode == HttpStatusCode.BadRequest, "Cannot create local fixture account.");
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password = account.Password });
        Require(login.IsSuccessStatusCode, "Cannot log into the local fixture account. Preserve its state and volumes together.");
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        account.UserId = session.GetProperty("userId").GetGuid();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(account, Json));
        var uploadedPath = Path.Combine(state, "uploaded.jsonl");
        var uploaded = new ConcurrentDictionary<int, Guid>();
        if (File.Exists(uploadedPath))
        {
            foreach (var line in await File.ReadAllLinesAsync(uploadedPath))
            {
                var saved = JsonSerializer.Deserialize<UploadedAsset>(line, Json)!;
                Require(saved.Index >= 0 && saved.Index < coordinates.Count && uploaded.TryAdd(saved.Index, saved.AssetId), "Invalid upload checkpoint.");
            }
        }
        using var checkpointLock = new SemaphoreSlim(1, 1);
        Console.WriteLine($"Uploading synthetic assets: {uploaded.Count}/{coordinates.Count} already checkpointed.");
        await Parallel.ForEachAsync(Enumerable.Range(0, coordinates.Count), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (index, ct) =>
        {
            if (uploaded.ContainsKey(index))
            {
                return;
            }
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(CreatePng($"{digest}:{index}"));
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            // Keep generated multipart filenames ASCII; Unicode place labels remain
            // in the manifest and later resolved metadata, not MIME header encodings.
            form.Add(image, "assetData", $"rg-{index:D5}-{coordinates[index].Category}.png");
            var date = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index).ToString("O");
            form.Add(new StringContent(date), "fileCreatedAt");
            form.Add(new StringContent(date), "fileModifiedAt");
            using var result = await http.PostAsync("/api/assets", form, ct);
            Require(result.IsSuccessStatusCode, $"Local upload {index} failed with HTTP {(int)result.StatusCode}; rerun seed to resume.");
            var response = await result.Content.ReadFromJsonAsync<JsonElement>(ct);
            var id = response.GetProperty("id").GetGuid();
            await checkpointLock.WaitAsync(ct);
            try
            {
                // Append one small durable mapping per upload instead of rewriting thousands of IDs.
                await File.AppendAllTextAsync(uploadedPath, JsonSerializer.Serialize(new UploadedAsset(index, id)) + Environment.NewLine, ct);
                uploaded[index] = id;
                if (uploaded.Count % 250 == 0)
                {
                    Console.WriteLine($"Uploaded {uploaded.Count}/{coordinates.Count} synthetic assets.");
                }
            }
            finally
            {
                checkpointLock.Release();
            }
        });
        var ids = uploaded.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        Require(ids.Length == coordinates.Count && ids.Distinct().Count() == ids.Length, "Uploads did not produce distinct fixture assets.");
        using (var ownership = new NpgsqlCommand("SELECT COUNT(*) FROM asset WHERE id = ANY(@ids) AND \"ownerId\"=@owner", connection))
        {
            ownership.Parameters.AddWithValue("ids", ids);
            ownership.Parameters.AddWithValue("owner", account.UserId);
            Require(Convert.ToInt64(await ownership.ExecuteScalarAsync()) == ids.Length, "A saved asset is missing or belongs to another owner; refusing coordinate writes.");
        }
        // Metadata extraction must finish before injecting GPS; otherwise Immich could overwrite it.
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (true)
        {
            using var count = new NpgsqlCommand("SELECT COUNT(*) FROM asset_exif WHERE \"assetId\" = ANY(@ids) AND \"exifImageWidth\" IS NOT NULL", connection);
            count.Parameters.AddWithValue("ids", ids);
            var ready = Convert.ToInt64(await count.ExecuteScalarAsync());
            if (ready == ids.Length)
            {
                break;
            }
            Require(DateTime.UtcNow < deadline, "Metadata extraction is still pending; rerun seed to resume without reuploading.");
            Console.WriteLine($"Metadata ready: {ready}/{ids.Length}.");
            await Task.Delay(TimeSpan.FromSeconds(10));
        }
        await using var tx = await connection.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("CREATE TEMP TABLE seed_coordinates (id uuid PRIMARY KEY, lat double precision, lon double precision, created timestamptz) ON COMMIT DROP", connection, tx))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using (var copy = await connection.BeginBinaryImportAsync("COPY seed_coordinates (id,lat,lon,created) FROM STDIN (FORMAT BINARY)"))
        {
            for (var index = 0; index < coordinates.Count; index++)
            {
                await copy.StartRowAsync();
                await copy.WriteAsync(uploaded[index], NpgsqlDbType.Uuid);
                await copy.WriteAsync(coordinates[index].Latitude, NpgsqlDbType.Double);
                await copy.WriteAsync(coordinates[index].Longitude, NpgsqlDbType.Double);
                await copy.WriteAsync(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index / 10), NpgsqlDbType.TimestampTz);
            }
            await copy.CompleteAsync();
        }
        await using (var apply = new NpgsqlCommand("""
            UPDATE asset_exif e SET latitude=s.lat, longitude=s.lon, city=NULL, state=NULL, country=NULL
            FROM seed_coordinates s WHERE e."assetId"=s.id;
            UPDATE asset a SET "createdAt"=s.created FROM seed_coordinates s WHERE a.id=s.id;
            """, connection, tx))
        {
            Require(await apply.ExecuteNonQueryAsync() == ids.Length * 2, "Not all fixture coordinate rows were updated.");
        }
        await tx.CommitAsync();
        if (account.AlbumId is null)
        {
            using var album = await http.PostAsJsonAsync("/api/albums", new { albumName = "ReverseGeo worldwide coordinate workload", description = "Synthetic images; GPS from public fixtures and a private coordinate-only sample." });
            Require(album.IsSuccessStatusCode, "Could not create fixture album.");
            account.AlbumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(account, Json));
        }
        foreach (var chunk in ids.Chunk(500))
        {
            using var added = await http.PutAsJsonAsync($"/api/albums/{account.AlbumId}/assets", new { ids = chunk });
            Require(added.IsSuccessStatusCode, "Could not add fixture assets to the album.");
        }
        Console.WriteLine($"Seeded {ids.Length} synthetic GPS assets. Immich UI: http://127.0.0.1:{ApiPort}");
        await ReportAsync(state);
    }

    private static async Task ResetAsync(string state)
    {
        await using var connection = await OpenLocalAsync(state);
        var ids = await ReadIdsAsync(state);
        using var reset = new NpgsqlCommand("UPDATE asset_exif SET city=NULL,state=NULL,country=NULL WHERE \"assetId\"=ANY(@ids)", connection);
        reset.Parameters.AddWithValue("ids", ids);
        Console.WriteLine($"Cleared location names on {await reset.ExecuteNonQueryAsync()} owned fixture IDs. GPS is preserved.");
    }

    private static async Task<Guid[]> ReadIdsAsync(string state) => (await File.ReadAllLinesAsync(Path.Combine(state, "uploaded.jsonl")))
        .Select(line => JsonSerializer.Deserialize<UploadedAsset>(line, Json)!.AssetId).Distinct().ToArray();

    private static async Task ReportAsync(string state)
    {
        await using var connection = await OpenLocalAsync(state);
        var ids = await ReadIdsAsync(state);
        using var cmd = new NpgsqlCommand("""
            SELECT COUNT(*), COUNT(*) FILTER(WHERE e.latitude IS NOT NULL AND e.longitude IS NOT NULL),
                   COUNT(*) FILTER(WHERE e.city IS NULL AND e.country IS NULL),
                   COUNT(*) FILTER(WHERE e.country IS NOT NULL), COUNT(DISTINCT e.country)
            FROM asset a JOIN asset_exif e ON e."assetId"=a.id WHERE a.id=ANY(@ids) AND a."deletedAt" IS NULL
            """, connection);
        cmd.Parameters.AddWithValue("ids", ids);
        using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        var report = new { observedAtUtc = DateTime.UtcNow, assets = reader.GetInt64(0), gps = reader.GetInt64(1), pending = reader.GetInt64(2), located = reader.GetInt64(3), resolvedCountries = reader.GetInt64(4) };
        await File.WriteAllTextAsync(Path.Combine(state, "library-status.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine($"Local library: {report.assets} assets, {report.gps} GPS, {report.pending} pending, {report.located} located.");
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}"), Timeout = TimeSpan.FromSeconds(30) };
        var account = JsonSerializer.Deserialize<Account>(await File.ReadAllTextAsync(Path.Combine(state, "account.json")), Json)!;
        using var login = await http.PostAsJsonAsync("/api/auth/login", new { email = "geographic-workload@immich-reversegeo.test", password = account.Password });
        Require(login.IsSuccessStatusCode, "Local map validation login failed.");
        var session = await login.Content.ReadFromJsonAsync<JsonElement>();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        var markers = await http.GetFromJsonAsync<JsonElement>("/api/map/markers");
        Require(markers.ValueKind == JsonValueKind.Array && markers.GetArrayLength() == report.gps, "Immich map marker count does not match the seeded GPS rows.");
        Console.WriteLine($"Immich map API: {markers.GetArrayLength()} GPS markers visible.");
    }

    private static byte[] CreatePng(string identity)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), 24);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), 24);
        header[8] = 8;
        header[9] = 2;
        WriteChunk("IHDR", header);
        WriteChunk("tEXt", Encoding.UTF8.GetBytes("Fixture\0" + identity));
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            for (var y = 0; y < 24; y++)
            {
                zlib.WriteByte(0);
                for (var x = 0; x < 24; x++)
                {
                    zlib.WriteByte((byte)(x * 8));
                    zlib.WriteByte((byte)(y * 8));
                    zlib.WriteByte((byte)((x + y) * 4));
                }
            }
        }
        WriteChunk("IDAT", compressed.ToArray());
        WriteChunk("IEND", []);
        return output.ToArray();

        void WriteChunk(string type, byte[] bytes)
        {
            Span<byte> buffer = stackalloc byte[4];
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

    private sealed record UploadedAsset(int Index, Guid AssetId);
    private sealed class Account
    {
        public string Password { get; set; } = Guid.NewGuid().ToString("N") + "Aa1!";
        public string DatasetHash { get; set; } = "";
        public Guid UserId { get; set; }
        public Guid? AlbumId { get; set; }
    }
}

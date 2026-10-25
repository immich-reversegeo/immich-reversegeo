using System.Diagnostics;
using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 0 || (args[0] != "compatibility" && args.Length < 3) || (args[0] == "build" && args.Length != 4))
{
    throw new ArgumentException("snapshot <input-directory> FRA|USA | build <input-directory> FRA|USA <new-output-directory> | verify <baseline.db> <candidate.db> | compatibility");
}
const string release = "2026-09-23.1";
static string Literal(string path) => "'" + Path.GetFullPath(path).Replace('\\', '/').Replace("'", "''") + "'";
static DuckDBConnection Open()
{
    var connection = new DuckDBConnection("Data Source=:memory:");
    connection.Open();
    OvertureDataAccess.LoadAzureAndSpatial(connection);
    using var command = connection.CreateCommand();
    command.CommandText = "SET memory_limit='1GiB'; SET threads=4; SET preserve_insertion_order=false;";
    command.ExecuteNonQuery();
    return connection;
}
if (args[0] == "snapshot")
{
    var country = args[2];
    var alpha2 = country switch { "FRA" => "FR", "USA" => "US", _ => throw new ArgumentException(country) };
    var directory = Path.Combine(Path.GetFullPath(args[1]), country);
    using var connection = Open();
    using var command = connection.CreateCommand();
    foreach (var type in new[] { "division", "division_area" })
    {
        var path = Path.Combine(directory, "type=" + type, "rows.parquet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            throw new IOException("Snapshot already exists: " + path);
        }
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate, release)
            .Replace("type=division_area/", "type=" + type + "/", StringComparison.Ordinal);
        var columns = type == "division" ? "id, country, names, hierarchies"
            : "id, country, names, subtype, class, admin_level, is_land, is_territorial, geometry, bbox, division_id";
        command.CommandText = $"COPY (SELECT {columns} FROM read_parquet('{url}', hive_partitioning=1) WHERE {OvertureCountrySourceCodes.BuildCountryFilter(alpha2)}) TO {Literal(path)} (FORMAT PARQUET, ROW_GROUP_SIZE 2048)";
        Console.WriteLine("Downloading " + country + " " + type);
        command.ExecuteNonQuery();
        using var input = File.OpenRead(path);
        Console.WriteLine(JsonSerializer.Serialize(new { country, type, bytes = input.Length, sha256 = Convert.ToHexString(SHA256.HashData(input)) }));
    }
}
else if (args[0] == "build")
{
    var root = Path.GetFullPath(args[3]);
    if (Directory.Exists(root))
    {
        throw new IOException("Use a fresh output directory: " + root);
    }
    Directory.CreateDirectory(root);
    var service = new BenchmarkCache(Path.GetFullPath(args[1]), root);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(15));
    var allocated = GC.GetTotalAllocatedBytes(true);
    var cpu = Process.GetCurrentProcess().TotalProcessorTime;
    var timer = Stopwatch.StartNew();
    double? importStart = null;
    double? importEnd = null;
    var task = service.EnsureDataAsync(args[2], cancellation.Token);
    while (!task.IsCompleted)
    {
        foreach (var progress in service.GetActiveDownloads())
        {
            if (progress.Phase == 3)
            {
                importStart ??= timer.Elapsed.TotalMilliseconds;
            }
            if (progress.Phase == 5)
            {
                importEnd ??= timer.Elapsed.TotalMilliseconds;
            }
        }
        await Task.WhenAny(task, Task.Delay(5, cancellation.Token));
    }
    await task;
    timer.Stop();
    if (!service.HasData(args[2]))
    {
        throw new InvalidOperationException("Production cache validation failed.");
    }
    var process = Process.GetCurrentProcess();
    var result = new
    {
        country = args[2], managedVersion = typeof(DuckDBConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        nativeVersion = service.NativeVersion, memoryLimit = "1GiB", threads = 4,
        elapsedMs = timer.Elapsed.TotalMilliseconds,
        importMs = importStart is null ? (double?)null : (importEnd ?? timer.Elapsed.TotalMilliseconds) - importStart,
        cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds,
        allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
        peakWorkingSetBytes = process.PeakWorkingSet64,
        cacheBytes = new FileInfo(Path.Combine(root, "overture-divisions", args[2] + ".db")).Length
    };
    var json = JsonSerializer.Serialize(result);
    File.WriteAllText(Path.Combine(root, "result.json"), json);
    Console.WriteLine(json);
}
else if (args[0] == "verify")
{
    using var connection = new SqliteConnection($"Data Source={Path.GetFullPath(args[1])};Mode=ReadOnly;Pooling=false");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "ATTACH DATABASE $path AS candidate";
    command.Parameters.AddWithValue("$path", Path.GetFullPath(args[2]));
    command.ExecuteNonQuery();
    command.Parameters.Clear();
    foreach (var table in new[] { "division_area", "division_name" })
    {
        foreach (var (left, right) in new[] { ("main", "candidate"), ("candidate", "main") })
        {
            command.CommandText = $"SELECT count(*) FROM (SELECT * FROM {left}.{table} EXCEPT SELECT * FROM {right}.{table})";
            if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            {
                throw new InvalidOperationException("Cache contents differ: " + table);
            }
        }
        command.CommandText = $"SELECT count(*) FROM {table}";
        Console.WriteLine($"IDENTICAL {table}: {command.ExecuteScalar()} rows");
    }
}
else if (args[0] == "compatibility")
{
    using var connection = Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT version(), ST_AsText(ST_GeomFromText('POINT (2 48)')), current_setting('azure_transport_option_type')";
    using (var reader = command.ExecuteReader())
    {
        reader.Read();
        Console.WriteLine($"Native={reader.GetString(0)} spatial={reader.GetString(1)} azureTransport={reader.GetString(2)}");
        if (OperatingSystem.IsLinux() && reader.GetString(2) != "curl")
        {
            throw new InvalidOperationException("Linux curl transport missing.");
        }
    }
    // Exercise a real Azure request through the same bootstrap, not just LOAD azure.
    var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate, release);
    command.CommandText = $"SELECT id FROM read_parquet('{url}', hive_partitioning=1) LIMIT 1";
    Console.WriteLine("Azure parquet read: " + command.ExecuteScalar());
}
else
{
    throw new ArgumentException("Unknown operation: " + args[0]);
}

sealed class BenchmarkCache(string inputs, string output)
    : OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, output,
        code => code switch { "FRA" => "FR", "USA" => "US", _ => throw new ArgumentException(code) },
        new MemoryResourcePolicy { DuckDbThreads = 4 })
{
    public string? NativeVersion { get; private set; }
    protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2026-09-23.1";
    protected override string GetDivisionAreaReleaseUrl(string release) =>
        Path.Combine(inputs, _country!, "type=division_area", "*.parquet").Replace('\\', '/');
    private string? _country;
    protected override long ExportOvertureDivisions(string path, string alpha2, string iso3, string release, CancellationToken ct)
    {
        _country = iso3;
        return base.ExportOvertureDivisions(path, alpha2, iso3, release, ct);
    }
    protected override void PrepareExportConnection(DuckDBConnection connection)
    {
        base.PrepareExportConnection(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SET memory_limit='1GiB'; SET threads=4;";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT version()";
        NativeVersion = (string)command.ExecuteScalar()!;
    }
}

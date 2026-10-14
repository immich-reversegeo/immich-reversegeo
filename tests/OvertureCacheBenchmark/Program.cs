using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Contains("--install-extensions"))
{
    using var install = new DuckDBConnection("Data Source=:memory:");
    install.Open();
    using var setting = install.CreateCommand();
    setting.CommandText = "SET extension_directory='/extensions'";
    setting.ExecuteNonQuery();
    OvertureDataAccess.LoadAzureAndSpatial(install);
    return 0;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
    {
        throw new ArgumentException("Arguments must be --name value pairs.");
    }
    options.Add(args[index], args[index + 1]);
}
string Value(string name, string fallback) => options.GetValueOrDefault("--" + name, fallback);
var output = Path.GetFullPath(Value("output", "_out/duckdb-benchmark"));
var release = Value("release", "2026-09-23.1");
var country = Value("country", "ES");
var variant = Value("variant", "phased");
var fixture = bool.Parse(Value("fixture", "false"));
var lowMemory = bool.Parse(Value("low-memory", "false"));
var threads = int.Parse(Value("threads", "0"), CultureInfo.InvariantCulture);
var bufferMiB = int.Parse(Value("buffer-mib", "0"), CultureInfo.InvariantCulture);
var timeoutSeconds = int.Parse(Value("timeout-seconds", "1200"), CultureInfo.InvariantCulture);
if (!Regex.IsMatch(release, @"^\d{4}-\d{2}-\d{2}\.\d+$") || !Regex.IsMatch(country, "^[A-Z]{2}$")
    || variant is not ("original" or "phased") || threads < 0 || threads > 64
    || bufferMiB < 0 || bufferMiB > 64 || timeoutSeconds < 1 || (lowMemory && (threads > 1 || bufferMiB > 0)))
{
    throw new ArgumentException("Invalid benchmark options. Low-memory runs retain their one-thread/default-buffer limits.");
}
Directory.CreateDirectory(output);
var summaryPath = Path.Combine(output, "summary.json");
if (File.Exists(summaryPath))
{
    throw new IOException("Use a fresh output directory for each run.");
}
var total = Stopwatch.StartNew();
var phases = new List<object>();
var progressGate = new object();
var phaseClock = Stopwatch.StartNew();
var phase = 0;
var phaseLabel = "";
var lastPrinted = TimeSpan.MinValue;
void Report(int nextPhase, string stage, DuckDbQueryProgressSnapshot? snapshot)
{
    lock (progressGate)
    {
        if (nextPhase != phase)
        {
            if (phase > 0)
            {
                phases.Add(new { phase, stage = phaseLabel, seconds = phaseClock.Elapsed.TotalSeconds });
            }
            phase = nextPhase;
            phaseLabel = stage;
            phaseClock.Restart();
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} step {phase}: {stage}");
        }
        if (snapshot is not null && (lastPrinted == TimeSpan.MinValue || total.Elapsed - lastPrinted > TimeSpan.FromSeconds(20)))
        {
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} step {phase}: {snapshot.Percentage?.ToString("F1", CultureInfo.InvariantCulture) ?? "?"}% query; {snapshot.RowsProcessed}/{snapshot.TotalRows} rows estimated");
            lastPrinted = total.Elapsed;
        }
    }
}
string Literal(string text) => "'" + text.Replace("\\", "/").Replace("'", "''") + "'";
var result = Path.Combine(output, "query-result.parquet");
try
{
    var policy = new MemoryResourcePolicy { LowMemoryMode = lowMemory };
    using var work = new DuckDbWorkScope(policy, output);
    var connection = work.Connection;
    using var settings = connection.CreateCommand();
    if (Directory.Exists("/extensions"))
    {
        settings.CommandText = "SET extension_directory='/extensions'";
        settings.ExecuteNonQuery();
    }
    OvertureDataAccess.LoadAzureAndSpatial(connection);
    settings.CommandText = "SET azure_http_stats=true";
    settings.ExecuteNonQuery();
    if (threads > 0)
    {
        settings.CommandText = $"SET threads={threads}";
        settings.ExecuteNonQuery();
    }
    if (bufferMiB > 0)
    {
        // Only the buffer is changed; the Azure chunk size/concurrency stay at their defaults.
        settings.CommandText = $"SET azure_read_buffer_size={bufferMiB * 1024L * 1024}";
        settings.ExecuteNonQuery();
    }
    settings.CommandText = "SELECT name,value FROM duckdb_settings() WHERE name IN ('threads','memory_limit','azure_read_buffer_size','azure_read_transfer_chunk_size','azure_read_transfer_concurrency','azure_transport_option_type')";
    var effective = new Dictionary<string, string>();
    using (var reader = settings.ExecuteReader())
    {
        while (reader.Read())
        {
            effective.Add(reader.GetString(0), reader.GetString(1));
        }
    }
    var source = string.Format(CultureInfo.InvariantCulture, OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate, release);
    if (fixture)
    {
        var fixtureRoot = Path.Combine(output, "fixture");
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "type=division"));
        Directory.CreateDirectory(Path.Combine(fixtureRoot, "type=division_area"));
        settings.CommandText = $$$"""
            CREATE TABLE fixture_divisions AS
            SELECT 'city' AS id, '{{{country}}}' AS country,
                {'primary': 'Fixture city', 'common': MAP {'en': 'Fixture City'}} AS names,
                [[{'division_id': 'city', 'subtype': 'locality', 'name': 'Fixture city'}]] AS hierarchies;
            COPY fixture_divisions TO {{{Literal(Path.Combine(fixtureRoot, "type=division/rows.parquet"))}}} (FORMAT PARQUET);
            COPY (SELECT 'area-' || i AS id, names, 'locality' AS subtype, 'land' AS class,
                8 AS admin_level, country, true AS is_land, false AS is_territorial,
                ST_Point(CAST(i % 360 AS DOUBLE) - 180, CAST(i % 180 AS DOUBLE) - 90) AS geometry,
                {'xmin': 0.0, 'ymin': 0.0, 'xmax': 1.0, 'ymax': 1.0} AS bbox, 'city' AS division_id
                FROM fixture_divisions, range(10000) t(i))
            TO {{{Literal(Path.Combine(fixtureRoot, "type=division_area/rows.parquet"))}}} (FORMAT PARQUET);
            """;
        settings.ExecuteNonQuery();
        source = Path.Combine(fixtureRoot, "type=division_area/*.parquet").Replace('\\', '/');
    }
    Console.WriteLine($"DuckDB {connection.ServerVersion}; {variant}; {country}; release {release}; fixture={fixture}; settings={JsonSerializer.Serialize(effective)}");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    var queryTime = Stopwatch.StartNew();
    using var command = connection.CreateCommand();
    using var cancellation = timeout.Token.Register(command.Cancel);
    void Export(string select)
    {
        command.CommandText = $"EXPLAIN ANALYZE COPY ({select}) TO {Literal(result)} (FORMAT PARQUET, ROW_GROUP_SIZE 2048)";
        using var monitor = new DuckDbQueryProgressMonitor(connection, snapshot => Report(phase, phaseLabel, snapshot), NullLogger.Instance);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            File.WriteAllText(Path.Combine(output, $"phase-{phase}-plan.txt"), reader.GetString(1));
        }
    }
    if (variant == "original")
    {
        Report(1, "Original combined query", null);
        Export(OvertureCacheQuery.BuildAreaProjection(OvertureDivisionsLogic.BuildDivisionAreaSource(source, country), true)
            + " WHERE " + OvertureCountrySourceCodes.BuildCountryFilter(country));
    }
    else
    {
        using var query = new OvertureCacheQuery(connection, output, NullLogger.Instance, Report,
            (number, plan) => File.WriteAllText(Path.Combine(output, $"phase-{number}-plan.txt"), plan));
        query.Prepare(source, country, timeout.Token);
        Report(4, "Writing staged query result", null);
        Export(query.BuildResultQuery());
    }
    queryTime.Stop();
    lock (progressGate)
    {
        phases.Add(new { phase, stage = phaseLabel, seconds = phaseClock.Elapsed.TotalSeconds });
    }
    var process = Process.GetCurrentProcess();
    process.Refresh();
    var peakWorkingSet = process.PeakWorkingSet64;
    var cgroupPeak = File.Exists("/sys/fs/cgroup/memory.peak") ? File.ReadAllText("/sys/fs/cgroup/memory.peak").Trim() : null;
    var hashes = new List<string>();
    command.CommandText = $"SELECT * FROM read_parquet({Literal(result)})";
    using (var reader = command.ExecuteReader())
    {
        while (reader.Read())
        {
            using var row = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var column = 0; column < reader.FieldCount; column++)
            {
                byte[] bytes;
                if (reader.IsDBNull(column))
                {
                    row.AppendData(BitConverter.GetBytes(-1));
                    continue;
                }
                var value = reader.GetValue(column);
                if (value is byte[] blob)
                {
                    bytes = blob;
                }
                else if (value is Stream stream)
                {
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                else
                {
                    bytes = Encoding.UTF8.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                }
                row.AppendData(BitConverter.GetBytes(bytes.Length));
                row.AppendData(bytes);
            }
            hashes.Add(Convert.ToHexString(row.GetHashAndReset()));
        }
    }
    hashes.Sort(StringComparer.Ordinal);
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join('\n', hashes))));
    var summary = new { success = true, release, country, variant, fixture, lowMemory,
        duckDbVersion = connection.ServerVersion, effectiveSettings = effective, phases,
        querySeconds = queryTime.Elapsed.TotalSeconds, totalSeconds = total.Elapsed.TotalSeconds,
        peakWorkingSetBytes = peakWorkingSet, cgroupPeakBytes = cgroupPeak,
        resultRows = hashes.Count, resultBytes = new FileInfo(result).Length, logicalSha256 = fingerprint,
        measurement = "EXPLAIN ANALYZE export to Parquet; excludes SQLite cache writing and result fingerprint validation" };
    File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(summary));
    return 0;
}
catch (Exception ex)
{
    File.WriteAllText(summaryPath, JsonSerializer.Serialize(new { success = false, release, country, variant, fixture,
        totalSeconds = total.Elapsed.TotalSeconds, phases, error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    Console.Error.WriteLine(ex);
    return 1;
}

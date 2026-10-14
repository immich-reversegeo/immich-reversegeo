using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Gadm.Services;

public class GadmDivisionCacheService
{
    private readonly ILogger<GadmDivisionCacheService> _logger;
    private readonly string _dataDir;
    private readonly MemoryResourcePolicy _memoryPolicy;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflightDownloads = new();
    private readonly ConcurrentDictionary<string, CacheFileStamp> _readyCaches = new();
    private readonly ConcurrentDictionary<string, CacheDownloadProgress> _downloadProgress = new();

    public CacheDownloadProgress[] GetActiveDownloads() => [.. _downloadProgress.Values];

    private void ReportProgress(string iso3, string stage, long bytes = 0, long? total = null, long rows = 0)
    {
        _downloadProgress.AddOrUpdate(iso3,
            _ => new CacheDownloadProgress("GADM", iso3, DateTimeOffset.UtcNow, stage, bytes, total, rows),
            (_, previous) => previous with { Stage = stage, BytesReceived = bytes, TotalBytes = total, RowsWritten = rows });
    }

    public GadmDivisionCacheService(ILogger<GadmDivisionCacheService> logger, StorageOptions dirs, MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _dataDir = dirs.DataDir;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
    }

    public GadmDivisionCacheService(ILogger<GadmDivisionCacheService> logger, string dataDir, MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _dataDir = dataDir;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
    }

    public Dictionary<string, GadmDivisionStatus> GetStatus()
    {
        var result = new Dictionary<string, GadmDivisionStatus>();
        var root = Path.Combine(_dataDir, "gadm-divisions");
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (var file in Directory.GetFiles(root, "*.db"))
        {
            var iso3 = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var conn = new SqliteConnection($"Data Source={file};Pooling=false");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM gadm_area";
                var count = (long)cmd.ExecuteScalar()!;
                var downloadedAt = ReadMetaTimestamp(conn, "downloadedAt");
                var version = ReadMetaValue(conn, "version");
                var fileSizeBytes = new FileInfo(file).Length;
                result[iso3] = new GadmDivisionStatus(count, downloadedAt, version, fileSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read GADM division database for {ISO3}", iso3);
                result[iso3] = new GadmDivisionStatus(0, null, null, null);
            }
        }

        return result;
    }

    public bool HasData(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return false;
        }

        var path = GetDbPath(iso3);
        if (!CacheFileStamp.TryRead(path, out var stamp))
        {
            _readyCaches.TryRemove(iso3, out _);
            return false;
        }

        if (_readyCaches.TryGetValue(iso3, out var cached))
        {
            if (cached == stamp)
            {
                return true;
            }
            _readyCaches.TryRemove(iso3, out _);
        }

        // Do not remember a validation result if the file changed while it was read.
        if (!HasRows(path, "gadm_area")
            || !CacheFileStamp.TryRead(path, out var after) || after != stamp)
        {
            return false;
        }

        _readyCaches[iso3] = stamp;
        return true;
    }

    public void DeleteFile(string iso3)
    {
        _readyCaches.TryRemove(iso3, out _);
        DeleteFileAndTemps(GetDbPath(iso3), iso3);
    }

    public (Task Task, GadmDivisionEnsureResult Result) GetOrStartDownload(string iso3, CancellationToken ct = default)
    {
        if (HasData(iso3))
        {
            return (Task.CompletedTask, GadmDivisionEnsureResult.AlreadyReady);
        }

        var startedNew = false;
        var lazyDownload = _inflightDownloads.GetOrAdd(
            iso3,
            key =>
            {
                startedNew = true;
                return new Lazy<Task>(
                    () => DownloadDataInternalAsync(key, ct),
                    LazyThreadSafetyMode.ExecutionAndPublication);
            });

        var result = startedNew
            ? GadmDivisionEnsureResult.StartedDownload
            : GadmDivisionEnsureResult.AwaitedExistingDownload;

        return (lazyDownload.Value, result);
    }

    public async Task<GadmDivisionEnsureResult> EnsureDataAsync(string iso3, CancellationToken ct = default)
    {
        var (downloadTask, result) = GetOrStartDownload(iso3, ct);

        try
        {
            await downloadTask.WaitAsync(ct);
            return result;
        }
        finally
        {
            if (downloadTask.IsCompleted)
            {
                _inflightDownloads.TryRemove(iso3, out _);
            }
        }
    }

    private async Task DownloadDataInternalAsync(string iso3, CancellationToken ct)
    {
        var dbPath = GetDbPath(iso3);
        if (HasData(iso3))
        {
            return;
        }

        var gadmCode = GadmCountryCodeMapper.ToGadmCode(iso3);

        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        var tmpDbPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.tmp");
        var tmpDownloadPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.gpkg.download");

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.gpkg.download"))
        {
            TryDelete(stale);
        }

        try
        {
            ReportProgress(iso3, "Waiting for a data processing slot");
            using var budget = await _memoryPolicy.EnterAsync(ct);
            await DownloadFileAsync(GadmDivisionsLogic.BuildCountryGeoPackageUrl(gadmCode), tmpDownloadPath, ct, (bytes, total) => ReportProgress(iso3, "Downloading GADM file", bytes, total));
            ReportProgress(iso3, "Building cache");
            var rowCount = await Task.Run(() => GadmCacheExporter.ExportGeoPackageToSqlite(
                tmpDownloadPath, tmpDbPath, iso3, ct, rows => ReportProgress(iso3, "Building cache", rows: rows)), ct);
            ReportProgress(iso3, "Validating and saving cache", rows: rowCount);
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"No GADM rows were downloaded for {iso3}.");
            }

            if (!IsValidDb(tmpDbPath))
            {
                throw new InvalidOperationException(
                    $"GADM division download for {iso3} produced an invalid SQLite file at {tmpDbPath}");
            }

            SqliteConnection.ClearAllPools();
            File.Move(tmpDbPath, dbPath, overwrite: true);
            _readyCaches.TryRemove(iso3, out _);
            _logger.LogInformation("GADM division download complete for {ISO3} via {GadmCode}: {Rows} areas", iso3, gadmCode, rowCount);
        }
        catch
        {
            TryDelete(tmpDbPath);
            throw;
        }
        finally
        {
            TryDelete(tmpDownloadPath);
            _downloadProgress.TryRemove(iso3, out _);
            _inflightDownloads.TryRemove(iso3, out _);
        }
    }

    private string GetDbPath(string iso3)
    {
        return Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
    }

    private static bool HasRows(string path, string tableName)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            return (long)cmd.ExecuteScalar()! > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidDb(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM _meta WHERE key = 'downloadedAt'";
            return cmd.ExecuteScalar() is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadMetaValue(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM _meta WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar()?.ToString() ?? string.Empty;
    }

    private static DateTime? ReadMetaTimestamp(SqliteConnection conn, string key)
    {
        var text = ReadMetaValue(conn, key);
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static void DeleteFileAndTemps(string path, string iso3)
    {
        TryDelete(path);
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken ct, Action<long, long?> report)
    {
        report(0, null);
        using var http = new HttpClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationPath);
        var total = response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long received = 0;
        report(received, total);
        while (true)
        {
            var count = await source.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0)
            {
                break;
            }
            await destination.WriteAsync(buffer.AsMemory(0, count), ct);
            received += count;
            report(received, total);
        }
    }
}

public enum GadmDivisionEnsureResult
{
    AlreadyReady,
    AwaitedExistingDownload,
    StartedDownload
}

public record GadmDivisionStatus(long RowCount, DateTime? DownloadedAt, string? Version, long? FileSizeBytes);

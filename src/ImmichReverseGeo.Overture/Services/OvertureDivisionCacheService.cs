using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Overture.Services;

public class OvertureDivisionCacheService
{
    // Bump when export behavior changes invalidate previously downloaded results.
    public const string CacheFormatVersion = "2";
    private static readonly TimeSpan ReleaseCheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RefreshRetryDelay = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _releaseCheckLock = new(1, 1);
    private readonly object _releaseScopeLock = new();
    private Lazy<Task<string?>>? _runRelease;
    private int _releaseScopeCount;
    private sealed record ReleaseStatus(string? Release, DateTimeOffset NextCheck);
    private ReleaseStatus _releaseStatus = new(null, DateTimeOffset.MinValue);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _refreshRetries = new();
    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    private readonly ILogger<OvertureDivisionCacheService> _logger;
    private readonly string _dataDir;
    private readonly Func<string, string?> _iso3ToAlpha2;
    private readonly MemoryResourcePolicy _memoryPolicy;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _inflightDownloads = new();
    private sealed record ReadyCache(string Release, CacheFileStamp Stamp);
    private readonly ConcurrentDictionary<string, ReadyCache> _readyCaches = new();
    private readonly ConcurrentDictionary<string, CacheDownloadProgress> _downloadProgress = new();

    public CacheDownloadProgress[] GetActiveDownloads() => [.. _downloadProgress.Values];

    private void ReportProgress(string iso3, string stage, long rows = 0, int phase = 0,
        DuckDbQueryProgressSnapshot? query = null, long? totalRows = null)
    {
        _downloadProgress.AddOrUpdate(iso3,
            _ => new CacheDownloadProgress("Overture", iso3, DateTimeOffset.UtcNow, stage)
                .Observe(stage, phase, query?.Percentage, query?.RowsProcessed ?? 0, query?.TotalRows ?? 0, rows, totalRows),
            (_, previous) => previous.Observe(stage, phase, query?.Percentage,
                query?.RowsProcessed ?? 0, query?.TotalRows ?? 0, rows, totalRows));
    }

    public OvertureDivisionCacheService(
        ILogger<OvertureDivisionCacheService> logger,
        StorageOptions dirs,
        Func<string, string?> iso3ToAlpha2,
        MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _dataDir = dirs.DataDir;
        _iso3ToAlpha2 = iso3ToAlpha2;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
    }

    public OvertureDivisionCacheService(ILogger<OvertureDivisionCacheService> logger, string dataDir, Func<string, string?> iso3ToAlpha2, MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _dataDir = dataDir;
        _iso3ToAlpha2 = iso3ToAlpha2;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
    }

    public Dictionary<string, OvertureDivisionStatus> GetStatus()
    {
        var result = new Dictionary<string, OvertureDivisionStatus>();
        var root = Path.Combine(_dataDir, "overture-divisions");
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
                cmd.CommandText = "SELECT COUNT(*) FROM division_area";
                var count = (long)cmd.ExecuteScalar()!;
                var downloadedAt = ReadMetaTimestamp(conn, "downloadedAt");
                var release = ReadMetaValue(conn, "release");
                var fileSizeBytes = new FileInfo(file).Length;
                result[iso3] = new OvertureDivisionStatus(count, downloadedAt, release, fileSizeBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Overture division database for {ISO3}", iso3);
                result[iso3] = new OvertureDivisionStatus(0, null, null, null);
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
            _refreshRetries.TryRemove(iso3, out _);
            return false;
        }

        if (_readyCaches.TryGetValue(iso3, out var cached))
        {
            if (cached.Stamp == stamp)
            {
                return true;
            }
            _readyCaches.TryRemove(iso3, out _);
            _refreshRetries.TryRemove(iso3, out _);
        }

        // Do not remember a validation result if the file changed while it was read.
        if (!HasCurrentRows(path, iso3, out var release)
            || !CacheFileStamp.TryRead(path, out var after) || after != stamp)
        {
            return false;
        }

        _readyCaches[iso3] = new ReadyCache(release, stamp);
        return true;
    }

    public void DeleteFile(string iso3)
    {
        _readyCaches.TryRemove(iso3, out _);
        _refreshRetries.TryRemove(iso3, out _);
        var status = Volatile.Read(ref _releaseStatus);
        Volatile.Write(ref _releaseStatus, status with { NextCheck = DateTimeOffset.MinValue });
        DeleteFileAndTemps(GetDbPath(iso3), iso3);
    }

    public (Task Task, OvertureDivisionEnsureResult Result) GetOrStartDownload(string iso3, CancellationToken ct = default)
    {
        if (HasData(iso3) && CanReuseWithoutReleaseCheck(iso3))
        {
            return (Task.CompletedTask, OvertureDivisionEnsureResult.AlreadyReady);
        }

        var candidate = new Lazy<Task>(() => PrepareDataAsync(iso3, ct), LazyThreadSafetyMode.ExecutionAndPublication);
        var lazyDownload = _inflightDownloads.GetOrAdd(iso3, candidate);
        var startedNew = ReferenceEquals(candidate, lazyDownload);

        var result = startedNew
            ? (HasData(iso3) ? OvertureDivisionEnsureResult.CheckingForUpdates : OvertureDivisionEnsureResult.StartedDownload)
            : OvertureDivisionEnsureResult.AwaitedExistingDownload;

        return (lazyDownload.Value, result);
    }

    public async Task<OvertureDivisionEnsureResult> EnsureDataAsync(string iso3, CancellationToken ct = default)
    {
        var (downloadTask, result) = GetOrStartDownload(iso3, ct);

        await downloadTask.WaitAsync(ct);
        return result;
    }

    public IDisposable BeginReleaseScope()
    {
        lock (_releaseScopeLock)
        {
            _runRelease ??= new Lazy<Task<string?>>(() => CheckLatestReleaseAsync(CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _releaseScopeCount++;
        }
        return new ReleaseScope(this);
    }

    private sealed class ReleaseScope(OvertureDivisionCacheService owner) : IDisposable
    {
        private OvertureDivisionCacheService? _owner = owner;
        public void Dispose()
        {
            var service = Interlocked.Exchange(ref _owner, null);
            if (service is null)
            {
                return;
            }
            lock (service._releaseScopeLock)
            {
                if (--service._releaseScopeCount == 0)
                {
                    service._runRelease = null;
                }
            }
        }
    }

    private bool CanReuseWithoutReleaseCheck(string iso3)
    {
        lock (_releaseScopeLock)
        {
            if (_runRelease is not null)
            {
                return _runRelease.IsValueCreated && _runRelease.Value.IsCompletedSuccessfully
                    && IsReleaseReady(iso3, _runRelease.Value.Result);
            }
        }
        var status = Volatile.Read(ref _releaseStatus);
        return UtcNow < status.NextCheck && IsReleaseReady(iso3, status.Release);
    }

    private bool IsReleaseReady(string iso3, string? release)
    {
        if (_refreshRetries.TryGetValue(iso3, out var retry) && UtcNow < retry)
        {
            return true;
        }
        if (release is null)
        {
            return true;
        }
        if (!_readyCaches.TryGetValue(iso3, out var cached))
        {
            return false;
        }
        if (cached.Release == release)
        {
            return true;
        }
        return TryParseRelease(cached.Release, out var cachedDate, out var cachedPatch)
            && TryParseRelease(release, out var latestDate, out var latestPatch)
            && (cachedDate > latestDate || (cachedDate == latestDate && cachedPatch >= latestPatch));
    }

    private static bool TryParseRelease(string release, out DateOnly date, out int patch)
    {
        date = default;
        patch = 0;
        return release.Length > 11 && release[10] == '.'
            && DateOnly.TryParseExact(release[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out date)
            && int.TryParse(release[11..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out patch);
    }

    private async Task<string?> GetTargetReleaseAsync(CancellationToken ct)
    {
        Lazy<Task<string?>>? runRelease;
        lock (_releaseScopeLock)
        {
            runRelease = _runRelease;
        }
        return runRelease is null
            ? await CheckLatestReleaseAsync(ct)
            : await runRelease.Value.WaitAsync(ct);
    }

    private async Task<string?> CheckLatestReleaseAsync(CancellationToken ct)
    {
        await _releaseCheckLock.WaitAsync(ct);
        try
        {
            var status = Volatile.Read(ref _releaseStatus);
            if (UtcNow < status.NextCheck)
            {
                return status.Release;
            }
            try
            {
                var release = await Task.Run(() => GetLatestOvertureReleaseForCache(ct), ct);
                status = new ReleaseStatus(release, UtcNow + ReleaseCheckInterval);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                status = status with { NextCheck = UtcNow + RefreshRetryDelay };
                _logger.LogWarning(ex, "Unable to check the latest Overture release; retaining compatible caches and retrying in one hour");
            }
            Volatile.Write(ref _releaseStatus, status);
            return status.Release;
        }
        finally
        {
            _releaseCheckLock.Release();
        }
    }

    private async Task PrepareDataAsync(string iso3, CancellationToken ct)
    {
        try
        {
            var release = await GetTargetReleaseAsync(ct);
            if (HasData(iso3) && IsReleaseReady(iso3, release))
            {
                return;
            }
            if (release is null)
            {
                throw new InvalidOperationException("Unable to discover an Overture release for cache preparation. Check network access and retry.");
            }
            try
            {
                await DownloadDataInternalAsync(iso3, release, ct);
                _refreshRetries.TryRemove(iso3, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (HasData(iso3))
            {
                _refreshRetries[iso3] = UtcNow + RefreshRetryDelay;
                _logger.LogWarning(ex, "Refresh to Overture {Release} failed for {ISO3}; using the previous compatible cache and retrying in one hour", release, iso3);
            }
        }
        finally
        {
            _inflightDownloads.TryRemove(iso3, out _);
        }
    }

    private async Task DownloadDataInternalAsync(string iso3, string release, CancellationToken ct)
    {
        var dbPath = GetDbPath(iso3);

        var alpha2 = _iso3ToAlpha2(iso3);
        if (alpha2 is null)
        {
            throw new InvalidOperationException($"Could not determine alpha-2 code for {iso3}.");
        }

        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        var tmpPath = Path.Combine(dir, $"{iso3}.{Guid.NewGuid():N}.tmp");

        foreach (var stale in Directory.GetFiles(dir, $"{iso3}.*.tmp"))
        {
            TryDelete(stale);
        }

        try
        {
            ReportProgress(iso3, "Finding the latest Overture release");
            var rowCount = await Task.Run(() => ExportOvertureDivisions(tmpPath, alpha2, iso3, release, ct), ct);
            ReportProgress(iso3, "Validating and saving cache", rows: rowCount, phase: 5, totalRows: rowCount);
            if (rowCount == 0)
            {
                throw new InvalidOperationException($"No Overture division rows were downloaded for {iso3}.");
            }

            if (!IsValidDb(tmpPath) || !HasCurrentRows(tmpPath, iso3, out _))
            {
                throw new InvalidOperationException(
                    $"Overture division download for {iso3} produced an invalid SQLite file at {tmpPath}");
            }

            ct.ThrowIfCancellationRequested();
            using (var replacement = new SqliteConnection($"Data Source={tmpPath};Mode=ReadOnly;Pooling=false"))
            {
                replacement.Open();
                if (ReadMetaValue(replacement, "release") != release)
                {
                    throw new InvalidOperationException($"Overture cache for {iso3} does not match the requested release {release}.");
                }
            }
            File.Move(tmpPath, dbPath, overwrite: true);
            _readyCaches.TryRemove(iso3, out _);
            _logger.LogInformation("Overture division download complete for {ISO3}: {Rows} areas", iso3, rowCount);
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            _logger.LogWarning(ex, "Overture cache preparation failed for {ISO3}; any existing cache file was retained", iso3);
            throw;
        }
        finally
        {
            _downloadProgress.TryRemove(iso3, out _);
        }
    }

    protected virtual long ExportOvertureDivisions(string tmpPath, string alpha2, string iso3, string release, CancellationToken ct)
    {
        var releaseUrl = GetDivisionAreaReleaseUrl(release);

        ReportProgress(iso3, "Waiting for a data processing slot");
        using var work = new DuckDbWorkScope(_memoryPolicy, _dataDir, ct);
        var duck = work.Connection;
        ReportProgress(iso3, "Preparing download connection");
        PrepareExportConnection(duck);

        using var query = new OvertureCacheQuery(duck, _dataDir, _logger,
            (phase, stage, snapshot) => ReportProgress(iso3, stage, phase: phase, query: snapshot));
        var areaCount = query.Prepare(releaseUrl, alpha2, ct);
        using var selectCmd = duck.CreateCommand();
        // Avoid materializing all polygon results before the SQLite writer starts.
        selectCmd.UseStreamingMode = true;
        selectCmd.CommandText = query.BuildResultQuery();
        using var cancellation = ct.Register(selectCmd.Cancel);
        ReportProgress(iso3, "Writing country cache", phase: 4, totalRows: areaCount);
        using var reader = selectCmd.ExecuteReader();

        using var sqlite = new SqliteConnection($"Data Source={tmpPath};Pooling=false");
        sqlite.Open();

        using (var ddl = sqlite.CreateCommand())
        {
            ddl.CommandText = @"
                CREATE TABLE division_area (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    subtype TEXT NULL,
                    class_name TEXT NULL,
                    admin_level INTEGER NULL,
                    country TEXT NULL,
                    is_land INTEGER NOT NULL,
                    is_territorial INTEGER NOT NULL,
                    geom_wkb BLOB NULL,
                    bbox_xmin REAL NULL,
                    bbox_ymin REAL NULL,
                    bbox_xmax REAL NULL,
                    bbox_ymax REAL NULL,
                    division_id TEXT NULL,
                    hierarchy_json TEXT NULL,
                    names_json TEXT NULL
                );
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE INDEX idx_division_area_bbox_x ON division_area (bbox_xmin, bbox_xmax);
                CREATE INDEX idx_division_area_bbox_y ON division_area (bbox_ymin, bbox_ymax);
                CREATE INDEX idx_division_area_subtype ON division_area (subtype);
                CREATE INDEX idx_division_area_admin_level ON division_area (admin_level);
                CREATE INDEX idx_division_area_country ON division_area (country);
                ";
            ddl.ExecuteNonQuery();
        }

        using (var meta = sqlite.CreateCommand())
        {
            meta.CommandText = @"
                INSERT INTO _meta VALUES ('downloadedAt', $at);
                INSERT INTO _meta VALUES ('release', $release);
                INSERT INTO _meta VALUES ('sourceCountries', $sourceCountries);
                INSERT INTO _meta VALUES ('cacheFormatVersion', $cacheFormatVersion);
                ";
            meta.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            meta.Parameters.AddWithValue("$release", release);
            meta.Parameters.AddWithValue("$sourceCountries", OvertureCountrySourceCodes.GetCoverageSignature(alpha2));
            meta.Parameters.AddWithValue("$cacheFormatVersion", CacheFormatVersion);
            meta.ExecuteNonQuery();
        }

        using var tx = sqlite.BeginTransaction();
        using var insert = sqlite.CreateCommand();
        insert.CommandText = @"
            INSERT OR IGNORE INTO division_area (
                id, name, subtype, class_name, admin_level, country, is_land, is_territorial,
                geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax, division_id, hierarchy_json, names_json
            ) VALUES (
                $id, $name, $subtype, $class, $adminLevel, $country, $isLand, $isTerritorial,
                $geom, $xmin, $ymin, $xmax, $ymax, $divisionId, $hierarchy, $names
            )
            ";
        var pId = insert.Parameters.Add("$id", SqliteType.Text);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pSubType = insert.Parameters.Add("$subtype", SqliteType.Text);
        var pClass = insert.Parameters.Add("$class", SqliteType.Text);
        var pAdminLevel = insert.Parameters.Add("$adminLevel", SqliteType.Integer);
        var pCountry = insert.Parameters.Add("$country", SqliteType.Text);
        var pIsLand = insert.Parameters.Add("$isLand", SqliteType.Integer);
        var pIsTerritorial = insert.Parameters.Add("$isTerritorial", SqliteType.Integer);
        var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
        var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
        var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
        var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
        var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

        var pDivisionId = insert.Parameters.Add("$divisionId", SqliteType.Text);
        var pHierarchy = insert.Parameters.Add("$hierarchy", SqliteType.Text);
        var pNames = insert.Parameters.Add("$names", SqliteType.Text);

        long rows = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            pId.Value = reader.GetString(0);
            pName.Value = reader.GetString(1);
            pSubType.Value = reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2);
            pClass.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
            pAdminLevel.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetInt32(4);
            pCountry.Value = reader.IsDBNull(5) ? DBNull.Value : reader.GetString(5);
            pIsLand.Value = reader.GetBoolean(6) ? 1 : 0;
            pIsTerritorial.Value = reader.GetBoolean(7) ? 1 : 0;
            pGeom.Value = reader.IsDBNull(8) ? DBNull.Value : ReadBlobValue(reader, 8);
            pXMin.Value = reader.IsDBNull(9) ? DBNull.Value : reader.GetDouble(9);
            pYMin.Value = reader.IsDBNull(10) ? DBNull.Value : reader.GetDouble(10);
            pXMax.Value = reader.IsDBNull(11) ? DBNull.Value : reader.GetDouble(11);
            pYMax.Value = reader.IsDBNull(12) ? DBNull.Value : reader.GetDouble(12);
            pDivisionId.Value = reader.IsDBNull(13) ? DBNull.Value : reader.GetString(13);
            pHierarchy.Value = reader.IsDBNull(14) ? DBNull.Value : reader.GetString(14);
            pNames.Value = reader.IsDBNull(15) ? DBNull.Value : reader.GetString(15);
            insert.ExecuteNonQuery();
            rows++;
            if (rows % 100 == 0)
            {
                ReportProgress(iso3, "Writing country cache", rows: rows, phase: 4, totalRows: areaCount);
            }
        }

        tx.Commit();
        sqlite.Close();
        return rows;
    }

    protected virtual string GetDivisionAreaReleaseUrl(string release) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture, OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate, release);

    protected virtual void PrepareExportConnection(DuckDBConnection connection) => OvertureDataAccess.LoadAzureAndSpatial(connection);

    protected virtual string GetLatestOvertureReleaseForCache(CancellationToken ct) => OvertureReleaseCatalog.GetLatestRelease(_memoryPolicy, _dataDir, ct);

    private string GetDbPath(string iso3)
    {
        return Path.Combine(_dataDir, "overture-divisions", $"{iso3}.db");
    }

    private bool HasCurrentRows(string path, string iso3, out string release)
    {
        release = string.Empty;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM division_area";
            if ((long)cmd.ExecuteScalar()! <= 0 || !HasCurrentCacheMetadata(conn, iso3))
            {
                return false;
            }
            release = ReadMetaValue(conn, "release");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool HasCurrentCacheMetadata(SqliteConnection conn, string iso3)
    {
        if (ReadMetaValue(conn, "cacheFormatVersion") != CacheFormatVersion)
        {
            return false;
        }

        var alpha2 = _iso3ToAlpha2(iso3);
        if (alpha2 is null)
        {
            return false;
        }

        // Ordinary countries do not require expanded source coverage metadata.
        if (OvertureCountrySourceCodes.GetCodes(alpha2).Count == 1)
        {
            return true;
        }

        return ReadMetaValue(conn, "sourceCountries") == OvertureCountrySourceCodes.GetCoverageSignature(alpha2);
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

    private static byte[] ReadBlobValue(System.Data.Common.DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
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

        throw new InvalidCastException($"Unsupported blob value type '{value.GetType().FullName}' at ordinal {ordinal}.");
    }
}

public record OvertureDivisionStatus(long RowCount, DateTime? DownloadedAt, string? Release, long? FileSizeBytes);

public enum OvertureDivisionEnsureResult
{
    AlreadyReady,
    AwaitedExistingDownload,
    StartedDownload,
    CheckingForUpdates
}

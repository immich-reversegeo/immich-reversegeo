using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Write-ahead snapshots are retained independently of saved scans.</summary>
public sealed class LocationChangeRepository
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public LocationChangeRepository(StorageOptions storage) : this(storage.DataDir)
    {
    }
    public LocationChangeRepository(string dataDir) => _path = Path.Combine(dataDir, "location-changes.db");

    private async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await conn.OpenAsync();
            if (!_initialized)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=FULL;
                    CREATE TABLE IF NOT EXISTS operations (id TEXT PRIMARY KEY, scan_id TEXT NOT NULL, created_at TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS changes (
                        operation_id TEXT NOT NULL REFERENCES operations(id), asset_id TEXT NOT NULL,
                        snapshot_json TEXT NOT NULL, apply_status TEXT NOT NULL, undo_status TEXT NOT NULL,
                        applied_revision TEXT, message TEXT, updated_at TEXT NOT NULL,
                        PRIMARY KEY(operation_id, asset_id)
                    );
                    CREATE INDEX IF NOT EXISTS ix_operations_scan ON operations(scan_id, created_at);
                    UPDATE changes SET apply_status='NeedsReview', message='Interrupted write: check Immich before making another change.'
                        WHERE apply_status='Writing';
                    UPDATE changes SET undo_status='NeedsReview', message='Interrupted undo: check Immich before making another change.'
                        WHERE undo_status='Writing';
                    """;
                await cmd.ExecuteNonQueryAsync();
                _initialized = true;
            }
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid> CreateAsync(Guid scanId, IReadOnlyList<LocationPreviewRow> rows)
    {
        if (rows.Count is < 1 or > 1000 || rows.Any(row => !row.HasChange)
            || rows.Select(row => row.Asset.Id).Distinct().Count() != rows.Count)
        {
            throw new ArgumentException("Select between 1 and 1,000 distinct, resolvable differences.");
        }
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            using var transaction = conn.BeginTransaction();
            var id = Guid.NewGuid();
            var at = DateTimeOffset.UtcNow.ToString("O");
            await using var operation = conn.CreateCommand();
            operation.Transaction = transaction;
            operation.CommandText = "INSERT INTO operations VALUES ($id, $scan, $at)";
            operation.Parameters.AddWithValue("$id", id.ToString());
            operation.Parameters.AddWithValue("$scan", scanId.ToString());
            operation.Parameters.AddWithValue("$at", at);
            await operation.ExecuteNonQueryAsync();
            foreach (var row in rows)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO changes VALUES ($id, $asset, $snapshot, 'Pending', 'NotRequested', NULL, NULL, $at)";
                cmd.Parameters.AddWithValue("$id", id.ToString());
                cmd.Parameters.AddWithValue("$asset", row.Asset.Id.ToString());
                cmd.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(row));
                cmd.Parameters.AddWithValue("$at", at);
                await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationChangeOperation>> GetOperationsAsync(int page = 1, Guid? id = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT o.id,o.scan_id,o.created_at,count(*),
                    sum(c.apply_status='Applied'),sum(c.apply_status='Skipped'),
                    sum(c.apply_status='NeedsReview' OR c.undo_status='NeedsReview'),
                    sum(c.apply_status IN ('Pending','Writing')),sum(c.undo_status='Undone'),
                    sum(c.undo_status IN ('Pending','Writing'))
                FROM operations o JOIN changes c ON c.operation_id=o.id
                WHERE ($id IS NULL OR o.id=$id)
                GROUP BY o.id ORDER BY o.created_at DESC,o.id DESC LIMIT 26 OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$id", (object?)id?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$offset", checked((page - 1) * 25));
            var result = new List<LocationChangeOperation>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
                    reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationChangeItem>> GetItemsAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM changes WHERE operation_id=$id ORDER BY asset_id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            var result = new List<LocationChangeItem>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(ReadItem(reader));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static LocationChangeItem ReadItem(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<LocationPreviewRow>(reader.GetString(2))!,
        Enum.Parse<LocationChangeStatus>(reader.GetString(3)), Enum.Parse<LocationUndoStatus>(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
        DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture));

    public async Task<IReadOnlyDictionary<Guid, LocationChangeItem>> GetScanResultsAsync(Guid scanId, IReadOnlyCollection<Guid> assetIds)
    {
        var ids = assetIds.Distinct().Take(1001).ToArray();
        if (ids.Length > 1000)
        {
            throw new ArgumentException("Request at most 1,000 assets.");
        }
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, LocationChangeItem>();
        }
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd = conn.CreateCommand();
            var parameters = ids.Select((_, index) => "$asset" + index).ToArray();
            cmd.CommandText = $"""
                WITH latest AS (
                    SELECT c.*, ROW_NUMBER() OVER (
                        PARTITION BY c.asset_id ORDER BY o.created_at DESC,o.id DESC
                    ) AS position
                    FROM changes c JOIN operations o ON o.id=c.operation_id
                    WHERE o.scan_id=$scan AND c.asset_id IN ({string.Join(',', parameters)})
                )
                SELECT operation_id,asset_id,snapshot_json,apply_status,undo_status,
                    applied_revision,message,updated_at FROM latest WHERE position=1
                """;
            cmd.Parameters.AddWithValue("$scan", scanId.ToString());
            for (var index = 0; index < ids.Length; index++)
            {
                cmd.Parameters.AddWithValue(parameters[index], ids[index].ToString());
            }
            var result = new Dictionary<Guid, LocationChangeItem>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var item = ReadItem(reader);
                result[item.AssetId] = item;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // This durable claim must succeed before a writer is called.
    public async Task<bool> ClaimAsync(Guid operationId, Guid assetId, bool undo)
    {
        var column = undo ? "undo_status" : "apply_status";
        return await UpdateAsync(operationId, assetId,
            $"{column}='Writing', message=NULL", $"AND {column}='Pending'") == 1;
    }

    public async Task QueueUndoAsync(Guid operationId, IReadOnlyCollection<Guid> assetIds)
    {
        var selected = assetIds.Distinct().Take(1001).ToArray();
        if (selected.Length is < 1 or > 1000)
        {
            throw new ArgumentException("Select between 1 and 1,000 applied changes for undo.");
        }
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            using var transaction = conn.BeginTransaction();
            foreach (var assetId in selected)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    UPDATE changes SET undo_status='Pending',message=NULL,updated_at=$at
                    WHERE operation_id=$id AND asset_id=$asset AND apply_status='Applied'
                        AND undo_status IN ('NotRequested','Skipped')
                        AND applied_revision IS NOT NULL AND trim(applied_revision)<>''
                    """;
                cmd.Parameters.AddWithValue("$id", operationId.ToString());
                cmd.Parameters.AddWithValue("$asset", assetId.ToString());
                cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                if (await cmd.ExecuteNonQueryAsync() != 1)
                {
                    throw new ArgumentException("The undo selection changed. Refresh results and select applied changes that have not already been undone or marked uncertain.");
                }
            }
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task FinishAsync(LocationChangeItem item, bool undo, string status, string? revision, string? message) =>
        UpdateAsync(item.OperationId, item.AssetId,
            undo ? "undo_status=$status,message=$message" : "apply_status=$status,applied_revision=$revision,message=$message",
            undo ? "AND undo_status='Writing'" : "AND apply_status='Writing'", status, revision, message);

    public async Task RecoverAsync(Guid id)
    {
        // Called only while the management service holds its execution lock.
        foreach (var item in await GetItemsAsync(id))
        {
            if (item.ApplyStatus == LocationChangeStatus.Writing)
            {
                await FinishAsync(item, false, "NeedsReview", null, "Interrupted write: check Immich; this item will not be retried automatically.");
            }
            if (item.UndoStatus == LocationUndoStatus.Writing)
            {
                await FinishAsync(item, true, "NeedsReview", null, "Interrupted undo: check Immich; this item will not be retried automatically.");
            }
        }
    }

    private async Task<int> UpdateAsync(Guid operationId, Guid assetId, string set, string condition,
        string? status = null, string? revision = null, string? message = null)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = await OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE changes SET {set},updated_at=$at WHERE operation_id=$id AND asset_id=$asset {condition}";
            cmd.Parameters.AddWithValue("$id", operationId.ToString());
            cmd.Parameters.AddWithValue("$asset", assetId.ToString());
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$revision", (object?)revision ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}

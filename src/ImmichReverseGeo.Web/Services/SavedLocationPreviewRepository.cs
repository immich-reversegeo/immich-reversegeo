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

public sealed record SavedLocationPreview(
    Guid Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    LocationPreviewSelection Selection, ProcessingConfig Settings,
    string Status, string? Error, long CheckedCount, long DifferentCount, LocationPreviewCheckpoint? Checkpoint = null)
{
    public bool CanResume => Status is "Paused" or "More assets available" or "Cancelled" or "Interrupted" or "Failed";
    public string StatusLabel => Status == "Paused" ? "Paused for review" : Status;
}

/// <summary>Local snapshots only; this repository never connects to Immich.</summary>
public sealed class SavedLocationPreviewRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _dataDir;
    private bool _initialised;

    public SavedLocationPreviewRepository(StorageOptions storage) : this(storage.DataDir)
    {
    }

    public SavedLocationPreviewRepository(string dataDir)
    {
        _dataDir = dataDir;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDir, "location-previews.db"),
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_dataDir);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct);
            if (!_initialised)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS previews (
                        id TEXT PRIMARY KEY, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                        selection_json TEXT NOT NULL, settings_json TEXT NOT NULL,
                        status TEXT NOT NULL, error TEXT
                    );
                    CREATE TABLE IF NOT EXISTS preview_rows (
                        row_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        preview_id TEXT NOT NULL REFERENCES previews(id) ON DELETE CASCADE,
                        asset_id TEXT NOT NULL, row_json TEXT NOT NULL, has_change INTEGER NOT NULL,
                        UNIQUE(preview_id, asset_id)
                    );
                    CREATE INDEX IF NOT EXISTS ix_preview_rows_page ON preview_rows(preview_id, row_id);
                    CREATE INDEX IF NOT EXISTS ix_preview_rows_changes ON preview_rows(preview_id, has_change, row_id);
                    UPDATE previews SET status = 'Interrupted' WHERE status = 'Running';
                    """;
                await command.ExecuteNonQueryAsync(ct);
                var columns = new HashSet<string>(StringComparer.Ordinal);
                await using (var info = connection.CreateCommand())
                {
                    info.CommandText = "PRAGMA table_info(previews)";
                    await using var reader = await info.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        columns.Add(reader.GetString(1));
                    }
                }
                foreach (var column in new[] { "checkpoint_json", "run_token" })
                {
                    if (!columns.Contains(column))
                    {
                        await using var alter = connection.CreateCommand();
                        alter.CommandText = $"ALTER TABLE previews ADD COLUMN {column} TEXT";
                        await alter.ExecuteNonQueryAsync(ct);
                    }
                }
                await using var recovery = connection.CreateCommand();
                recovery.CommandText = "UPDATE previews SET run_token=NULL WHERE status='Interrupted'";
                await recovery.ExecuteNonQueryAsync(ct);
                _initialised = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<Guid> CreateAsync(LocationPreviewSelection selection, ProcessingConfig settings, CancellationToken ct = default, Guid? runToken = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            var id = Guid.NewGuid();
            command.CommandText = """
                INSERT INTO previews(id,created_at,updated_at,selection_json,settings_json,status,error,run_token) VALUES ($id, $at, $at, $selection, $settings, 'Running', NULL, $token)
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$selection", JsonSerializer.Serialize(selection));
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings));
            command.Parameters.AddWithValue("$token", (object?)runToken?.ToString() ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(Guid previewId, LocationPreviewRow row, CancellationToken ct = default, Guid? runToken = null, LocationPreviewCheckpoint? checkpoint = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            if (runToken is { } owner)
            {
                await RequireOwnerAsync(connection, transaction, previewId, owner, ct);
                await using var duplicate = connection.CreateCommand();
                duplicate.Transaction = transaction;
                duplicate.CommandText = "SELECT EXISTS(SELECT 1 FROM preview_rows WHERE preview_id=$id AND asset_id=$asset)";
                duplicate.Parameters.AddWithValue("$id", previewId.ToString());
                duplicate.Parameters.AddWithValue("$asset", row.Asset.Id.ToString());
                if ((long)(await duplicate.ExecuteScalarAsync(ct))! == 1)
                {
                    throw new InvalidOperationException("This asset was already saved in the scan. Refresh the saved results before continuing.");
                }
            }
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO preview_rows(preview_id, asset_id, row_json, has_change)
                VALUES ($id, $asset, $row, $changed)
                ON CONFLICT(preview_id, asset_id) DO NOTHING;
                UPDATE previews SET updated_at = $at,checkpoint_json=coalesce($checkpoint,checkpoint_json) WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", previewId.ToString());
            command.Parameters.AddWithValue("$asset", row.Asset.Id.ToString());
            command.Parameters.AddWithValue("$row", JsonSerializer.Serialize(row));
            command.Parameters.AddWithValue("$changed", row.HasChange);
            command.Parameters.AddWithValue("$checkpoint", checkpoint is null ? DBNull.Value : JsonSerializer.Serialize(checkpoint));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            transaction.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetStatusAsync(Guid id, string status, string? error = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE previews SET status = $status, error = $error, updated_at = $at WHERE id = $id AND run_token IS NULL";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException("This scan is running or no longer exists. Only its owning page can update its status.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private const string SummarySql = """
        SELECT p.id, p.created_at, p.updated_at, p.selection_json, p.settings_json, p.status, p.error,
            (SELECT count(*) FROM preview_rows r WHERE r.preview_id = p.id),
            (SELECT count(*) FROM preview_rows r WHERE r.preview_id = p.id AND r.has_change = 1), p.checkpoint_json
        FROM previews p
        """;

    public async Task<IReadOnlyList<SavedLocationPreview>> GetReportsAsync(int page = 1, Guid? id = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = SummarySql + " WHERE ($id IS NULL OR p.id = $id) ORDER BY p.created_at DESC, p.id DESC LIMIT 26 OFFSET $offset";
            command.Parameters.AddWithValue("$id", (object?)id?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$offset", checked((page - 1) * 25));
            var result = new List<SavedLocationPreview>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new SavedLocationPreview(
                    Guid.Parse(reader.GetString(0)), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    JsonSerializer.Deserialize<LocationPreviewSelection>(reader.GetString(3))!,
                    JsonSerializer.Deserialize<ProcessingConfig>(reader.GetString(4))!,
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8),
                    reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<LocationPreviewCheckpoint>(reader.GetString(9))));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationPreviewRow>> GetRowsAsync(Guid id, int page = 1, bool onlyChanges = false, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT row_json FROM preview_rows WHERE preview_id = $id AND (NOT $changes OR has_change = 1)
                ORDER BY row_id LIMIT 101 OFFSET $offset
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$changes", onlyChanges);
            command.Parameters.AddWithValue("$offset", checked((page - 1) * 100));
            var result = new List<LocationPreviewRow>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(JsonSerializer.Deserialize<LocationPreviewRow>(reader.GetString(0))!);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationPreviewRow>> GetReviewRowsAsync(Guid id, int page, LocationReviewFilter filter, bool onlyChanges = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentException("Choose a valid result filter.", nameof(filter));
        }
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await using var attach = connection.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS journal";
            attach.Parameters.AddWithValue("$path", Path.Combine(_dataDir, "location-changes.db"));
            await attach.ExecuteNonQueryAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH ranked AS (
                    SELECT c.asset_id,c.apply_status,c.undo_status,
                        ROW_NUMBER() OVER (PARTITION BY c.asset_id ORDER BY o.created_at DESC,o.id DESC) AS position
                    FROM journal.changes c JOIN journal.operations o ON o.id=c.operation_id WHERE o.scan_id=$id
                )
                SELECT r.row_json FROM preview_rows r
                LEFT JOIN ranked c ON c.asset_id=r.asset_id AND c.position=1
                WHERE r.preview_id=$id AND (NOT $onlyChanges OR r.has_change=1) AND (
                    $filter='All'
                    OR ($filter='Ready' AND r.has_change=1 AND c.asset_id IS NULL)
                    OR ($filter='Applied' AND c.apply_status='Applied' AND c.undo_status='NotRequested')
                    OR ($filter='Attention' AND (
                        (c.asset_id IS NOT NULL AND c.undo_status<>'Undone'
                            AND NOT (c.apply_status='Applied' AND c.undo_status='NotRequested'))
                        OR (c.asset_id IS NULL AND (json_extract(r.row_json,'$.Error') IS NOT NULL
                            OR json_extract(r.row_json,'$.Proposed.City') IS NULL
                            OR json_extract(r.row_json,'$.Proposed.Country') IS NULL))
                    ))
                ) ORDER BY r.row_id LIMIT 101 OFFSET $offset
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$filter", filter.ToString());
            command.Parameters.AddWithValue("$onlyChanges", onlyChanges);
            command.Parameters.AddWithValue("$offset", checked((page - 1) * 100));
            var result = new List<LocationPreviewRow>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(JsonSerializer.Deserialize<LocationPreviewRow>(reader.GetString(0))!);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationPreviewRow>> GetSelectedRowsAsync(Guid id, IReadOnlyCollection<Guid> assetIds)
    {
        var ids = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Distinct(assetIds));
        if (ids.Length is < 1 or > 1000)
        {
            throw new ArgumentException("Select between 1 and 1,000 assets.");
        }
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            var parameters = new List<string>();
            for (var index = 0; index < ids.Length; index++)
            {
                var name = "$asset" + index;
                parameters.Add(name);
                command.Parameters.AddWithValue(name, ids[index].ToString());
            }
            command.CommandText = $"SELECT row_json FROM preview_rows WHERE preview_id=$id AND asset_id IN ({string.Join(',', parameters)}) ORDER BY row_id";
            command.Parameters.AddWithValue("$id", id.ToString());
            var result = new List<LocationPreviewRow>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(JsonSerializer.Deserialize<LocationPreviewRow>(reader.GetString(0))!);
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }


    private static async Task RequireOwnerAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, Guid token, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM previews WHERE id=$id AND run_token=$token AND status='Running')";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$token", token.ToString());
        if ((long)(await command.ExecuteScalarAsync(ct))! != 1)
        {
            throw new InvalidOperationException("This scan is no longer owned by this page. Open the saved scan to check its status.");
        }
    }

    public async Task<LocationPreviewResumeState> GetResumeStateAsync(Guid id, CancellationToken ct = default)
    {
        var report = (await GetReportsAsync(id: id, ct: ct)).SingleOrDefault()
            ?? throw new ArgumentException("This saved scan no longer exists.");
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT row_json FROM preview_rows WHERE preview_id=$id ORDER BY row_id DESC LIMIT 1";
            command.Parameters.AddWithValue("$id", id.ToString());
            var json = (string?)await command.ExecuteScalarAsync(ct);
            var cursor = json is null ? null : LocationPreviewCursor.FromAsset(JsonSerializer.Deserialize<LocationPreviewRow>(json)!.Asset);
            return new(report, cursor);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LocationPreviewResumeState> ClaimResumeAsync(Guid id, Guid token, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE previews SET status='Running',run_token=$token,error=NULL,updated_at=$at
                WHERE id=$id AND run_token IS NULL
                    AND status IN ('Paused','More assets available','Cancelled','Interrupted','Failed')
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$token", token.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync(ct) != 1)
            {
                throw new InvalidOperationException("This scan is already running, complete, or deleted. Refresh its saved results before continuing.");
            }
        }
        finally
        {
            _gate.Release();
        }
        // The ownership claim prevents another run from appending while this snapshot is read.
        try
        {
            return await GetResumeStateAsync(id, ct);
        }
        catch
        {
            await _gate.WaitAsync();
            try
            {
                await using var connection = await OpenAsync(CancellationToken.None);
                await using var release = connection.CreateCommand();
                release.CommandText = "UPDATE previews SET status='Interrupted',run_token=NULL WHERE id=$id AND run_token=$token";
                release.Parameters.AddWithValue("$id", id.ToString());
                release.Parameters.AddWithValue("$token", token.ToString());
                await release.ExecuteNonQueryAsync();
            }
            finally
            {
                _gate.Release();
            }
            throw;
        }
    }

    public async Task FinishRunAsync(Guid id, Guid token, string status, string? error, LocationPreviewCheckpoint checkpoint)
    {
        await _gate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE previews SET status=$status,error=$error,checkpoint_json=$checkpoint,run_token=NULL,updated_at=$at
                WHERE id=$id AND run_token=$token
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$token", token.ToString());
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint));
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            if (await command.ExecuteNonQueryAsync() != 1)
            {
                throw new InvalidOperationException("The scan outcome could not be saved because this page no longer owns the scan.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LocationPreviewAsset>> LoadUnseenBatchAsync(Guid id, LocationPreviewCursor? cursor,
        Func<LocationPreviewCursor?, CancellationToken, Task<IReadOnlyList<LocationPreviewAsset>>> load, CancellationToken ct)
    {
        var result = new List<LocationPreviewAsset>();
        var seen = new HashSet<Guid>();
        while (result.Count < 101)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await load(cursor, ct);
            if (batch.Count == 0)
            {
                break;
            }
            var saved = await GetSavedIdsAsync(id, batch.Select(asset => asset.Id).ToArray(), ct);
            foreach (var asset in batch)
            {
                if (!saved.Contains(asset.Id) && seen.Add(asset.Id))
                {
                    result.Add(asset);
                    if (result.Count == 101)
                    {
                        return result;
                    }
                }
            }
            cursor = LocationPreviewCursor.FromAsset(batch[^1]);
            if (batch.Count < 101)
            {
                break;
            }
        }
        return result;
    }

    private async Task<HashSet<Guid>> GetSavedIdsAsync(Guid id, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            var parameters = ids.Select((_, index) => "$asset" + index).ToArray();
            command.CommandText = $"SELECT asset_id FROM preview_rows WHERE preview_id=$id AND asset_id IN ({string.Join(',', parameters)})";
            command.Parameters.AddWithValue("$id", id.ToString());
            for (var index = 0; index < ids.Count; index++)
            {
                command.Parameters.AddWithValue(parameters[index], ids[index].ToString());
            }
            var result = new HashSet<Guid>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(Guid.Parse(reader.GetString(0)));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ContainsAssetAsync(Guid id, Guid assetId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM preview_rows WHERE preview_id = $id AND asset_id = $asset)";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$asset", assetId.ToString());
            return (long)(await command.ExecuteScalarAsync(ct))! == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM previews WHERE id = $id AND status <> 'Running'";
            command.Parameters.AddWithValue("$id", id.ToString());
            return await command.ExecuteNonQueryAsync(ct) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Web.Services;

public sealed record SavedLocationPreview(
    Guid Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    LocationPreviewSelection Selection, ProcessingConfig Settings,
    string Status, string? Error, long CheckedCount, long DifferentCount);

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

    public async Task<Guid> CreateAsync(LocationPreviewSelection selection, ProcessingConfig settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            var id = Guid.NewGuid();
            command.CommandText = """
                INSERT INTO previews VALUES ($id, $at, $at, $selection, $settings, 'Running', NULL)
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$selection", JsonSerializer.Serialize(selection));
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings));
            await command.ExecuteNonQueryAsync(ct);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(Guid previewId, LocationPreviewRow row, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO preview_rows(preview_id, asset_id, row_json, has_change)
                VALUES ($id, $asset, $row, $changed)
                ON CONFLICT(preview_id, asset_id) DO NOTHING;
                UPDATE previews SET updated_at = $at WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", previewId.ToString());
            command.Parameters.AddWithValue("$asset", row.Asset.Id.ToString());
            command.Parameters.AddWithValue("$row", JsonSerializer.Serialize(row));
            command.Parameters.AddWithValue("$changed", row.HasChange);
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
            command.CommandText = "UPDATE previews SET status = $status, error = $error, updated_at = $at WHERE id = $id";
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private const string SummarySql = """
        SELECT p.id, p.created_at, p.updated_at, p.selection_json, p.settings_json, p.status, p.error,
            (SELECT count(*) FROM preview_rows r WHERE r.preview_id = p.id),
            (SELECT count(*) FROM preview_rows r WHERE r.preview_id = p.id AND r.has_change = 1)
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
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8)));
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

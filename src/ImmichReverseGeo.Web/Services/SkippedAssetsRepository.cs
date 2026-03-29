using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

/// <summary>
/// Tracks asset IDs that had no ADM0 boundary match, preventing endless reprocessing.
/// Stored in /data/skipped.db (SQLite).
/// </summary>
public class SkippedAssetsRepository(ILogger<SkippedAssetsRepository> logger, string dataDir)
{
    // DI constructor
    public SkippedAssetsRepository(ILogger<SkippedAssetsRepository> logger, StorageOptions dirs)
        : this(logger, dirs.DataDir) { }

    private readonly string _dbPath = Path.Combine(dataDir, "skipped.db");

    // Pooling=false: this is a singleton-owned file; pooling adds no benefit and forces
    // callers to ClearAllPools() before deleting the file on Windows (global side-effect).
    private string ConnectionString => $"Data Source={_dbPath};Pooling=false";

    public async Task InitialiseAsync()
    {
        logger.LogInformation("Initialising skipped assets database at {Path}", _dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS skipped_assets (
                asset_id  TEXT PRIMARY KEY,
                skipped_at TEXT NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task AddAsync(Guid assetId)
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO skipped_assets (asset_id, skipped_at)
            VALUES ($id, $at)
            """;
        cmd.Parameters.AddWithValue("$id", assetId.ToString());
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HashSet<Guid>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_id FROM skipped_assets";
        var result = new HashSet<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(Guid.Parse(reader.GetString(0)));
        }

        return result;
    }

    public async Task<long> GetCountAsync()
    {
        if (!File.Exists(_dbPath)) { return 0; }
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM skipped_assets";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task ClearAllAsync()
    {
        if (!File.Exists(_dbPath)) { return; }
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM skipped_assets";
        var rows = await cmd.ExecuteNonQueryAsync();
        logger.LogInformation("Cleared {Count} skipped assets", rows);
    }
}

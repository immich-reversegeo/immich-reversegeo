using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ImmichReverseGeo.Web.Services;

public class ImmichDbRepository(NpgsqlDataSource dataSource, ILogger<ImmichDbRepository> logger)
{
    /// <summary>
    /// Returns the next batch of assets with null city/country using keyset pagination.
    /// Caller passes AssetCursor.Initial for the first page.
    /// Returns empty list when no more assets remain.
    /// </summary>
    public async Task<List<AssetRecord>> GetUnprocessedBatchAsync(
        AssetCursor cursor, int batchSize, CancellationToken ct = default)
    {
        const string sql = """
            SELECT a.id, e.latitude, e.longitude, a."createdAt"
            FROM   asset a
            INNER JOIN asset_exif e ON e."assetId" = a.id
            WHERE  e.city      IS NULL
              AND  e.country   IS NULL
              AND  e.latitude  IS NOT NULL
              AND  e.longitude IS NOT NULL
              AND  a."deletedAt"  IS NULL
              AND  (a."createdAt", a.id) > (@lastCreatedAt, @lastId)
            ORDER BY a."createdAt", a.id
            LIMIT @batchSize
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("lastCreatedAt", cursor.CreatedAt.ToUniversalTime());
        cmd.Parameters.AddWithValue("lastId", cursor.Id);
        cmd.Parameters.AddWithValue("batchSize", batchSize);

        var results = new List<AssetRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AssetRecord(
                Id: reader.GetGuid(0),
                Latitude: reader.GetDouble(1),
                Longitude: reader.GetDouble(2),
                CreatedAt: reader.GetDateTime(3)));
        }

        return results;
    }

    /// <summary>
    /// Writes city/state/country back to the exif table for a single asset.
    /// Only called when GeoResult.HasMatch is true.
    /// </summary>
    public async Task WriteLocationAsync(Guid assetId, GeoResult geo, CancellationToken ct = default)
    {
        const string sql = """
                           UPDATE asset_exif
                           SET    city    = @city,
                                  state   = @state,
                                  country = @country
                           WHERE  "assetId" = @assetId
                           """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("city", (object?)geo.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", (object?)geo.State ?? DBNull.Value);
        cmd.Parameters.AddWithValue("country", (object?)geo.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("assetId", assetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Returns the count of assets still awaiting processing (for Dashboard display).
    /// </summary>
    public async Task<long> GetUnprocessedCountAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT COUNT(*) FROM asset a
            INNER JOIN asset_exif e ON e."assetId" = a.id
            WHERE e.city IS NULL AND e.country IS NULL
              AND e.latitude IS NOT NULL AND e.longitude IS NOT NULL
              AND a."deletedAt" IS NULL
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Clears city/state/country from all asset_exif rows that have location data.
    /// Used to reset Immich location data so processing can start from scratch.
    /// </summary>
    public async Task<long> ClearAllLocationDataAsync(CancellationToken ct = default)
    {
        const string sql = """
            UPDATE asset_exif
            SET city    = NULL,
                state   = NULL,
                country = NULL
            WHERE city IS NOT NULL
               OR state IS NOT NULL
               OR country IS NOT NULL
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = await dataSource.OpenConnectionAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DB connection test failed");
            return false;
        }
    }
}

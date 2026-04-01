using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

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

    public async Task<IReadOnlyList<Guid>> ClearLocationDataForAssetsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct = default)
    {
        if (assetIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        const string sql = """
            UPDATE asset_exif
            SET city    = NULL,
                state   = NULL,
                country = NULL
            WHERE "assetId" = ANY(@assetIds)
              AND (city IS NOT NULL
                   OR state IS NOT NULL
                   OR country IS NOT NULL)
            RETURNING "assetId"
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(
            "assetIds",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            assetIds.Distinct().ToArray());

        var clearedIds = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            clearedIds.Add(reader.GetGuid(0));
        }

        return clearedIds;
    }

    public async Task<IReadOnlyList<Guid>> ClearLocationDataByValueAsync(
        LocationResetScope scope,
        string value,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<Guid>();
        }

        var column = GetLocationColumn(scope);
        var sql = $"""
            UPDATE asset_exif e
            SET city    = NULL,
                state   = NULL,
                country = NULL
            FROM asset a
            WHERE e."assetId" = a.id
              AND a."deletedAt" IS NULL
              AND e.{column} = @value
              AND (e.city IS NOT NULL
                   OR e.state IS NOT NULL
                   OR e.country IS NOT NULL)
            RETURNING e."assetId"
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("value", value);

        var clearedIds = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            clearedIds.Add(reader.GetGuid(0));
        }

        return clearedIds;
    }

    public async Task<IReadOnlyList<LocationValueOption>> GetLocationValueOptionsAsync(
        LocationResetScope scope,
        CancellationToken ct = default)
    {
        var column = GetLocationColumn(scope);
        var sql = $"""
            SELECT e.{column} AS value,
                   COUNT(*)::bigint AS asset_count
            FROM asset_exif e
            INNER JOIN asset a ON a.id = e."assetId"
            WHERE a."deletedAt" IS NULL
              AND e.{column} IS NOT NULL
            GROUP BY e.{column}
            ORDER BY COUNT(*) DESC, e.{column}
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);

        var results = new List<LocationValueOption>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new LocationValueOption(
                reader.GetString(0),
                reader.GetInt64(1)));
        }

        return results;
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

    private static string GetLocationColumn(LocationResetScope scope) =>
        scope switch
        {
            LocationResetScope.City => "city",
            LocationResetScope.State => "state",
            LocationResetScope.Country => "country",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
        };
}

public enum LocationResetScope
{
    City,
    State,
    Country
}

public sealed record LocationValueOption(string Value, long AssetCount);

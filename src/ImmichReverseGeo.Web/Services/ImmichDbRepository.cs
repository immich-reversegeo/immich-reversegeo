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

public class ImmichDbRepository(NpgsqlDataSource dataSource, ILogger<ImmichDbRepository> logger) : ILocationChangeWriter
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

    /// <summary>Do not overwrite a correction applied after the processing batch was fetched.</summary>
    public async Task<bool> WriteUnprocessedLocationAsync(AssetRecord asset, GeoResult geo, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE asset_exif e SET city=@city,state=@state,country=@country
            FROM asset a WHERE e."assetId"=a.id AND a.id=@id AND a."deletedAt" IS NULL
                AND e.city IS NULL AND e.country IS NULL
                AND e.latitude=@latitude AND e.longitude=@longitude
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", asset.Id);
        cmd.Parameters.AddWithValue("latitude", asset.Latitude);
        cmd.Parameters.AddWithValue("longitude", asset.Longitude);
        cmd.Parameters.AddWithValue("city", NpgsqlDbType.Text, (object?)geo.City ?? DBNull.Value);
        cmd.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)geo.State ?? DBNull.Value);
        cmd.Parameters.AddWithValue("country", NpgsqlDbType.Text, (object?)geo.Country ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }


    /// <summary>Locks the asset and metadata row, replacing only names from an unchanged reviewed snapshot.</summary>
    public async Task<LocationWriteResult> TryReplaceLocationAsync(LocationPreviewAsset expected,
        GeoResult replacement, string? expectedRevision = null, CancellationToken ct = default)
    {
        if (expected.Latitude is null || expected.Longitude is null)
        {
            return LocationWriteResult.Skip("The reviewed asset has incomplete GPS coordinates.");
        }
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        await using var read = new NpgsqlCommand("""
            SELECT e.latitude,e.longitude,e.country,e.state,e.city,e.xmin::text,a."deletedAt"
            FROM asset a INNER JOIN asset_exif e ON e."assetId"=a.id
            WHERE a.id=@id FOR UPDATE OF a,e
            """, conn, transaction);
        read.Parameters.AddWithValue("id", expected.Id);
        string revision;
        await using (var reader = await read.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || !reader.IsDBNull(6))
            {
                return LocationWriteResult.Skip("The asset was deleted or its metadata row no longer exists.");
            }
            var current = new GeoResult(reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            revision = reader.GetString(5);
            if (reader.IsDBNull(0) || reader.IsDBNull(1)
                || reader.GetDouble(0) != expected.Latitude.Value || reader.GetDouble(1) != expected.Longitude.Value
                || current != expected.Existing || (expectedRevision is not null && expectedRevision != revision))
            {
                return LocationWriteResult.Skip("Coordinates, location names, or the metadata revision changed since review/apply. Run a new scan.");
            }
        }
        await using var write = new NpgsqlCommand("""
            UPDATE asset_exif SET country=@country,state=@state,city=@city
            WHERE "assetId"=@id RETURNING xmin::text
            """, conn, transaction);
        write.Parameters.AddWithValue("id", expected.Id);
        write.Parameters.AddWithValue("country", NpgsqlDbType.Text, (object?)replacement.Country ?? DBNull.Value);
        write.Parameters.AddWithValue("state", NpgsqlDbType.Text, (object?)replacement.State ?? DBNull.Value);
        write.Parameters.AddWithValue("city", NpgsqlDbType.Text, (object?)replacement.City ?? DBNull.Value);
        var appliedRevision = (string)(await write.ExecuteScalarAsync(ct))!;
        await transaction.CommitAsync(ct);
        return new(true, appliedRevision, null);
    }


    /// <summary>Returns the count of assets still awaiting processing.</summary>
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

    /// <summary>Count using the same scope as reset writes, including deleted rows for a full reset.</summary>
    public async Task<long> GetResetCountAsync(
        IReadOnlyCollection<Guid>? assetIds = null, LocationResetScope? scope = null,
        string? value = null, CancellationToken ct = default)
    {
        var predicate = assetIds is not null ? "e.\"assetId\" = ANY(@assetIds)"
            : scope is not null ? $"e.{GetLocationColumn(scope.Value)} = @value AND EXISTS (SELECT 1 FROM asset a WHERE a.id = e.\"assetId\" AND a.\"deletedAt\" IS NULL)"
            : "TRUE";
        var sql = $"SELECT count(*) FROM asset_exif e WHERE ({predicate}) AND (e.city IS NOT NULL OR e.state IS NOT NULL OR e.country IS NOT NULL)";
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (assetIds is not null)
        {
            cmd.Parameters.AddWithValue("assetIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid, assetIds.Distinct().ToArray());
        }
        else if (scope is not null)
        {
            cmd.Parameters.AddWithValue("value", NpgsqlDbType.Text, (object?)value ?? DBNull.Value);
        }
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
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

    private static string GetPreviewPredicate(LocationPreviewSelection selection) => selection.Scope switch
    {
        LocationPreviewScope.All => "TRUE",
        LocationPreviewScope.AssetIds => "a.id = ANY(@assetIds)",
        LocationPreviewScope.City => "e.city = @value",
        LocationPreviewScope.State => "e.state = @value",
        LocationPreviewScope.Country => "e.country = @value",
        _ => throw new ArgumentOutOfRangeException(nameof(selection))
    };

    private static void AddPreviewParameters(NpgsqlCommand cmd, LocationPreviewSelection selection)
    {
        if (selection.Scope == LocationPreviewScope.AssetIds)
        {
            cmd.Parameters.AddWithValue("assetIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid, selection.AssetIds);
        }
        else if (selection.Scope != LocationPreviewScope.All)
        {
            cmd.Parameters.AddWithValue("value", NpgsqlDbType.Text, (object?)selection.Value ?? DBNull.Value);
        }
    }

    public async Task<long> GetLocationPreviewCountAsync(
        LocationPreviewSelection selection, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT count(*)
            FROM asset a
            INNER JOIN asset_exif e ON e."assetId" = a.id
            WHERE a."deletedAt" IS NULL
              AND ({GetPreviewPredicate(selection)})
              AND e.latitude IS NOT NULL
              AND e.longitude IS NOT NULL
            """;
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        AddPreviewParameters(cmd, selection);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Reads up to 101 assets; the extra row indicates another 100-row preview page.</summary>
    public async Task<IReadOnlyList<LocationPreviewAsset>> GetLocationPreviewBatchAsync(
        LocationPreviewSelection selection, LocationPreviewCursor? after, CancellationToken ct = default)
    {
        var predicate = GetPreviewPredicate(selection);
        const string latitudeTile = "floor(e.latitude * 4)";
        const string longitudeTile = "floor(e.longitude * 4)";
        var (seek, order) = selection.Order switch
        {
            LocationPreviewOrder.AssetId => ("a.id > @afterId", "a.id"),
            LocationPreviewOrder.NearbyCoordinates => (
                $"({latitudeTile}, {longitudeTile}, a.id) > (@afterLatitudeTile, @afterLongitudeTile, @afterId)",
                $"{latitudeTile}, {longitudeTile}, a.id"),
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
        var sql = $"""
            SELECT a.id, e.latitude, e.longitude, e.country, e.state, e.city
            FROM asset a
            INNER JOIN asset_exif e ON e."assetId" = a.id
            WHERE a."deletedAt" IS NULL
              AND ({predicate})
              AND e.latitude IS NOT NULL
              AND e.longitude IS NOT NULL
              AND (@afterId IS NULL OR ({seek}))
            ORDER BY {order}
            LIMIT 101
            """;

        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("afterId", NpgsqlDbType.Uuid, (object?)after?.Id ?? DBNull.Value);
        if (selection.Order == LocationPreviewOrder.NearbyCoordinates)
        {
            cmd.Parameters.AddWithValue("afterLatitudeTile", NpgsqlDbType.Double, (object?)after?.LatitudeTile ?? DBNull.Value);
            cmd.Parameters.AddWithValue("afterLongitudeTile", NpgsqlDbType.Double, (object?)after?.LongitudeTile ?? DBNull.Value);
        }
        AddPreviewParameters(cmd, selection);

        var results = new List<LocationPreviewAsset>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new LocationPreviewAsset(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetDouble(2),
                new GeoResult(
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5))));
        }

        return results;
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

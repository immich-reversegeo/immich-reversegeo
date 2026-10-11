using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Services;

internal sealed class CountryPolygonDiskIndex(string dataDir, MemoryResourcePolicy policy)
{
    private readonly SemaphoreSlim _prepareLock = new(1, 1);
    private readonly string _path = Path.Combine(dataDir, "country-polygon-parts.db");
    private SourceStamp? _readySource;
    private IndexInfo? _ready;

    internal sealed record IndexInfo(string Path, bool HasAlpha3);
    private sealed record SourceStamp(string Path, long Length, long ModifiedTicks);

    public async Task<IndexInfo> EnsureAsync(string sourcePath, CancellationToken ct)
    {
        await _prepareLock.WaitAsync(ct);
        try
        {
            var source = new FileInfo(sourcePath);
            var stamp = new SourceStamp(source.FullName, source.Length, source.LastWriteTimeUtc.Ticks);
            if (_readySource == stamp && _ready is not null && File.Exists(_path))
            {
                return _ready;
            }
            Directory.CreateDirectory(dataDir);
            await using var fileLease = await AcquireLeaseAsync(ct);
            string hash;
            await using (var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            }
            var hasAlpha3 = ReadHasAlpha3(sourcePath);
            if (!HasMatchingIndex(hash))
            {
                using var work = await policy.EnterAsync(ct);
                await Task.Run(() => Build(sourcePath, hash, hasAlpha3, stamp, ct), ct);
            }
            _readySource = stamp;
            _ready = new IndexInfo(_path, hasAlpha3);
            return _ready;
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 300)
            {
                await Task.Delay(100, ct);
            }
        }
    }

    private bool HasMatchingIndex(string hash)
    {
        if (!File.Exists(_path))
        {
            return false;
        }
        try
        {
            using var connection = OpenReadOnly(_path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM _meta WHERE key='sourceSha256'";
            if (!string.Equals(command.ExecuteScalar()?.ToString(), hash, StringComparison.Ordinal))
            {
                return false;
            }
            command.CommandText = "SELECT value FROM _meta WHERE key='formatVersion'";
            if (command.ExecuteScalar()?.ToString() != "1")
            {
                return false;
            }
            command.CommandText = "PRAGMA quick_check";
            if (command.ExecuteScalar()?.ToString() != "ok")
            {
                return false;
            }
            command.CommandText = "SELECT rtreecheck('part_bounds')";
            return command.ExecuteScalar()?.ToString() == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void Build(string sourcePath, string hash, bool hasAlpha3, SourceStamp stamp, CancellationToken ct)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = OpenReadOnly(sourcePath))
            using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
            {
                target.Open();
                using var transaction = target.BeginTransaction();
                using var schema = target.CreateCommand();
                schema.Transaction = transaction;
                schema.CommandText = """
                    CREATE TABLE country (
                        source_rowid INTEGER PRIMARY KEY, id TEXT, name TEXT, subtype TEXT, class_name TEXT,
                        admin_level INTEGER, country TEXT, alpha3 TEXT, is_land INTEGER, is_territorial INTEGER,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    CREATE TABLE part (id INTEGER PRIMARY KEY, source_rowid INTEGER, byte_offset INTEGER, byte_length INTEGER);
                    CREATE VIRTUAL TABLE part_bounds USING rtree(id, xmin, xmax, ymin, ymax);
                    CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT);
                    INSERT INTO _meta VALUES ('sourceSha256', $hash);
                    INSERT INTO _meta VALUES ('formatVersion', '1');
                    """;
                schema.Parameters.AddWithValue("$hash", hash);
                schema.ExecuteNonQuery();
                var admin = HasColumn(source, "admin_level") ? "admin_level" : "NULL";
                using var select = source.CreateCommand();
                select.CommandText = $"""
                    SELECT rowid, id, name, subtype, class_name, {admin}, country, {(hasAlpha3 ? "alpha3" : "NULL")},
                           is_land, is_territorial, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax, geom_wkb
                    FROM division_area ORDER BY rowid
                    """;
                using var reader = select.ExecuteReader();
                using var countryInsert = target.CreateCommand();
                countryInsert.Transaction = transaction;
                countryInsert.CommandText = "INSERT INTO country VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12,$p13)";
                for (var column = 0; column < 14; column++)
                {
                    countryInsert.Parameters.AddWithValue("$p" + column, DBNull.Value);
                }
                using var partInsert = target.CreateCommand();
                partInsert.Transaction = transaction;
                partInsert.CommandText = """
                    INSERT INTO part VALUES ($id,$row,$offset,$length);
                    INSERT INTO part_bounds VALUES ($id,$xmin,$xmax,$ymin,$ymax);
                    """;
                foreach (var parameter in new[] { "$id", "$row", "$offset", "$length", "$xmin", "$xmax", "$ymin", "$ymax" })
                {
                    partInsert.Parameters.AddWithValue(parameter, 0);
                }
                long partId = 0;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.IsDBNull(14) || reader.IsDBNull(10) || reader.IsDBNull(11) || reader.IsDBNull(12) || reader.IsDBNull(13))
                    {
                        continue;
                    }
                    for (var column = 0; column < 14; column++)
                    {
                        countryInsert.Parameters[column].Value = reader.GetValue(column);
                    }
                    countryInsert.ExecuteNonQuery();
                    var bounds = new Envelope(reader.GetDouble(10), reader.GetDouble(12), reader.GetDouble(11), reader.GetDouble(13));
                    var wkb = OvertureDataAccess.ReadBlobValue(reader.GetValue(14));
                    foreach (var slice in CountryPolygonSlices.Read(wkb, bounds, ct))
                    {
                        ct.ThrowIfCancellationRequested();
                        partInsert.Parameters["$id"].Value = ++partId;
                        partInsert.Parameters["$row"].Value = reader.GetInt64(0);
                        partInsert.Parameters["$offset"].Value = slice.Offset;
                        partInsert.Parameters["$length"].Value = slice.Length;
                        partInsert.Parameters["$xmin"].Value = slice.Bounds.MinX;
                        partInsert.Parameters["$xmax"].Value = slice.Bounds.MaxX;
                        partInsert.Parameters["$ymin"].Value = slice.Bounds.MinY;
                        partInsert.Parameters["$ymax"].Value = slice.Bounds.MaxY;
                        partInsert.ExecuteNonQuery();
                    }
                }
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            var after = new FileInfo(sourcePath);
            if (after.Length != stamp.Length || after.LastWriteTimeUtc.Ticks != stamp.ModifiedTicks)
            {
                throw new IOException("Bundled country data changed while preparing its disk index.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Do not hide a cancelled/failed build if its temporary file cannot be removed.
            }
        }
    }

    private static bool ReadHasAlpha3(string sourcePath)
    {
        using var source = OpenReadOnly(sourcePath);
        return HasColumn(source, "alpha3");
    }

    private static bool HasColumn(SqliteConnection source, string name)
    {
        using var command = source.CreateCommand();
        command.CommandText = "PRAGMA table_info(division_area)";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1) == name)
            {
                return true;
            }
        }
        return false;
    }

    internal static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

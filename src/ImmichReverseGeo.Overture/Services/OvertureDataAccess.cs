using System;
using System.IO;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureDataAccess
{
    private static readonly WKBReader WkbReader = new();

    public static void LoadHttpfs(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSTALL httpfs; LOAD httpfs;";
        cmd.ExecuteNonQuery();
    }

    public static void LoadAzureAndSpatial(DuckDBConnection conn)
    {
        using var cmd = conn.CreateCommand();
        var commandText = """
            INSTALL azure;
            LOAD azure;
            """;

        if (OperatingSystem.IsLinux())
        {
            // Linux containers have been more reliable with DuckDB's curl transport.
            commandText += """
                SET azure_transport_option_type='curl';
                """;
        }

        commandText += """
            INSTALL spatial;
            LOAD spatial;
            """;

        cmd.CommandText = commandText;
        cmd.ExecuteNonQuery();
    }

    public static string? ReadNullableString(DuckDBDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static bool ReadSqliteBool(SqliteDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture) != 0;

    public static byte[] ReadBlobValue(object value)
    {
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

        throw new InvalidCastException($"Unsupported blob value type '{value.GetType().FullName}'.");
    }

    public static bool TryGeometryContains(byte[] wkb, Point point)
    {
        try
        {
            var geometry = WkbReader.Read(wkb);
            return geometry.Covers(point) || geometry.Distance(point) <= 0.00015;
        }
        catch
        {
            return false;
        }
    }
}

using System;
using System.IO;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public static class OvertureDataAccess
{
    /// <summary>
    /// Tolerance, in degrees (~16m), for treating a point just outside a boundary as inside it.
    /// </summary>
    public const double BoundaryToleranceDegrees = 0.00015;

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

    public static (bool HasBoundary, bool ContainsPoint) ReadAirportBoundary(byte[]? wkb, Point point)
    {
        if (wkb is null)
        {
            return (false, false);
        }

        try
        {
            var geometry = WkbReader.Read(wkb);
            if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid)
            {
                return (false, false);
            }

            return (true, geometry.Covers(point));
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException)
        {
            return (false, false);
        }
    }

    public static bool TryGeometryContains(byte[] wkb, Point point)
    {
        try
        {
            var geometry = WkbReader.Read(wkb);
            if (geometry.Covers(point))
            {
                return true;
            }

            // A rectangle argument routes through NTS's short-circuiting RectangleIntersects
            // instead of DistanceOp, which computes an exact minimum distance and so copies
            // every boundary ring into a fresh Coordinate[] before it can reject the point.
            return geometry.Intersects(BuildToleranceRectangle(point));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds a small axis-aligned rectangle around <paramref name="point"/> for tolerance tests.
    /// A box tolerance is marginally more permissive than a radial one (~7m at the diagonal of a
    /// ~16m box), which is immaterial for a boundary-snapping heuristic.
    /// </summary>
    private static Geometry BuildToleranceRectangle(Point point) => point.Factory.ToGeometry(new Envelope(
        point.X - BoundaryToleranceDegrees,
        point.X + BoundaryToleranceDegrees,
        point.Y - BoundaryToleranceDegrees,
        point.Y + BoundaryToleranceDegrees));
}

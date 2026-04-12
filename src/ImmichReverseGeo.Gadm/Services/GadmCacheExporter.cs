using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Gadm.Services;

public static class GadmCacheExporter
{
    private static readonly WKBReader WkbReader = new();
    public static long ExportGeoPackageToSqlite(string geoPackagePath, string outputPath, string iso3)
    {
        var layers = LoadLayerDefinitions(geoPackagePath);

        using var output = new SqliteConnection($"Data Source={outputPath};Pooling=false");
        output.Open();
        CreateOutputSchema(output);
        WriteMeta(output, iso3);

        using var transaction = output.BeginTransaction();
        using var insert = output.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO gadm_area (
                id,
                name,
                english_type,
                local_type,
                admin_level,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            ) VALUES (
                $id,
                $name,
                $englishType,
                $localType,
                $adminLevel,
                $geom,
                $xmin,
                $ymin,
                $xmax,
                $ymax
            )
            """;

        var pId = insert.Parameters.Add("$id", SqliteType.Text);
        var pName = insert.Parameters.Add("$name", SqliteType.Text);
        var pEnglishType = insert.Parameters.Add("$englishType", SqliteType.Text);
        var pLocalType = insert.Parameters.Add("$localType", SqliteType.Text);
        var pAdminLevel = insert.Parameters.Add("$adminLevel", SqliteType.Integer);
        var pGeom = insert.Parameters.Add("$geom", SqliteType.Blob);
        var pXMin = insert.Parameters.Add("$xmin", SqliteType.Real);
        var pYMin = insert.Parameters.Add("$ymin", SqliteType.Real);
        var pXMax = insert.Parameters.Add("$xmax", SqliteType.Real);
        var pYMax = insert.Parameters.Add("$ymax", SqliteType.Real);

        long rows = 0;
        using var source = new SqliteConnection($"Data Source={geoPackagePath};Pooling=false");
        source.Open();

        foreach (var layer in layers)
        {
            using var cmd = source.CreateCommand();
            cmd.CommandText = $"""
                SELECT
                    {QuoteIdentifier(layer.IdColumn)} AS id,
                    {QuoteIdentifier(layer.NameColumn)} AS name,
                    {QuoteColumnOrNull(layer.EnglishTypeColumn)} AS english_type,
                    {QuoteColumnOrNull(layer.LocalTypeColumn)} AS local_type,
                    {QuoteIdentifier(layer.GeometryColumn)} AS geom
                FROM {QuoteIdentifier(layer.TableName)}
                WHERE {QuoteIdentifier(layer.GeometryColumn)} IS NOT NULL
                  AND {QuoteIdentifier(layer.NameColumn)} IS NOT NULL
                """;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rawGeometry = (byte[])reader["geom"];
                var parsed = GadmDataAccess.ReadGeoPackageGeometry(rawGeometry);
                var bbox = GetBoundingBox(parsed.Wkb, parsed.XMin, parsed.YMin, parsed.XMax, parsed.YMax);

                pId.Value = reader["id"].ToString() ?? string.Empty;
                pName.Value = reader["name"].ToString() ?? string.Empty;
                pEnglishType.Value = reader["english_type"] is DBNull ? DBNull.Value : reader["english_type"];
                pLocalType.Value = reader["local_type"] is DBNull ? DBNull.Value : reader["local_type"];
                pAdminLevel.Value = layer.AdminLevel;
                pGeom.Value = parsed.Wkb;
                pXMin.Value = bbox.XMin;
                pYMin.Value = bbox.YMin;
                pXMax.Value = bbox.XMax;
                pYMax.Value = bbox.YMax;
                insert.ExecuteNonQuery();
                rows++;
            }
        }

        transaction.Commit();
        output.Close();
        SqliteConnection.ClearPool(output);
        return rows;
    }

    private static (double XMin, double YMin, double XMax, double YMax) GetBoundingBox(
        byte[] wkb,
        double? xmin,
        double? ymin,
        double? xmax,
        double? ymax)
    {
        if (xmin.HasValue && ymin.HasValue && xmax.HasValue && ymax.HasValue)
        {
            return (xmin.Value, ymin.Value, xmax.Value, ymax.Value);
        }

        var geometry = WkbReader.Read(wkb);
        var envelope = geometry.EnvelopeInternal;
        return (envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
    }

    private static List<GadmLayerDefinition> LoadLayerDefinitions(string geoPackagePath)
    {
        using var conn = new SqliteConnection($"Data Source={geoPackagePath};Pooling=false");
        conn.Open();

        var geometryColumns = LoadGeometryColumns(conn);
        var layers = new List<GadmLayerDefinition>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT table_name FROM gpkg_contents WHERE data_type = 'features' ORDER BY table_name";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var tableName = reader.GetString(0);
            if (!TryGetAdminLevel(tableName, out var adminLevel))
            {
                continue;
            }

            if (!geometryColumns.TryGetValue(tableName, out var geometryColumn))
            {
                continue;
            }

            var columns = LoadColumns(conn, tableName);
            var idColumn = $"GID_{adminLevel}";
            var nameColumn = $"NAME_{adminLevel}";
            var englishTypeColumn = $"ENGTYPE_{adminLevel}";
            var localTypeColumn = $"TYPE_{adminLevel}";

            if (!columns.Contains(idColumn))
            {
                continue;
            }

            var resolvedNameColumn = columns.Contains(nameColumn)
                ? nameColumn
                : adminLevel == 0 && columns.Contains("COUNTRY")
                    ? "COUNTRY"
                    : null;

            if (resolvedNameColumn is null)
            {
                continue;
            }

            layers.Add(new GadmLayerDefinition(
                tableName,
                geometryColumn,
                adminLevel,
                idColumn,
                resolvedNameColumn,
                columns.Contains(englishTypeColumn) ? englishTypeColumn : null,
                columns.Contains(localTypeColumn) ? localTypeColumn : null));
        }

        return layers
            .OrderBy(layer => layer.AdminLevel)
            .ToList();
    }

    private static Dictionary<string, string> LoadGeometryColumns(SqliteConnection conn)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT table_name, column_name FROM gpkg_geometry_columns";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static HashSet<string> LoadColumns(SqliteConnection conn, string tableName)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdentifier(tableName)})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                result.Add(reader.GetString(1));
            }
        }

        return result;
    }

    private static void CreateOutputSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE gadm_area (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                english_type TEXT NULL,
                local_type TEXT NULL,
                admin_level INTEGER NOT NULL,
                geom_wkb BLOB NOT NULL,
                bbox_xmin REAL NOT NULL,
                bbox_ymin REAL NOT NULL,
                bbox_xmax REAL NOT NULL,
                bbox_ymax REAL NOT NULL
            );
            CREATE TABLE _meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE INDEX idx_gadm_area_admin_level ON gadm_area (admin_level);
            CREATE INDEX idx_gadm_area_bbox_x ON gadm_area (bbox_xmin, bbox_xmax);
            CREATE INDEX idx_gadm_area_bbox_y ON gadm_area (bbox_ymin, bbox_ymax);
            """;
        cmd.ExecuteNonQuery();
    }

    private static void WriteMeta(SqliteConnection conn, string iso3)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO _meta (key, value) VALUES ('downloadedAt', $downloadedAt);
            INSERT INTO _meta (key, value) VALUES ('version', $version);
            INSERT INTO _meta (key, value) VALUES ('iso3', $iso3);
            """;
        cmd.Parameters.AddWithValue("$downloadedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$version", GadmDivisionsLogic.DatasetVersion);
        cmd.Parameters.AddWithValue("$iso3", iso3.ToUpperInvariant());
        cmd.ExecuteNonQuery();
    }

    private static string QuoteColumnOrNull(string? columnName)
    {
        return columnName is null ? "NULL" : QuoteIdentifier(columnName);
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool TryGetAdminLevel(string tableName, out int adminLevel)
    {
        adminLevel = 0;
        var underscore = tableName.LastIndexOf('_');
        if (underscore < 0 || underscore == tableName.Length - 1)
        {
            return false;
        }

        return int.TryParse(
            tableName.AsSpan(underscore + 1),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out adminLevel);
    }

    private sealed record GadmLayerDefinition(
        string TableName,
        string GeometryColumn,
        int AdminLevel,
        string IdColumn,
        string NameColumn,
        string? EnglishTypeColumn,
        string? LocalTypeColumn);
}

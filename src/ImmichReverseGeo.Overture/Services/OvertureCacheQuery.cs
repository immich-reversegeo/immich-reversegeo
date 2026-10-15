using System;
using System.Globalization;
using System.IO;
using System.Threading;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Overture.Services;

/// <summary>Stages a single country's remote inputs on disk, then builds hierarchies locally.</summary>
public sealed class OvertureCacheQuery : IDisposable
{
    private readonly DuckDBConnection _connection;
    private readonly ILogger _logger;
    private readonly Action<int, string, DuckDbQueryProgressSnapshot?> _report;
    private readonly string _directory;
    private readonly string _names;
    private readonly string _areas;
    private readonly string _hierarchies;

    public OvertureCacheQuery(DuckDBConnection connection, string dataDir, ILogger logger,
        Action<int, string, DuckDbQueryProgressSnapshot?> report)
    {
        _connection = connection;
        _logger = logger;
        _report = report;
        _directory = Path.GetFullPath(Path.Combine(dataDir, "duckdb-cache-work", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_directory);
        _names = Path.Combine(_directory, "names.parquet");
        _areas = Path.Combine(_directory, "areas.parquet");
        _hierarchies = Path.Combine(_directory, "hierarchies.parquet");
    }

    public long Prepare(string releaseUrl, string alpha2, CancellationToken ct = default)
    {
        var divisionUrl = releaseUrl.Replace("type=division_area/", "type=division/", StringComparison.Ordinal);
        var filter = OvertureCountrySourceCodes.BuildCountryFilter(alpha2);
        Copy(1, "Reading country divisions and names", $"""
            SELECT id, hierarchies, country,
                struct_pack("primary" := names.primary, common := names.common) AS names,
                COALESCE(NULLIF(trim(names.common['en']), ''), NULLIF(trim(names.primary), '')) AS name
            FROM read_parquet({Literal(divisionUrl)}, hive_partitioning = 1)
            WHERE {filter}
            """, _names, ct);
        Copy(2, "Reading country boundaries", $"""
            {BuildAreaProjection($"read_parquet({Literal(releaseUrl)}, hive_partitioning = 1)")}
            WHERE {filter}
            """, _areas, ct);
        Copy(3, "Building parent hierarchies", $"""
            SELECT d.id, (
                SELECT to_json(list(struct_pack(
                    division_id := entry.division_id,
                    subtype := entry.subtype,
                    name := COALESCE(parent.name, entry.name),
                    names := parent.names,
                    country := parent.country
                ) ORDER BY position))
                FROM UNNEST(d.hierarchies[1]) WITH ORDINALITY AS ancestors(entry, position)
                LEFT JOIN read_parquet({Literal(_names)}) parent ON parent.id = entry.division_id
            ) AS hierarchy_json
            FROM read_parquet({Literal(_names)}) d
            """, _hierarchies, ct);
        ct.ThrowIfCancellationRequested();
        using var count = _connection.CreateCommand();
        count.CommandText = $"SELECT count(*) FROM read_parquet({Literal(_areas)})";
        return Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public string BuildResultQuery() => $"""
        SELECT a.* EXCLUDE (names_json), h.hierarchy_json, a.names_json
        FROM read_parquet({Literal(_areas)}) a
        LEFT JOIN read_parquet({Literal(_hierarchies)}) h ON a.division_id = h.id
        """;

    public static string BuildAreaProjection(string source, bool includeHierarchy = false) => $"""
        SELECT id, COALESCE(names.common['en'], names.primary, id) AS name,
            subtype, class, admin_level, country,
            COALESCE(is_land, true) AS is_land, COALESCE(is_territorial, false) AS is_territorial,
            ST_AsWKB(geometry) AS geom_wkb,
            bbox.xmin AS bbox_xmin, bbox.ymin AS bbox_ymin,
            bbox.xmax AS bbox_xmax, bbox.ymax AS bbox_ymax, division_id{(includeHierarchy ? ", hierarchy_json" : "")},
            to_json(struct_pack("primary" := names.primary, common := names.common)) AS names_json
        FROM {source}
        """;

    private void Copy(int phase, string stage, string select, string output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _report(phase, stage, null);
        using var command = _connection.CreateCommand();
        var sql = $"COPY ({select}) TO {Literal(output)} (FORMAT PARQUET, ROW_GROUP_SIZE 2048)";
        command.CommandText = sql;
        using var cancellation = ct.Register(command.Cancel);
        using var monitor = new DuckDbQueryProgressMonitor(_connection, snapshot => _report(phase, stage, snapshot), _logger);
        ct.ThrowIfCancellationRequested();
        command.ExecuteNonQuery();
        ct.ThrowIfCancellationRequested();
    }

    private static string Literal(string value) => "'" + value.Replace("\\", "/").Replace("'", "''") + "'";

    public void Dispose()
    {
        foreach (var path in new[] { _names, _areas, _hierarchies })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Unable to remove cache staging file {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unable to remove cache staging file {Path}", path);
            }
        }
        try
        {
            Directory.Delete(_directory);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Unable to remove cache staging directory {Path}", _directory);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unable to remove cache staging directory {Path}", _directory);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Gadm.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Gadm.Services;

public class GadmDivisionsService
{
    private static readonly WKBReader WkbReader = new();
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly ILogger<GadmDivisionsService> _logger;
    private readonly string _dataDir;

    public GadmDivisionsService(ILogger<GadmDivisionsService> logger, string dataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
    }

    public async Task<GadmAdministrativeResult?> ResolveAdministrativeGeoAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct = default)
    {
        var diagnostics = await FindContainingDivisionAreasAsync(lat, lon, iso3, ct);
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            return null;
        }

        return new GadmAdministrativeResult(
            GadmDivisionsLogic.SelectStateName(diagnostics.Candidates),
            GadmDivisionsLogic.SelectCityName(diagnostics.Candidates));
    }

    public Task<GadmDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct = default)
    {
        try
        {
            var dbPath = Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
            if (!File.Exists(dbPath))
            {
                return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion));
            }

            return Task.FromResult(QueryDivisionAreasFromSqlite(dbPath, lat, lon));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "GADM division lookup failed at ({Lat:F4}, {Lon:F4}) for {ISO3}: {Message}",
                lat,
                lon,
                iso3,
                ex.Message);
            return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion, ex.Message));
        }
    }

    public Task<GadmDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        IEnumerable<string> iso3Codes,
        CancellationToken ct = default)
    {
        try
        {
            var candidates = new List<GadmDivisionCandidateDiagnostic>();
            GadmDivisionResult? best = null;
            string? version = null;

            foreach (var iso3 in iso3Codes)
            {
                if (string.IsNullOrWhiteSpace(iso3))
                {
                    continue;
                }

                var dbPath = Path.Combine(_dataDir, "gadm-divisions", $"{iso3}.db");
                if (!File.Exists(dbPath))
                {
                    continue;
                }

                var partial = QueryDivisionAreasFromSqlite(dbPath, lat, lon);
                version ??= partial.Version;

                foreach (var candidate in partial.Candidates)
                {
                    candidates.Add(candidate);
                }

                if (partial.BestMatch is not null
                    && (best is null || GadmDivisionsLogic.ShouldPreferDivisionCandidate(partial.BestMatch, best)))
                {
                    best = partial.BestMatch;
                }
            }

            return Task.FromResult(new GadmDivisionLookupDiagnostics(best, candidates, version ?? GadmDivisionsLogic.DatasetVersion));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "GADM division multi-cache lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return Task.FromResult(new GadmDivisionLookupDiagnostics(null, [], GadmDivisionsLogic.DatasetVersion, ex.Message));
        }
    }

    private static GadmDivisionLookupDiagnostics QueryDivisionAreasFromSqlite(
        string dbPath,
        double lat,
        double lon)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
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
            FROM gadm_area
            WHERE bbox_xmax >= $lon
              AND bbox_xmin <= $lon
              AND bbox_ymax >= $lat
              AND bbox_ymin <= $lat
            """;
        cmd.Parameters.AddWithValue("$lon", lon);
        cmd.Parameters.AddWithValue("$lat", lat);

        using var reader = cmd.ExecuteReader();
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<GadmDivisionCandidateDiagnostic>();
        GadmDivisionResult? best = null;

        while (reader.Read())
        {
            var bboxContains = lon >= reader.GetDouble(6)
                               && lon <= reader.GetDouble(8)
                               && lat >= reader.GetDouble(7)
                               && lat <= reader.GetDouble(9);
            var geometryContains = bboxContains && GeometryContains((byte[])reader["geom_wkb"], point);
            var bboxArea = Math.Abs((reader.GetDouble(8) - reader.GetDouble(6)) * (reader.GetDouble(9) - reader.GetDouble(7)));

            var candidate = new GadmDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                bboxContains,
                geometryContains,
                bboxArea);

            var selected = false;
            var decision = "considered: weaker than current GADM best";
            if (best is null || GadmDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first containing GADM area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? "selected: GADM geometry containment outranked previous best"
                        : candidate.AdminLevel > best.AdminLevel
                            ? "selected: deeper GADM admin level outranked previous best"
                            : "selected: tighter GADM bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new GadmDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.EnglishType,
                candidate.LocalType,
                candidate.AdminLevel,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision));
        }

        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key = 'version'";
        var version = meta.ExecuteScalar()?.ToString() ?? GadmDivisionsLogic.DatasetVersion;

        return new GadmDivisionLookupDiagnostics(best, candidates, version);
    }

    private static bool GeometryContains(byte[] wkb, Point point)
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

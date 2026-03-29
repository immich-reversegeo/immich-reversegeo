using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Services;

public class OverturePlacesService
{
    private readonly ILogger<OverturePlacesService> _logger;
    private readonly string _dataDir;
    private readonly string _bundledDataDir;
    private readonly object _releaseLock = new();
    private string? _cachedRelease;
    private DateTime _cachedReleaseFetchedUtc;

    public OverturePlacesService(ILogger<OverturePlacesService> logger, string dataDir, string bundledDataDir)
    {
        _logger = logger;
        _dataDir = dataDir;
        _bundledDataDir = bundledDataDir;
    }

    public async Task<OvertureLookupDiagnostics> FindNearestPlaceWithDiagnosticsAsync(
        double lat,
        double lon,
        string? alpha2,
        CancellationToken ct = default)
    {
        try
        {
            var release = await GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => QueryOverture(lat, lon, alpha2, release), ct);
            return result with { Release = release, CountryFilter = alpha2 };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Overture lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return new OvertureLookupDiagnostics(null, [], null, alpha2, ex.Message);
        }
    }

    public async Task<OvertureInfrastructureLookupDiagnostics> FindNearestInfrastructureWithDiagnosticsAsync(
        double lat,
        double lon,
        string? iso3,
        CancellationToken ct = default)
    {
        try
        {
            var cached = QueryBundledInfrastructure(lat, lon);
            if (cached is not null)
            {
                return cached;
            }

            var release = await GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => QueryInfrastructure(lat, lon, release), ct);
            return result with { Release = release };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Overture infrastructure lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return new OvertureInfrastructureLookupDiagnostics(null, [], null, ex.Message);
        }
    }

    public async Task<string> GetLatestReleaseForOvertureAsync(CancellationToken ct = default)
    {
        lock (_releaseLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedRelease)
                && DateTime.UtcNow - _cachedReleaseFetchedUtc < TimeSpan.FromHours(12))
            {
                return _cachedRelease;
            }
        }

        var release = await Task.Run(GetLatestReleaseViaDuckDb, ct);

        lock (_releaseLock)
        {
            _cachedRelease = release;
            _cachedReleaseFetchedUtc = DateTime.UtcNow;
        }

        return release;
    }

    private string GetLatestReleaseViaDuckDb()
    {
        try
        {
            using var conn = new DuckDBConnection("Data Source=:memory:");
            conn.Open();
            OvertureDataAccess.LoadHttpfs(conn);

            using var query = conn.CreateCommand();
            query.CommandText = $"SELECT latest FROM '{OverturePlacesLogic.LatestCatalogUrl}'";
            var value = query.ExecuteScalar()?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Overture latest-release lookup via DuckDB failed; falling back to documented release {Release}: {Message}",
                OverturePlacesLogic.DocumentedFallbackRelease,
                ex.Message);
        }

        return OverturePlacesLogic.DocumentedFallbackRelease;
    }

    private OvertureLookupDiagnostics QueryOverture(double lat, double lon, string? alpha2, string release)
    {
        var latBuffer = OverturePlacesLogic.SearchRadiusMetres / 111_320d;
        var lonBuffer = latBuffer / Math.Max(0.1, Math.Cos(lat * Math.PI / 180d));
        var minLat = lat - latBuffer;
        var maxLat = lat + latBuffer;
        var minLon = lon - lonBuffer;
        var maxLon = lon + lonBuffer;
        var releaseUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture, OverturePlacesLogic.PlacesReleaseUrlTemplate, release);

        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        OvertureDataAccess.LoadAzureAndSpatial(conn);

        using var query = conn.CreateCommand();
        query.CommandText = OverturePlacesLogic.BuildQuery(lat, lon, minLat, maxLat, minLon, maxLon, alpha2, releaseUrl);

        var candidates = new List<OvertureCandidateDiagnostic>();
        OverturePlaceResult? best = null;

        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var candidateLat = reader.GetDouble(5);
            var candidateLon = reader.GetDouble(6);
            var distance = OverturePlacesLogic.HaversineMetres(lat, lon, candidateLat, candidateLon);
            if (distance > OverturePlacesLogic.SearchRadiusMetres)
            {
                continue;
            }

            var candidate = new OverturePlaceResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? 0d : reader.GetDouble(4),
                OvertureDataAccess.ReadNullableString(reader, 7),
                distance,
                !reader.IsDBNull(8) && reader.GetBoolean(8),
                OverturePlacesLogic.ParseSources(OvertureDataAccess.ReadNullableString(reader, 9)));

            if (!OverturePlacesLogic.IsInterestingPhotoPlace(candidate))
            {
                continue;
            }

            var selected = false;
            var decision = "considered: weaker than current best";
            if (best is null || OverturePlacesLogic.ShouldPreferCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first live Overture candidate"
                    : "selected: outranked previous live Overture best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.Category,
                candidate.BasicCategory,
                candidate.Confidence,
                candidate.OperatingStatus,
                candidate.DistanceMetres,
                candidate.BoundingBoxContainsPoint,
                candidate.Sources,
                selected,
                decision));
        }

        return new OvertureLookupDiagnostics(best, candidates, release, alpha2);
    }

    private OvertureInfrastructureLookupDiagnostics? QueryBundledInfrastructure(double lat, double lon)
    {
        var dbPath = Path.Combine(_bundledDataDir, "defaults", "overture-airports.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();

        var latBuffer = OverturePlacesLogic.InfrastructureSearchRadiusMetres / 111_320d;
        var lonBuffer = latBuffer / Math.Max(0.1, Math.Cos(lat * Math.PI / 180d));
        var minLat = lat - latBuffer;
        var maxLat = lat + latBuffer;
        var minLon = lon - lonBuffer;
        var maxLon = lon + lonBuffer;
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                feature_type,
                subtype,
                class_name,
                latitude,
                longitude,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM infrastructure
            WHERE bbox_xmax >= {minLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}
              AND bbox_xmin <= {maxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}
              AND bbox_ymax >= {minLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}
              AND bbox_ymin <= {maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            """;

        using var reader = cmd.ExecuteReader();
        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326).CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<OvertureInfrastructureCandidateDiagnostic>();
        OvertureInfrastructureResult? best = null;

        while (reader.Read())
        {
            var candidateLat = reader.GetDouble(5);
            var candidateLon = reader.GetDouble(6);
            var distance = OverturePlacesLogic.HaversineMetres(lat, lon, candidateLat, candidateLon);
            var bboxContains = !reader.IsDBNull(8)
                               && lon >= reader.GetDouble(8)
                               && lon <= reader.GetDouble(10)
                               && lat >= reader.GetDouble(9)
                               && lat <= reader.GetDouble(11);
            var geometryContains = bboxContains
                                   && !reader.IsDBNull(7)
                                   && OvertureDataAccess.TryGeometryContains(OvertureDataAccess.ReadBlobValue(reader.GetValue(7)), point);

            var candidate = new OvertureInfrastructureResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                OvertureDataAccess.ReadNullableString(reader, 4),
                distance,
                bboxContains,
                geometryContains,
                []);

            var selected = false;
            var decision = "considered: weaker than current bundled best";
            if (best is null || OverturePlacesLogic.ShouldPreferInfrastructureCandidate(candidate, best))
            {
                decision = best is null ? "selected: first bundled infrastructure candidate" : "selected: outranked previous bundled best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureInfrastructureCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.FeatureType,
                candidate.SubType,
                candidate.ClassName,
                candidate.DistanceMetres,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.Sources,
                selected,
                decision));
        }

        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key='release'";
        var release = meta.ExecuteScalar()?.ToString();
        return new OvertureInfrastructureLookupDiagnostics(best, candidates, release);
    }

    private OvertureInfrastructureLookupDiagnostics QueryInfrastructure(double lat, double lon, string release)
    {
        var latBuffer = OverturePlacesLogic.InfrastructureSearchRadiusMetres / 111_320d;
        var lonBuffer = latBuffer / Math.Max(0.1, Math.Cos(lat * Math.PI / 180d));
        var minLat = lat - latBuffer;
        var maxLat = lat + latBuffer;
        var minLon = lon - lonBuffer;
        var maxLon = lon + lonBuffer;
        var releaseUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            OverturePlacesLogic.InfrastructureReleaseUrlTemplate,
            release);

        using var conn = new DuckDBConnection("Data Source=:memory:");
        conn.Open();
        OvertureDataAccess.LoadAzureAndSpatial(conn);

        using var query = conn.CreateCommand();
        query.CommandText = OverturePlacesLogic.BuildInfrastructureQuery(
            lat,
            lon,
            minLat,
            maxLat,
            minLon,
            maxLon,
            releaseUrl);

        var candidates = new List<OvertureInfrastructureCandidateDiagnostic>();
        OvertureInfrastructureResult? best = null;

        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var candidateLat = reader.GetDouble(5);
            var candidateLon = reader.GetDouble(6);
            var distance = OverturePlacesLogic.HaversineMetres(lat, lon, candidateLat, candidateLon);
            if (distance > OverturePlacesLogic.InfrastructureSearchRadiusMetres)
            {
                continue;
            }

            var candidate = new OvertureInfrastructureResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                OvertureDataAccess.ReadNullableString(reader, 4),
                distance,
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                OverturePlacesLogic.ParseSources(OvertureDataAccess.ReadNullableString(reader, 9)));

            var selected = false;
            var decision = "considered: weaker than current best";
            if (best is null || OverturePlacesLogic.ShouldPreferInfrastructureCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first live infrastructure candidate"
                    : OverturePlacesLogic.GetInfrastructureClassRank(candidate.SubType, candidate.ClassName)
                      < OverturePlacesLogic.GetInfrastructureClassRank(best.SubType, best.ClassName)
                        ? "selected: preferred infrastructure class outranked previous best"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? "selected: geometry containment outranked previous best"
                    : candidate.BoundingBoxContainsPoint && !best.BoundingBoxContainsPoint
                        ? "selected: bbox containment outranked previous best"
                        : "selected: closer than previous infrastructure best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureInfrastructureCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.FeatureType,
                candidate.SubType,
                candidate.ClassName,
                candidate.DistanceMetres,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.Sources,
                selected,
                decision));
        }

        return new OvertureInfrastructureLookupDiagnostics(best, candidates, release);
    }

}

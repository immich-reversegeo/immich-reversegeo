using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Services;

public class OverturePlacesService
{
    private readonly ILogger<OverturePlacesService> _logger;
    private readonly MemoryResourcePolicy _memoryPolicy;
    private readonly string _dataDir;
    private readonly string _bundledDataDir;
    private readonly object _releaseLock = new();
    private string? _cachedRelease;
    private DateTime _cachedReleaseFetchedUtc;

    public OverturePlacesService(ILogger<OverturePlacesService> logger, string dataDir, string bundledDataDir, MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
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
            var result = await Task.Run(() => QueryOverture(lat, lon, alpha2, release, ct), ct);
            return result with { Release = release, CountryFilter = alpha2 };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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
            var cached = QueryBundledInfrastructure(lat, lon, ct);
            if (cached is not null)
            {
                return cached;
            }

            var release = await GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => QueryInfrastructure(lat, lon, release, ct), ct);
            return result with { Release = release };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
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

        var release = await Task.Run(() => GetLatestReleaseViaDuckDb(ct), ct);

        lock (_releaseLock)
        {
            _cachedRelease = release;
            _cachedReleaseFetchedUtc = DateTime.UtcNow;
        }

        return release;
    }

    private string GetLatestReleaseViaDuckDb(CancellationToken ct) => OvertureReleaseCatalog.GetLatestRelease(_memoryPolicy, _dataDir, ct);

    private OvertureLookupDiagnostics QueryOverture(double lat, double lon, string? alpha2, string release, CancellationToken ct)
    {
        var latBuffer = OverturePlacesLogic.SearchRadiusMetres / 111_320d;
        var lonBuffer = latBuffer / Math.Max(0.1, Math.Cos(lat * Math.PI / 180d));
        var minLat = lat - latBuffer;
        var maxLat = lat + latBuffer;
        var minLon = lon - lonBuffer;
        var maxLon = lon + lonBuffer;
        var releaseUrl = string.Format(System.Globalization.CultureInfo.InvariantCulture, OverturePlacesLogic.PlacesReleaseUrlTemplate, release);

        using var work = new DuckDbWorkScope(_memoryPolicy, _dataDir, ct);
        var conn = work.Connection;
        OvertureDataAccess.LoadAzureAndSpatial(conn);

        using var query = conn.CreateCommand();
        query.CommandText = OverturePlacesLogic.BuildQuery(lat, lon, minLat, maxLat, minLon, maxLon, alpha2, releaseUrl);

        var places = new List<OverturePlaceResult>();
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            places.Add(new OverturePlaceResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? 0d : reader.GetDouble(4),
                OvertureDataAccess.ReadNullableString(reader, 7),
                reader.GetDouble(10),
                false,
                OverturePlacesLogic.ParseSources(OvertureDataAccess.ReadNullableString(reader, 9)),
                !reader.IsDBNull(4)));
        }

        return OverturePlacesLogic.BuildPlaceDiagnostics(places, release, alpha2);
    }

    private OvertureInfrastructureLookupDiagnostics? QueryBundledInfrastructure(double lat, double lon, CancellationToken ct)
    {
        var dbPath = Path.Combine(_bundledDataDir, "defaults", "overture-airports.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
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
            ct.ThrowIfCancellationRequested();
            var candidateLat = reader.GetDouble(5);
            var candidateLon = reader.GetDouble(6);
            var distance = OverturePlacesLogic.HaversineMetres(lat, lon, candidateLat, candidateLon);
            var bboxContains = !reader.IsDBNull(8)
                               && lon >= reader.GetDouble(8)
                               && lon <= reader.GetDouble(10)
                               && lat >= reader.GetDouble(9)
                               && lat <= reader.GetDouble(11);
            var boundary = OvertureDataAccess.ReadAirportBoundary(
                reader.IsDBNull(7) ? null : OvertureDataAccess.ReadBlobValue(reader.GetValue(7)), point);

            var candidate = new OvertureInfrastructureResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                OvertureDataAccess.ReadNullableString(reader, 4),
                distance,
                bboxContains,
                boundary.ContainsPoint,
                [],
                boundary.HasBoundary);

            var selected = false;
            var decision = candidate.IsBoundaryMatch ? "considered: weaker than current bundled best" : $"excluded: {candidate.BoundaryStatus}";
            if (candidate.IsBoundaryMatch && (best is null || OverturePlacesLogic.ShouldPreferInfrastructureCandidate(candidate, best)))
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
                decision,
                candidate.HasMappedBoundary));
        }

        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key='release'";
        var release = meta.ExecuteScalar()?.ToString();
        return new OvertureInfrastructureLookupDiagnostics(best, candidates, release);
    }

    private OvertureInfrastructureLookupDiagnostics QueryInfrastructure(double lat, double lon, string release, CancellationToken ct)
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

        using var work = new DuckDbWorkScope(_memoryPolicy, _dataDir, ct);
        var conn = work.Connection;
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

        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326).CreatePoint(new Coordinate(lon, lat));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var candidateLat = reader.GetDouble(5);
            var candidateLon = reader.GetDouble(6);
            var distance = OverturePlacesLogic.HaversineMetres(lat, lon, candidateLat, candidateLon);
            var boundary = OvertureDataAccess.ReadAirportBoundary(
                reader.IsDBNull(8) ? null : OvertureDataAccess.ReadBlobValue(reader.GetValue(8)), point);
            if (!boundary.ContainsPoint && distance > OverturePlacesLogic.InfrastructureSearchRadiusMetres)
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
                boundary.ContainsPoint,
                OverturePlacesLogic.ParseSources(OvertureDataAccess.ReadNullableString(reader, 9)),
                boundary.HasBoundary);

            var selected = false;
            var decision = candidate.IsBoundaryMatch ? "considered: weaker than current best" : $"excluded: {candidate.BoundaryStatus}";
            if (candidate.IsBoundaryMatch && (best is null || OverturePlacesLogic.ShouldPreferInfrastructureCandidate(candidate, best)))
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
                decision,
                candidate.HasMappedBoundary));
        }

        return new OvertureInfrastructureLookupDiagnostics(best, candidates, release);
    }

}

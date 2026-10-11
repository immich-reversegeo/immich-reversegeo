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
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public class OvertureDivisionsService
{
    private static readonly WKBReader WkbReader = new();
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly ILogger<OvertureDivisionsService> _logger;
    private readonly OverturePlacesService _overturePlacesService;
    private readonly string _dataDir;
    private readonly string _bundledDataDir;
    private readonly Func<string, string?> _alpha2ToIso3;
    private readonly MemoryResourcePolicy _memoryPolicy;
    private readonly object _bundledCountryCacheLock = new();
    private STRtree<BundledCountryArea>? _bundledCountryIndex;

    public OvertureDivisionsService(
        ILogger<OvertureDivisionsService> logger,
        OverturePlacesService overturePlacesService,
        string dataDir,
        string bundledDataDir,
        Func<string, string?> alpha2ToIso3,
        MemoryResourcePolicy? memoryPolicy = null)
    {
        _logger = logger;
        _overturePlacesService = overturePlacesService;
        _dataDir = dataDir;
        _bundledDataDir = bundledDataDir;
        _alpha2ToIso3 = alpha2ToIso3;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
        _memoryPolicy.LowMemoryEnabled += ReleaseBundledCountryIndex;
    }

    public Task<BundledCountryLookupResult> FindBundledCountryAsync(
        double lat,
        double lon,
        CancellationToken ct = default)
    {
        var bundledPath = Path.Combine(_bundledDataDir, "defaults", "overture-country-divisions.db");
        if (!File.Exists(bundledPath))
        {
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch("Bundled country database is unavailable."));
        }

        ct.ThrowIfCancellationRequested();
        var best = _memoryPolicy.LowMemoryMode
            ? FindBundledCountryFromDisk(bundledPath, lat, lon, ct)
            : FindBundledCountryFromMemory(bundledPath, lat, lon);
        if (best is null)
        {
            return Task.FromResult(BundledCountryLookupResult.SpatialNoMatch());
        }

        var iso3 = best.Country is null ? null : _alpha2ToIso3(best.Country);
        if (iso3 is null)
        {
            return Task.FromResult(BundledCountryLookupResult.IdentityMappingFailure(
                best.Name,
                best.Country,
                best.Id,
                "A country boundary matched, but its country code could not be resolved."));
        }

        return Task.FromResult(BundledCountryLookupResult.Matched(iso3, best.Name, best.Country!, best.Id));
    }

    private OvertureDivisionResult? FindBundledCountryFromMemory(string bundledPath, double lat, double lon)
    {
        var countryIndex = GetBundledCountryIndex(bundledPath);
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var queryEnvelope = BuildToleranceEnvelope(lon, lat);
        var candidates = countryIndex.Query(queryEnvelope);

        // Pass 1: exact containment only. Country bounding boxes hugely overstate their
        // footprint (antimeridian crossings and distant overseas territories), so the index
        // routinely returns several very large countries that do not contain the point.
        // PreparedGeometry.Covers is index-backed and allocation-free; Geometry.Distance is
        // not, so it must never run while an exact match is still possible.
        var best = SelectBestBundledCountry(candidates, lat, lon, country => country.PreparedGeometry.Covers(point));
        if (best is not null)
        {
            return best;
        }

        // Pass 2: nothing contains the point, so retry with the boundary tolerance. Testing
        // against a small rectangle reuses the same prepared index, whereas Distance() runs a
        // brute-force DistanceOp that copies every boundary ring into a fresh Coordinate[].
        var tolerance = GeometryFactory.ToGeometry(queryEnvelope);
        return SelectBestBundledCountry(candidates, lat, lon, country => country.PreparedGeometry.Intersects(tolerance));
    }

    private static OvertureDivisionResult? SelectBestBundledCountry(
        IList<BundledCountryArea> candidates,
        double lat,
        double lon,
        Func<BundledCountryArea, bool> matches)
    {
        OvertureDivisionResult? best = null;

        foreach (var country in candidates)
        {
            if (!matches(country))
            {
                continue;
            }

            var candidate = new OvertureDivisionResult(
                country.Id,
                country.Name,
                country.SubType,
                country.ClassName,
                country.AdminLevel,
                country.Country,
                country.IsLand,
                country.IsTerritorial,
                lon >= country.BoundingBoxXMin && lon <= country.BoundingBoxXMax
                    && lat >= country.BoundingBoxYMin && lat <= country.BoundingBoxYMax,
                true,
                country.BoundingBoxArea);

            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static OvertureDivisionResult? FindBundledCountryFromDisk(string path, double lat, double lon, CancellationToken ct)
    {
        var exact = QueryBundledCountryFromDisk(path, lat, lon, false, ct);
        return exact ?? QueryBundledCountryFromDisk(path, lat, lon, true, ct);
    }

    private static OvertureDivisionResult? QueryBundledCountryFromDisk(string path, double lat, double lon, bool useTolerance, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
        conn.Open();
        var adminLevel = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, subtype, class_name, {adminLevel}, country, is_land, is_territorial,
                   geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax
            FROM division_area
            WHERE bbox_xmax >= $xmin AND bbox_xmin <= $xmax
              AND bbox_ymax >= $ymin AND bbox_ymin <= $ymax
            """;
        var envelope = BuildToleranceEnvelope(lon, lat);
        cmd.Parameters.AddWithValue("$xmin", envelope.MinX);
        cmd.Parameters.AddWithValue("$xmax", envelope.MaxX);
        cmd.Parameters.AddWithValue("$ymin", envelope.MinY);
        cmd.Parameters.AddWithValue("$ymax", envelope.MaxY);
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var tolerance = GeometryFactory.ToGeometry(envelope);
        using var reader = cmd.ExecuteReader();
        OvertureDivisionResult? best = null;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.IsDBNull(8))
            {
                continue;
            }
            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)));
            if (!(useTolerance ? geometry.Intersects(tolerance) : geometry.Covers(point)))
            {
                continue;
            }
            var xmin = reader.GetDouble(9);
            var ymin = reader.GetDouble(10);
            var xmax = reader.GetDouble(11);
            var ymax = reader.GetDouble(12);
            var candidate = new OvertureDivisionResult(
                reader.GetString(0), reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2), OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), OvertureDataAccess.ReadNullableString(reader, 5),
                OvertureDataAccess.ReadSqliteBool(reader, 6), OvertureDataAccess.ReadSqliteBool(reader, 7),
                lon >= xmin && lon <= xmax && lat >= ymin && lat <= ymax, true,
                Math.Abs((xmax - xmin) * (ymax - ymin)));
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                best = candidate;
            }
        }
        return best;
    }

    private void ReleaseBundledCountryIndex()
    {
        lock (_bundledCountryCacheLock)
        {
            _bundledCountryIndex = null;
        }
    }

    private static Envelope BuildToleranceEnvelope(double lon, double lat) => new(
        lon - OvertureDataAccess.BoundaryToleranceDegrees,
        lon + OvertureDataAccess.BoundaryToleranceDegrees,
        lat - OvertureDataAccess.BoundaryToleranceDegrees,
        lat + OvertureDataAccess.BoundaryToleranceDegrees);

    private STRtree<BundledCountryArea> GetBundledCountryIndex(string bundledPath)
    {
        var cachedIndex = _bundledCountryIndex;
        if (cachedIndex is not null)
        {
            return cachedIndex;
        }

        lock (_bundledCountryCacheLock)
        {
            if (_bundledCountryIndex is not null)
            {
                return _bundledCountryIndex;
            }

            var index = LoadBundledCountryIndex(bundledPath);
            if (!_memoryPolicy.LowMemoryMode)
            {
                _bundledCountryIndex = index;
            }
            return index;
        }
    }

    private static STRtree<BundledCountryArea> LoadBundledCountryIndex(string bundledPath)
    {
        var index = new STRtree<BundledCountryArea>();

        using var conn = new SqliteConnection($"Data Source={bundledPath};Pooling=false");
        conn.Open();
        var adminLevelColumn = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL AS admin_level";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                subtype,
                class_name,
                {adminLevelColumn},
                country,
                is_land,
                is_territorial,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM division_area
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(8))
            {
                continue;
            }

            var geometry = WkbReader.Read(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)));
            var preparedGeometry = PreparedGeometryFactory.Prepare(geometry);
            var xmin = reader.GetDouble(9);
            var ymin = reader.GetDouble(10);
            var xmax = reader.GetDouble(11);
            var ymax = reader.GetDouble(12);

            var country = new BundledCountryArea(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                OvertureDataAccess.ReadSqliteBool(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                geometry,
                preparedGeometry,
                reader.GetDouble(9),
                reader.GetDouble(10),
                reader.GetDouble(11),
                reader.GetDouble(12),
                Math.Abs((xmax - xmin) * (ymax - ymin)));
            index.Insert(new Envelope(xmin, xmax, ymin, ymax), country);
        }

        index.Build();
        return index;
    }

    public async Task<OvertureAdministrativeResult?> ResolveAdministrativeGeoAsync(
        double lat,
        double lon,
        string? alpha2,
        string? iso3,
        CityResolverProfile? cityResolverProfile = null,
        CancellationToken ct = default)
    {
        var diagnostics = await FindContainingDivisionAreasAsync(lat, lon, alpha2, iso3, ct);
        if (diagnostics.Error is not null || diagnostics.Candidates.Count == 0)
        {
            return null;
        }

        return OvertureDivisionsLogic.SelectAdministrativeAreas(diagnostics.Candidates, cityResolverProfile);
    }

    public async Task<OvertureDivisionLookupDiagnostics> FindContainingDivisionAreasAsync(
        double lat,
        double lon,
        string? alpha2,
        string? iso3 = null,
        CancellationToken ct = default)
    {
        try
        {
            var cached = QueryCachedDivisionAreas(lat, lon, iso3);
            if (cached is not null)
            {
                return cached;
            }

            var release = await _overturePlacesService.GetLatestReleaseForOvertureAsync(ct);
            var result = await Task.Run(() => QueryDivisionAreas(lat, lon, alpha2, release, ct), ct);
            return result with { Release = release };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Overture division lookup failed at ({Lat:F4}, {Lon:F4}): {Message}",
                lat,
                lon,
                ex.Message);
            return new OvertureDivisionLookupDiagnostics(null, [], null, ex.Message);
        }
    }

    private OvertureDivisionLookupDiagnostics? QueryCachedDivisionAreas(double lat, double lon, string? iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return null;
        }

        var dbPath = Path.Combine(_dataDir, "overture-divisions", $"{iso3}.db");
        if (!File.Exists(dbPath))
        {
            return null;
        }

        return QueryDivisionAreasFromSqlite(dbPath, lat, lon, "cached");
    }

    private static OvertureDivisionLookupDiagnostics QueryDivisionAreasFromSqlite(
        string dbPath,
        double lat,
        double lon,
        string selectionLabel)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        var adminLevelColumn = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL AS admin_level";
        var divisionIdColumn = HasColumn(conn, "division_area", "division_id") ? "division_id" : "NULL AS division_id";
        var hierarchyColumn = HasColumn(conn, "division_area", "hierarchy_json") ? "hierarchy_json" : "NULL AS hierarchy_json";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                subtype,
                class_name,
                {adminLevelColumn},
                country,
                is_land,
                is_territorial,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax,
                {divisionIdColumn},
                {hierarchyColumn}
            FROM division_area
            WHERE bbox_xmax >= $lon
              AND bbox_xmin <= $lon
              AND bbox_ymax >= $lat
              AND bbox_ymin <= $lat
            """;
        cmd.Parameters.AddWithValue("$lon", lon);
        cmd.Parameters.AddWithValue("$lat", lat);

        using var reader = cmd.ExecuteReader();
        var point = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326).CreatePoint(new Coordinate(lon, lat));
        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        while (reader.Read())
        {
            var bboxContains = !reader.IsDBNull(9)
                               && !reader.IsDBNull(10)
                               && !reader.IsDBNull(11)
                               && !reader.IsDBNull(12)
                               && lon >= reader.GetDouble(9)
                               && lon <= reader.GetDouble(11)
                               && lat >= reader.GetDouble(10)
                               && lat <= reader.GetDouble(12);
            var geometryContains = bboxContains
                                   && !reader.IsDBNull(8)
                                   && OvertureDataAccess.TryGeometryContains(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)), point);
            var bboxArea = bboxContains
                ? Math.Abs((reader.GetDouble(11) - reader.GetDouble(9)) * (reader.GetDouble(12) - reader.GetDouble(10)))
                : double.MaxValue;

            var candidate = new OvertureDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                OvertureDataAccess.ReadSqliteBool(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                bboxContains,
                geometryContains,
                bboxArea);

            var selected = false;
            var decision = $"considered: weaker than current {selectionLabel} best";
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? $"selected: first {selectionLabel} division area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? $"selected: {selectionLabel} geometry containment outranked previous best"
                        : OvertureDivisionsLogic.GetSubtypeRank(candidate.SubType) < OvertureDivisionsLogic.GetSubtypeRank(best.SubType)
                            ? $"selected: {selectionLabel} subtype specificity outranked previous best"
                            : candidate.AdminLevel.HasValue && best.AdminLevel.HasValue && candidate.AdminLevel.Value < best.AdminLevel.Value
                                ? $"selected: {selectionLabel} lower admin level outranked previous best"
                            : candidate.IsTerritorial && !best.IsTerritorial
                                ? $"selected: {selectionLabel} territorial area outranked previous best"
                                : $"selected: {selectionLabel} tighter bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.SubType,
                candidate.ClassName,
                candidate.AdminLevel,
                candidate.Country,
                candidate.IsLand,
                candidate.IsTerritorial,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision,
                OvertureDataAccess.ReadNullableString(reader, 13),
                OvertureDataAccess.ReadNullableString(reader, 14)));
        }

        using var meta = conn.CreateCommand();
        meta.CommandText = "SELECT value FROM _meta WHERE key='release'";
        var release = meta.ExecuteScalar()?.ToString();
        return new OvertureDivisionLookupDiagnostics(best, candidates, release);
    }

    private OvertureDivisionLookupDiagnostics QueryDivisionAreas(
        double lat,
        double lon,
        string? alpha2,
        string release,
        CancellationToken ct)
    {
        var releaseUrl = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            OvertureDivisionsLogic.DivisionAreaReleaseUrlTemplate,
            release);

        using var work = new DuckDbWorkScope(_memoryPolicy, _dataDir, ct);
        var conn = work.Connection;
        OvertureDataAccess.LoadAzureAndSpatial(conn);

        using var query = conn.CreateCommand();
        query.CommandText = OvertureDivisionsLogic.BuildDivisionAreaQuery(lat, lon, alpha2, releaseUrl);

        var candidates = new List<OvertureDivisionCandidateDiagnostic>();
        OvertureDivisionResult? best = null;

        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var candidate = new OvertureDivisionResult(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                OvertureDataAccess.ReadNullableString(reader, 5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetDouble(10));

            var selected = false;
            var decision = "considered: weaker than current best";
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? "selected: first containing division area"
                    : candidate.GeometryContainsPoint && !best.GeometryContainsPoint
                        ? "selected: geometry containment outranked previous best"
                    : OvertureDivisionsLogic.GetSubtypeRank(candidate.SubType) < OvertureDivisionsLogic.GetSubtypeRank(best.SubType)
                        ? "selected: more specific division subtype outranked previous best"
                    : candidate.AdminLevel.HasValue && best.AdminLevel.HasValue && candidate.AdminLevel.Value < best.AdminLevel.Value
                        ? "selected: lower admin level outranked previous best"
                    : candidate.IsTerritorial && !best.IsTerritorial
                        ? "selected: territorial area outranked previous best"
                        : "selected: tighter bounding area outranked previous best";
                best = candidate;
                selected = true;
            }

            candidates.Add(new OvertureDivisionCandidateDiagnostic(
                candidate.Id,
                candidate.Name,
                candidate.SubType,
                candidate.ClassName,
                candidate.AdminLevel,
                candidate.Country,
                candidate.IsLand,
                candidate.IsTerritorial,
                candidate.BoundingBoxContainsPoint,
                candidate.GeometryContainsPoint,
                candidate.BoundingBoxArea,
                selected,
                decision,
                OvertureDataAccess.ReadNullableString(reader, 11),
                OvertureDataAccess.ReadNullableString(reader, 12)));
        }

        return new OvertureDivisionLookupDiagnostics(best, candidates, release);
    }

    private sealed record BundledCountryArea(
        string Id,
        string Name,
        string? SubType,
        string? ClassName,
        int? AdminLevel,
        string? Country,
        bool IsLand,
        bool IsTerritorial,
        Geometry Geometry,
        IPreparedGeometry PreparedGeometry,
        double BoundingBoxXMin,
        double BoundingBoxYMin,
        double BoundingBoxXMax,
        double BoundingBoxYMax,
        double BoundingBoxArea);

    private static bool HasColumn(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1) && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

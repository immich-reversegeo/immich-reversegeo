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
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Implementation;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Services;

public class OvertureDivisionsService : IDisposable
{
    private static readonly WKBReader WkbReader = new();
    // Compact the retained index and streamed parts. The compatibility path keeps its original decoder.
    // Doubles preserve coordinate precision without one managed Coordinate object per vertex.
    private static readonly WKBReader BundledCountryWkbReader = new(new NtsGeometryServices(PackedCoordinateSequenceFactory.DoubleFactory));
    private static readonly GeometryFactory GeometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    private readonly ILogger<OvertureDivisionsService> _logger;
    private readonly OverturePlacesService _overturePlacesService;
    private readonly string _dataDir;
    private readonly string _bundledDataDir;
    private readonly Func<string, string?> _alpha2ToIso3;
    private readonly MemoryResourcePolicy _memoryPolicy;
    private readonly object _bundledCountryCacheLock = new();
    private volatile STRtree<BundledCountryArea>? _bundledCountryIndex;
    private readonly CountryPolygonDiskIndex _countryParts;
    private readonly TimeProvider _timeProvider;
    private readonly IdleCheck _idleCheck;
    private long _lastCountryLookup;
    private int _activeCountryLookups;
    private int _diskIndexWarningLogged;
    private volatile bool _disposed;
    private readonly CountryPartCache _countryCache;
    public object CountryCacheStatistics => _countryCache.Statistics;

    public OvertureDivisionsService(
        ILogger<OvertureDivisionsService> logger,
        OverturePlacesService overturePlacesService,
        string dataDir,
        string bundledDataDir,
        Func<string, string?> alpha2ToIso3,
        MemoryResourcePolicy? memoryPolicy = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _overturePlacesService = overturePlacesService;
        _dataDir = dataDir;
        _bundledDataDir = bundledDataDir;
        _alpha2ToIso3 = alpha2ToIso3;
        _memoryPolicy = memoryPolicy ?? new MemoryResourcePolicy();
        _countryCache = new CountryPartCache(() => !_disposed && !_memoryPolicy.LowMemoryMode && _memoryPolicy.UseBoundedCountryCache
            ? (long)_memoryPolicy.CountryCacheMiB * 1024 * 1024 : 0);
        _countryParts = new CountryPolygonDiskIndex(dataDir, _memoryPolicy);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastCountryLookup = _timeProvider.GetTimestamp();
        _memoryPolicy.LowMemoryEnabled += ReleaseBundledCountryIndex;
        _memoryPolicy.CountryCacheSettingsChanged += ReleaseBundledCountryIndex;
        // A weak callback lets short-lived test/lookup services be collected as well.
        _idleCheck = new IdleCheck(this);
        _idleCheck.Timer = _timeProvider.CreateTimer(static state => ((IdleCheck)state!).Tick(), _idleCheck,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task<BundledCountryLookupResult> FindBundledCountryAsync(
        double lat,
        double lon,
        CancellationToken ct = default)
    {
        var bundledPath = Path.Combine(_bundledDataDir, "defaults", "overture-country-divisions.db");
        if (!File.Exists(bundledPath))
        {
            return BundledCountryLookupResult.SpatialNoMatch("Bundled country database is unavailable.");
        }
        lock (_bundledCountryCacheLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCountryLookups++;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            var best = _memoryPolicy.LowMemoryMode || _memoryPolicy.UseBoundedCountryCache
                ? await FindBundledCountryFromPartsAsync(bundledPath, lat, lon, ct)
                : FindBundledCountryFromMemory(bundledPath, lat, lon);
            if (best is null)
            {
                return BundledCountryLookupResult.SpatialNoMatch();
            }

            if (string.IsNullOrWhiteSpace(best.Country))
            {
                return BundledCountryLookupResult.IdentityMappingFailure(
                    best.Name,
                    null,
                    best.Id,
                    "Matched bundled geometry has no Alpha-2 identity.");
            }

            var mappedAlpha3 = _alpha2ToIso3(best.Country);
            if (string.IsNullOrWhiteSpace(best.Alpha3)
                || string.IsNullOrWhiteSpace(mappedAlpha3)
                || !string.Equals(best.Alpha3, mappedAlpha3, StringComparison.OrdinalIgnoreCase))
            {
                return BundledCountryLookupResult.IdentityMappingFailure(
                    best.Name,
                    best.Country,
                    best.Id,
                    $"Matched bundled identity '{best.Country}' has missing or inconsistent Alpha-3 mapping.");
            }

            return BundledCountryLookupResult.Matched(
                mappedAlpha3,
                best.Name,
                best.Country,
                best.Id);
        }
        finally
        {
            lock (_bundledCountryCacheLock)
            {
                _activeCountryLookups--;
                _lastCountryLookup = _timeProvider.GetTimestamp();
            }
        }
    }

    private async Task<OvertureDivisionResult?> FindBundledCountryFromPartsAsync(string path, double lat, double lon, CancellationToken ct)
    {
        try
        {
            var index = await _countryParts.EnsureAsync(path, ct);
            return QueryBundledCountryParts(path, index, lat, lon, false, ct)
                ?? QueryBundledCountryParts(path, index, lat, lon, true, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidCastException or NotSupportedException)
        {
            if (Interlocked.Exchange(ref _diskIndexWarningLogged, 1) == 0)
            {
                _logger.LogWarning(ex, "Country polygon disk index is unavailable; using the original bundled country lookup.");
            }
            return FindBundledCountryFromDisk(path, lat, lon, ct);
        }
    }

    private OvertureDivisionResult? QueryBundledCountryParts(string sourcePath, CountryPolygonDiskIndex.IndexInfo index, double lat, double lon, bool useTolerance, CancellationToken ct)
    {
        using var connection = CountryPolygonDiskIndex.OpenReadOnly(index.Path);
        using var source = CountryPolygonDiskIndex.OpenReadOnly(sourcePath);
        using var sourceSnapshot = source.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.name, c.subtype, c.class_name, c.admin_level, c.country, c.is_land, c.is_territorial,
                   c.source_rowid, c.bbox_xmin, c.bbox_ymin, c.bbox_xmax, c.bbox_ymax, c.alpha3, p.byte_offset, p.byte_length
            FROM part_bounds b JOIN part p ON p.id=b.id JOIN country c ON c.source_rowid=p.source_rowid
            WHERE b.xmax >= $xmin AND b.xmin <= $xmax AND b.ymax >= $ymin AND b.ymin <= $ymax
              AND c.bbox_xmax >= $xmin AND c.bbox_xmin <= $xmax AND c.bbox_ymax >= $ymin AND c.bbox_ymin <= $ymax
            ORDER BY c.source_rowid, p.id
            """;
        var envelope = BuildToleranceEnvelope(lon, lat);
        command.Parameters.AddWithValue("$xmin", envelope.MinX);
        command.Parameters.AddWithValue("$xmax", envelope.MaxX);
        command.Parameters.AddWithValue("$ymin", envelope.MinY);
        command.Parameters.AddWithValue("$ymax", envelope.MaxY);
        var point = GeometryFactory.CreatePoint(new Coordinate(lon, lat));
        var tolerance = GeometryFactory.ToGeometry(envelope);
        using var reader = command.ExecuteReader();
        OvertureDivisionResult? bestMapped = null;
        OvertureDivisionResult? bestUnmapped = null;
        var matchedRows = new HashSet<long>();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var row = reader.GetInt64(8);
            if (matchedRows.Contains(row))
            {
                continue;
            }
            var offset = reader.GetInt64(14);
            var length = reader.GetInt32(15);
            Geometry ReadPart()
            {
                using var blob = new SqliteBlob(source, "division_area", "geom_wkb", row, readOnly: true);
                blob.Seek(offset, SeekOrigin.Begin);
                var wkb = new byte[length];
                blob.ReadExactly(wkb);
                return BundledCountryWkbReader.Read(wkb);
            }
            var geometry = _countryCache.Get(index, (row, offset), length, ReadPart);
            var coversPoint = geometry is Polygon
                ? SimplePointInAreaLocator.IsContained(point.Coordinate, geometry)
                : geometry.Covers(point);
            if (!(useTolerance ? geometry.Intersects(tolerance) : coversPoint))
            {
                continue;
            }
            matchedRows.Add(row);
            var xmin = reader.GetDouble(9);
            var ymin = reader.GetDouble(10);
            var xmax = reader.GetDouble(11);
            var ymax = reader.GetDouble(12);
            var alpha2 = OvertureDataAccess.ReadNullableString(reader, 5);
            var mappedAlpha3 = string.IsNullOrWhiteSpace(alpha2) ? null : _alpha2ToIso3(alpha2);
            var alpha3 = index.HasAlpha3 ? OvertureDataAccess.ReadNullableString(reader, 13) : mappedAlpha3;
            // Rank using the ORIGINAL parent envelope, not the smaller part's envelope.
            var candidate = new OvertureDivisionResult(
                reader.GetString(0), reader.GetString(1), OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3), reader.IsDBNull(4) ? null : reader.GetInt32(4), alpha2, alpha3,
                OvertureDataAccess.ReadSqliteBool(reader, 6), OvertureDataAccess.ReadSqliteBool(reader, 7),
                lon >= xmin && lon <= xmax && lat >= ymin && lat <= ymax, true, Math.Abs((xmax - xmin) * (ymax - ymin)));
            if (!string.IsNullOrWhiteSpace(mappedAlpha3))
            {
                if (bestMapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestMapped))
                {
                    bestMapped = candidate;
                }
            }
            else if (bestUnmapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestUnmapped))
            {
                bestUnmapped = candidate;
            }
        }
        return bestMapped ?? bestUnmapped;
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

    private OvertureDivisionResult? SelectBestBundledCountry(
        IList<BundledCountryArea> candidates,
        double lat,
        double lon,
        Func<BundledCountryArea, bool> matches)
    {
        OvertureDivisionResult? bestMapped = null;
        OvertureDivisionResult? bestUnmapped = null;

        foreach (var country in candidates)
        {
            if (!matches(country))
            {
                continue;
            }

            var boundingBoxContains = lon >= country.BoundingBoxXMin
                                      && lon <= country.BoundingBoxXMax
                                      && lat >= country.BoundingBoxYMin
                                      && lat <= country.BoundingBoxYMax;
            var candidate = new OvertureDivisionResult(
                country.Id,
                country.Name,
                country.SubType,
                country.ClassName,
                country.AdminLevel,
                country.Country,
                country.Alpha3,
                country.IsLand,
                country.IsTerritorial,
                boundingBoxContains,
                true,
                country.BoundingBoxArea);

            var mappedAlpha3 = string.IsNullOrWhiteSpace(candidate.Country)
                ? null
                : _alpha2ToIso3(candidate.Country);
            // Rank known countries spatially before validating their stored Alpha-3.
            // A broken territory identity must surface as an error, not silently become its sovereign.
            if (!string.IsNullOrWhiteSpace(mappedAlpha3))
            {
                if (bestMapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestMapped))
                {
                    bestMapped = candidate;
                }
            }
            else if (bestUnmapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestUnmapped))
            {
                bestUnmapped = candidate;
            }
        }

        return bestMapped ?? bestUnmapped;
    }

    private OvertureDivisionResult? FindBundledCountryFromDisk(string path, double lat, double lon, CancellationToken ct)
    {
        var exact = QueryBundledCountryFromDisk(path, lat, lon, false, ct);
        return exact ?? QueryBundledCountryFromDisk(path, lat, lon, true, ct);
    }

    private OvertureDivisionResult? QueryBundledCountryFromDisk(string path, double lat, double lon, bool useTolerance, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=false");
        conn.Open();
        var adminLevel = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL";
        var hasAlpha3 = HasColumn(conn, "division_area", "alpha3");
        var alpha3Column = hasAlpha3 ? "alpha3" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, subtype, class_name, {adminLevel}, country, is_land, is_territorial,
                   geom_wkb, bbox_xmin, bbox_ymin, bbox_xmax, bbox_ymax, {alpha3Column}
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
        OvertureDivisionResult? bestMapped = null;
        OvertureDivisionResult? bestUnmapped = null;
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
            var alpha2 = OvertureDataAccess.ReadNullableString(reader, 5);
            var mappedAlpha3 = string.IsNullOrWhiteSpace(alpha2) ? null : _alpha2ToIso3(alpha2);
            var alpha3 = hasAlpha3 ? OvertureDataAccess.ReadNullableString(reader, 13) : mappedAlpha3;
            var candidate = new OvertureDivisionResult(
                reader.GetString(0), reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2), OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4), alpha2, alpha3,
                OvertureDataAccess.ReadSqliteBool(reader, 6), OvertureDataAccess.ReadSqliteBool(reader, 7),
                lon >= xmin && lon <= xmax && lat >= ymin && lat <= ymax, true,
                Math.Abs((xmax - xmin) * (ymax - ymin)));
            // Match the retained index: rank known identities before unmapped geometry,
            // but preserve invalid stored Alpha-3 so a broken dependency stays an error.
            if (!string.IsNullOrWhiteSpace(mappedAlpha3))
            {
                if (bestMapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestMapped))
                {
                    bestMapped = candidate;
                }
            }
            else if (bestUnmapped is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, bestUnmapped))
            {
                bestUnmapped = candidate;
            }
        }
        return bestMapped ?? bestUnmapped;
    }

    private void ReleaseBundledCountryIndex()
    {
        lock (_bundledCountryCacheLock)
        {
            _bundledCountryIndex = null;
            _countryCache.Clear();
        }
    }

    private void ReleaseIdleCountryIndex()
    {
        var released = false;
        lock (_bundledCountryCacheLock)
        {
            var minutes = _memoryPolicy.CountryIndexIdleMinutes;
            if (!_disposed && minutes > 0 && _activeCountryLookups == 0 && (_bundledCountryIndex is not null || _countryCache.HasEntries)
                && _timeProvider.GetElapsedTime(_lastCountryLookup) >= TimeSpan.FromMinutes(minutes))
            {
                _bundledCountryIndex = null;
                _countryCache.Clear();
                released = true;
            }
        }
        if (released)
        {
            _logger.LogInformation("Released the idle country boundary index; it will rebuild on the next lookup.");
        }
    }

    public void Dispose()
    {
        lock (_bundledCountryCacheLock)
        {
            _disposed = true;
            _bundledCountryIndex = null;
            _countryCache.Clear();
        }
        _memoryPolicy.LowMemoryEnabled -= ReleaseBundledCountryIndex;
        _memoryPolicy.CountryCacheSettingsChanged -= ReleaseBundledCountryIndex;
        _idleCheck.Timer?.Dispose();
    }

    private sealed class IdleCheck(OvertureDivisionsService owner)
    {
        private readonly WeakReference<OvertureDivisionsService> _owner = new(owner);
        public ITimer? Timer { get; set; }
        public void Tick()
        {
            if (_owner.TryGetTarget(out var target))
            {
                target.ReleaseIdleCountryIndex();
            }
            else
            {
                Timer?.Dispose();
            }
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
            if (!_memoryPolicy.LowMemoryMode && !_memoryPolicy.UseBoundedCountryCache)
            {
                _bundledCountryIndex = index;
            }
            return index;
        }
    }

    private STRtree<BundledCountryArea> LoadBundledCountryIndex(string bundledPath)
    {
        var index = new STRtree<BundledCountryArea>();

        using var conn = CountryPolygonDiskIndex.OpenReadOnly(bundledPath);
        using var snapshot = conn.BeginTransaction(deferred: true);
        var adminLevelColumn = HasColumn(conn, "division_area", "admin_level") ? "admin_level" : "NULL AS admin_level";
        var hasAlpha3Column = HasColumn(conn, "division_area", "alpha3");
        var alpha3Column = hasAlpha3Column ? "alpha3" : "NULL AS alpha3";
        using var cmd = conn.CreateCommand();
        cmd.Transaction = snapshot;
        cmd.CommandText = $"""
            SELECT
                id,
                name,
                subtype,
                class_name,
                {adminLevelColumn},
                country,
                {alpha3Column},
                is_land,
                is_territorial,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax,
                rowid
            FROM division_area
            WHERE geom_wkb IS NOT NULL
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            // Buffer small decoder reads without allocating a copy of the country's entire WKB.
            using var blob = new SqliteBlob(conn, "division_area", "geom_wkb", reader.GetInt64(13), readOnly: true);
            using var buffered = new BufferedStream(blob, 64 * 1024);
            var geometry = BundledCountryWkbReader.Read(buffered);
            var preparedGeometry = PreparedGeometryFactory.Prepare(geometry);
            var xmin = reader.GetDouble(9);
            var ymin = reader.GetDouble(10);
            var xmax = reader.GetDouble(11);
            var ymax = reader.GetDouble(12);

            var alpha2 = OvertureDataAccess.ReadNullableString(reader, 5);
            // Older artifacts have no Alpha-3 column. Resolve their existing code through the catalog.
            // A present but empty or inconsistent Alpha-3 remains an identity error.
            var alpha3 = hasAlpha3Column
                ? OvertureDataAccess.ReadNullableString(reader, 6)
                : string.IsNullOrWhiteSpace(alpha2) ? null : _alpha2ToIso3(alpha2);
            var country = new BundledCountryArea(
                reader.GetString(0),
                reader.GetString(1),
                OvertureDataAccess.ReadNullableString(reader, 2),
                OvertureDataAccess.ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                alpha2,
                alpha3,
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                OvertureDataAccess.ReadSqliteBool(reader, 8),
                geometry,
                preparedGeometry,
                xmin,
                ymin,
                xmax,
                ymax,
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
            var exactGeometryContains = false;
            var geometryContains = bboxContains
                                   && !reader.IsDBNull(8)
                                   && OvertureDataAccess.TryGeometryContains(OvertureDataAccess.ReadBlobValue(reader.GetValue(8)), point, out exactGeometryContains);
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
                null,
                OvertureDataAccess.ReadSqliteBool(reader, 6),
                OvertureDataAccess.ReadSqliteBool(reader, 7),
                bboxContains,
                geometryContains,
                bboxArea,
                exactGeometryContains);

            var selected = false;
            var decision = $"considered: weaker than current {selectionLabel} best";
            if (best is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, best))
            {
                decision = best is null
                    ? $"selected: first {selectionLabel} division area"
                    : candidate.IsExactGeometryMatch && !best.IsExactGeometryMatch
                        ? $"selected: {selectionLabel} exact containment outranked tolerance or bounding box match"
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
                OvertureDataAccess.ReadNullableString(reader, 14),
                candidate.ExactGeometryContainsPoint));
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
                null,
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
        string? Alpha3,
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

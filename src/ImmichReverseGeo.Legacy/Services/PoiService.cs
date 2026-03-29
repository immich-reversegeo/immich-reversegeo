using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Legacy.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Legacy.Services;

public class PoiService
{
    private static readonly Regex CategoryIdRegex = new(@"^[0-9a-zA-Z]{1,30}$", RegexOptions.Compiled);
    private static readonly WKBReader WkbReader = new();

    private readonly ILogger<PoiService> _logger;
    private readonly Func<Task<LegacyPoiConfig>> _getConfigAsync;
    private readonly IReadOnlyDictionary<string, CategoryTier> _defaultCategoryTierMap;
    private readonly string _dataDir;
    private readonly object _effectiveTierMapLock = new();
    private IReadOnlyDictionary<string, CategoryTier>? _effectiveTierMap;
    private DateTime _effectiveTierMapStampUtc;
    private readonly GeometryFactory _geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public IReadOnlyList<CategoryAllowlistEntry> DefaultCategories { get; }

    public PoiService(ILogger<PoiService> logger, Func<Task<LegacyPoiConfig>> getConfigAsync, StorageOptions dirs)
    {
        _logger = logger;
        _getConfigAsync = getConfigAsync;
        _dataDir = dirs.DataDir;

        var sw = Stopwatch.StartNew();
        logger.LogInformation("PoiService: loading category tier map");
        (DefaultCategories, _defaultCategoryTierMap) = LoadCategoryData(dirs.BundledDataDir);
        logger.LogInformation("PoiService: category tier map loaded ({Count} entries) in {Elapsed}ms",
            _defaultCategoryTierMap.Count, sw.ElapsedMilliseconds);
    }

    public static PoiService CreateForTest(
        ILogger<PoiService> logger,
        LegacyPoiConfig config,
        IReadOnlyDictionary<string, CategoryTier> tierMap,
        string dataDir)
    {
        return new PoiService(logger, () => Task.FromResult(config), tierMap, dataDir);
    }

    private PoiService(
        ILogger<PoiService> logger,
        Func<Task<LegacyPoiConfig>> getConfigAsync,
        IReadOnlyDictionary<string, CategoryTier> tierMap,
        string dataDir)
    {
        _logger = logger;
        _getConfigAsync = getConfigAsync;
        _defaultCategoryTierMap = tierMap;
        _dataDir = dataDir;
        DefaultCategories = [];
    }

    public bool TryGetCategoryTier(string categoryId, out CategoryTier tier) =>
        GetEffectiveTierMap().TryGetValue(categoryId, out tier);

    public IReadOnlyList<string> NormalizeConfiguredIds(IEnumerable<string> categoryIds) =>
        FoursquareCategoryIds.NormalizeConfiguredIds(categoryIds);

    private static (IReadOnlyList<CategoryAllowlistEntry> entries, IReadOnlyDictionary<string, CategoryTier> map)
        LoadCategoryData(string bundledDataDir)
    {
        var path = Path.Combine(bundledDataDir, "defaults", "category-allowlist.json");
        if (!File.Exists(path))
        {
            return ([], new Dictionary<string, CategoryTier>());
        }

        var entries = new List<CategoryAllowlistEntry>();
        var map = new Dictionary<string, CategoryTier>();
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("id", out var idEl) && e.TryGetProperty("tier", out var tierEl))
            {
                var id = idEl.GetString() ?? string.Empty;
                var name = e.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? id) : id;
                if (!string.IsNullOrEmpty(id) && Enum.TryParse<CategoryTier>(tierEl.GetString(), out var tier))
                {
                    entries.Add(new CategoryAllowlistEntry(id, name, tier.ToString()));
                    map[id] = tier;
                }
            }
        }

        return (entries, map);
    }

    public async Task<PoiResult?> FindNearestPoiAsync(double lat, double lon, string iso3, CancellationToken ct = default)
    {
        var diagnostics = await FindNearestPoiWithDiagnosticsAsync(lat, lon, iso3, ct);
        return diagnostics?.BestMatch;
    }

    public async Task<PoiLookupDiagnostics?> FindNearestPoiWithDiagnosticsAsync(
        double lat,
        double lon,
        string iso3,
        CancellationToken ct = default)
    {
        var dbPath = Path.Combine(_dataDir, "poi", $"{iso3}.db");
        if (!File.Exists(dbPath))
        {
            _logger.LogDebug("POI {ISO3}: no db file at {Path}", iso3, dbPath);
            return null;
        }

        var cfg = await _getConfigAsync();

        const double latBuffer = 0.03;
        var lonBuffer = latBuffer / Math.Max(0.1, Math.Cos(lat * Math.PI / 180.0));
        var minLat = lat - latBuffer;
        var maxLat = lat + latBuffer;
        var minLon = lon - lonBuffer;
        var maxLon = lon + lonBuffer;

        var configuredIds = cfg.CategoryAllowlist.Count > 0
            ? cfg.CategoryAllowlist
            : _defaultCategoryTierMap.Keys.ToList();

        var validIds = NormalizeAllowlist(configuredIds)
            .Where(id =>
            {
                if (CategoryIdRegex.IsMatch(id))
                {
                    return true;
                }

                _logger.LogWarning("PoiService: skipping invalid category ID '{Id}' — does not match allowed pattern", id);
                return false;
            })
            .ToList();

        if (validIds.Count == 0)
        {
            _logger.LogWarning("POI {ISO3}: allowlist is empty (or all IDs invalid) — returning null", iso3);
            return new PoiLookupDiagnostics(null, [], 0);
        }

        _logger.LogDebug("POI {ISO3}: querying db at ({Lat:F4},{Lon:F4}), bbox=[{MinLat:F4},{MaxLat:F4},{MinLon:F4},{MaxLon:F4}], {Count} allowlist IDs",
            iso3, lat, lon, minLat, maxLat, minLon, maxLon, validIds.Count);

        var allowlistSql = string.Join(", ", validIds.Select(id => $"'{id}'"));

        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = BuildPoiQuery(minLat, maxLat, minLon, maxLon, allowlistSql);

        PoiResult? best = null;
        var candidates = new List<PoiCandidateDiagnostic>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var point = _geometryFactory.CreatePoint(new Coordinate(lon, lat));
        while (await reader.ReadAsync(ct))
        {
            var poiLat = reader.GetDouble(2);
            var poiLon = reader.GetDouble(3);
            var catId = reader.GetString(4);
            var dist = HaversineMetres(lat, lon, poiLat, poiLon);
            TryReadBoundingBox(reader, out var bbox);
            var bboxContainsPoint = bbox is not null && BoundingBoxContains(bbox, lat, lon);
            Geometry? geometry = null;
            var hasGeometry = bboxContainsPoint && TryReadGeometry(reader, out geometry);
            var geometryContainsPoint = hasGeometry
                && geometry is not null
                && GeometryMatchesPoint(geometry, point);
            var containmentRank = geometryContainsPoint ? 2 : bboxContainsPoint ? 1 : 0;
            var bboxArea = bboxContainsPoint && bbox is not null ? BoundingBoxArea(bbox) : double.MaxValue;

            if (!GetEffectiveTierMap().TryGetValue(catId, out var tier))
            {
                tier = CategoryTier.Default;
            }

            var maxRadius = cfg.RadiusTiers.GetRadius(tier);
            var withinTierRadius = dist <= maxRadius;
            if (!withinTierRadius)
            {
                candidates.Add(new PoiCandidateDiagnostic(
                    reader.GetString(1),
                    catId,
                    tier,
                    dist,
                    containmentRank > 0,
                    containmentRank,
                    false,
                    false,
                    $"rejected: outside {maxRadius} m radius",
                    bboxContainsPoint ? bboxArea : null));
                continue;
            }

            var selected = false;
            var decision = "considered";
            if (best is null || ShouldPreferCandidate(best, tier, dist, containmentRank, bboxArea))
            {
                decision = best is null
                    ? "selected: first valid candidate"
                    : containmentRank > best.ContainmentRank
                        ? "selected: bbox/geometry containment outranked previous best"
                        : containmentRank > 0 && best.ContainmentRank > 0 && bboxArea < (best.BoundingBoxArea ?? double.MaxValue)
                            ? "selected: smaller matching bbox outranked previous best"
                            : "selected: outranked previous best";
                best = new PoiResult(
                    reader.GetString(1),
                    tier,
                    dist,
                    bboxContainsPoint,
                    bboxContainsPoint ? bboxArea : null,
                    containmentRank);
                selected = true;
            }
            else
            {
                decision = containmentRank == 0 && best.ContainmentRank > 0
                    ? "considered: outside winning bbox/geometry match"
                    : "considered: weaker than current best";
            }

            candidates.Add(new PoiCandidateDiagnostic(
                reader.GetString(1),
                catId,
                tier,
                dist,
                bboxContainsPoint,
                containmentRank,
                true,
                selected,
                decision,
                bboxContainsPoint ? bboxArea : null));
        }

        _logger.LogDebug("POI {ISO3}: result={Result}", iso3, best?.Name ?? "(none)");
        return new PoiLookupDiagnostics(best, candidates, validIds.Count);
    }

    private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6_371_000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
              * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static string BuildPoiQuery(
        double minLat,
        double maxLat,
        double minLon,
        double maxLon,
        string allowlistSql)
    {
        return $"""
            SELECT
                id,
                name,
                latitude,
                longitude,
                primary_category_id,
                geom_wkb,
                bbox_xmin,
                bbox_ymin,
                bbox_xmax,
                bbox_ymax
            FROM poi
            WHERE latitude  BETWEEN {minLat} AND {maxLat}
              AND longitude BETWEEN {minLon} AND {maxLon}
              AND primary_category_id IN ({allowlistSql})
            """;
    }

    private static bool TryReadBoundingBox(SqliteDataReader reader, out BoundingBox? bbox)
    {
        bbox = null;
        if (reader.FieldCount < 10
            || reader.IsDBNull(6)
            || reader.IsDBNull(7)
            || reader.IsDBNull(8)
            || reader.IsDBNull(9))
        {
            return false;
        }

        bbox = new BoundingBox(
            reader.GetDouble(6),
            reader.GetDouble(7),
            reader.GetDouble(8),
            reader.GetDouble(9));
        return true;
    }

    private static bool TryReadGeometry(SqliteDataReader reader, out Geometry? geometry)
    {
        geometry = null;
        if (reader.FieldCount < 10 || reader.IsDBNull(5))
        {
            return false;
        }

        try
        {
            geometry = WkbReader.Read(ReadBlobValue(reader.GetValue(5)));
            return geometry is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool GeometryMatchesPoint(Geometry geometry, Point point)
    {
        if (geometry.Covers(point))
        {
            return true;
        }

        return geometry.Distance(point) <= 0.00015;
    }

    private static byte[] ReadBlobValue(object value)
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

    private static bool BoundingBoxContains(BoundingBox bbox, double lat, double lon) =>
        lon >= bbox.XMin && lon <= bbox.XMax && lat >= bbox.YMin && lat <= bbox.YMax;

    private static double BoundingBoxArea(BoundingBox bbox) =>
        Math.Abs((bbox.XMax - bbox.XMin) * (bbox.YMax - bbox.YMin));

    private static bool ShouldPreferCandidate(
        PoiResult currentBest,
        CategoryTier candidateTier,
        double candidateDistance,
        int candidateContainmentRank,
        double candidateBboxArea)
    {
        if (candidateTier < currentBest.Tier)
        {
            return true;
        }

        if (candidateTier > currentBest.Tier)
        {
            return false;
        }

        if (candidateContainmentRank > currentBest.ContainmentRank)
        {
            return true;
        }

        if (candidateContainmentRank < currentBest.ContainmentRank)
        {
            return false;
        }

        if (candidateContainmentRank > 0 && currentBest.ContainmentRank > 0)
        {
            var currentBestArea = currentBest.BoundingBoxArea ?? double.MaxValue;
            if (candidateBboxArea < currentBestArea)
            {
                return true;
            }
        }

        return candidateDistance < currentBest.DistanceMetres;
    }

    private sealed record BoundingBox(double XMin, double YMin, double XMax, double YMax);

    private IReadOnlyDictionary<string, CategoryTier> GetEffectiveTierMap()
    {
        var path = GetCategoryCatalogPath();
        var stampUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        lock (_effectiveTierMapLock)
        {
            if (_effectiveTierMap is not null && stampUtc == _effectiveTierMapStampUtc)
            {
                return _effectiveTierMap;
            }

            var map = new Dictionary<string, CategoryTier>(_defaultCategoryTierMap);
            var catalog = LoadCachedCatalog(path);
            if (catalog is not null)
            {
                foreach (var category in catalog.Categories)
                {
                    if (map.ContainsKey(category.CategoryId))
                    {
                        continue;
                    }

                    foreach (var ancestorId in category.GetHierarchyIds().Reverse())
                    {
                        if (_defaultCategoryTierMap.TryGetValue(ancestorId, out var inheritedTier))
                        {
                            map[category.CategoryId] = inheritedTier;
                            break;
                        }
                    }
                }
            }

            _effectiveTierMap = map;
            _effectiveTierMapStampUtc = stampUtc;
            return _effectiveTierMap;
        }
    }

    private IEnumerable<string> NormalizeAllowlist(IEnumerable<string> categoryIds)
    {
        var normalized = FoursquareCategoryIds.NormalizeConfiguredIds(categoryIds);
        if (!normalized.SequenceEqual(categoryIds))
        {
            _logger.LogInformation(
                "PoiService: upgraded legacy airport category ID {LegacyId} to live Airport category ID {AirportId}",
                FoursquareCategoryIds.LegacyAirportGate,
                FoursquareCategoryIds.Airport);
        }

        return normalized;
    }

    private static FoursquareCategoryCatalog? LoadCachedCatalog(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FoursquareCategoryCatalog>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    private string GetCategoryCatalogPath() => Path.Combine(_dataDir, "poi", "categories_os.json");
}

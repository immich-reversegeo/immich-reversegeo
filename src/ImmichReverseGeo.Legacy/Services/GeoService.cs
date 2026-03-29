using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO.Converters;

namespace ImmichReverseGeo.Legacy.Services;

public class GeoService
{
    private readonly ILogger<GeoService> _logger;
    private readonly Task<STRtree<(Geometry Geom, IAttributesTable Props)>> _adm0Task;
    private readonly ConcurrentDictionary<string, CountryIndex> _countryIndexes = new();

    private static readonly JsonSerializerOptions _geoJsonOptions = new()
    {
        Converters = { new GeoJsonConverterFactory() }
    };

    public GeoService(ILogger<GeoService> logger, StorageOptions dirs)
        : this(logger, StartLoading(dirs.BundledDataDir, logger))
    {
    }

    private GeoService(ILogger<GeoService> logger,
                       Task<STRtree<(Geometry, IAttributesTable)>> adm0Task)
    {
        _logger = logger;
        _adm0Task = adm0Task;
    }

    private static Task<STRtree<(Geometry, IAttributesTable)>> StartLoading(
        string bundledDataDir, ILogger logger)
    {
        var adm0Path = Path.Combine(bundledDataDir, "adm0", "ne_10m_admin_0_countries.geojson");
        var sw = Stopwatch.StartNew();
        logger.LogInformation("GeoService: ADM0 index load starting ({Path})", adm0Path);

        var task = Task.Factory.StartNew(
            () => BuildIndex(File.ReadAllText(adm0Path)),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        task.ContinueWith(t =>
        {
            sw.Stop();
            if (t.IsCompletedSuccessfully)
            {
                logger.LogInformation("GeoService: ADM0 index ready in {Elapsed}ms", sw.ElapsedMilliseconds);
            }
            else
            {
                logger.LogError(t.Exception, "GeoService: ADM0 index load failed after {Elapsed}ms", sw.ElapsedMilliseconds);
            }
        }, TaskScheduler.Default);

        return task;
    }

    public static GeoService CreateFromString(string adm0GeoJson, ILogger<GeoService> logger)
    {
        return new GeoService(logger, Task.FromResult(BuildIndex(adm0GeoJson)));
    }

    public Task EnsureInitializedAsync() => _adm0Task;

    public bool IsInitialized => _adm0Task.IsCompletedSuccessfully;

    public (string? iso3, string? name) FindCountry(double lat, double lon)
    {
        var point = new Point(lon, lat);
        var candidates = _adm0Task.GetAwaiter().GetResult().Query(point.EnvelopeInternal);

        (Geometry Geom, IAttributesTable Props)? nearest = null;
        double nearestDist = double.MaxValue;

        foreach (var (geom, props) in candidates)
        {
            if (geom.Contains(point))
            {
                var iso3 = props["ISO_A3"]?.ToString();
                if (iso3 == "-99")
                {
                    iso3 = props["ADM0_A3"]?.ToString();
                }

                return (iso3, props["NAME"]?.ToString());
            }

            var dist = geom.Distance(point);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = (geom, props);
            }
        }

        const double MaxProximityDegrees = 0.1;
        if (nearest.HasValue && nearestDist < MaxProximityDegrees)
        {
            var props = nearest.Value.Props;
            var iso3 = props["ISO_A3"]?.ToString();
            if (iso3 == "-99")
            {
                iso3 = props["ADM0_A3"]?.ToString();
            }

            return (iso3, props["NAME"]?.ToString());
        }

        return (null, null);
    }

    public Envelope? GetCountryEnvelope(string iso3)
    {
        foreach (var (geom, props) in _adm0Task.GetAwaiter().GetResult().Query(new Envelope(-180, 180, -90, 90)))
        {
            var currentIso3 = props["ISO_A3"]?.ToString();
            if (currentIso3 == "-99")
            {
                currentIso3 = props["ADM0_A3"]?.ToString();
            }

            if (string.Equals(currentIso3, iso3, StringComparison.OrdinalIgnoreCase))
            {
                return geom.EnvelopeInternal;
            }
        }

        return null;
    }

    public GeoResult FindAdminLevels(double lat, double lon, string iso3, string countryName)
    {
        if (!_countryIndexes.TryGetValue(iso3, out var idx))
        {
            _logger.LogWarning("No country index loaded for {ISO3}", iso3);
            return new GeoResult(Country: countryName, State: null, City: null);
        }

        var point = new Point(lon, lat);
        string? adm1 = QueryIndex(idx.Adm1, point);
        string? adm2 = QueryIndex(idx.Adm2, point);
        string? adm3 = QueryIndex(idx.Adm3, point);
        string? city = adm3 ?? adm2;

        return new GeoResult(Country: countryName, State: adm1, City: city);
    }

    public bool IsCountryLoaded(string iso3) => _countryIndexes.ContainsKey(iso3);

    public void UnloadCountry(string iso3) => _countryIndexes.TryRemove(iso3, out _);

    public async Task LoadCountryIndexAsync(string iso3, string dataDir)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("GeoService: loading country index for {ISO3}", iso3);
        var boundaryDir = Path.Combine(dataDir, "boundaries", iso3);

        STRtree<(Geometry, IAttributesTable)>? adm1 = null, adm2 = null, adm3 = null;

        for (int level = 1; level <= 3; level++)
        {
            var path = Path.Combine(boundaryDir, $"adm{level}.geojson");
            if (!File.Exists(path))
            {
                continue;
            }

            var json = await File.ReadAllTextAsync(path);
            var tree = BuildIndex(json);
            if (level == 1)
            {
                adm1 = tree;
            }
            else if (level == 2)
            {
                adm2 = tree;
            }
            else
            {
                adm3 = tree;
            }
        }

        _countryIndexes[iso3] = new CountryIndex(adm1, adm2, adm3);
        _logger.LogInformation("GeoService: country index for {ISO3} ready in {Elapsed}ms", iso3, sw.ElapsedMilliseconds);
    }

    private static STRtree<(Geometry, IAttributesTable)> BuildIndex(string geoJson)
    {
        var tree = new STRtree<(Geometry, IAttributesTable)>();
        var collection = JsonSerializer.Deserialize<FeatureCollection>(geoJson, _geoJsonOptions)!;
        foreach (var feature in collection)
        {
            if (feature.Geometry is null)
            {
                continue;
            }

            tree.Insert(feature.Geometry.EnvelopeInternal, (feature.Geometry, feature.Attributes));
        }

        tree.Build();
        return tree;
    }

    private static string? QueryIndex(STRtree<(Geometry, IAttributesTable)>? tree, Point point)
    {
        if (tree is null)
        {
            return null;
        }

        foreach (var (geom, props) in tree.Query(point.EnvelopeInternal))
        {
            if (geom.Contains(point))
            {
                return props["shapeName"]?.ToString();
            }
        }

        return null;
    }

    private record CountryIndex(
        STRtree<(Geometry, IAttributesTable)>? Adm1,
        STRtree<(Geometry, IAttributesTable)>? Adm2,
        STRtree<(Geometry, IAttributesTable)>? Adm3);
}

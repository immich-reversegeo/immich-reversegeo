using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Legacy.Services;

public record AirportResult(string Name, string? Iata, string Type, string? Municipality, double DistanceMetres);

public sealed class AirportService
{
    private readonly ILogger<AirportService> _logger;
    private readonly string _csvPath;
    private IReadOnlyList<AirportRow>? _airports;
    private readonly object _loadLock = new();

    public const double DefaultRadiusMetres = 2_000;

    public AirportService(ILogger<AirportService> logger, StorageOptions dirs)
    {
        _logger = logger;
        _csvPath = Path.Combine(dirs.BundledDataDir, "airports", "airports.csv");
    }

    public AirportResult? FindNearest(double lat, double lon, double radiusMetres = DefaultRadiusMetres)
    {
        var airports = EnsureLoaded();
        if (airports is null)
        {
            return null;
        }

        double latBuf = radiusMetres / 111_000.0;
        double lonBuf = latBuf / Math.Max(0.1, Math.Cos(lat * Math.PI / 180.0));
        double minLat = lat - latBuf, maxLat = lat + latBuf;
        double minLon = lon - lonBuf, maxLon = lon + lonBuf;

        AirportResult? best = null;
        double bestDist = double.MaxValue;

        foreach (var a in airports)
        {
            if (a.Lat < minLat || a.Lat > maxLat || a.Lon < minLon || a.Lon > maxLon)
            {
                continue;
            }

            var dist = HaversineMetres(lat, lon, a.Lat, a.Lon);
            if (dist > radiusMetres || dist >= bestDist)
            {
                continue;
            }

            bestDist = dist;
            best = new AirportResult(a.Name, a.Iata, a.Type, a.Municipality, dist);
        }

        return best;
    }

    public bool IsAvailable => File.Exists(_csvPath);

    private IReadOnlyList<AirportRow>? EnsureLoaded()
    {
        if (_airports is not null)
        {
            return _airports;
        }

        lock (_loadLock)
        {
            if (_airports is not null)
            {
                return _airports;
            }

            if (!File.Exists(_csvPath))
            {
                _logger.LogWarning("AirportService: CSV not found at {Path}", _csvPath);
                return null;
            }

            var sw = Stopwatch.StartNew();
            _logger.LogInformation("AirportService: loading {Path}", _csvPath);
            var rows = new List<AirportRow>();

            using var reader = new StreamReader(_csvPath);
            reader.ReadLine();

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var row = TryParseLine(line);
                if (row is not null)
                {
                    rows.Add(row);
                }
            }

            _airports = rows;
            _logger.LogInformation("AirportService: loaded {Count} airports in {Elapsed}ms",
                rows.Count, sw.ElapsedMilliseconds);
            return _airports;
        }
    }

    private static AirportRow? TryParseLine(string line)
    {
        var fields = SplitCsvLine(line);
        if (fields.Length < 12)
        {
            return null;
        }

        var type = fields[2];
        if (type is "heliport" or "seaplane_base" or "balloonport" or "closed")
        {
            return null;
        }

        if (!double.TryParse(fields[4], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
            !double.TryParse(fields[5], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var lon))
        {
            return null;
        }

        var name = fields[3];
        var iata = fields.Length > 13 ? NullIfEmpty(fields[13]) : null;
        var municipality = fields.Length > 10 ? NullIfEmpty(fields[10]) : null;

        return new AirportRow(name, lat, lon, type, iata, municipality);
    }

    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static string? NullIfEmpty(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

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

    private record AirportRow(string Name, double Lat, double Lon,
                               string Type, string? Iata, string? Municipality);
}

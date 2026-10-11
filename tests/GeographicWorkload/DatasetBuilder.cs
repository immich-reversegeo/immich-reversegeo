using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.GeographicWorkload;

internal static partial class Program
{
    private const string GeoNamesUrl = "https://download.geonames.org/export/dump/cities5000.zip";

    private static async Task GenerateAsync(string root, string state)
    {
        Require(!File.Exists(Path.Combine(state, "uploaded.jsonl")), "A seeded manifest is immutable. Use a new state directory to generate a different workload.");
        var archivePath = Path.Combine(state, "cities5000.zip");
        if (!File.Exists(archivePath))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await using var source = await http.GetStreamAsync(GeoNamesUrl);
            await using var output = File.Create(archivePath + ".download");
            await source.CopyToAsync(output);
            await output.FlushAsync();
            output.Close();
            File.Move(archivePath + ".download", archivePath);
        }
        var cities = new List<CoordinateCase>();
        using (var archive = ZipFile.OpenRead(archivePath))
        using (var text = new StreamReader(archive.GetEntry("cities5000.txt")!.Open()))
        {
            while (await text.ReadLineAsync() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length < 19 || !double.TryParse(parts[4], CultureInfo.InvariantCulture, out var lat)
                    || !double.TryParse(parts[5], CultureInfo.InvariantCulture, out var lon) || !ValidCoordinate(lat, lon))
                {
                    continue;
                }
                cities.Add(new(parts[1], "geonames-city", lat, lon, parts[8], parts[1],
                    $"https://www.geonames.org/{parts[0]}/", "reference-country"));
            }
        }
        var selected = Balanced(cities, item => item.ReferenceCountry ?? "", 3000);
        Require(selected.Count == 3000, "GeoNames archive does not contain enough valid cities.");
        var workload = new List<CoordinateCase>(selected);
        var random = new Random(20261011);
        // Repeated and nearby GPS locations model a library clustered around trips/home.
        for (var index = 0; index < 1000; index++)
        {
            var city = selected[index % 100];
            var lat = city.Latitude;
            var lon = city.Longitude;
            if (index % 2 != 0)
            {
                lat = Math.Clamp(lat + (random.NextDouble() - 0.5) * 0.003, -90, 90);
                lon = Math.Clamp(lon + (random.NextDouble() - 0.5) * 0.003, -180, 180);
            }
            workload.Add(city with { Label = $"{city.Label} cluster {index:D4}", Category = index % 2 == 0 ? "repeated-coordinate" : "nearby-cluster", Latitude = lat, Longitude = lon, Expectation = "review" });
        }
        var airports = new List<CoordinateCase>();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "src/ImmichReverseGeo.Web/bundled-data/defaults/overture-airports.db")};Mode=ReadOnly;Pooling=false"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name, latitude, longitude FROM infrastructure";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var lat = reader.GetDouble(2);
                var lon = reader.GetDouble(3);
                if (ValidCoordinate(lat, lon))
                {
                    airports.Add(new(reader.GetString(1), "airport", lat, lon, null, null, $"bundled Overture airport {reader.GetString(0)}", "review-airport-setting"));
                }
            }
        }
        var airportSample = Balanced(airports, item => $"{(int)(item.Latitude / 15)}:{(int)(item.Longitude / 30)}", 125);
        Require(airportSample.Count == 125, "Airport artifact does not contain enough valid points.");
        foreach (var airport in airportSample)
        {
            workload.Add(airport);
            workload.Add(airport with { Label = airport.Label + " nearby", Category = "airport-nearby", Latitude = Math.Clamp(airport.Latitude + 0.02, -90, 90), Expectation = "review-airport-fallback" });
        }
        var edges = new List<CoordinateCase>();
        foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories.Concat(CountryResolutionFixtureCatalog.ParentCountryControls))
        {
            edges.Add(new(fixture.Label, "territory-control", fixture.Latitude, fixture.Longitude, fixture.Alpha2, null, "repository country-resolution fixture catalog", "required-country"));
        }
        edges.AddRange([
            new("Munich district choice", "issue", 48.149908, 11.586298, "DE", "München", "https://github.com/immich-reversegeo/immich-reversegeo/issues/2", "review-city-profile"),
            new("Salo neighborhood choice", "issue", 60.387, 23.128, "FI", "Salo", "https://github.com/immich-reversegeo/immich-reversegeo/issues/2", "review-city-profile"),
            new("Lyon-Bron airport vs commune", "issue", 45.728054, 4.938994, "FR", "Bron", "https://github.com/immich-reversegeo/immich-reversegeo/issues/2", "review-airport-setting"),
            new("Chokoloskee rural island", "issue", 25.810042802497108, -81.36240449944354, "US", "Chokoloskee", "https://github.com/immich-reversegeo/immich-reversegeo/issues/10", "review-rural-fallback"),
            new("Florida wetlands", "issue", 25.767375, -81.3800694, "US", null, "https://github.com/immich-reversegeo/immich-reversegeo/issues/10", "review-no-city"),
            new("Farsta labels", "issue", 59.241061, 18.101586, "SE", "Larsboda", "https://github.com/immich-reversegeo/immich-reversegeo/issues/16", "review-city-profile"),
            new("Skogås labels", "issue", 59.218311, 18.15625, "SE", "Skogås", "https://github.com/immich-reversegeo/immich-reversegeo/issues/17", "review-city-profile"),
            new("Rod an der Weil municipality", "issue", 50.3364087, 8.3754539, "DE", "Rod an der Weil", "https://github.com/immich-app/immich/issues/31157", "review-municipality"),
            new("Null Island", "no-match-probe", 0, 0, null, null, "synthetic equator/prime-meridian intersection", "review-no-match"),
            new("Antimeridian east", "antimeridian", -16, 179.99999, null, null, "synthetic antimeridian probe", "review"),
            new("Antimeridian west", "antimeridian", -16, -179.99999, null, null, "synthetic antimeridian probe", "review"),
            new("North pole", "polar", 90, 0, null, null, "synthetic polar limit", "review"),
            new("South pole", "polar", -90, 0, null, null, "synthetic polar limit", "review")
        ]);
        foreach (var code in new[] { "VA", "SM", "MC", "AD", "LI", "GI" })
        {
            var city = cities.FirstOrDefault(item => item.ReferenceCountry == code);
            if (city is not null)
            {
                edges.Add(city with { Category = "microstate", Expectation = "reference-country" });
            }
        }
        var edgeAnchors = edges.ToArray();
        while (edges.Count < 250)
        {
            var index = edges.Count;
            var anchor = edgeAnchors[index % edgeAnchors.Length];
            var offset = new[] { -0.00005, 0.00005, -0.00015, 0.00015, -0.0003, 0.0003 }[(index / edgeAnchors.Length) % 6];
            edges.Add(anchor with { Label = anchor.Label + $" offset {offset:G}", Category = "edge-nearby", Latitude = Math.Clamp(anchor.Latitude + offset, -90, 90), Expectation = "review-boundary-tolerance" });
        }
        workload.AddRange(edges.Take(250));
        for (var index = 0; index < 500; index++)
        {
            // Uniform area sampling, unlike uniform latitude which oversamples poles.
            var lat = Math.Asin(random.NextDouble() * 2 - 1) * 180 / Math.PI;
            var lon = random.NextDouble() * 360 - 180;
            workload.Add(new($"Global area probe {index:D3}", "global-area", lat, lon, null, null, "deterministic synthetic sphere sample; seed 20261011", "review-no-match"));
        }
        var privatePath = Path.Combine(state, "production-coordinates.json");
        if (File.Exists(privatePath))
        {
            workload.AddRange(JsonSerializer.Deserialize<List<CoordinateCase>>(await File.ReadAllTextAsync(privatePath), Json)!);
        }
        Require(workload.All(item => ValidCoordinate(item.Latitude, item.Longitude)), "Invalid coordinate in workload.");
        // Interleave countries/categories so concurrent assets exercise shared cache creation.
        workload = workload.OrderBy(item => Hash(item.Label + item.Category)).ToList();
        await File.WriteAllTextAsync(Path.Combine(state, "coordinates.json"), JsonSerializer.Serialize(workload, Json));
        var sourceInfo = new
        {
            generatedAtUtc = DateTime.UtcNow, seed = 20261011, total = workload.Count,
            countries = selected.Select(item => item.ReferenceCountry).Distinct().Count(),
            categories = workload.GroupBy(item => item.Category).ToDictionary(group => group.Key, group => group.Count()),
            geonames = new { url = GeoNamesUrl, license = "CC BY 4.0", attribution = "GeoNames https://www.geonames.org", sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath))) },
            expectations = "Reference city names and issue reports require review; only curated country fixtures are required-country assertions. Private coordinates are not correctness oracles."
        };
        await File.WriteAllTextAsync(Path.Combine(state, "dataset-summary.json"), JsonSerializer.Serialize(sourceInfo, Json));
        Console.WriteLine($"Generated {workload.Count} coordinates across {sourceInfo.countries} GeoNames countries/territories.");
        foreach (var category in sourceInfo.categories)
        {
            Console.WriteLine($"  {category.Key}: {category.Value}");
        }
    }

    private static List<CoordinateCase> Balanced(List<CoordinateCase> values, Func<CoordinateCase, string> grouping, int count)
    {
        var groups = values.GroupBy(grouping).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new Queue<CoordinateCase>(group.OrderBy(item => Hash(item.Source)))).ToList();
        var result = new List<CoordinateCase>();
        while (result.Count < count && groups.Any(group => group.Count > 0))
        {
            foreach (var group in groups)
            {
                if (group.Count > 0 && result.Count < count)
                {
                    result.Add(group.Dequeue());
                }
            }
        }
        return result;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

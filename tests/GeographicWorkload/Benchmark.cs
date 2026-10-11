using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.GeographicWorkload;

internal static partial class Program
{
    // Opt-in resolver benchmark: no production access, database writes, or UI changes.
    private static async Task BenchmarkAsync(string root, string state, string[] args)
    {
        Require(args.Length is >= 3 and <= 5 && args[0] is "normal" or "serial" or "low"
            && args[1] is "country" or "location" && int.TryParse(args[2], out var idle) && idle is >= 0 and <= 900,
            "Usage: benchmark <root> <state> <normal|serial|low> <country|location> <idle-seconds> [country-point-limit | location-country location-point-limit]");
        var idleSeconds = int.Parse(args[2]);
        using var revisionProcess = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = root, ArgumentList = { "rev-parse", "HEAD" },
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        })!;
        var revision = (await revisionProcess.StandardOutput.ReadToEndAsync()).Trim();
        await revisionProcess.WaitForExitAsync();
        Require(revisionProcess.ExitCode == 0, "Cannot record the app revision.");
        var mode = args[0];
        var scope = args[1];
        var gcMode = System.Runtime.GCSettings.IsServerGC ? "server" : "workstation";
        var selectedCountry = scope == "location" && args.Length >= 4 ? args[3] : "DEU";
        Require(selectedCountry is "DEU" or "CHE", "This bounded benchmark supports only DEU or CHE location subsets.");
        var limitText = scope == "country" && args.Length >= 4 ? args[3] : args.Length == 5 ? args[4] : "20000";
        var controlsOnly = scope == "country" && limitText == "controls";
        Require(int.TryParse(controlsOnly ? "20000" : limitText, out var limit) && limit is >= 1 and <= 20000 && (scope != "country" || args.Length <= 4), "Point limit must be 1 through 20000; country scope also accepts controls.");
        var output = Path.Combine(state, "benchmarks");
        Directory.CreateDirectory(output);
        var data = Path.Combine(output, "data", mode);
        Directory.CreateDirectory(data);
        var bundled = Path.Combine(root, "src", "ImmichReverseGeo.Web", "bundled-data");
        var storage = new StorageOptions(data, bundled);
        var countryPath = Path.Combine(bundled, "defaults", "overture-country-divisions.db");
        await using var countryStream = File.OpenRead(countryPath);
        var countryHash = Convert.ToHexString(await SHA256.HashDataAsync(countryStream));
        var policy = new MemoryResourcePolicy { LowMemoryMode = mode == "low" };
        var config = new ProcessingConfig { LowMemoryMode = mode == "low", MaxDegreeOfParallelism = mode == "normal" ? 4 : 1 };
        var codes = new CountryCodeService(bundled);
        var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, data, bundled, policy);
        var cacheMiB = int.TryParse(Environment.GetEnvironmentVariable("COUNTRY_CACHE_MIB"), out var requestedMiB) ? requestedMiB : 0;
        Require(cacheMiB is >= 0 and <= 1024, "COUNTRY_CACHE_MIB must be 0 (default cache) or 1 through 1024.");
        config.UseBoundedCountryCache = policy.UseBoundedCountryCache = cacheMiB > 0;
        config.CountryCacheMiB = policy.CountryCacheMiB = cacheMiB > 0 ? cacheMiB : 256;
        using var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, data, bundled, codes.Alpha2ToIso3, policy);
        var cache = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, storage, codes.Iso3ToAlpha2, policy);
        var resolver = new AssetLocationResolverService(new AdministrativeAreaResolverService(
            NullLogger<AdministrativeAreaResolverService>.Instance,
            new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage), divisions, cache,
            new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, data),
            new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, storage, policy)), places);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(state, "coordinates.json"));
        var coordinates = JsonSerializer.Deserialize<List<CoordinateCase>>(manifestBytes, Json)!;
        var indices = Enumerable.Range(0, coordinates.Count).ToArray();
        if (controlsOnly)
        {
            indices = indices.Where(index => coordinates[index].Expectation == "required-country").ToArray();
            Require(indices.Length > 0, "No maintained country controls found.");
        }
        string? cacheHash = null;
        if (scope == "location")
        {
            // Country benchmark establishes actual country matches, including private samples.
            // Reuse a real existing cache rather than download hundreds of countries implicitly.
            var countryResults = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(output, "normal-country-results.json")), Json)!;
            Require(countryResults.Length == coordinates.Count, "Country result manifest length mismatch.");
            indices = indices.Where(index => countryResults[index].StartsWith(selectedCountry + "|", StringComparison.Ordinal)).ToArray();
            Require(indices.Length > 0, "No selected-country coordinates in this workload.");
            var source = selectedCountry == "DEU"
                ? Path.Combine(root, "src", "ImmichReverseGeo.Web", "localdata", "overture-divisions", "DEU.db")
                : Path.Combine(output, "prepared-data", "overture-divisions", "CHE.db");
            Require(File.Exists(source), "Prepare the selected administrative cache first.");
            Directory.CreateDirectory(Path.Combine(data, "overture-divisions"));
            var target = Path.Combine(data, "overture-divisions", selectedCountry + ".db");
            File.Copy(source, target, true);
            await using (var cacheStream = File.OpenRead(target))
            {
                cacheHash = Convert.ToHexString(await SHA256.HashDataAsync(cacheStream));
            }
            Require(cache.HasData(selectedCountry), "Existing cache does not satisfy current coverage; refusing an implicit download.");
        }
        var eligiblePoints = indices.Length;
        if (indices.Length > limit)
        {
            // Evenly spaced positions in the immutable shuffled manifest, without RNG drift.
            indices = Enumerable.Range(0, limit).Select(index => indices[(int)((long)index * eligiblePoints / limit)]).ToArray();
        }
        var profile = controlsOnly ? "country-controls" : scope == "country" && eligiblePoints == indices.Length ? scope : $"{scope}-{(scope == "location" ? selectedCountry + "-" : "")}{indices.Length}";
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var name = $"{mode}-{profile}-{gcMode}-{stamp}";
        using var process = Process.GetCurrentProcess();
        var samples = new List<BenchmarkSample>();
        var phase = "baseline";
        var clock = Stopwatch.StartNew();
        using var sampleCts = new CancellationTokenSource();
        var sampleJson = new JsonSerializerOptions(Json) { WriteIndented = false };
        var sampling = Task.Run(async () =>
        {
            using var stream = new StreamWriter(Path.Combine(output, name + "-samples.jsonl"));
            while (!sampleCts.IsCancellationRequested)
            {
                process.Refresh();
                var sample = new BenchmarkSample(clock.Elapsed.TotalSeconds, Volatile.Read(ref phase), process.WorkingSet64,
                    process.PrivateMemorySize64, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes,
                    GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
                lock (samples)
                {
                    samples.Add(sample);
                }
                await stream.WriteLineAsync(JsonSerializer.Serialize(sample, sampleJson));
                await stream.FlushAsync();
                try { await Task.Delay(100, sampleCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        var passes = new List<object>();
        try
        {
            await Task.Delay(1000);
            for (var pass = 1; pass <= 2; pass++)
            {
                phase = $"pass-{pass}";
                var results = new string[coordinates.Count];
                Array.Fill(results, "not-selected");
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var cpuBefore = process.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                var done = 0;
                var errors = 0;
                await Parallel.ForEachAsync(indices, new ParallelOptions { MaxDegreeOfParallelism = config.EffectiveParallelism }, async (index, ct) =>
                {
                    var point = coordinates[index];
                    try
                    {
                        if (scope == "country")
                        {
                            var result = await divisions.FindBundledCountryAsync(point.Latitude, point.Longitude, ct);
                            results[index] = $"{result.Iso3}|{result.Status}|{result.CountryName}";
                        }
                        else
                        {
                            var result = await resolver.ResolveAsync(point.Latitude, point.Longitude, config, ct: ct);
                            results[index] = result is null ? "no-match" : $"{result.Country}|{result.State}|{result.City}|{result.HasMatch}";
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        results[index] = "error:" + ex.GetType().Name;
                    }
                    var count = Interlocked.Increment(ref done);
                    if (count % 1000 == 0 || count % 100 == 0 && timer.Elapsed.TotalSeconds >= 10)
                    {
                        Console.WriteLine($"{mode}/{scope} pass {pass}: {count}/{indices.Length} in {timer.Elapsed.TotalSeconds:F1}s");
                    }
                });
                timer.Stop();
                process.Refresh();
                BenchmarkSample[] passSamples;
                lock (samples)
                {
                    passSamples = samples.Where(sample => sample.Phase == phase).ToArray();
                }
                var resultBytes = JsonSerializer.SerializeToUtf8Bytes(results, Json);
                var metrics = new
                {
                    pass, points = indices.Length, errors, seconds = timer.Elapsed.TotalSeconds,
                    countryCache = divisions.CountryCacheStatistics,
                    cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                    peakRssBytes = Math.Max(process.WorkingSet64, passSamples.Select(sample => sample.RssBytes).DefaultIfEmpty().Max()),
                    peakPrivateBytes = Math.Max(process.PrivateMemorySize64, passSamples.Select(sample => sample.PrivateBytes).DefaultIfEmpty().Max()),
                    endManagedBytes = GC.GetTotalMemory(false), outputSha256 = Convert.ToHexString(SHA256.HashData(resultBytes))
                };
                passes.Add(metrics);
                await File.WriteAllBytesAsync(Path.Combine(output, $"{mode}-{profile}-results.json"), resultBytes);
                Console.WriteLine(JsonSerializer.Serialize(metrics, Json));
                phase = $"idle-after-{pass}";
                await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
            }
            // Diagnostic only: distinguish collectible garbage from retained live objects.
            // This is deliberately outside timed runs and ordinary idle measurements.
            phase = "forced-gc-diagnostic";
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            await Task.Delay(1000);
            GC.KeepAlive(resolver);
            GC.KeepAlive(divisions);
            if (!policy.LowMemoryMode)
            {
                phase = "switch-to-low-idle";
                policy.LowMemoryMode = true;
                await Task.Delay(1000);
                phase = "switch-to-low-forced-gc";
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                await Task.Delay(1000);
                GC.KeepAlive(resolver);
                GC.KeepAlive(divisions);
            }
        }
        finally
        {
            sampleCts.Cancel();
            await sampling;
            var report = new { mode, scope, profile, gcMode, eligiblePoints, selectedCountry, revision, config, cacheMiB,
                countryCache = divisions.CountryCacheStatistics, countrySha256 = countryHash, workers = config.EffectiveParallelism, idleSeconds,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                processorCount = Environment.ProcessorCount, datasetSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes)),
                cacheSha256 = cacheHash, passes, samples };
            await File.WriteAllTextAsync(Path.Combine(output, name + "-summary.json"), JsonSerializer.Serialize(report, Json));
        }
    }
    private sealed record BenchmarkSample(double Seconds, string Phase, long RssBytes, long PrivateBytes,
        long ManagedBytes, long GcCommittedBytes, long AllocatedBytes, int Gen0, int Gen1, int Gen2);

    private static async Task PrepareBenchmarkCacheAsync(string root, string state)
    {
        var data = Path.Combine(state, "benchmarks", "prepared-data");
        Directory.CreateDirectory(data);
        var bundled = Path.Combine(root, "src", "ImmichReverseGeo.Web", "bundled-data");
        var codes = new CountryCodeService(bundled);
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        var cache = new OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance,
            new StorageOptions(data, bundled), codes.Iso3ToAlpha2, policy);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        await cache.EnsureDataAsync("CHE", timeout.Token);
        process.Refresh();
        var report = new { country = "CHE", mode = "low", seconds = clock.Elapsed.TotalSeconds,
            processPeakRssBytes = process.PeakWorkingSet64, status = cache.GetStatus()["CHE"] };
        await File.WriteAllTextAsync(Path.Combine(state, "benchmarks", "cache-preparation.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(report, Json));
    }
}

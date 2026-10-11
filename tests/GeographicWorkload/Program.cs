using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;

namespace ImmichReverseGeo.GeographicWorkload;

internal static partial class Program
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal const string Marker = "immich-reversegeo-geographic-workload";
    internal const int ApiPort = 18283;
    internal const int DbPort = 18433;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length < 3)
            {
                throw new ArgumentException("Usage: GeographicWorkload <import|generate|seed|report|reset|benchmark|prepare-benchmark-cache> <repository-root> <local-state-directory>");
            }
            var root = Path.GetFullPath(args[1]);
            var state = Path.GetFullPath(args[2]);
            using var git = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = root,
                ArgumentList = { "rev-parse", "--path-format=absolute", "--git-common-dir" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var commonGitDir = (await git.StandardOutput.ReadToEndAsync()).Trim();
            await git.WaitForExitAsync();
            Require(git.ExitCode == 0 && Path.IsPathRooted(commonGitDir), "Cannot locate the primary checkout.");
            var primaryRoot = Path.GetDirectoryName(commonGitDir)!;
            if (!state.StartsWith(Path.Combine(primaryRoot, ".local") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Keep private coordinates and credentials under this checkout's ignored .local directory.");
            }
            Directory.CreateDirectory(state);
            await File.WriteAllTextAsync(Path.Combine(state, ".gitignore"), "*\n");
            await using var lease = new FileStream(Path.Combine(state, "workload.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            switch (args[0])
            {
                case "import": await ImportProductionAsync(state); break;
                case "generate": await GenerateAsync(root, state); break;
                case "seed": await SeedAsync(state); break;
                case "report": await ReportAsync(state); break;
                case "reset": await ResetAsync(state); break;
                case "benchmark": await BenchmarkAsync(root, state, args.Skip(3).ToArray()); break;
                case "prepare-benchmark-cache": await PrepareBenchmarkCacheAsync(root, state); break;
                default: throw new ArgumentException("Unknown action.");
            }
            return 0;
        }
        catch (Exception ex)
        {
            // Do not print response bodies or credentials from either server.
            Console.Error.WriteLine($"Workload action failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task ImportProductionAsync(string state)
    {
        var config = JsonSerializer.Deserialize<ProductionSource>(await File.ReadAllTextAsync(Path.Combine(state, "production-source.json")), Json)!;
        Require(Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https"
            && baseUri.UserInfo.Length == 0 && baseUri.Query.Length == 0 && baseUri.Fragment.Length == 0,
            "Production source must be an HTTP(S) base URL without embedded credentials.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        var version = await http.GetFromJsonAsync<JsonElement>("/api/server/version");
        Require(version.GetProperty("major").GetInt32() == 3 && version.GetProperty("minor").GetInt32() == 3
            && version.GetProperty("patch").GetInt32() == 1, "The production import currently supports the verified Immich 3.3.1 API only.");
        var key = (await File.ReadAllTextAsync(Path.Combine(state, "production-api-key.txt"))).Trim();
        Require(key.Length > 0 && !key.Contains('\n') && !key.Contains('\r'), "The API-key file must contain one key.");
        http.DefaultRequestHeaders.Add("x-api-key", key);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var coordinates = new List<CoordinateCase>();
        // Bounded random metadata sampling avoids scanning the entire production library.
        // These POSTs are read-only searches; this code has no production mutation paths.
        for (var round = 0; round < 20 && coordinates.Count < 2000; round++)
        {
            using var response = await http.PostAsJsonAsync("/api/search/random", new { size = 1000, withExif = true, withPeople = false, withDeleted = false });
            Require(response.IsSuccessStatusCode, $"Read-only production search returned HTTP {(int)response.StatusCode}. The key needs asset.read.");
            var assets = await response.Content.ReadFromJsonAsync<JsonElement>();
            Require(assets.ValueKind == JsonValueKind.Array, "Unexpected production search response.");
            foreach (var asset in assets.EnumerateArray())
            {
                if (!seen.Add(asset.GetProperty("id").GetString()!) || !asset.TryGetProperty("exifInfo", out var exif)
                    || exif.ValueKind != JsonValueKind.Object || !exif.TryGetProperty("latitude", out var lat)
                    || !exif.TryGetProperty("longitude", out var lon) || lat.ValueKind != JsonValueKind.Number
                    || lon.ValueKind != JsonValueKind.Number || !ValidCoordinate(lat.GetDouble(), lon.GetDouble()))
                {
                    continue;
                }
                coordinates.Add(new($"Production sample {coordinates.Count + 1:D4}", "production-coordinate", lat.GetDouble(), lon.GetDouble(),
                    null, null, "private production coordinate sample", "review"));
                if (coordinates.Count == 2000)
                {
                    break;
                }
            }
            Console.WriteLine($"Production sampling: {coordinates.Count} GPS coordinates from {seen.Count} distinct metadata records.");
        }
        Require(coordinates.Count > 0, "No usable GPS coordinates were found in the key's accessible assets.");
        await File.WriteAllTextAsync(Path.Combine(state, "production-coordinates.json"), JsonSerializer.Serialize(coordinates, Json));
        Console.WriteLine($"Saved {coordinates.Count} private coordinate samples. No production asset IDs, filenames, photos, or timestamps were saved.");
    }

    internal static bool ValidCoordinate(double lat, double lon) => double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record CoordinateCase(string Label, string Category, double Latitude, double Longitude,
    string? ReferenceCountry, string? ReferenceCity, string Source, string Expectation);
internal sealed record ProductionSource(string BaseUrl);

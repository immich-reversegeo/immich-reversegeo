using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Legacy.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[TestCategory("Integration")]
public class IntegrationTests
{
    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task EnsureCountryBoundaries_LIE_DownloadsAndLoads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            const string adm0GeoJson = """
                {
                  "type": "FeatureCollection",
                  "features": [
                    {
                      "type": "Feature",
                      "properties": { "ISO_A3": "LIE", "ADM0_A3": "LIE", "NAME": "Liechtenstein" },
                      "geometry": {
                        "type": "Polygon",
                        "coordinates": [[[9.47,47.04],[9.64,47.04],[9.64,47.27],[9.47,47.27],[9.47,47.04]]]
                      }
                    }
                  ]
                }
                """;

            var geo = GeoService.CreateFromString(adm0GeoJson, NullLogger<GeoService>.Instance);

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            var factory = new SingleClientFactory(httpClient);

            var cache = new DataCacheService(
                NullLogger<DataCacheService>.Instance,
                geo,
                new ConfigService(NullLogger<ConfigService>.Instance),
                factory,
                new StorageOptions(tempDir, "data"));

            await cache.EnsureCountryBoundariesAsync("LIE");

            var adm1Path = Path.Combine(tempDir, "boundaries", "LIE", "adm1.geojson");
            Assert.IsTrue(File.Exists(adm1Path), $"ADM1 file should exist at {adm1Path}");

            var result = geo.FindAdminLevels(47.1410, 9.5209, "LIE", "Liechtenstein");
            Assert.IsNotNull(result.State, "ADM1 (State) should be found for Vaduz, Liechtenstein");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}

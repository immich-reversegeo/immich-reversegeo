using System;
using System.IO;
using System.Threading.Tasks;
using ImmichReverseGeo.Legacy.Services;
using ImmichReverseGeo.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Legacy.Tests;

[TestClass]
public class GeoServiceTests
{
    private GeoService _svc = null!;

    [TestInitialize]
    public void Setup()
    {
        _svc = GeoService.CreateFromString(
            adm0GeoJson: MiniGeoJson.Adm0,
            logger: NullLogger<GeoService>.Instance);
    }

    [TestMethod]
    public void FindCountry_Zurich_ReturnsCHE()
    {
        var result = _svc.FindCountry(47.3769, 8.5417);
        Assert.AreEqual("CHE", result.iso3);
    }

    [TestMethod]
    public void FindCountry_Vienna_ReturnsAUT()
    {
        var result = _svc.FindCountry(48.2082, 16.3738);
        Assert.AreEqual("AUT", result.iso3);
    }

    [TestMethod]
    public void FindCountry_AtSea_ReturnsNull()
    {
        var result = _svc.FindCountry(0.0, 0.0);
        Assert.IsNull(result.iso3);
    }

    [TestMethod]
    public async Task FindAdminLevels_Zurich_ReturnsZurichCanton()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var boundaryDir = Path.Combine(tempDir, "boundaries", "CHE");
        Directory.CreateDirectory(boundaryDir);
        await File.WriteAllTextAsync(Path.Combine(boundaryDir, "adm1.geojson"), MiniGeoJson.CheAdm1);

        await _svc.LoadCountryIndexAsync("CHE", tempDir);

        var result = _svc.FindAdminLevels(47.3769, 8.5417, "CHE", countryName: "Switzerland");
        Assert.AreEqual("Switzerland", result.Country);
        Assert.AreEqual("Zurich", result.State);
        Assert.IsNull(result.City);
        Directory.Delete(tempDir, recursive: true);
    }

    [TestMethod]
    public void FindAdminLevels_IndexNotLoaded_ReturnsCountryOnly()
    {
        var result = _svc.FindAdminLevels(52.52, 13.405, "CHE", "Switzerland");
        Assert.AreEqual("Switzerland", result.Country);
        Assert.IsNull(result.State);
        Assert.IsNull(result.City);
    }
}

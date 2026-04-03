using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CityResolverProfileCatalogServiceTests
{
    [TestMethod]
    public void GetProfile_UsesBundledFranceDefaults()
    {
        using var fixture = CreateFixture();
        var service = fixture.CreateService();

        var profile = service.GetProfile(new CityResolverConfig(), "FRA");

        CollectionAssert.AreEqual(
            new[] { "localadmin", "locality", "county", "borough", "macrohood", "neighborhood", "microhood" },
            profile.PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.LargestArea, profile.TieBreakMode);
    }

    [TestMethod]
    public void GetProfile_AppliesUserOverrideOnTopOfBundledDefaults()
    {
        using var fixture = CreateFixture();
        var service = fixture.CreateService();
        var overrides = new CityResolverConfig
        {
            CountryOverrides = new Dictionary<string, CityResolverProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["FRA"] = new CityResolverProfile
                {
                    PreferredSubtypes = ["locality", "localadmin"],
                    TieBreakMode = CityResolverTieBreakModes.SmallestArea
                }
            }
        };

        var profile = service.GetProfile(overrides, "FRA");

        CollectionAssert.AreEqual(
            new[] { "locality", "localadmin" },
            profile.PreferredSubtypes);
        Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, profile.TieBreakMode);
    }

    private static TestFixture CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var defaults = Path.Combine(root, "defaults");
        Directory.CreateDirectory(defaults);

        var source = Path.Combine(AppContext.BaseDirectory, "data", "city-resolver-profiles.json");
        File.Copy(source, Path.Combine(defaults, "city-resolver-profiles.json"));

        return new TestFixture(root);
    }

    private sealed class TestFixture(string bundledDataDir) : IDisposable
    {
        public CityResolverProfileCatalogService CreateService()
        {
            return new CityResolverProfileCatalogService(
                NullLogger<CityResolverProfileCatalogService>.Instance,
                new StorageOptions(DataDir: Path.GetTempPath(), bundledDataDir));
        }

        public void Dispose()
        {
            if (Directory.Exists(bundledDataDir))
            {
                Directory.Delete(bundledDataDir, recursive: true);
            }
        }
    }
}

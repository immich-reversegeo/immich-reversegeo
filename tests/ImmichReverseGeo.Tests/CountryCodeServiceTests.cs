using System.Text.Json;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CountryCodeServiceTests
{
    [TestMethod]
    public void MandatoryCountryIdentities_MapBothDirectionsAndCanonicalNames()
    {
        var service = CountryCodeService.CreateForTest();
        foreach (var fixture in CountryResolutionFixtureCatalog.MandatoryTerritories.DistinctBy(fixture => fixture.Alpha2))
        {
            Assert.AreEqual(fixture.Alpha2, service.Iso3ToAlpha2(fixture.Alpha3), fixture.Label);
            Assert.AreEqual(fixture.Alpha3, service.Alpha2ToIso3(fixture.Alpha2), fixture.Label);
            Assert.AreEqual(fixture.DisplayName, service.FindByAlpha2(fixture.Alpha2)?.DisplayName, fixture.Label);
        }
    }
    [TestMethod]
    public void Iso3ToAlpha2_CHE_ReturnsCH()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.AreEqual("CH", service.Iso3ToAlpha2("CHE"));
    }

    [TestMethod]
    public void Iso3ToAlpha2_Unknown_ReturnsNull()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.IsNull(service.Iso3ToAlpha2("XYZ"));
    }

    [TestMethod]
    public void Alpha2ToIso3_VA_ReturnsVAT()
    {
        var service = CountryCodeService.CreateForTest();
        Assert.AreEqual("VAT", service.Alpha2ToIso3("VA"));
    }

    [TestMethod]
    [DataRow("HK", "HKG", "Hong Kong")]
    [DataRow("MO", "MAC", "Macao")]
    [DataRow("GL", "GRL", "Greenland")]
    [DataRow("FO", "FRO", "Faroe Islands")]
    [DataRow("JE", "JEY", "Jersey")]
    public void CountryIdentities_MapBothDirectionsAndPinnedNames(string alpha2, string alpha3, string displayName)
    {
        var service = CountryCodeService.CreateForTest();
        Assert.AreEqual(alpha2, service.Iso3ToAlpha2(alpha3));
        Assert.AreEqual(alpha3, service.Alpha2ToIso3(alpha2));
        Assert.AreEqual(displayName, service.FindByAlpha2(alpha2)?.DisplayName);
    }
    [TestMethod]
    public void CanonicalCatalog_ExplicitlyNamesEveryMappedIdentity()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var isoPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");
        using var document = JsonDocument.Parse(File.ReadAllText(isoPath));
        var root = document.RootElement;
        var mappedAlpha2 = root.GetProperty("iso3ToAlpha2")
            .EnumerateObject()
            .Select(property => property.Value.GetString()!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var explicitlyNamedAlpha2 = root.GetProperty("displayNamesByAlpha2")
            .EnumerateObject()
            .Select(property => property.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(mappedAlpha2, explicitlyNamedAlpha2);
    }

    [TestMethod]
    public void BundledCountryCodes_AreMappedOrExplicitlyNonIso()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dbPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db");
        var isoPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");

        Assert.IsTrue(File.Exists(dbPath), $"Bundled country divisions DB not found at {dbPath}");
        Assert.IsTrue(File.Exists(isoPath), $"ISO identity catalog not found at {isoPath}");

        var catalog = CountryIdentityCatalog.Load(isoPath);
        var missing = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT country FROM division_area WHERE country IS NOT NULL AND TRIM(country) <> ''";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var alpha2 = reader.GetString(0).ToUpperInvariant();
            if (catalog.FindByAlpha2(alpha2) is null && !catalog.IsExplicitlyNonIso(alpha2))
            {
                missing.Add(alpha2);
            }
        }

        CollectionAssert.AreEquivalent(Array.Empty<string>(), missing);
    }
}

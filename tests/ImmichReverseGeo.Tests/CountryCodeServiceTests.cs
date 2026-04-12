using System.Text.Json;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CountryCodeServiceTests
{
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
    public void BundledCountryCodes_AreMappedOrExplicitlyNonIso()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dbPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "defaults", "overture-country-divisions.db");
        var isoPath = Path.Combine(repoRoot, "src", "ImmichReverseGeo.Web", "bundled-data", "iso3166.json");

        Assert.IsTrue(File.Exists(dbPath), $"Bundled country divisions DB not found at {dbPath}");
        Assert.IsTrue(File.Exists(isoPath), $"ISO mapping file not found at {isoPath}");

        var iso3ToAlpha2 = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(isoPath))
            ?? throw new InvalidOperationException("Failed to parse iso3166.json");
        var mappedAlpha2 = iso3ToAlpha2.Values
            .Select(value => value.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var explicitlyNonIso = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "XA", "XB", "XC", "XD", "XG", "XH", "XI", "XL", "XM", "XN", "XO",
            "XP", "XQ", "XR", "XT", "XU", "XW", "XX", "XY", "XZ"
        };

        var missing = new List<string>();

        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT country FROM division_area WHERE country IS NOT NULL AND TRIM(country) <> ''";
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var alpha2 = reader.GetString(0).ToUpperInvariant();
            if (!mappedAlpha2.Contains(alpha2) && !explicitlyNonIso.Contains(alpha2))
            {
                missing.Add(alpha2);
            }
        }

        CollectionAssert.AreEquivalent(Array.Empty<string>(), missing);
    }
}

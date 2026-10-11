using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CityNameOverridesTests
{
    [TestMethod]
    public void Resolve_UsesStateSpecificRuleBeforeCountryWideRule()
    {
        var config = Config(
            Rule("Springfield", "General replacement"),
            Rule("Springfield", "Specific replacement", "Illinois"));

        var result = CityNameOverrides.Resolve(new GeoResult("United States", "Illinois", "Springfield"), "USA", config);

        Assert.AreEqual("Specific replacement", result.GeoResult.City);
        Assert.AreEqual("Illinois", result.GeoResult.State);
        Assert.AreEqual("United States", result.GeoResult.Country);
        Assert.AreEqual("Illinois", result.Rule?.State);
        Assert.AreEqual("General replacement", CityNameOverrides.Resolve(new GeoResult("United States", "Missouri", "Springfield"), "USA", config).GeoResult.City);
    }

    [TestMethod]
    public void Resolve_DoesNotChangeOtherCountriesOrStates()
    {
        var config = Config(Rule("Springfield", "Replacement", "Illinois"));
        var original = new GeoResult("United States", "Missouri", "Springfield");

        Assert.AreEqual(original, CityNameOverrides.Resolve(original, "USA", config).GeoResult);
        Assert.AreEqual(original, CityNameOverrides.Resolve(original, "CAN", config).GeoResult);
        Assert.IsNull(CityNameOverrides.Resolve(original, "CAN", config).Rule);
    }

    [TestMethod]
    public void Resolve_AppliesOnlyOnceAndMatchesWithoutCaseOrOuterWhitespace()
    {
        var config = Config(Rule("Springfield", "First"), Rule("First", "Second"));

        var result = CityNameOverrides.Resolve(new GeoResult("United States", null, " springFIELD "), "usa", config);

        Assert.AreEqual("First", result.GeoResult.City);
    }

    [TestMethod]
    public void Resolve_DoesNotInventCityOrCountryMatches()
    {
        var config = Config(Rule("Springfield", "Replacement"));
        var missingCity = new GeoResult("United States", null, null);
        var original = new GeoResult("United States", null, "Springfield");

        Assert.AreEqual(missingCity, CityNameOverrides.Resolve(missingCity, "USA", config).GeoResult);
        Assert.AreEqual(original, CityNameOverrides.Resolve(original, null, config).GeoResult);
    }

    [TestMethod]
    public void Resolve_CanReplaceFinalAirportAndFallbackNames()
    {
        var config = Config(Rule("Airport", "City"), Rule("Florida", "Preferred area"));

        Assert.AreEqual("City", CityNameOverrides.Resolve(new GeoResult("United States", "Florida", "Airport"), "USA", config).GeoResult.City);
        Assert.AreEqual("Preferred area", CityNameOverrides.Resolve(new GeoResult("United States", "Florida", null).WithFallbackCity(), "USA", config).GeoResult.City);
    }

    [TestMethod]
    public void NormalizeAndValidate_TrimsRulesAndPreservesInput()
    {
        var rule = new CityNameOverride { CountryIso3 = " usa ", OriginalCity = " Original ", State = "  ", OverrideCity = " Replacement " };

        var normalized = CityNameOverrides.NormalizeAndValidate([rule]);

        Assert.AreEqual("USA", normalized[0].CountryIso3);
        Assert.AreEqual("Original", normalized[0].OriginalCity);
        Assert.AreEqual("Replacement", normalized[0].OverrideCity);
        Assert.IsNull(normalized[0].State);
        Assert.AreEqual(" usa ", rule.CountryIso3);
    }

    [TestMethod]
    public void NormalizeAndValidate_RejectsAmbiguousDuplicateRules()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CityNameOverrides.NormalizeAndValidate([Rule("Springfield", "One"), Rule(" springFIELD ", "Two")]));
        Assert.ThrowsExactly<ArgumentException>(() => CityNameOverrides.NormalizeAndValidate([Rule("Springfield", "One", "Illinois"), Rule("Springfield", "Two", " illinois ")]));
    }

    [TestMethod]
    [DataRow("", "Original", "Replacement")]
    [DataRow("US", "Original", "Replacement")]
    [DataRow("123", "Original", "Replacement")]
    [DataRow("USA", " ", "Replacement")]
    [DataRow("USA", "Original", " ")]
    public void NormalizeAndValidate_RejectsIncompleteRules(string country, string original, string replacement)
    {
        Assert.ThrowsExactly<ArgumentException>(() => CityNameOverrides.NormalizeAndValidate([
            new CityNameOverride { CountryIso3 = country, OriginalCity = original, OverrideCity = replacement }
        ]));
    }

    private static CityNameOverride Rule(string original, string replacement, string? state = null) =>
        new() { CountryIso3 = "USA", OriginalCity = original, OverrideCity = replacement, State = state };

    private static CityResolverConfig Config(params CityNameOverride[] rules) =>
        new() { CityNameOverrides = CityNameOverrides.NormalizeAndValidate(rules) };
}

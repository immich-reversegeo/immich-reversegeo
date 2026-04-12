using ImmichReverseGeo.Gadm.Models;
using ImmichReverseGeo.Gadm.Services;

namespace ImmichReverseGeo.Gadm.Tests;

[TestClass]
public class GadmDivisionsLogicTests
{
    [TestMethod]
    public void BuildCountryGeoPackageUrl_Che_ReturnsExpectedUrl()
    {
        var url = GadmDivisionsLogic.BuildCountryGeoPackageUrl("che");

        Assert.AreEqual("https://geodata.ucdavis.edu/gadm/gadm4.1/gpkg/gadm41_CHE.gpkg", url);
    }

    [TestMethod]
    public void ToGadmCode_Xkx_MapsToXko()
    {
        Assert.AreEqual("XKO", GadmCountryCodeMapper.ToGadmCode("XKX"));
    }

    [TestMethod]
    public void ToAppCode_Xko_MapsToXkx()
    {
        Assert.AreEqual("XKX", GadmCountryCodeMapper.ToAppCode("XKO"));
    }

    [TestMethod]
    public void ExpandCandidateCodes_Dnk_IncludesGreenlandAndFaroeIslands()
    {
        CollectionAssert.AreEqual(
            new[] { "DNK", "GRL", "FRO" },
            GadmCountryFallbackCatalog.ExpandCandidateCodes("DNK").ToArray());
    }

    [TestMethod]
    public void ExpandCandidateCodes_Gbr_IncludesChannelIslandsAndIsleOfMan()
    {
        CollectionAssert.AreEqual(
            new[] { "GBR", "JEY", "GGY", "IMN" },
            GadmCountryFallbackCatalog.ExpandCandidateCodes("GBR").ToArray());
    }

    [TestMethod]
    public void SelectStateName_PrefersLowestNonCountryLevel()
    {
        var result = GadmDivisionsLogic.SelectStateName(
        [
            new GadmDivisionCandidateDiagnostic("country", "Switzerland", "Country", null, 0, true, true, 10, false, "country"),
            new GadmDivisionCandidateDiagnostic("state", "Zurich", "Canton", null, 1, true, true, 5, true, "state"),
            new GadmDivisionCandidateDiagnostic("city", "Buelach", "District", null, 2, true, true, 1, false, "city")
        ]);

        Assert.AreEqual("Zurich", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersDeepestCityLikeLevel()
    {
        var result = GadmDivisionsLogic.SelectCityName(
        [
            new GadmDivisionCandidateDiagnostic("state", "Zurich", "Canton", null, 1, true, true, 5, false, "state"),
            new GadmDivisionCandidateDiagnostic("district", "Buelach", "District", null, 2, true, true, 2, false, "district"),
            new GadmDivisionCandidateDiagnostic("municipality", "Kloten", "Municipality", null, 3, true, true, 1, true, "municipality")
        ]);

        Assert.AreEqual("Kloten", result);
    }
}

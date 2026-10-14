using System.Globalization;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public sealed class CoordinateLookupInputParserTests
{
    [TestMethod]
    [DataRow("47.5, 8.5", 47.5, 8.5)]
    [DataRow("47.5;8.5", 47.5, 8.5)]
    [DataRow("47.5 | 8.5", 47.5, 8.5)]
    [DataRow("N47.5 E8.5", 47.5, 8.5)]
    [DataRow("47.5S 8.5W", -47.5, -8.5)]
    [DataRow("lat=47.5 lon=8.5", 47.5, 8.5)]
    [DataRow("47° 30' 0 N 8° 30' 0 E", 47.5, 8.5)]
    [DataRow("90 -180", 90, -180)]
    public void SupportedFormats_ParseIndependentlyOfCurrentCulture(string text, double latitude, double longitude)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-CH");
            Assert.IsTrue(CoordinateLookupInputParser.TryParse(text, out var lat, out var lon));
            Assert.AreEqual(latitude, lat, 0.0000001);
            Assert.AreEqual(longitude, lon, 0.0000001);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("٤٧° ٣٠' ٠ N ٨° ٣٠' ٠ E")]
    [DataRow("N４７.５ E８.５")]
    [DataRow("NaN NaN")]
    [DataRow("Infinity 8")]
    [DataRow("91 8")]
    [DataRow("47 181")]
    [DataRow("47° 60' 0 N 8° 30' 0 E")]
    [DataRow("47° 30' 60 N 8° 30' 0 E")]
    public void MalformedValues_ReturnFailureWithoutThrowing(string? text)
    {
        Assert.IsFalse(CoordinateLookupInputParser.TryParse(text, out var lat, out var lon));
        Assert.AreEqual(0d, lat);
        Assert.AreEqual(0d, lon);
    }
}

using System.Net;
using System.Text.RegularExpressions;
using ImmichReverseGeo.Web.Components;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationTextDiffTests
{
    [TestMethod]
    public void Compare_AirportDashHighlightsOnlyChangedCharacterOnBothSides()
    {
        var existing = "Larnaca International Airport – Glafcos Clerides";
        var proposed = "Larnaca International Airport - Glafcos Clerides";
        Assert.AreEqual("–", ChangedText(existing, proposed));
        Assert.AreEqual("-", ChangedText(proposed, existing));
        Assert.AreEqual(existing, string.Concat(LocationTextDiff.Compare(existing, proposed).Select(s => s.Text)));
    }

    [TestMethod]
    public void Compare_HighlightsSeparateEditsAndAddedOrRemovedWhitespace()
    {
        Assert.AreEqual("AB", ChangedText("xAyBz", "xCyDz"));
        Assert.AreEqual("CD", ChangedText("xCyDz", "xAyBz"));
        Assert.AreEqual(" ", ChangedText("New  York", "New York"));
        Assert.AreEqual("", ChangedText("New York", "New  York"));
        Assert.AreEqual(" ", ChangedText("City ", "City"));
        Assert.AreEqual("CITY", ChangedText("CITY", "city"));
    }

    [TestMethod]
    public void Compare_KeepsCombiningCharactersAndEmojiTogether()
    {
        Assert.AreEqual("e\u0301", ChangedText("Cafe\u0301", "Cafe"));
        Assert.AreEqual("👩‍💻", ChangedText("City 👩‍💻", "City 👨‍💻"));
    }

    [TestMethod]
    public void Compare_DistinguishesMissingEmptyAndIdenticalValues()
    {
        Assert.AreEqual("—", ChangedText(null, ""));
        Assert.AreEqual("(empty)", ChangedText("", null));
        Assert.AreEqual("", ChangedText(null, null));
        Assert.AreEqual("", ChangedText("City", "City"));
    }

    [TestMethod]
    public void Compare_LongMetadataKeepsCommonEndsWithoutLargeDiffMatrix()
    {
        var prefix = new string('a', 1000);
        var suffix = new string('z', 1000);
        Assert.AreEqual("old", ChangedText(prefix + "old" + suffix, prefix + "new" + suffix));
    }

    [TestMethod]
    public void VisibleChanges_ExposesWhitespaceAndInvisibleCharacters()
    {
        Assert.AreEqual("␠⟦U+00A0⟧⟦U+200B⟧⇥␍↵", LocationTextDiff.VisibleChanges(" \u00a0\u200b\t\r\n"));
        Assert.AreEqual("Changed characters: U+2013", LocationTextDiff.DescribeChanges("–"));
    }

    [TestMethod]
    public async Task Component_RendersEncodedNamesAndPreservesUnchangedSpacing()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<LocationDiffValue>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(LocationDiffValue.Value)] = "<script>City – Name</script>",
                    [nameof(LocationDiffValue.OtherValue)] = "<script>City - Name</script>",
                    [nameof(LocationDiffValue.Highlight)] = true
                }));
            return output.ToHtmlString();
        });
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("<mark", html);
        Assert.Contains("U+2013", WebUtility.HtmlDecode(html));
        Assert.AreEqual("<script>City – Name</script>", WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")));
    }

    private static string ChangedText(string? value, string? other) =>
        string.Concat(LocationTextDiff.Compare(value, other).Where(s => s.Changed).Select(s => s.Text));
}

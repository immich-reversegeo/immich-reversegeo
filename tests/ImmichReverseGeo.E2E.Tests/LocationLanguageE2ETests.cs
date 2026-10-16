using System.Security.Cryptography;
using ImmichReverseGeo.Core.Models;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

[assembly: DoNotParallelize]

namespace ImmichReverseGeo.E2E.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class LocationLanguageE2ETests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1280, 900)]
    [DataRow(390, 844)]
    public async Task LanguageSetting_LookupPreviewAndProcessingAgree(int width, int height)
    {
        await RunAsync(width, height, async (fixture, page) =>
        {
            await NavigateAsync(page, "Settings");
            await Expect(page.GetByLabel("Location name language", new() { Exact = true })).ToHaveValueAsync("english");
            await SetLanguageAsync(page, "preferred", "de_DE", save: false);
            await Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("Enter a language code");
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true })).ToBeDisabledAsync();
            await SetLanguageAsync(page, "english");

            await LookupAsync(page, "Munich", "Bavaria", "Germany");
            Assert.AreEqual(1, fixture.Factory.Cache.Exports, "The cold lookup must run the production Parquet-to-SQLite exporter.");
            var cachePath = Path.Combine(fixture.DataPath, "overture-divisions", "DEU.db");
            var originalCache = SHA256.HashData(await File.ReadAllBytesAsync(cachePath));

            await NavigateAsync(page, "Settings");
            await SetLanguageAsync(page, "local");
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.DirectoryPath, "settings-local.png"), FullPage = true });
            await page.ReloadAsync();
            await Expect(page.GetByLabel("Location name language", new() { Exact = true })).ToHaveValueAsync("local");
            await LookupAsync(page, "München", "Bayern", "Deutschland");
            await page.ScreenshotAsync(new() { Path = Path.Combine(fixture.DirectoryPath, "lookup-local.png"), FullPage = true });

            await NavigateAsync(page, "Location Management");
            await page.GetByRole(AriaRole.Link, new() { Name = "Scan & Review", Exact = true }).ClickAsync();
            await page.GetByLabel("Check assets by").SelectOptionAsync("All");
            await page.GetByRole(AriaRole.Button, new() { Name = "Start Scan", Exact = true }).ClickAsync();
            var row = page.GetByRole(AriaRole.Row).Filter(new() { HasText = LanguageFixture.ExistingAsset.ToString() });
            await Expect(row).ToContainTextAsync("Different");
            await Expect(row.Locator("td").Nth(4)).ToContainTextAsync("München");
            await Expect(row.Locator("td").Nth(4)).ToContainTextAsync("Bayern");
            await Expect(row.Locator("td").Nth(4)).ToContainTextAsync("Deutschland");
            Assert.AreEqual(new GeoResult("Germany", "Bavaria", "Munich"), await fixture.ReadLocationAsync(LanguageFixture.ExistingAsset));
            Assert.AreEqual(new GeoResult(null, null, null), await fixture.ReadLocationAsync(LanguageFixture.MissingAsset));

            await NavigateAsync(page, "Dashboard");
            await page.GetByRole(AriaRole.Button, new() { Name = "Run Now", Exact = true }).ClickAsync();
            await Expect(page.GetByText("Run ended: Completed.", new() { Exact = false })).ToBeVisibleAsync();
            Assert.AreEqual(new GeoResult("Deutschland", "Bayern", "München"), await fixture.ReadLocationAsync(LanguageFixture.MissingAsset));
            Assert.AreEqual(new GeoResult("Germany", "Bavaria", "Munich"), await fixture.ReadLocationAsync(LanguageFixture.ExistingAsset));

            await NavigateAsync(page, "Settings");
            await SetLanguageAsync(page, "preferred", "French (Canada) (fr-CA)");
            await LookupAsync(page, "Munich français", "Bavière", "Allemagne");
            await NavigateAsync(page, "Settings");
            await SetLanguageAsync(page, "preferred", "ja");
            await LookupAsync(page, "München", "Bayern", "Deutschland");
            Assert.AreEqual(1, fixture.Factory.Cache.Exports, "Language switches must reuse the same multilingual cache.");
            CollectionAssert.AreEqual(originalCache, SHA256.HashData(await File.ReadAllBytesAsync(cachePath)));
            Assert.AreEqual(new GeoResult("Deutschland", "Bayern", "München"), await fixture.ReadLocationAsync(LanguageFixture.MissingAsset),
                "Changing preferences must not rewrite existing metadata.");
            Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"),
                "The tested viewport must not have horizontal overflow.");
        });
    }

    [TestMethod]
    [DataRow(1280, 900)]
    [DataRow(390, 844)]
    public async Task LanguagePicker_SearchNamesAndTypeRegionalOrCustomCodes(int width, int height)
    {
        await RunAsync(width, height, async (fixture, page) =>
        {
            await NavigateAsync(page, "Settings");
            await page.GetByLabel("Location name language", new() { Exact = true }).SelectOptionAsync("preferred");
            var input = page.GetByLabel("Preferred language", new() { Exact = true });
            await Expect(input).ToHaveValueAsync("English (en)");
            await Expect(page.Locator("#location-name-languages option[value='French (fr)']")).ToHaveCountAsync(1);
            await Expect(page.Locator("#location-name-languages option[value='Chinese (zh)']")).ToHaveCountAsync(1);
            await Expect(page.Locator("#location-name-languages option[value='French (Canada) (fr-CA)']")).ToHaveCountAsync(0);
            await Expect(page.Locator("#location-name-languages option[value='Chinese (Traditional) (zh-Hant)']")).ToHaveCountAsync(0);
            await input.FillAsync("French (fr)");
            await input.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Expect(page.GetByText("All changes saved", new() { Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(input).ToHaveValueAsync("French (fr)");

            // Real keystrokes must not expand 'fr' midway through typing 'fr-CA'.
            await input.FillAsync("");
            await input.PressSequentiallyAsync("fr-CA", new() { Delay = 80 });
            await Expect(input).ToHaveValueAsync("fr-CA");
            await input.PressAsync("Tab");
            await Expect(input).ToHaveValueAsync("French (Canada) (fr-CA)");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Expect(page.GetByText("All changes saved", new() { Exact = true })).ToBeVisibleAsync();
            await LookupAsync(page, "Munich français", "Bavière", "Allemagne");

            await NavigateAsync(page, "Settings");
            await input.FillAsync("zz-ZZ");
            await input.PressAsync("Tab");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Expect(page.GetByText("All changes saved", new() { Exact = true })).ToBeVisibleAsync();
            await page.ReloadAsync();
            await Expect(input).ToHaveValueAsync("zz-ZZ");
            Assert.IsFalse(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth"));
        });
    }

    [TestMethod]
    public async Task CityResolverSave_FromAnOlderTabPreservesNewLanguagePreference()
    {
        await RunAsync(1280, 900, async (fixture, page) =>
        {
            await NavigateAsync(page, "City Resolver");
            await page.GetByLabel("Customize the global default profile").CheckAsync();
            await page.GetByLabel("Tie-break mode").SelectOptionAsync("largest-area");
            var settings = await page.Context.NewPageAsync();
            await settings.GotoAsync(fixture.BaseUrl);
            await Expect(settings.GetByRole(AriaRole.Button, new() { Name = "Run Now", Exact = true })).ToBeEnabledAsync();
            await NavigateAsync(settings, "Settings");
            await SetLanguageAsync(settings, "preferred", "fr");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).ClickAsync();
            await Expect(page.GetByText("All changes saved", new() { Exact = true })).ToBeVisibleAsync();
            await settings.ReloadAsync();
            await Expect(settings.GetByLabel("Location name language", new() { Exact = true })).ToHaveValueAsync("preferred");
            await Expect(settings.GetByLabel("Preferred language", new() { Exact = true })).ToHaveValueAsync("French (fr)");
            await LookupAsync(settings, "Munich français", "Bavière", "Allemagne");
            await settings.CloseAsync();
        });
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("2")]
    public async Task IncompatibleCache_FailedRebuildPreservesFile_ThenRetryRestoresLocalizedLookup(string oldVersion)
    {
        await RunAsync(1280, 900, async (fixture, page) =>
        {
            await LookupAsync(page, "Munich", "Bavaria", "Germany");
            var cache = fixture.Factory.Cache;
            cache.DeleteFile("DEU");
            // Produce a valid old-format file through the real exporter, then invalidate its metadata.
            await LookupAsync(page, "Munich", "Bavaria", "Germany");
            cache = fixture.Factory.Cache;
            var path = Path.Combine(fixture.DataPath, "overture-divisions", "DEU.db");
            using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=false"))
            {
                database.Open();
                using var update = database.CreateCommand();
                update.CommandText = "UPDATE _meta SET value=$version WHERE key='cacheFormatVersion'";
                update.Parameters.AddWithValue("$version", oldVersion);
                update.ExecuteNonQuery();
            }
            cache.GetStatus(); // Inventory must discard the now-incompatible readiness entry.
            var before = await File.ReadAllBytesAsync(path);
            cache.FailExport = true;
            await NavigateAsync(page, "Settings");
            await SetLanguageAsync(page, "local");
            await NavigateAsync(page, "Coordinate Lookup");
            await FillCoordinatesAsync(page);
            await page.GetByRole(AriaRole.Button, new() { Name = "Lookup", Exact = true }).ClickAsync();
            await Expect(page.Locator(".final-card")).ToContainTextAsync("incomplete");
            await Expect(page.Locator(".final-card")).Not.ToContainTextAsync("Munich");
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
            await NavigateAsync(page, "Dashboard");
            await page.GetByRole(AriaRole.Button, new() { Name = "Run Now", Exact = true }).ClickAsync();
            await Expect(page.GetByText("Run ended: CompletedWithErrors.", new() { Exact = false })).ToBeVisibleAsync();
            Assert.AreEqual(new GeoResult(null, null, null), await fixture.ReadLocationAsync(LanguageFixture.MissingAsset));
            Assert.AreEqual(3, cache.Exports, "Processing must reuse the failed lookup cooldown without another export.");
            cache.Now += CacheBuildRetryPolicy.RetryDelay;
            cache.FailExport = false;
            await LookupAsync(page, "München", "Bayern", "Deutschland");
            Assert.AreEqual(4, cache.Exports, "Cold export, replacement, one failed build, and successful retry after cooldown.");
        });
    }

    private async Task RunAsync(int width, int height, Func<LanguageFixture, IPage, Task> test)
    {
        await using var fixture = new LanguageFixture();
        await fixture.StartAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        var page = await context.NewPageAsync();
        var browserErrors = new List<string>();
        page.PageError += (_, error) => browserErrors.Add(error);
        // Block third-party browser requests; all fixture geodata is local.
        await page.RouteAsync("**/*", route => route.Request.Url.StartsWith(fixture.BaseUrl, StringComparison.Ordinal)
            ? route.ContinueAsync() : route.AbortAsync());
        try
        {
            await page.GotoAsync(fixture.BaseUrl);
            await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Run Now", Exact = true })).ToBeEnabledAsync();
            await test(fixture, page);
            Assert.HasCount(0, browserErrors, string.Join(Environment.NewLine, browserErrors));
        }
        finally
        {
            var screenshot = Path.Combine(fixture.DirectoryPath, "last-page.png");
            await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
            var trace = Path.Combine(fixture.DirectoryPath, "trace.zip");
            await context.Tracing.StopAsync(new() { Path = trace });
            TestContext.AddResultFile(screenshot);
            TestContext.AddResultFile(trace);
            TestContext.WriteLine($"E2E evidence: {fixture.DirectoryPath}");
        }
    }

    private static async Task NavigateAsync(IPage page, string name)
    {
        var link = page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });
        if (!await link.IsVisibleAsync())
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Open Navigation", Exact = true }).ClickAsync();
        }
        await link.ClickAsync();
    }

    private static async Task SetLanguageAsync(IPage page, string mode, string? language = null, bool save = true)
    {
        await page.GetByLabel("Location name language", new() { Exact = true }).SelectOptionAsync(mode);
        if (language is not null)
        {
            await page.GetByLabel("Preferred language", new() { Exact = true }).FillAsync(language);
        }
        if (save)
        {
            var button = page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true });
            if (await button.IsEnabledAsync())
            {
                await button.ClickAsync();
            }
            await Expect(page.GetByText("All changes saved", new() { Exact = true })).ToBeVisibleAsync();
        }
    }

    private static async Task FillCoordinatesAsync(IPage page)
    {
        await page.GetByLabel("Latitude", new() { Exact = true }).FillAsync("1");
        await page.GetByLabel("Longitude", new() { Exact = true }).FillAsync("1");
    }

    private static async Task LookupAsync(IPage page, string city, string state, string country)
    {
        await NavigateAsync(page, "Coordinate Lookup");
        await FillCoordinatesAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Lookup", Exact = true }).ClickAsync();
        foreach (var (label, value) in new[] { ("City", city), ("State", state), ("Country", country) })
        {
            var row = page.Locator(".final-card .result-row").Filter(new() { Has = page.Locator(".result-key").GetByText(label, new() { Exact = true }) });
            await Expect(row.Locator(".result-val")).ToHaveTextAsync(value);
        }
    }
}

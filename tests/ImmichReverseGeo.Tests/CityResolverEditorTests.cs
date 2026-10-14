using System.Collections;
using System.Reflection;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Components.Pages;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CityResolverEditorTests
{
    [TestMethod]
    [DataRow(false, "", false)]
    [DataRow(false, "smallest-area", false)]
    [DataRow(false, "largest-area", false)]
    [DataRow(true, "", false)]
    [DataRow(true, "smallest-area", false)]
    [DataRow(true, "largest-area", false)]
    [DataRow(false, "", true)]
    [DataRow(false, "smallest-area", true)]
    [DataRow(false, "largest-area", true)]
    [DataRow(true, "", true)]
    [DataRow(true, "smallest-area", true)]
    [DataRow(true, "largest-area", true)]
    public async Task SaveAndReload_PreservesIndependentTieBreakAndSubtypeOverrides(bool country, string tieBreak, bool types)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, root);
            var config = new AppConfig();
            var profile = new CityResolverProfile { PreferredSubtypes = types ? ["localadmin"] : [], TieBreakMode = tieBreak };
            if (country)
            {
                config.Processing.CityResolver.DefaultProfile = new CityResolverProfile { PreferredSubtypes = [], TieBreakMode = CityResolverTieBreakModes.LargestArea };
                config.Processing.CityResolver.CountryOverrides["FRA"] = profile;
            }
            else
            {
                config.Processing.CityResolver.DefaultProfile = profile;
            }
            await service.SaveConfigAsync(config);
            var page = await LoadPageAsync(service, root);
            var built = ((AppConfig)Invoke(page, "BuildSaveConfig")!).Processing.CityResolver;
            var loaded = country ? built.CountryOverrides["FRA"] : built.DefaultProfile;
            Assert.AreEqual(tieBreak, loaded.TieBreakMode);
            CollectionAssert.AreEqual(profile.PreferredSubtypes, loaded.PreferredSubtypes);

            // Edit a different field, then exercise the real save and reload paths.
            if (country)
            {
                SetField(page, "_useDefaultOverride", true);
                GetField<List<string>>(page, "_defaultSubtypeOrder").Add("borough");
            }
            else
            {
                SetField(page, "_newCountryInput", "FRA");
                Invoke(page, "AddCountryOverride");
            }
            await (Task)Invoke(page, "Save")!;
            var saved = (await service.GetConfigAsync()).Processing.CityResolver;
            var persisted = country ? saved.CountryOverrides["FRA"] : saved.DefaultProfile;
            Assert.AreEqual(tieBreak, persisted.TieBreakMode);
            CollectionAssert.AreEqual(profile.PreferredSubtypes, persisted.PreferredSubtypes);
            var reloadedPage = await LoadPageAsync(service, root);
            var reloaded = ((AppConfig)Invoke(reloadedPage, "BuildSaveConfig")!).Processing.CityResolver;
            Assert.AreEqual(tieBreak, (country ? reloaded.CountryOverrides["FRA"] : reloaded.DefaultProfile).TieBreakMode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    public async Task NewCountryOverride_InheritsAndTracksSubsequentGlobalEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, root);
            var config = new AppConfig();
            config.Processing.CityResolver.DefaultProfile = new CityResolverProfile { PreferredSubtypes = ["localadmin"], TieBreakMode = CityResolverTieBreakModes.LargestArea };
            await service.SaveConfigAsync(config);
            var page = await LoadPageAsync(service, root);
            SetField(page, "_newCountryInput", "FRA");
            Invoke(page, "AddCountryOverride");
            var editor = GetField<IList>(page, "_countryEditors")[0]!;
            var profile = ((AppConfig)Invoke(page, "BuildSaveConfig")!).Processing.CityResolver.CountryOverrides["FRA"];
            Assert.AreEqual(string.Empty, profile.TieBreakMode);
            Assert.HasCount(0, profile.PreferredSubtypes);
            SetField(page, "_defaultTieBreakMode", CityResolverTieBreakModes.SmallestArea);
            GetField<List<string>>(page, "_defaultSubtypeOrder").Clear();
            GetField<List<string>>(page, "_defaultSubtypeOrder").Add("borough");
            var effective = (CityResolverProfile)Invoke(page, "GetEffectiveCountryProfile", editor)!;
            Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, effective.TieBreakMode);
            CollectionAssert.AreEqual(new[] { "borough" }, effective.PreferredSubtypes);
            await (Task)Invoke(page, "Save")!;
            var reloaded = await LoadPageAsync(service, root);
            editor = GetField<IList>(reloaded, "_countryEditors")[0]!;
            effective = (CityResolverProfile)Invoke(reloaded, "GetEffectiveCountryProfile", editor)!;
            Assert.AreEqual(CityResolverTieBreakModes.SmallestArea, effective.TieBreakMode);
            CollectionAssert.AreEqual(new[] { "borough" }, effective.PreferredSubtypes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static async Task<CityResolver> LoadPageAsync(ConfigService config, string root)
    {
        var page = new CityResolver();
        var storage = new StorageOptions(root, AppContext.BaseDirectory);
        SetProperty(page, "Config", config);
        SetProperty(page, "CountryCodes", CountryCodeService.CreateForTest(Path.Combine(AppContext.BaseDirectory, "data")));
        SetProperty(page, "CityResolverCatalog", new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, storage));
        await (Task)Invoke(page, "LoadSettings")!;
        Assert.IsNull(GetField<string?>(page, "_loadError"));
        return page;
    }

    private static object? Invoke(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
}

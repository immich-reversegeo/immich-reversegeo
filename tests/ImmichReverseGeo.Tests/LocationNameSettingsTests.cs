using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationNameSettingsTests
{
    [TestMethod]
    [DataRow(null, "")]
    [DataRow("zz-ZZ", "zz-ZZ")]
    [DataRow("fr-CA", "French (Canada) (fr-CA)")]
    public void LanguageCatalog_KeepsSavedRegionalCustomAndInvalidValuesEditable(string? code, string display)
    {
        Assert.AreEqual(display, LocationLanguageCatalog.Display(code));
    }

    [TestMethod]
    [DataRow("de")]
    [DataRow(" fr-CA ")]
    [DataRow("zh-Hant-TW")]
    [DataRow("haw")]
    public void PreferredLanguage_AcceptsLanguageCodes(string language)
    {
        var config = new AppConfig();
        config.Processing.LocationNames = new() { Mode = LocationNameOptions.Preferred, PreferredLanguage = language };
        Assert.IsNull(SettingsValidation.GetError(config, DateTime.UtcNow));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("german")]
    [DataRow("de_DE")]
    [DataRow("de'")]
    [DataRow("de--DE")]
    public void PreferredLanguage_RejectsInvalidCodesBeforeSaving(string language)
    {
        var config = new AppConfig();
        config.Processing.LocationNames = new() { Mode = LocationNameOptions.Preferred, PreferredLanguage = language };
        Assert.IsNotNull(SettingsValidation.GetError(config, DateTime.UtcNow));
        config.Processing.LocationNames.Mode = LocationNameOptions.Local;
        Assert.IsNull(SettingsValidation.GetError(config, DateTime.UtcNow));
    }

    [TestMethod]
    public async Task Settings_RoundTripLanguage_AndPreserveItWhenCityResolverIsSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-language-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new ConfigService(NullLogger<ConfigService>.Instance, root);
            foreach (var json in new[] { "{}", """{"processing":{"locationNames":null}}""" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), json);
                Assert.AreEqual(LocationNameOptions.English, (await service.GetConfigAsync()).Processing.LocationNames.Mode);
            }
            var config = await service.GetConfigAsync();
            var state = new SettingsSaveState();
            state.Load(config);
            config.Processing.LocationNames.Mode = LocationNameOptions.Preferred;
            config.Processing.LocationNames.PreferredLanguage = "de";
            Assert.IsTrue(state.HasChanges(config));
            await service.SaveConfigAsync(state.BeginSave(config)!);
            state.CompleteSave();
            Assert.IsFalse(state.HasChanges(config));
            await service.UpdateConfigAsync(latest => latest.Processing.CityResolver.DefaultProfile.TieBreakMode = CityResolverTieBreakModes.LargestArea);
            var loaded = await new ConfigService(NullLogger<ConfigService>.Instance, root).GetConfigAsync();
            Assert.AreEqual(LocationNameOptions.Preferred, loaded.Processing.LocationNames.Mode);
            Assert.AreEqual("de", loaded.Processing.LocationNames.PreferredLanguage);
            Assert.AreEqual(CityResolverTieBreakModes.LargestArea, loaded.Processing.CityResolver.DefaultProfile.TieBreakMode);
            Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(root, "settings.json"))).Contains("validationError", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

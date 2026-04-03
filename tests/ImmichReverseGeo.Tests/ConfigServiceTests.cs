using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ConfigServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        // Clear env vars set by GetDbSettings_ReadsEnvVars
        foreach (var key in new[] { "DB_HOST", "DB_PORT", "DB_USERNAME", "DB_PASSWORD", "DB_DATABASE_NAME" })
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [TestMethod]
    public async Task GetConfig_NoFile_ReturnsDefaults()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        Assert.AreEqual("0 * * * *", cfg.Schedule.Cron);
        Assert.AreEqual(50, cfg.Processing.BatchSize);
        Assert.IsTrue(cfg.Processing.UseAirportInfrastructure);
        Assert.AreEqual(0, cfg.Processing.CityResolver.CountryOverrides.Count);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        cfg.Processing.BatchSize = 99;
        cfg.Processing.UseAirportInfrastructure = false;
        await svc.SaveConfigAsync(cfg);

        var svc2 = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var loaded = await svc2.GetConfigAsync();
        Assert.AreEqual(99, loaded.Processing.BatchSize);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
    }

    [TestMethod]
    public async Task GetConfig_LegacySettingsWithoutCityResolver_AddsCompatibleDefaults()
    {
        Directory.CreateDirectory(_tempDir);
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schedule": {
                "cron": "0 * * * *",
                "enabled": true
              },
              "processing": {
                "batchSize": 25,
                "batchDelayMs": 250,
                "maxDegreeOfParallelism": 2,
                "useAirportInfrastructure": false,
                "verboseLogging": true
              }
            }
            """);

        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);

        var loaded = await svc.GetConfigAsync();

        Assert.IsNotNull(loaded.Processing.CityResolver);
        Assert.IsNotNull(loaded.Processing.CityResolver.DefaultProfile);
        Assert.IsNotNull(loaded.Processing.CityResolver.CountryOverrides);
        Assert.AreEqual(0, loaded.Processing.CityResolver.CountryOverrides.Count);
        Assert.AreEqual(25, loaded.Processing.BatchSize);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
        Assert.IsTrue(loaded.Processing.VerboseLogging);
    }

    [TestMethod]
    public void GetDbSettings_ReadsEnvVars()
    {
        Environment.SetEnvironmentVariable("DB_HOST", "testhost");
        Environment.SetEnvironmentVariable("DB_PORT", "5433");
        Environment.SetEnvironmentVariable("DB_USERNAME", "testuser");
        Environment.SetEnvironmentVariable("DB_PASSWORD", "testpass");
        Environment.SetEnvironmentVariable("DB_DATABASE_NAME", "testdb");

        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var db = svc.GetDbSettings();

        Assert.AreEqual("testhost", db.Host);
        Assert.AreEqual(5433, db.Port);
        Assert.AreEqual("testuser", db.Username);
        Assert.AreEqual("testdb", db.Database);
    }
}

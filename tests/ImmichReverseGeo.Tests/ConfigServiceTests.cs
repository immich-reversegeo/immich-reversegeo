using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
[DoNotParallelize]
public class ConfigServiceTests
{
    private static readonly string[] DbEnvironmentKeys =
        ["DB_HOST", "DB_HOSTNAME", "DB_PORT", "DB_USERNAME", "DB_PASSWORD", "DB_DATABASE_NAME"];
    private readonly Dictionary<string, string?> _originalEnvironment = new();
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        foreach (var key in DbEnvironmentKeys)
        {
            _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        foreach (var key in DbEnvironmentKeys)
        {
            Environment.SetEnvironmentVariable(key, _originalEnvironment[key]);
        }
    }

    [TestMethod]
    public async Task GetConfig_NoFile_ReturnsDefaults()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        Assert.AreEqual("0 * * * *", cfg.Schedule.Cron);
        Assert.AreEqual(50, cfg.Processing.BatchSize);
        Assert.IsFalse(cfg.Processing.LowMemoryMode);
        Assert.IsTrue(cfg.Processing.UseAirportInfrastructure);
        Assert.IsFalse(cfg.Processing.UsePointOnlyAirportFallback);
        Assert.AreEqual(0, cfg.Processing.CityResolver.CountryOverrides.Count);
    }

    [TestMethod]
    public async Task LegacyConfig_PointOnlyFallbackRemainsOff()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "settings.json"), """{"processing":{"useAirportInfrastructure":true}}""");
        var service = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        Assert.IsFalse((await service.GetConfigAsync()).Processing.UsePointOnlyAirportFallback);
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        cfg.Processing.BatchSize = 99;
        cfg.Processing.UseAirportInfrastructure = false;
        cfg.Processing.UsePointOnlyAirportFallback = true;
        await svc.SaveConfigAsync(cfg);

        var svc2 = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var loaded = await svc2.GetConfigAsync();
        Assert.AreEqual(99, loaded.Processing.BatchSize);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
        Assert.IsTrue(loaded.Processing.UsePointOnlyAirportFallback);
    }

    [TestMethod]
    public async Task Save_WhileReading_KeepsCompleteOldAndNewSnapshots()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var original = new AppConfig();
        original.Processing.BatchSize = 17;
        await svc.SaveConfigAsync(original);
        var path = Path.Combine(_tempDir, "settings.json");
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        var updated = new AppConfig();
        updated.Processing.BatchSize = 83;
        await svc.SaveConfigAsync(updated);

        var oldSnapshot = await System.Text.Json.JsonSerializer.DeserializeAsync<AppConfig>(reader,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.AreEqual(17, oldSnapshot!.Processing.BatchSize);
        Assert.AreEqual(83, (await svc.GetConfigAsync()).Processing.BatchSize);
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, ".settings.*.tmp").Length);
    }

    [TestMethod]
    public async Task Save_WhenReplacementFails_PreservesSettingsAndRemovesTemporaryFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file sharing is required for this failure probe.");
        }

        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        await svc.SaveConfigAsync(new AppConfig());
        var path = Path.Combine(_tempDir, "settings.json");
        var original = await File.ReadAllBytesAsync(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => svc.SaveConfigAsync(new AppConfig
            {
                Processing = new ProcessingConfig { BatchSize = 91 }
            }));
        }

        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, Directory.GetFiles(_tempDir, ".settings.*.tmp").Length);
    }

    [TestMethod]
    public async Task GetConfig_CorruptFile_ThrowsAndPreservesFile()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "settings.json");
        await File.WriteAllTextAsync(path, "{broken settings");
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => svc.GetConfigAsync());
        Assert.AreEqual("{broken settings", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task ConcurrentReadsAndSaves_ReturnCompleteMatchingValues()
    {
        var writer = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var reader = writer; // Runtime uses one singleton for the worker and all UI circuits.
        await writer.SaveConfigAsync(new AppConfig());
        var saves = Task.Run(async () =>
        {
            for (var i = 1; i <= 40; i++)
            {
                await writer.SaveConfigAsync(new AppConfig
                {
                    Processing = new ProcessingConfig { BatchSize = i, BatchDelayMs = i }
                });
            }
        });
        try
        {
            while (!saves.IsCompleted)
            {
                var snapshot = await reader.GetConfigAsync();
                Assert.IsTrue(snapshot.Processing.BatchSize == snapshot.Processing.BatchDelayMs
                    || snapshot.Processing.BatchSize == 50 && snapshot.Processing.BatchDelayMs == 100);
            }
        }
        finally
        {
            await saves;
        }
        Assert.AreEqual(40, (await reader.GetConfigAsync()).Processing.BatchSize);
    }

    [TestMethod]
    public async Task SectionUpdates_PreserveOtherEditorsChanges()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        await svc.SaveConfigAsync(new AppConfig());
        var staleSettings = await svc.GetConfigAsync();
        staleSettings.Processing.BatchSize = 79;
        await svc.UpdateConfigAsync(latest =>
        {
            latest.Processing.CityResolver.CountryOverrides["CHE"] = new CityResolverProfile
            {
                PreferredSubtypes = ["localadmin"]
            };
        });
        await svc.UpdateConfigAsync(latest =>
        {
            var resolver = latest.Processing.CityResolver;
            latest.Schedule = staleSettings.Schedule;
            latest.Processing = staleSettings.Processing;
            latest.Processing.CityResolver = resolver;
        });
        await svc.UpdateConfigAsync(latest =>
        {
            latest.Processing.CityResolver.DefaultProfile = new CityResolverProfile
            {
                PreferredSubtypes = ["locality"]
            };
        });

        var saved = await svc.GetConfigAsync();
        Assert.AreEqual(79, saved.Processing.BatchSize);
        CollectionAssert.AreEqual(new[] { "localadmin" }, saved.Processing.CityResolver.CountryOverrides["CHE"].PreferredSubtypes);
        CollectionAssert.AreEqual(new[] { "locality" }, saved.Processing.CityResolver.DefaultProfile.PreferredSubtypes);
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
        Assert.IsFalse(loaded.Processing.LowMemoryMode);
        Assert.IsFalse(loaded.Processing.UseAirportInfrastructure);
        Assert.IsTrue(loaded.Processing.VerboseLogging);
    }

    [TestMethod]
    [DataRow(null, null, "database")]
    [DataRow("legacy-host", null, "legacy-host")]
    [DataRow(null, "immich-host", "immich-host")]
    [DataRow("legacy-host", "immich-host", "legacy-host")]
    [DataRow("", "immich-host", "immich-host")]
    [DataRow("  ", "immich-host", "immich-host")]
    [DataRow(null, "", "database")]
    [DataRow("", "  ", "database")]
    public void GetDbSettings_ResolvesHostWithLegacyPrecedence(
        string? host, string? hostname, string expectedHost)
    {
        Environment.SetEnvironmentVariable("DB_HOST", host);
        Environment.SetEnvironmentVariable("DB_HOSTNAME", hostname);

        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);

        Assert.AreEqual(expectedHost, svc.GetDbSettings().Host);
    }

    [TestMethod]
    public async Task LowMemoryMode_RoundTripsAndUpdatesRuntimePolicyWithoutChangingSavedLimits()
    {
        var policy = new MemoryResourcePolicy();
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, _tempDir, policy);
        var cfg = new AppConfig();
        cfg.Processing.BatchSize = 100;
        cfg.Processing.MaxDegreeOfParallelism = 8;
        cfg.Processing.LowMemoryMode = true;
        await svc.SaveConfigAsync(cfg);
        var loaded = await svc.GetConfigAsync();

        Assert.IsTrue(policy.LowMemoryMode);
        Assert.IsTrue(loaded.Processing.LowMemoryMode);
        Assert.AreEqual(25, loaded.Processing.EffectiveBatchSize);
        Assert.AreEqual(1, loaded.Processing.EffectiveParallelism);
        loaded.Processing.LowMemoryMode = false;
        await svc.SaveConfigAsync(loaded);
        Assert.IsFalse(policy.LowMemoryMode);
        Assert.AreEqual(100, loaded.Processing.EffectiveBatchSize);
        Assert.AreEqual(8, loaded.Processing.EffectiveParallelism);
        await svc.UpdateConfigAsync(current => current.Processing.LowMemoryMode = true);
        Assert.IsTrue(policy.LowMemoryMode);
        await svc.UpdateConfigAsync(current => current.Schedule.Enabled = false);
        Assert.IsTrue(policy.LowMemoryMode, "Updating another section must preserve the active memory mode.");
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

    [TestMethod]
    public async Task CountryIndexIdleTimeout_RoundTripsAndUpdatesRuntimePolicy()
    {
        var policy = new MemoryResourcePolicy();
        var service = new ConfigService(NullLogger<ConfigService>.Instance, _tempDir, policy);
        Assert.AreEqual(5, (await service.GetConfigAsync()).Processing.CountryIndexIdleMinutes);
        await service.UpdateConfigAsync(config => config.Processing.CountryIndexIdleMinutes = 15);
        Assert.AreEqual(15, policy.CountryIndexIdleMinutes);
        Assert.AreEqual(15, (await service.GetConfigAsync()).Processing.CountryIndexIdleMinutes);
        await service.UpdateConfigAsync(config => config.Processing.CountryIndexIdleMinutes = 0);
        Assert.AreEqual(0, policy.CountryIndexIdleMinutes);
        policy.CountryIndexIdleMinutes = -1;
        Assert.AreEqual(0, policy.CountryIndexIdleMinutes);
        policy.CountryIndexIdleMinutes = int.MaxValue;
        Assert.AreEqual(1440, policy.CountryIndexIdleMinutes);
    }
    [TestMethod]
    public async Task BoundedCountryCache_RoundTripsAndUpdatesRuntimePolicy()
    {
        var policy = new MemoryResourcePolicy();
        var service = new ConfigService(NullLogger<ConfigService>.Instance, _tempDir, policy);
        var original = (await service.GetConfigAsync()).Processing;
        Assert.IsFalse(original.UseBoundedCountryCache);
        Assert.AreEqual(256, original.CountryCacheMiB);
        await service.UpdateConfigAsync(config =>
        {
            config.Processing.UseBoundedCountryCache = true;
            config.Processing.CountryCacheMiB = 64;
        });
        var loaded = await new ConfigService(NullLogger<ConfigService>.Instance, _tempDir).GetConfigAsync();
        Assert.IsTrue(loaded.Processing.UseBoundedCountryCache);
        Assert.AreEqual(64, loaded.Processing.CountryCacheMiB);
        Assert.IsTrue(policy.UseBoundedCountryCache);
        Assert.AreEqual(64, policy.CountryCacheMiB);
        Assert.AreEqual(original.EffectiveParallelism, loaded.Processing.EffectiveParallelism);
        Assert.AreEqual(original.EffectiveBatchSize, loaded.Processing.EffectiveBatchSize);
        await service.UpdateConfigAsync(config => config.Processing.UseBoundedCountryCache = false);
        Assert.IsFalse(policy.UseBoundedCountryCache);
        policy.CountryCacheMiB = -1;
        Assert.AreEqual(1, policy.CountryCacheMiB);
        policy.CountryCacheMiB = int.MaxValue;
        Assert.AreEqual(1024, policy.CountryCacheMiB);
    }

}

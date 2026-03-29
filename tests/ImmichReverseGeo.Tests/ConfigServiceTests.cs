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
    }

    [TestMethod]
    public async Task SaveAndLoad_RoundTrips()
    {
        var svc = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var cfg = await svc.GetConfigAsync();
        cfg.Processing.BatchSize = 99;
        await svc.SaveConfigAsync(cfg);

        var svc2 = new ConfigService(NullLogger<ConfigService>.Instance, configDir: _tempDir);
        var loaded = await svc2.GetConfigAsync();
        Assert.AreEqual(99, loaded.Processing.BatchSize);
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

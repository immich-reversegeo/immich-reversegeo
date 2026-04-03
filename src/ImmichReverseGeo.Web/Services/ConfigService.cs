using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ConfigService(ILogger<ConfigService> logger, string? configDir = null)
{
    private readonly string _configPath = Path.Combine(
        configDir ?? "/config", "settings.json");

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<AppConfig> GetConfigAsync()
    {
        if (!File.Exists(_configPath))
        {
            logger.LogInformation("No settings file found at {Path}, using defaults", _configPath);
            return new AppConfig();
        }

        await using var fs = File.OpenRead(_configPath);
        var config = await JsonSerializer.DeserializeAsync<AppConfig>(fs, _json) ?? new AppConfig();
        return EnsureDefaults(config);
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        await using var fs = File.Create(_configPath);
        await JsonSerializer.SerializeAsync(fs, config, _json);
        logger.LogInformation("Config saved to {Path}", _configPath);
    }

    public DbSettings GetDbSettings() => new(
        Host: Environment.GetEnvironmentVariable("DB_HOST") ?? "database",
        Port: int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 5432,
        Username: Environment.GetEnvironmentVariable("DB_USERNAME") ?? "postgres",
        Password: Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "",
        Database: Environment.GetEnvironmentVariable("DB_DATABASE_NAME") ?? "immich");

    private static AppConfig EnsureDefaults(AppConfig config)
    {
        config.Schedule ??= new ScheduleConfig();
        config.Processing ??= new ProcessingConfig();
        config.Processing.CityResolver ??= new CityResolverConfig();
        config.Processing.CityResolver.DefaultProfile ??= CityResolverProfile.CreateEmpty();
        config.Processing.CityResolver.CountryOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        return config;
    }
}

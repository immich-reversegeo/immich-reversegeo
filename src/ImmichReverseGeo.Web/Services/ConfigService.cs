using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ConfigService(ILogger<ConfigService> logger, string? configDir = null)
{
    private readonly string _configPath = Path.Combine(
        configDir ?? "/config", "settings.json");

    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<AppConfig> GetConfigAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_configPath))
            {
                logger.LogInformation("No settings file found at {Path}, using defaults", _configPath);
                return new AppConfig();
            }

            await using var fs = new FileStream(
                _configPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(fs, _json) ?? new AppConfig();
            return EnsureDefaults(config);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        config = EnsureDefaults(config);
        // Capture the caller's values before awaiting; later UI edits belong to the next save.
        var json = JsonSerializer.SerializeToUtf8Bytes(config, _json);
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");

        await _fileLock.WaitAsync();
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, json);
            // Same-directory replacement publishes a complete file. Existing readers retain
            // their old snapshot through FileShare.Delete, including on Windows.
            if (File.Exists(_configPath))
            {
                File.Replace(temporaryPath, _configPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _configPath);
            }
            logger.LogInformation("Config saved to {Path}", _configPath);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove temporary settings file {Path}", temporaryPath);
            }
            _fileLock.Release();
        }
    }

    public DbSettings GetDbSettings() => new(
        Host: GetDbHost(),
        Port: int.TryParse(Environment.GetEnvironmentVariable("DB_PORT"), out var p) ? p : 5432,
        Username: Environment.GetEnvironmentVariable("DB_USERNAME") ?? "postgres",
        Password: Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "",
        Database: Environment.GetEnvironmentVariable("DB_DATABASE_NAME") ?? "immich");

    private static string GetDbHost()
    {
        var host = Environment.GetEnvironmentVariable("DB_HOST");
        if (!string.IsNullOrWhiteSpace(host))
        {
            return host;
        }

        var hostname = Environment.GetEnvironmentVariable("DB_HOSTNAME");
        return !string.IsNullOrWhiteSpace(hostname) ? hostname : "database";
    }

    private static AppConfig EnsureDefaults(AppConfig config)
    {
        config.Schedule ??= new ScheduleConfig();
        config.Processing ??= new ProcessingConfig();
        config.Processing.CityResolver ??= new CityResolverConfig();
        config.Processing.CityResolver.DefaultProfile ??= CityResolverProfile.CreateEmpty();
        config.Processing.CityResolver.CountryOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        config.Processing.CityResolver.CityNameOverrides = CityNameOverrides.NormalizeAndValidate(config.Processing.CityResolver.CityNameOverrides);
        return config;
    }
}

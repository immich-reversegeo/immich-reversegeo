using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ConfigService(ILogger<ConfigService> logger, string? configDir = null, MemoryResourcePolicy? memoryPolicy = null)
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
            return await ReadConfigAsync();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveConfigAsync(AppConfig config)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(config, _json);
        await _fileLock.WaitAsync();
        try
        {
            await WriteConfigAsync(json);
            ApplyMemoryPolicy(config);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    // Merge only the edited section against the latest settings under the same lock.
    public async Task UpdateConfigAsync(Action<AppConfig> update)
    {
        await _fileLock.WaitAsync();
        try
        {
            var config = await ReadConfigAsync();
            update(config);
            await WriteConfigAsync(JsonSerializer.SerializeToUtf8Bytes(config, _json));
            ApplyMemoryPolicy(config);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void ApplyMemoryPolicy(AppConfig config)
    {
        if (memoryPolicy is not null)
        {
            memoryPolicy.LowMemoryMode = config.Processing.LowMemoryMode;
            memoryPolicy.CountryIndexIdleMinutes = config.Processing.CountryIndexIdleMinutes;
        }
    }

    private async Task<AppConfig> ReadConfigAsync()
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

    private async Task WriteConfigAsync(byte[] json)
    {
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, json);
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
        return config;
    }
}

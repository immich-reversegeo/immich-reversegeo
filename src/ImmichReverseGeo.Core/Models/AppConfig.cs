using System.Collections.Generic;

namespace ImmichReverseGeo.Core.Models;

public class AppConfig
{
    public ScheduleConfig Schedule { get; set; } = new();
    public ProcessingConfig Processing { get; set; } = new();
}

public class ScheduleConfig
{
    public string Cron { get; set; } = "0 * * * *";
    public bool Enabled { get; set; } = true;
}

public class ProcessingConfig
{
    public int BatchSize { get; set; } = 50;
    public int BatchDelayMs { get; set; } = 100;
    public int MaxDegreeOfParallelism { get; set; } = 4;
    public bool UseAirportInfrastructure { get; set; } = true;
    public bool VerboseLogging { get; set; } = false;
}

public record StorageOptions(string DataDir, string BundledDataDir);

public record DbSettings(
    string Host,
    int Port,
    string Username,
    string Password,
    string Database);

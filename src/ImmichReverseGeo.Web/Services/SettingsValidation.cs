using System;
using System.Globalization;
using Cronos;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public static class SettingsValidation
{
    public static string? GetScheduleEditorError(ScheduleEditorState editor)
    {
        if ((editor.Mode is ScheduleEditorState.ModeHourly or ScheduleEditorState.ModeEveryHours)
            && editor.Minute is < 0 or > 59)
        {
            return "Minute past the hour must be between 0 and 59.";
        }
        if ((editor.Mode is ScheduleEditorState.ModeDaily or ScheduleEditorState.ModeWeekly)
            && !TimeOnly.TryParseExact(editor.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return "Choose a valid time for the UTC schedule.";
        }
        return null;
    }

    public static DateTime? NextRun(string cron, DateTime utcNow)
    {
        try
        {
            return CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(utcNow, TimeZoneInfo.Utc);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }

    public static string? GetError(AppConfig config, DateTime utcNow)
    {
        if (config.Schedule.Enabled && NextRun(config.Schedule.Cron, utcNow) is null)
        {
            return "Enter a valid five-part cron expression with a future run. Schedule times use UTC.";
        }
        if (config.Processing.BatchSize < 1)
        {
            return "Batch size must be at least 1.";
        }
        if (config.Processing.BatchDelayMs < 0)
        {
            return "Batch delay cannot be negative.";
        }
        if (config.Processing.MaxDegreeOfParallelism is < 1 or > 32)
        {
            return "Parallel workers must be between 1 and 32.";
        }
        return null;
    }
}

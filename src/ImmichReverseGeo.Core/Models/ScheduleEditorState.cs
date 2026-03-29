using System;
using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Core.Models;

public sealed class ScheduleEditorState
{
    public const string ModeHourly = "hourly";
    public const string ModeEveryMinutes = "every-minutes";
    public const string ModeEveryHours = "every-hours";
    public const string ModeDaily = "daily";
    public const string ModeWeekly = "weekly";
    public const string ModeCustom = "custom";

    public string Mode { get; set; } = ModeDaily;
    public string Time { get; set; } = "02:00";
    public string WeeklyDay { get; set; } = "MON";
    public int Minute { get; set; }
    public int MinuteInterval { get; set; } = 15;
    public int HourInterval { get; set; } = 6;

    public static ScheduleEditorState FromCron(string? cron)
    {
        var state = new ScheduleEditorState();

        if (TryParseHourlyCron(cron, out var hourlyMinute))
        {
            state.Mode = ModeHourly;
            state.Minute = hourlyMinute;
            return state;
        }

        if (TryParseEveryMinutesCron(cron, out var minuteInterval))
        {
            state.Mode = ModeEveryMinutes;
            state.MinuteInterval = minuteInterval;
            return state;
        }

        if (TryParseEveryHoursCron(cron, out var hourInterval, out var hourMinute))
        {
            state.Mode = ModeEveryHours;
            state.HourInterval = hourInterval;
            state.Minute = hourMinute;
            return state;
        }

        if (TryParseDailyCron(cron, out var dailyTime))
        {
            state.Mode = ModeDaily;
            state.Time = dailyTime;
            return state;
        }

        if (TryParseWeeklyCron(cron, out var weeklyTime, out var weeklyDay))
        {
            state.Mode = ModeWeekly;
            state.Time = weeklyTime;
            state.WeeklyDay = weeklyDay;
            return state;
        }

        state.Mode = ModeCustom;
        return state;
    }

    public string ToCron()
    {
        var (hour, minute) = ParseTimeParts(Time);
        var scheduleMinute = Math.Clamp(Minute, 0, 59);

        return Mode switch
        {
            ModeHourly => $"{scheduleMinute} * * * *",
            ModeEveryMinutes => $"*/{Math.Clamp(MinuteInterval, 1, 59)} * * * *",
            ModeEveryHours => $"{scheduleMinute} */{Math.Clamp(HourInterval, 1, 23)} * * *",
            ModeWeekly => $"{minute} {hour} * * {WeeklyDay}",
            _ => $"{minute} {hour} * * *"
        };
    }

    public string Describe()
    {
        return Mode switch
        {
            ModeHourly => $"Every hour at minute {Math.Clamp(Minute, 0, 59):00}",
            ModeEveryMinutes => $"Every {Math.Clamp(MinuteInterval, 1, 59)} minutes",
            ModeEveryHours => $"Every {Math.Clamp(HourInterval, 1, 23)} hours at minute {Math.Clamp(Minute, 0, 59):00}",
            ModeWeekly => $"Every {GetWeeklyDayLabel(WeeklyDay)} at {Time}",
            ModeCustom => "Uses a custom cron expression",
            _ => $"Every day at {Time}"
        };
    }

    private static (int Hour, int Minute) ParseTimeParts(string time)
    {
        var parts = time.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var hour)
            && int.TryParse(parts[1], out var minute))
        {
            return (Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
        }

        return (2, 0);
    }

    private static string GetWeeklyDayLabel(string day)
    {
        return day.ToUpperInvariant() switch
        {
            "MON" => "Monday",
            "TUE" => "Tuesday",
            "WED" => "Wednesday",
            "THU" => "Thursday",
            "FRI" => "Friday",
            "SAT" => "Saturday",
            "SUN" => "Sunday",
            _ => day
        };
    }

    private static bool TryParseDailyCron(string? cron, out string time)
    {
        var match = Regex.Match(cron ?? string.Empty, @"^(?<min>\d{1,2})\s+(?<hour>\d{1,2})\s+\*\s+\*\s+\*$");
        if (match.Success)
        {
            time = $"{int.Parse(match.Groups["hour"].Value):00}:{int.Parse(match.Groups["min"].Value):00}";
            return true;
        }

        time = "02:00";
        return false;
    }

    private static bool TryParseWeeklyCron(string? cron, out string time, out string day)
    {
        var match = Regex.Match(
            cron ?? string.Empty,
            @"^(?<min>\d{1,2})\s+(?<hour>\d{1,2})\s+\*\s+\*\s+(?<day>MON|TUE|WED|THU|FRI|SAT|SUN)$",
            RegexOptions.IgnoreCase);
        if (match.Success)
        {
            time = $"{int.Parse(match.Groups["hour"].Value):00}:{int.Parse(match.Groups["min"].Value):00}";
            day = match.Groups["day"].Value.ToUpperInvariant();
            return true;
        }

        time = "02:00";
        day = "MON";
        return false;
    }

    private static bool TryParseHourlyCron(string? cron, out int minute)
    {
        var match = Regex.Match(cron ?? string.Empty, @"^(?<min>\d{1,2})\s+\*\s+\*\s+\*\s+\*$");
        if (match.Success)
        {
            minute = Math.Clamp(int.Parse(match.Groups["min"].Value), 0, 59);
            return true;
        }

        minute = 0;
        return false;
    }

    private static bool TryParseEveryMinutesCron(string? cron, out int interval)
    {
        var match = Regex.Match(cron ?? string.Empty, @"^\*/(?<interval>\d{1,2})\s+\*\s+\*\s+\*\s+\*$");
        if (match.Success)
        {
            interval = Math.Clamp(int.Parse(match.Groups["interval"].Value), 1, 59);
            return true;
        }

        interval = 15;
        return false;
    }

    private static bool TryParseEveryHoursCron(string? cron, out int interval, out int minute)
    {
        var match = Regex.Match(cron ?? string.Empty, @"^(?<min>\d{1,2})\s+\*/(?<interval>\d{1,2})\s+\*\s+\*\s+\*$");
        if (match.Success)
        {
            minute = Math.Clamp(int.Parse(match.Groups["min"].Value), 0, 59);
            interval = Math.Clamp(int.Parse(match.Groups["interval"].Value), 1, 23);
            return true;
        }

        interval = 6;
        minute = 0;
        return false;
    }
}

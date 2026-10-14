using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Web.Services;

public static partial class CoordinateLookupInputParser
{
    // Parsing is independent of the page so malformed pasted values cannot interrupt its circuit.
    public static bool TryParse(string? input, out double latitude, out double longitude)
    {
        if (TryParseCore(input, out latitude, out longitude)
            && double.IsFinite(latitude) && latitude is >= -90 and <= 90
            && double.IsFinite(longitude) && longitude is >= -180 and <= 180)
        {
            return true;
        }

        latitude = longitude = 0;
        return false;
    }

    private static bool TryParseCore(string? input, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        foreach (Rune character in input.EnumerateRunes())
        {
            if (!character.IsAscii && Rune.IsDigit(character))
            {
                return false;
            }
        }

        string text = SeparatorRegex().Replace(input.Trim(), " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();

        Match dms = DmsRegex().Match(text);
        if (dms.Success)
        {
            if (Parse(dms.Groups[2]) >= 60 || Parse(dms.Groups[3]) >= 60
                || Parse(dms.Groups[6]) >= 60 || Parse(dms.Groups[7]) >= 60)
            {
                return false;
            }

            latitude = DmsToDecimal(dms.Groups[1], dms.Groups[2], dms.Groups[3], dms.Groups[4].Value);
            longitude = DmsToDecimal(dms.Groups[5], dms.Groups[6], dms.Groups[7], dms.Groups[8].Value);
            return true;
        }

        Match prefix = DirectionPrefixRegex().Match(text);
        if (prefix.Success)
        {
            latitude = ApplyDirection(Parse(prefix.Groups[2]), prefix.Groups[1].Value);
            longitude = ApplyDirection(Parse(prefix.Groups[4]), prefix.Groups[3].Value);
            return true;
        }

        Match suffix = DirectionSuffixRegex().Match(text);
        if (suffix.Success)
        {
            latitude = ApplyDirection(Parse(suffix.Groups[1]), suffix.Groups[2].Value);
            longitude = ApplyDirection(Parse(suffix.Groups[3]), suffix.Groups[4].Value);
            return true;
        }

        Match named = NamedRegex().Match(text);
        if (named.Success
            && TryParseNumber(named.Groups[1].Value, out latitude)
            && TryParseNumber(named.Groups[2].Value, out longitude))
        {
            return true;
        }

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && TryParseNumber(parts[0], out double first)
            && TryParseNumber(parts[1], out double second))
        {
            latitude = first;
            longitude = second;
            return true;
        }

        return false;
    }

    private static double DmsToDecimal(
        Group degrees,
        Group minutes,
        Group seconds,
        string direction)
    {
        double value = Parse(degrees)
            + Parse(minutes) / 60.0
            + Parse(seconds) / 3600.0;
        return ApplyDirection(value, direction);
    }

    private static double Parse(Group group) => Parse(group.Value);

    private static double Parse(string value) =>
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static bool TryParseNumber(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private static double ApplyDirection(double value, string direction) =>
        direction.ToUpperInvariant() is "S" or "W" ? -value : value;

    [GeneratedRegex(@"[,;|]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"([0-9]+)[°d\s]\s*([0-9]+)['']\s*([0-9]+(?:\.[0-9]+)?)['""s]?\s*([NSns])\s+" +
        @"([0-9]+)[°d\s]\s*([0-9]+)['']\s*([0-9]+(?:\.[0-9]+)?)['""s]?\s*([EWew])")]
    private static partial Regex DmsRegex();

    [GeneratedRegex(@"([NSns])\s*([0-9]+(?:\.[0-9]+)?)\s+([EWew])\s*([0-9]+(?:\.[0-9]+)?)")]
    private static partial Regex DirectionPrefixRegex();

    [GeneratedRegex(@"([0-9]+(?:\.[0-9]+)?)\s*([NSns])\s+([0-9]+(?:\.[0-9]+)?)\s*([EWew])")]
    private static partial Regex DirectionSuffixRegex();

    [GeneratedRegex(
        @"lat\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)\s+lon\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedRegex();
}

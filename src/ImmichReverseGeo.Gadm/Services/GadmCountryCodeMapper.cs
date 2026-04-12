using System;
using System.Collections.Generic;

namespace ImmichReverseGeo.Gadm.Services;

public static class GadmCountryCodeMapper
{
    private static readonly IReadOnlyDictionary<string, string> AppToGadmAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XKX"] = "XKO"
        };

    private static readonly IReadOnlyDictionary<string, string> GadmToAppAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["XKO"] = "XKX"
        };

    public static string ToGadmCode(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return string.Empty;
        }

        var normalized = iso3.Trim().ToUpperInvariant();
        return AppToGadmAliases.TryGetValue(normalized, out var mapped) ? mapped : normalized;
    }

    public static string ToAppCode(string gadmCode)
    {
        if (string.IsNullOrWhiteSpace(gadmCode))
        {
            return string.Empty;
        }

        var normalized = gadmCode.Trim().ToUpperInvariant();
        return GadmToAppAliases.TryGetValue(normalized, out var mapped) ? mapped : normalized;
    }
}

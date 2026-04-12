using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Gadm.Services;

public static class GadmCountryFallbackCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Families =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["DNK"] = ["DNK", "GRL", "FRO"],
            ["GRL"] = ["GRL", "DNK", "FRO"],
            ["FRO"] = ["FRO", "DNK", "GRL"],

            ["GBR"] = ["GBR", "JEY", "GGY", "IMN"],
            ["JEY"] = ["JEY", "GBR", "GGY", "IMN"],
            ["GGY"] = ["GGY", "GBR", "JEY", "IMN"],
            ["IMN"] = ["IMN", "GBR", "JEY", "GGY"],

            ["NLD"] = ["NLD", "ABW", "CUW", "SXM", "BES"],
            ["ABW"] = ["ABW", "NLD", "CUW", "SXM", "BES"],
            ["CUW"] = ["CUW", "NLD", "ABW", "SXM", "BES"],
            ["SXM"] = ["SXM", "NLD", "ABW", "CUW", "BES"],
            ["BES"] = ["BES", "NLD", "ABW", "CUW", "SXM"],

            ["FRA"] = ["FRA", "GLP", "GUF", "MTQ", "REU", "MYT", "BLM", "MAF", "PYF", "SPM", "WLF", "NCL"],
            ["NOR"] = ["NOR", "SJM"],
            ["FIN"] = ["FIN", "ALA"],
            ["USA"] = ["USA", "PRI", "VIR", "GUM", "ASM", "MNP"],
            ["AUS"] = ["AUS", "CXR", "CCK", "NFK"],
            ["NZL"] = ["NZL", "COK", "NIU", "TKL"]
        };

    public static IReadOnlyList<string> ExpandCandidateCodes(string iso3)
    {
        if (string.IsNullOrWhiteSpace(iso3))
        {
            return [];
        }

        var normalized = iso3.Trim().ToUpperInvariant();
        if (!Families.TryGetValue(normalized, out var family))
        {
            return [normalized];
        }

        return family
            .Select(GadmCountryCodeMapper.ToAppCode)
            .Append(normalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Legacy.Models;

public static class FoursquareCategoryIds
{
    public const string Airport = "4bf58dd8d48988d1ed931735";
    public const string LegacyAirportGate = "4bf58dd8d48988d1f0931735";

    public static IReadOnlyList<string> NormalizeConfiguredIds(IEnumerable<string> categoryIds)
    {
        var results = new List<string>();

        foreach (var categoryId in categoryIds)
        {
            results.Add(categoryId == LegacyAirportGate ? Airport : categoryId);
        }

        return results.Distinct(StringComparer.Ordinal).ToList();
    }
}

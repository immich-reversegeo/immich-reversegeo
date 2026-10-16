using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;

namespace ImmichReverseGeo.Overture.Services;

/// <summary>Chooses labels at lookup time; geographic selection never depends on language.</summary>
public static class OvertureNameLocalizer
{
    public static string SelectName(string? namesJson, string fallback, LocationNameOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(namesJson))
        {
            return fallback;
        }

        options ??= new LocationNameOptions();
        try
        {
            using var json = JsonDocument.Parse(namesJson);
            var names = json.RootElement;
            if (names.ValueKind != JsonValueKind.Object)
            {
                return fallback;
            }
            var primary = ReadString(names, "primary");
            names.TryGetProperty("common", out var common);
            if (options.Mode == LocationNameOptions.Preferred && LocationNameOptions.IsLanguageTag(options.PreferredLanguage))
            {
                var language = options.PreferredLanguage.Trim();
                while (true)
                {
                    var translated = ReadString(common, language);
                    if (translated is not null)
                    {
                        return translated;
                    }
                    var separator = language.LastIndexOf('-');
                    if (separator < 0)
                    {
                        break;
                    }
                    language = language[..separator];
                }
            }

            return options.Mode is LocationNameOptions.Local or LocationNameOptions.Preferred
                ? primary ?? ReadString(common, "en") ?? fallback
                : ReadString(common, "en") ?? primary ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    public static OvertureDivisionLookupDiagnostics Localize(
        OvertureDivisionLookupDiagnostics diagnostics, LocationNameOptions? options,
        IReadOnlyDictionary<string, (string? NamesJson, string? Country)>? divisionNames = null)
    {
        var candidates = diagnostics.Candidates.Select(candidate => candidate with
        {
            Name = SelectName(candidate.NamesJson, candidate.Name, options),
            HierarchyJson = LocalizeHierarchy(candidate.HierarchyJson, options, divisionNames)
        }).ToList();
        var best = diagnostics.BestMatch;
        return diagnostics with
        {
            Candidates = candidates,
            BestMatch = best is null ? null : best with
            {
                Name = candidates.FirstOrDefault(candidate => candidate.Id == best.Id)?.Name ?? best.Name
            }
        };
    }

    public static string SelectCountryName(OvertureDivisionLookupDiagnostics diagnostics,
        string alpha2, string fallback, LocationNameOptions? options)
    {
        if (options is null || options.Mode is not (LocationNameOptions.Local or LocationNameOptions.Preferred))
        {
            return fallback;
        }

        // Only borrow a name for the already resolved identity, never a territory's sovereign.
        foreach (var candidate in diagnostics.Candidates.Where(c => c.GeometryContainsPoint)
                     .OrderByDescending(c => c.IsExactGeometryMatch).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var ancestor = OvertureDivisionsLogic.ParseHierarchy(candidate).LastOrDefault(a =>
                a.SubType is "country" or "dependency"
                && string.Equals(a.Country, alpha2, StringComparison.OrdinalIgnoreCase));
            if (ancestor is not null)
            {
                return ancestor.Name;
            }
            if (candidate.SubType is "country" or "dependency"
                && string.Equals(candidate.Country, alpha2, StringComparison.OrdinalIgnoreCase))
            {
                return candidate.Name;
            }
        }
        return fallback;
    }

    private static string? LocalizeHierarchy(string? hierarchy, LocationNameOptions? options,
        IReadOnlyDictionary<string, (string? NamesJson, string? Country)>? divisionNames)
    {
        if (string.IsNullOrWhiteSpace(hierarchy))
        {
            return hierarchy;
        }
        try
        {
            if (JsonNode.Parse(hierarchy) is not JsonArray entries)
            {
                return hierarchy;
            }
            foreach (var entry in entries.OfType<JsonObject>())
            {
                string? fallback = null;
                if (entry["name"] is JsonValue name && name.TryGetValue<string>(out var original))
                {
                    fallback = original;
                }
                var namesJson = entry["names"]?.ToJsonString();
                if (divisionNames is not null && entry["division_id"] is JsonValue id
                    && id.TryGetValue<string>(out var divisionId) && divisionNames.TryGetValue(divisionId, out var stored))
                {
                    namesJson = stored.NamesJson;
                    entry["country"] = stored.Country;
                }
                if (fallback is not null || namesJson is not null)
                {
                    var localized = SelectName(namesJson, fallback ?? string.Empty, options);
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        entry["name"] = localized;
                    }
                }
            }
            return entries.ToJsonString();
        }
        catch (JsonException)
        {
            return hierarchy;
        }
    }

    private static string? ReadString(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString()!.Trim();
                }
            }
        }
        return null;
    }
}

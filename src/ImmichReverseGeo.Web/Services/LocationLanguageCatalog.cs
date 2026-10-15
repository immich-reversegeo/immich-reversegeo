using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public static class LocationLanguageCatalog
{
    public sealed record Language(string Code, string Name, string NativeName)
    {
        public string Label => $"{Name} ({Code})";
    }

    // This is a language chooser, not a promise that Overture has translations.
    private static IReadOnlyList<Language> AllLanguages { get; } = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
        .Where(culture => LocationNameOptions.IsLanguageTag(culture.Name))
        .Select(culture => new Language(culture.Name, culture.EnglishName, culture.NativeName))
        .DistinctBy(language => language.Code, StringComparer.OrdinalIgnoreCase)
        .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<Language> Languages { get; } = AllLanguages
        .Where(language => !language.Code.Contains('-'))
        .ToArray();

    public static string Display(string? code) => AllLanguages
        .FirstOrDefault(language => language.Code.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase))?.Label ?? code ?? string.Empty;

    public static string Resolve(string input)
    {
        var text = input.Trim();
        return AllLanguages.FirstOrDefault(language => language.Label.Equals(text, StringComparison.OrdinalIgnoreCase)
            || language.Code.Equals(text, StringComparison.OrdinalIgnoreCase)
            || language.Name.Equals(text, StringComparison.OrdinalIgnoreCase)
            || language.NativeName.Equals(text, StringComparison.OrdinalIgnoreCase))?.Code ?? text;
    }
}

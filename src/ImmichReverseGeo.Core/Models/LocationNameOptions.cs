using System.Text.RegularExpressions;

namespace ImmichReverseGeo.Core.Models;

public sealed class LocationNameOptions
{
    public const string English = "english";
    public const string Local = "local";
    public const string Preferred = "preferred";

    public string Mode { get; set; } = English;
    public string PreferredLanguage { get; set; } = "en";

    [System.Text.Json.Serialization.JsonIgnore]
    public string? ValidationError => Mode switch
    {
        English or Local => null,
        Preferred when IsLanguageTag(PreferredLanguage) => null,
        Preferred => "Enter a language code such as de, fr, ja, or zh-Hant.",
        _ => "Choose a location name language option."
    };

    public static bool IsLanguageTag(string? value) => value is not null
        && value.Length <= 35
        && Regex.IsMatch(value.Trim(), @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);
}

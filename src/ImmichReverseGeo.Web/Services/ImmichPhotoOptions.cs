using System;
using System.Linq;

namespace ImmichReverseGeo.Web.Services;

public sealed class ImmichPhotoOptions
{
    public Uri? ApiBaseUri { get; }
    public Uri? WebBaseUri { get; }
    internal string? ApiKey { get; }
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool ThumbnailsEnabled => ApiBaseUri is not null && HasApiKey;
    public string? ConfigurationError { get; }

    public ImmichPhotoOptions(string? serverUrl, string? webUrl, string? apiKey)
    {
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (ApiKey is not null && ApiKey.Any(char.IsControl))
        {
            ApiKey = null;
            ConfigurationError = "The Immich API key must be a single line without control characters.";
        }
        ApiBaseUri = ParseBaseUri(serverUrl);
        WebBaseUri = string.IsNullOrWhiteSpace(webUrl) ? ApiBaseUri : ParseBaseUri(webUrl);
        if ((!string.IsNullOrWhiteSpace(serverUrl) && ApiBaseUri is null)
            || (!string.IsNullOrWhiteSpace(webUrl) && WebBaseUri is null))
        {
            ConfigurationError = "Immich URLs must use HTTP or HTTPS without credentials, query parameters, or fragments.";
        }
    }

    public static ImmichPhotoOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("IMMICH_URL"),
        Environment.GetEnvironmentVariable("IMMICH_WEB_URL"),
        Environment.GetEnvironmentVariable("IMMICH_API_KEY"));

    public string? GetAssetUrl(Guid assetId) => WebBaseUri is null
        ? null : new Uri(WebBaseUri, $"photos/{assetId:D}").AbsoluteUri;

    private static Uri? ParseBaseUri(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return null;
        }
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }
        return new UriBuilder(uri) { Path = path + "/" }.Uri;
    }
}

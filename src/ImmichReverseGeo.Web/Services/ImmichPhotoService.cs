using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

public sealed record ImmichThumbnail(byte[] Bytes, string ContentType);

public sealed class ImmichPhotoService(HttpClient client, ImmichPhotoOptions options, SavedLocationPreviewRepository saved)
{
    public const int MaxThumbnailBytes = 2 * 1024 * 1024;

    public async Task<ImmichThumbnail?> GetThumbnailAsync(Guid previewId, Guid assetId, CancellationToken ct = default)
    {
        if (!options.ThumbnailsEnabled || !await saved.ContainsAssetAsync(previewId, assetId, ct))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var uri = new Uri(options.ApiBaseUri!, $"api/assets/{assetId:D}/thumbnail?size=thumbnail");
        var thumbnailPath = uri.AbsolutePath;
        try
        {
            for (var redirects = 0; redirects <= 2; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Add("x-api-key", options.ApiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return null;
                    }
                    var target = new Uri(uri, location);
                    // Never forward the key to another origin or request an original file.
                    if (target.GetLeftPart(UriPartial.Authority) != options.ApiBaseUri!.GetLeftPart(UriPartial.Authority)
                        || target.UserInfo.Length > 0 || target.Fragment.Length > 0
                        || target.AbsolutePath != thumbnailPath
                        || (target.Query != "?size=thumbnail" && target.Query != "?size=preview"))
                    {
                        return null;
                    }
                    uri = target;
                    continue;
                }

                var contentType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (!response.IsSuccessStatusCode
                    || contentType is not ("image/jpeg" or "image/png" or "image/webp" or "image/avif" or "image/gif")
                    || response.Content.Headers.ContentLength > MaxThumbnailBytes)
                {
                    return null;
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (bytes.Length + read > MaxThumbnailBytes)
                    {
                        return null;
                    }
                    bytes.Write(buffer, 0, read);
                }
                return bytes.Length == 0 ? null : new ImmichThumbnail(bytes.ToArray(), contentType!);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        return null;
    }
}

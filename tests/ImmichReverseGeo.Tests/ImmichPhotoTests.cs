using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ImmichPhotoTests
{
    private string _directory = null!;
    private readonly List<HttpClient> _clients = [];

    [TestInitialize]
    public void Setup() => _directory = Path.Combine(Path.GetTempPath(), $"preview-photos-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public void Links_UseBrowserUrlAndNeverContainApiKey()
    {
        var options = new ImmichPhotoOptions("http://immich:2283/api/", "https://photos.example.com/immich/", "secret-test-key");
        var asset = Guid.NewGuid();
        Assert.AreEqual("http://immich:2283/", options.ApiBaseUri!.AbsoluteUri);
        Assert.AreEqual($"https://photos.example.com/immich/photos/{asset:D}", options.GetAssetUrl(asset));
        Assert.IsFalse(JsonSerializer.Serialize(options).Contains("secret-test-key", StringComparison.Ordinal));
        Assert.IsTrue(options.ThumbnailsEnabled);
        var linksOnly = new ImmichPhotoOptions(null, "https://photos.example.com", null);
        Assert.IsNotNull(linksOnly.GetAssetUrl(asset));
        Assert.IsFalse(linksOnly.ThumbnailsEnabled);
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("file:///tmp/photo")]
    [DataRow("https://user:password@photos.example.com")]
    [DataRow("https://photos.example.com?apiKey=secret")]
    [DataRow("https://photos.example.com/#secret")]
    public void InvalidUrls_DisableTheConnectionWithoutEchoingValues(string url)
    {
        var options = new ImmichPhotoOptions(url, url, "test-key");
        Assert.IsNull(options.ApiBaseUri);
        Assert.IsNull(options.WebBaseUri);
        Assert.IsFalse(options.ThumbnailsEnabled);
        Assert.IsNotNull(options.ConfigurationError);
        Assert.IsFalse(options.ConfigurationError.Contains(url, StringComparison.Ordinal));
    }

    [TestMethod]
    public void InvalidKey_IsRejectedWithoutExposingItsValue()
    {
        var options = new ImmichPhotoOptions("https://photos.example.com", null, "secret\r\ninjected-value");
        Assert.IsFalse(options.HasApiKey);
        Assert.IsFalse(options.ThumbnailsEnabled);
        Assert.IsNotNull(options.ConfigurationError);
        Assert.IsFalse(options.ConfigurationError.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Thumbnail_IsReadOnlyAndSendsKeyInHeader()
    {
        var handler = new FakeHandler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("test-key", request.Headers.GetValues("x-api-key").Single());
            StringAssert.EndsWith(request.RequestUri!.AbsoluteUri, "/thumbnail?size=thumbnail");
            Assert.IsFalse(request.RequestUri.AbsoluteUri.Contains("test-key", StringComparison.Ordinal));
            return Image();
        });
        var (service, report, asset) = await ServiceAsync(handler);
        var thumbnail = await service.GetThumbnailAsync(report, asset);
        Assert.IsNotNull(thumbnail);
        Assert.AreEqual("image/webp", thumbnail.ContentType);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, thumbnail.Bytes);
    }

    [TestMethod]
    public async Task UnknownAssetsAndReports_DoNotContactImmich()
    {
        var handler = new FakeHandler(_ => Image());
        var (service, report, asset) = await ServiceAsync(handler);
        Assert.IsNull(await service.GetThumbnailAsync(report, Guid.NewGuid()));
        Assert.IsNull(await service.GetThumbnailAsync(Guid.NewGuid(), asset));
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    [DataRow("https://other.example.com/thumbnail?size=thumbnail")]
    [DataRow("/api/assets/another-asset/thumbnail?size=thumbnail")]
    public async Task Redirects_CannotForwardKeyToAnotherOriginOrAsset(string target)
    {
        var handler = new FakeHandler(_ => Redirect(target));
        var (service, report, asset) = await ServiceAsync(handler);
        Assert.IsNull(await service.GetThumbnailAsync(report, asset));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Redirects_AllowSameAssetPreviewButNeverOriginalDownloads()
    {
        var handler = new FakeHandler(request =>
            request.RequestUri!.Query == "?size=thumbnail"
                ? Redirect(request.RequestUri.AbsolutePath + "?size=preview") : Image());
        var (service, report, asset) = await ServiceAsync(handler);
        Assert.IsNotNull(await service.GetThumbnailAsync(report, asset));
        Assert.AreEqual(2, handler.Calls);

        var original = new FakeHandler(request => Redirect(request.RequestUri!.AbsolutePath.Replace("thumbnail", "original")));
        var (blocked, otherReport, otherAsset) = await ServiceAsync(original);
        Assert.IsNull(await blocked.GetThumbnailAsync(otherReport, otherAsset));
        Assert.AreEqual(1, original.Calls);
    }

    [TestMethod]
    public async Task UnavailableNonImageAndOversizedResponses_AreNotServed()
    {
        foreach (var response in new[]
        {
            new HttpResponseMessage(HttpStatusCode.Forbidden),
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not an image</html>") },
            Image(new byte[ImmichPhotoService.MaxThumbnailBytes + 1]),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ChunkedImageContent(new byte[ImmichPhotoService.MaxThumbnailBytes + 1])
            }
        })
        {
            var handler = new FakeHandler(_ => response);
            var (service, report, asset) = await ServiceAsync(handler);
            Assert.IsNull(await service.GetThumbnailAsync(report, asset));
        }
    }

    private async Task<(ImmichPhotoService Service, Guid Report, Guid Asset)> ServiceAsync(FakeHandler handler)
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var report = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var asset = Guid.NewGuid();
        await repository.AppendAsync(report, new(new(asset, 47, 8, new("Switzerland", "Aargau", "City")), new("Switzerland", "Aargau", "Other city")));
        var client = new HttpClient(handler);
        _clients.Add(client);
        return (new(client, new("http://immich:2283", null, "test-key"), repository), report, asset);
    }

    private static HttpResponseMessage Image(byte[]? bytes = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes ?? [1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        return response;
    }

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reply(request));
        }
    }

    private sealed class ChunkedImageContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ChunkedImageContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("image/webp");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes));
    }
}

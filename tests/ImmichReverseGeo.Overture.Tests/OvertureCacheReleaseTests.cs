using System.Collections.Concurrent;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCacheReleaseTests
{
    private const string OldRelease = "2026-08-19.0";
    private const string NewRelease = "2026-09-23.1";

    [TestMethod]
    public async Task NewRelease_RefreshesUsedCountriesAndSharesOneCatalogCheck()
    {
        using var service = new TestService();
        service.Seed("ARE", OldRelease);
        service.Seed("CHE", OldRelease);
        await Task.WhenAll(service.EnsureDataAsync("ARE"), service.EnsureDataAsync("CHE"));
        Assert.AreEqual(1, service.Checks);
        Assert.AreEqual(2, service.Exported.Count);
        Assert.AreEqual(NewRelease, service.StoredRelease("ARE"));
        Assert.AreEqual(NewRelease, service.StoredRelease("CHE"));
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(1, service.Checks);
        Assert.AreEqual(2, service.Exported.Count);
    }

    [TestMethod]
    public async Task DailyCheck_RefreshesOnlyAfterAnotherReleaseIsAvailable()
    {
        using var service = new TestService();
        service.Seed("ARE", NewRelease);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(0, service.Exported.Count);
        service.Latest = "2026-10-21.0";
        service.Now += TimeSpan.FromHours(23);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(1, service.Checks);
        Assert.AreEqual(NewRelease, service.StoredRelease("ARE"));
        service.Now += TimeSpan.FromHours(1);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(2, service.Checks);
        Assert.AreEqual(service.Latest, service.StoredRelease("ARE"));
    }

    [TestMethod]
    [DataRow("2026-09-23.10", "2026-09-23.9", false)]
    [DataRow("2026-09-23.9", "2026-09-23.10", true)]
    [DataRow("2026-10-21.0", NewRelease, false)]
    public async Task ReleaseComparison_UsesNumericPatchAndDoesNotDowngrade(string cached, string latest, bool refresh)
    {
        using var service = new TestService { Latest = latest };
        service.Seed("ARE", cached);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(refresh ? 1 : 0, service.Exported.Count);
        Assert.AreEqual(refresh ? latest : cached, service.StoredRelease("ARE"));
    }

    [TestMethod]
    public async Task ManualDownload_RechecksCatalogBeforeDailyIntervalExpires()
    {
        using var service = new TestService();
        service.Seed("ARE", NewRelease);
        await service.EnsureDataAsync("ARE");
        service.Latest = "2026-10-21.0";
        service.DeleteFile("ARE");
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(2, service.Checks);
        Assert.AreEqual(service.Latest, service.StoredRelease("ARE"));
    }

    [TestMethod]
    public async Task ProcessingScope_PinsReleaseUntilThePassFinishes()
    {
        using var service = new TestService { Latest = OldRelease };
        service.Seed("ARE", OldRelease);
        service.Seed("CHE", OldRelease);
        using (service.BeginReleaseScope())
        {
            await service.EnsureDataAsync("ARE");
            service.Latest = NewRelease;
            service.Now += TimeSpan.FromDays(2);
            await service.EnsureDataAsync("CHE");
            Assert.AreEqual(1, service.Checks);
            Assert.AreEqual(OldRelease, service.StoredRelease("CHE"));
        }
        await service.EnsureDataAsync("CHE");
        Assert.AreEqual(2, service.Checks);
        Assert.AreEqual(NewRelease, service.StoredRelease("CHE"));
    }

    [TestMethod]
    public async Task CatalogFailure_RetainsCompatibleCacheAndRetriesAfterOneHour()
    {
        using var service = new TestService { CatalogFails = true };
        service.Seed("ARE", OldRelease);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(OldRelease, service.StoredRelease("ARE"));
        service.Now += TimeSpan.FromMinutes(30);
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(1, service.Checks);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await service.EnsureDataAsync("CHE"));
        service.Now += TimeSpan.FromMinutes(30);
        service.CatalogFails = false;
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(2, service.Checks);
        Assert.AreEqual(NewRelease, service.StoredRelease("ARE"));
    }

    [TestMethod]
    [DataRow("throw")]
    [DataRow("wrong-release")]
    [DataRow("invalid")]
    public async Task RefreshFailure_RetainsOriginalAndThrottlesRetries(string failure)
    {
        using var service = new TestService { ExportFailure = failure };
        service.Seed("ARE", OldRelease);
        var original = File.ReadAllBytes(service.CachePath("ARE"));
        await service.EnsureDataAsync("ARE");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(service.CachePath("ARE")));
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(1, service.Exported.Count);
        Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(service.CachePath("ARE"))!, "*.tmp").Length);
        service.Now += TimeSpan.FromHours(1);
        service.ExportFailure = null;
        await service.EnsureDataAsync("ARE");
        Assert.AreEqual(2, service.Exported.Count);
        Assert.AreEqual(NewRelease, service.StoredRelease("ARE"));
    }

    [TestMethod]
    public async Task CatalogFailure_DoesNotMakeIncompatibleFormatReady()
    {
        using var service = new TestService { CatalogFails = true };
        service.Seed("ARE", OldRelease, "0");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await service.EnsureDataAsync("ARE"));
        Assert.IsFalse(service.HasData("ARE"));
        Assert.AreEqual(OldRelease, service.StoredRelease("ARE"));
    }

    private sealed class TestService : OvertureDivisionCacheService, IDisposable
    {
        private readonly string _root;
        public DateTimeOffset Now = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public string Latest = NewRelease;
        public bool CatalogFails;
        public string? ExportFailure;
        public int Checks;
        public ConcurrentBag<string> Exported = [];
        protected override DateTimeOffset UtcNow => Now;

        public TestService() : this(Path.Combine(Path.GetTempPath(), "reversegeo-release-" + Guid.NewGuid().ToString("N"))) { }
        private TestService(string root) : base(NullLogger<OvertureDivisionCacheService>.Instance, root, code => code == "ARE" ? "AE" : "CH")
        {
            _root = root;
            Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        }

        public string CachePath(string code) => Path.Combine(_root, "overture-divisions", code + ".db");
        public void Seed(string code, string release, string format = CacheFormatVersion) => Write(CachePath(code), release, format);
        public string StoredRelease(string code)
        {
            using var connection = new SqliteConnection($"Data Source={CachePath(code)};Mode=ReadOnly;Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM _meta WHERE key='release'";
            return (string)command.ExecuteScalar()!;
        }

        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct)
        {
            Interlocked.Increment(ref Checks);
            if (CatalogFails)
            {
                throw new InvalidOperationException("Catalog unavailable");
            }
            return Latest;
        }

        protected override long ExportOvertureDivisions(string tmpPath, string alpha2, string iso3, string release, CancellationToken ct)
        {
            Exported.Add(release);
            if (ExportFailure == "invalid")
            {
                File.WriteAllText(tmpPath, "invalid database");
                return 1;
            }
            Write(tmpPath, ExportFailure == "wrong-release" ? OldRelease : release, CacheFormatVersion);
            if (ExportFailure == "throw")
            {
                throw new InvalidOperationException("Download unavailable");
            }
            return 1;
        }

        private static void Write(string path, string release, string format)
        {
            using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO division_area VALUES ('city', 'Business Bay');
                CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO _meta VALUES ('downloadedAt', '2026-08-19T00:00:00Z');
                INSERT INTO _meta VALUES ('release', $release);
                INSERT INTO _meta VALUES ('cacheFormatVersion', $format);
                """;
            command.Parameters.AddWithValue("$release", release);
            command.Parameters.AddWithValue("$format", format);
            command.ExecuteNonQuery();
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCacheUpgradeTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("0")]
    [DataRow("999")]
    [DataRow("invalid")]
    public void NonCurrentFormat_IsNotReadyEvenAfterStatusRead(string? version)
    {
        using var fixture = new CacheFixture(version);
        var original = File.ReadAllBytes(fixture.Path);
        Assert.AreEqual(1L, fixture.Service.GetStatus()["ARE"].RowCount);
        Assert.IsFalse(fixture.Service.HasData("ARE"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Path));
    }

    [TestMethod]
    public async Task CurrentFormat_IsReusedEvenWithAnOldOvertureRelease()
    {
        using var fixture = new CacheFixture(OvertureDivisionCacheService.CacheFormatVersion);
        fixture.Service.GetStatus();
        Assert.IsTrue(fixture.Service.HasData("ARE"));
        await fixture.Service.EnsureDataAsync("ARE");
        Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, await fixture.Service.EnsureDataAsync("ARE"));
        Assert.AreEqual(0, fixture.Service.Exports);
    }

    [TestMethod]
    public async Task LegacyArabicCache_IsReplacedOnceForConcurrentRequests()
    {
        using var fixture = new CacheFixture(null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Service.Export = (path, ct) =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10), ct))
            {
                throw new TimeoutException("Test export was not released");
            }
            WriteCache(path, OvertureDivisionCacheService.CacheFormatVersion, "Business Bay");
            return 1;
        };
        var first = fixture.Service.GetOrStartDownload("ARE");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = fixture.Service.GetOrStartDownload("ARE");
            Assert.AreSame(first.Task, second.Task);
            Assert.AreEqual(OvertureDivisionEnsureResult.AwaitedExistingDownload, second.Result);
            Assert.AreEqual("الخليج التجاري", ReadName(fixture.Path));
        }
        finally
        {
            release.Set();
            await first.Task;
        }
        Assert.AreEqual(1, fixture.Service.Exports);
        Assert.IsTrue(fixture.Service.HasData("ARE"));
        Assert.AreEqual("Business Bay", ReadName(fixture.Path));
        Assert.AreEqual(OvertureDivisionEnsureResult.AlreadyReady, await fixture.Service.EnsureDataAsync("ARE"));
        Assert.AreEqual(1, fixture.Service.Exports);
    }

    [TestMethod]
    [DataRow("failure")]
    [DataRow("invalid")]
    [DataRow("old-format")]
    [DataRow("empty")]
    public async Task FailedReplacement_PreservesOriginalAndCanRetry(string outcome)
    {
        using var fixture = new CacheFixture(null);
        var original = File.ReadAllBytes(fixture.Path);
        fixture.Service.Export = (path, _) =>
        {
            if (outcome == "invalid")
            {
                File.WriteAllText(path, "not a database");
                return 1;
            }
            WriteCache(path, outcome == "old-format" ? null : OvertureDivisionCacheService.CacheFormatVersion, "Business Bay");
            if (outcome == "failure")
            {
                throw new InvalidOperationException("Download failed");
            }
            return outcome == "empty" ? 0 : 1;
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await fixture.Service.EnsureDataAsync("ARE"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Path));
        Assert.IsFalse(fixture.Service.HasData("ARE"));
        Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.tmp").Length);
        fixture.Service.Export = (path, _) =>
        {
            WriteCache(path, OvertureDivisionCacheService.CacheFormatVersion, "Business Bay");
            return 1;
        };
        await fixture.Service.EnsureDataAsync("ARE");
        Assert.IsTrue(fixture.Service.HasData("ARE"));
        Assert.AreEqual("Business Bay", ReadName(fixture.Path));
        Assert.AreEqual(2, fixture.Service.Exports);
    }

    [TestMethod]
    public async Task CancelledReplacement_PreservesOriginal()
    {
        using var fixture = new CacheFixture(null);
        var original = File.ReadAllBytes(fixture.Path);
        fixture.Service.Export = (path, _) =>
        {
            WriteCache(path, OvertureDivisionCacheService.CacheFormatVersion, "Business Bay");
            throw new OperationCanceledException();
        };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await fixture.Service.EnsureDataAsync("ARE"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(fixture.Path));
        Assert.IsFalse(fixture.Service.HasData("ARE"));
        Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.tmp").Length);
    }

    private static void WriteCache(string path, string? version, string name)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (id TEXT PRIMARY KEY, name TEXT NOT NULL);
            INSERT INTO division_area VALUES ('business-bay', $name);
            CREATE TABLE _meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO _meta VALUES ('downloadedAt', '2020-01-01T00:00:00Z');
            INSERT INTO _meta VALUES ('release', '2020-01-01.0');
            """;
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        if (version is not null)
        {
            command.CommandText = "INSERT INTO _meta VALUES ('cacheFormatVersion', $version)";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$version", version);
            command.ExecuteNonQuery();
        }
    }

    private static string ReadName(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM division_area";
        return (string)command.ExecuteScalar()!;
    }

    private sealed class CacheFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "reversegeo-upgrade-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public TestCacheService Service { get; }

        public CacheFixture(string? version)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(_root, "overture-divisions"));
            Path = System.IO.Path.Combine(_root, "overture-divisions", "ARE.db");
            WriteCache(Path, version, "الخليج التجاري");
            Service = new TestCacheService(_root);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class TestCacheService(string root)
        : OvertureDivisionCacheService(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "AE")
    {
        public int Exports;
        public Func<string, CancellationToken, long> Export { get; set; } = (_, _) => throw new InvalidOperationException("Unexpected export");

        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2020-01-01.0";

        protected override long ExportOvertureDivisions(string tmpPath, string alpha2, string iso3, string release, CancellationToken ct)
        {
            Interlocked.Increment(ref Exports);
            return Export(tmpPath, ct);
        }
    }
}

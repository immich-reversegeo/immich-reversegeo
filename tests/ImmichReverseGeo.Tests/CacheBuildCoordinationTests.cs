using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class CacheBuildCoordinationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuildGate_RemainsExclusiveAcrossModeChanges_AndCancelledWaitersDoNotLeak(bool lowMemory)
    {
        var policy = new MemoryResourcePolicy { LowMemoryMode = lowMemory };
        var first = await policy.EnterCacheBuildAsync();
        using var cancellation = new CancellationTokenSource();
        var cancelled = policy.EnterCacheBuildAsync(cancellation.Token);
        Assert.IsFalse(cancelled.IsCompleted);
        policy.LowMemoryMode = !lowMemory;
        var next = policy.EnterCacheBuildAsync();
        Assert.IsFalse(next.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
        first.Dispose();
        first.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(5));
        using var anotherCancellation = new CancellationTokenSource();
        var another = policy.EnterCacheBuildAsync(anotherCancellation.Token);
        Assert.IsFalse(another.IsCompleted, "Disposing a lease twice must not admit a second builder.");
        anotherCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => another);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OvertureAndGadm_ShareOneBuildSlot_AndReleaseItAfterFailure(bool lowMemory)
    {
        var root = Path.Combine(Path.GetTempPath(), "cache-build-coordination-" + Guid.NewGuid().ToString("N"));
        var policy = new MemoryResourcePolicy { LowMemoryMode = lowMemory };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gadmEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        Task? first = null;
        Task? second = null;
        Task? third = null;
        try
        {
            var overture = new OvertureFixture(root, policy)
            {
                Build = ct =>
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10), ct))
                    {
                        throw new TimeoutException("Builder was not released.");
                    }
                }
            };
            var gadm = new GadmFixture(root, policy)
            {
                Download = _ =>
                {
                    gadmEntered.TrySetResult();
                    throw new IOException("Simulated GADM download failure");
                }
            };
            first = overture.EnsureDataAsync("POL");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            third = overture.EnsureDataAsync("USA");
            var gadmRequest = gadm.GetOrStartDownload("FRA");
            second = gadmRequest.Task;
            Assert.AreSame(second, gadm.GetOrStartDownload("FRA").Task, "Same-country callers share the queued build.");
            Assert.IsTrue(gadm.GetActiveDownloads().Any(p => p.Stage.Contains("another country cache build", StringComparison.Ordinal)));
            Assert.AreNotSame(gadmEntered.Task, await Task.WhenAny(gadmEntered.Task, Task.Delay(150)),
                "GADM must not start downloading while Overture owns the build slot.");
            Assert.AreEqual(1, overture.Attempts, "A second Overture country must also wait for the shared slot.");
            release.Set();
            await Assert.ThrowsAsync<IOException>(() => first);
            await gadmEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IOException>(() => second);
            await Assert.ThrowsAsync<IOException>(() => third);
            using var next = await policy.EnterCacheBuildAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, overture.GetActiveDownloads().Length);
            Assert.AreEqual(0, gadm.GetActiveDownloads().Length);
        }
        finally
        {
            release.Set();
            foreach (var task in new[] { first, second, third })
            {
                if (task is not null)
                {
                    try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (IOException) { }
                }
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedBuilds_CoolDownPerCountry_Expire_AndCancellationIsRetryable()
    {
        var root = Path.Combine(Path.GetTempPath(), "cache-build-retries-" + Guid.NewGuid().ToString("N"));
        try
        {
            var policy = new MemoryResourcePolicy();
            var overture = new OvertureFixture(root, policy);
            var gadm = new GadmFixture(root, policy);
            await Assert.ThrowsAsync<IOException>(() => overture.EnsureDataAsync("POL"));
            await Assert.ThrowsAsync<IOException>(() => gadm.EnsureDataAsync("FRA"));
            for (var i = 0; i < 3; i++)
            {
                var o = overture.GetOrStartDownload("POL");
                var g = gadm.GetOrStartDownload("FRA");
                Assert.AreEqual(OvertureDivisionEnsureResult.RetryDeferred, o.Result);
                Assert.AreEqual(GadmDivisionEnsureResult.RetryDeferred, g.Result);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => o.Task);
                StringAssert.Contains(error.Message, "Retry after");
                await Assert.ThrowsAsync<InvalidOperationException>(() => g.Task);
            }
            Assert.AreEqual(1, overture.Attempts);
            Assert.AreEqual(1, gadm.Attempts);
            await Assert.ThrowsAsync<IOException>(() => overture.EnsureDataAsync("USA"));
            Assert.AreEqual(2, overture.Attempts, "A different country must remain eligible.");
            overture.Now += CacheBuildRetryPolicy.RetryDelay;
            gadm.Now += CacheBuildRetryPolicy.RetryDelay;
            await Assert.ThrowsAsync<IOException>(() => overture.EnsureDataAsync("POL"));
            await Assert.ThrowsAsync<IOException>(() => gadm.EnsureDataAsync("FRA"));
            Assert.AreEqual(3, overture.Attempts);
            Assert.AreEqual(2, gadm.Attempts);

            using var build = await policy.EnterCacheBuildAsync();
            using var cancellation = new CancellationTokenSource();
            var waitingOverture = overture.GetOrStartDownload("CHE", cancellation.Token).Task;
            var waitingGadm = gadm.GetOrStartDownload("CHE", cancellation.Token).Task;
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waitingOverture);
            await Assert.ThrowsAsync<OperationCanceledException>(() => waitingGadm);
            build.Dispose();
            // No cooldown after cancellation: the next requests reach the real builder.
            await Assert.ThrowsAsync<IOException>(() => overture.EnsureDataAsync("CHE"));
            await Assert.ThrowsAsync<IOException>(() => gadm.EnsureDataAsync("CHE"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OvertureFixture : OvertureDivisionCacheService
    {
        public DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        protected override DateTimeOffset UtcNow => Now;
        public int Attempts;
        public Action<CancellationToken>? Build;

        public OvertureFixture(string root, MemoryResourcePolicy policy)
            : base(NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "PL", policy) { }

        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "2026-09-23.1";

        protected override long ExportOvertureDivisions(string path, string alpha2, string iso3, string release, CancellationToken ct)
        {
            Interlocked.Increment(ref Attempts);
            Build?.Invoke(ct);
            throw new IOException("Simulated Overture export failure");
        }
    }

    private sealed class GadmFixture : GadmDivisionCacheService
    {
        public DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        protected override DateTimeOffset UtcNow => Now;
        public int Attempts;
        public Func<CancellationToken, Task>? Download;

        public GadmFixture(string root, MemoryResourcePolicy policy)
            : base(NullLogger<GadmDivisionCacheService>.Instance, root, policy) { }

        protected override Task DownloadFileAsync(string url, string path, CancellationToken ct, Action<long, long?> report)
        {
            Interlocked.Increment(ref Attempts);
            return Download?.Invoke(ct) ?? Task.FromException(new IOException("Simulated GADM download failure"));
        }
    }
}

using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class MemoryResourcePolicyTests
{
    [TestMethod]
    public async Task LowMemoryWork_IsSerializedAndReleasesAfterDispose()
    {
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        var first = await policy.EnterAsync();
        var waiting = policy.EnterAsync();
        Assert.IsFalse(waiting.IsCompleted);
        first.Dispose();
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(second.IsLowMemoryMode);
        var third = policy.EnterAsync();
        Assert.IsFalse(third.IsCompleted, "Disposing a lease twice must not grant an extra slot.");
        second.Dispose();
        using var thirdLease = await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CancelledWait_DoesNotConsumeSlot()
    {
        var policy = new MemoryResourcePolicy { LowMemoryMode = true };
        using var first = policy.Enter();
        using var cts = new CancellationTokenSource();
        var waiting = policy.EnterAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => waiting);
        first.Dispose();
        using var next = await policy.EnterAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task NormalMode_PreservesConcurrentWork()
    {
        var policy = new MemoryResourcePolicy();
        using var first = policy.Enter();
        using var second = await policy.EnterAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(second.IsLowMemoryMode);
    }
}

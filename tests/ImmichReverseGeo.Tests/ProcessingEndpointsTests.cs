using System.Net;
using System.Net.Http.Json;
using ImmichReverseGeo.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingEndpointsTests
{
    [TestMethod]
    public async Task Post_AcceptsRunWithoutRequiringAuthorizationBodyOrAntiforgeryToken()
    {
        var calls = 0;
        await using var app = await StartAppAsync(() => { calls++; return true; });
        using var client = CreateClient(app);
        using var response = await client.PostAsync("/api/processing/run", null);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode);
        Assert.AreEqual("accepted", (await response.Content.ReadFromJsonAsync<ApiResponse>())!.Status);
        Assert.IsTrue(response.Headers.CacheControl!.NoStore);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Get_DoesNotStartProcessing()
    {
        var calls = 0;
        await using var app = await StartAppAsync(() => { calls++; return true; });
        using var client = CreateClient(app);
        using var response = await client.GetAsync("/api/processing/run");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Maintenance_ReturnsConflictAndDoesNotQueueRun()
    {
        var state = new ProcessingState();
        using var processor = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance,
            null!, null!, state, null!, null!);
        using var maintenance = processor.TryBeginMaintenance();
        Assert.IsNotNull(maintenance);
        await using var app = await StartAppAsync(processor.TryTriggerRun);
        using var client = CreateClient(app);
        using var response = await client.PostAsync("/api/processing/run", null);
        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("busy", (await response.Content.ReadFromJsonAsync<ApiResponse>())!.Status);
        Assert.IsFalse(state.IsRunning);
        Assert.IsTrue(state.IsMaintaining);
    }

    [TestMethod]
    public async Task PendingRun_RejectsConcurrentTriggersAndMaintenance()
    {
        var state = new ProcessingState();
        // The pending notification runs before database work is dispatched. The background
        // pass then fails with these unused dependencies; this test exercises only admission.
        using var processor = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance,
            null!, null!, state, null!, null!);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingObserved = false;
        bool[]? concurrentStarts = null;
        IDisposable? maintenance = null;
        state.OnChanged += () =>
        {
            if (state.IsRunning && !pendingObserved)
            {
                pendingObserved = true;
                concurrentStarts = Task.WhenAll(Enumerable.Range(0, 8)
                    .Select(_ => Task.Run(processor.TryTriggerRun)))
                    .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                maintenance = processor.TryBeginMaintenance();
            }

            if (state.LastOutcome is not null)
            {
                completed.TrySetResult();
            }
        };

        Assert.IsTrue(processor.TryTriggerRun());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(pendingObserved);
        Assert.IsNotNull(concurrentStarts);
        Assert.IsTrue(concurrentStarts.All(started => !started));
        Assert.IsNull(maintenance);
    }

    private static async Task<WebApplication> StartAppAsync(Func<bool> trigger)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddAntiforgery();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseAntiforgery();
        app.MapProcessingEndpoints(trigger);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.Single()) };

    private sealed record ApiResponse(string Status, string Message);
}

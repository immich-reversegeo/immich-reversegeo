using System;
using System.Diagnostics;
using System.Threading;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Reports lookup stages without logging every successful asset or cache hit.</summary>
public sealed class LocationPreviewProgress(Action<string> reportStage, Action<string> logEvent)
    : IAdministrativeAreaResolutionProgress
{
    public void Report(string message)
    {
        reportStage(message);
        if (message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            logEvent($"[WARN] {message}");
        }
    }

    public IDisposable BeginActivity(string activity)
    {
        reportStage(activity);
        logEvent(activity);
        return new ActivityScope(activity, logEvent);
    }

    private sealed class ActivityScope(string activity, Action<string> logEvent) : IDisposable
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                logEvent($"Cache wait ended after {_elapsed.Elapsed.TotalSeconds:F1}s: {activity}");
            }
        }
    }
}

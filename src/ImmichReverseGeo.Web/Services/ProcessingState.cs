using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ImmichReverseGeo.Web.Services;

public enum ProcessingRunOutcome
{
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed
}

/// <summary>
/// Singleton shared between ProcessingBackgroundService and Blazor UI pages.
/// All mutations are thread-safe via Interlocked or lock.
/// UI subscribes to OnChanged to receive real-time updates.
/// </summary>
public class ProcessingState
{
    private volatile bool _isRunning;
    private volatile bool _isMaintaining;
    private long _totalUnprocessed;
    private readonly object _stateLock = new();
    private readonly object _activityLock = new();
    private DateTime? _lastRunStarted;
    private DateTime? _lastRunCompleted;
    private string? _lastError;
    private ProcessingRunOutcome? _lastOutcome;
    private long _checkedThisRun;
    private long _previouslySkippedThisRun;
    private readonly Dictionary<string, int> _activityCounts = new(StringComparer.Ordinal);

    public bool IsRunning => _isRunning;
    public bool IsMaintaining => _isMaintaining;

    public void SetMaintenance(bool active)
    {
        _isMaintaining = active;
        Notify();
    }
    public long TotalUnprocessed => Volatile.Read(ref _totalUnprocessed);
    public long ProcessedThisRun => Volatile.Read(ref _processedThisRun);
    public long ErrorsThisRun => Volatile.Read(ref _errorsThisRun);
    public long SkippedThisRun => Volatile.Read(ref _skippedThisRun);
    public long CheckedThisRun => Volatile.Read(ref _checkedThisRun);
    public long PreviouslySkippedThisRun => Volatile.Read(ref _previouslySkippedThisRun);
    public ProcessingRunOutcome? LastOutcome
    {
        get { lock (_stateLock) { return _lastOutcome; } }
    }
    public DateTime? LastRunStarted
    {
        get { lock (_stateLock) { return _lastRunStarted; } }
    }

    public DateTime? LastRunCompleted
    {
        get { lock (_stateLock) { return _lastRunCompleted; } }
    }

    public string? LastError
    {
        get { lock (_stateLock) { return _lastError; } }
    }

    // Ring buffer of last 100 log lines
    private readonly Queue<ProcessingLogEntry> _recentLog = new(capacity: 100);
    public IReadOnlyCollection<string> RecentLog => GetRecentLog();

    private volatile string? _currentActivity;
    /// <summary>Short description of what the run is doing right now (e.g. "Downloading ARE boundaries…").</summary>
    public string? CurrentActivity => _currentActivity;

    public void SetActivity(string? activity)
    {
        lock (_activityLock)
        {
            _currentActivity = activity;
            if (activity is null)
            {
                _activityCounts.Clear();
            }
        }
        Notify();
    }

    public IDisposable BeginActivity(string activity)
    {
        lock (_activityLock)
        {
            _activityCounts.TryGetValue(activity, out var currentCount);
            _activityCounts[activity] = currentCount + 1;
            _currentActivity = activity;
        }

        Notify();
        return new ActivityScope(this, activity);
    }

    public event Action? OnChanged;

    /// <summary>
    /// Called immediately when a run is triggered, before the background task starts,
    /// so the UI disables the Run Now button on the same render cycle.
    /// </summary>
    public void MarkPending()
    {
        ResetRunCounters();
        _isRunning = true;
        lock (_stateLock)
        {
            _lastRunStarted = DateTime.UtcNow;
            _lastError = null;
            _lastOutcome = null;
        }
        Notify();
    }

    public void StartRun(long totalUnprocessed)
    {
        _isRunning = true;
        ResetRunCounters();
        Volatile.Write(ref _totalUnprocessed, totalUnprocessed);
        lock (_stateLock)
        {
            _lastRunStarted = DateTime.UtcNow;
            _lastError = null;
            _lastOutcome = null;
        }
        Notify();
    }

    public void IncrementProcessed()
    {
        Interlocked.Increment(ref _processedThisRun);
        Notify();
    }

    public void IncrementError(string message)
    {
        Interlocked.Increment(ref _errorsThisRun);
        lock (_stateLock)
        {
            _lastError = message;
        }
        AppendLog($"[ERROR] {message}");
        Notify();
    }

    public void IncrementSkipped()
    {
        Interlocked.Increment(ref _skippedThisRun);
        Notify();
    }

    public void RecordChecked(bool previouslySkipped = false)
    {
        if (previouslySkipped)
        {
            Interlocked.Increment(ref _previouslySkippedThisRun);
        }
        Interlocked.Increment(ref _checkedThisRun);
        Notify();
    }

    private void ResetRunCounters()
    {
        Interlocked.Exchange(ref _processedThisRun, 0);
        Interlocked.Exchange(ref _errorsThisRun, 0);
        Interlocked.Exchange(ref _skippedThisRun, 0);
        Interlocked.Exchange(ref _checkedThisRun, 0);
        Interlocked.Exchange(ref _previouslySkippedThisRun, 0);
        Volatile.Write(ref _totalUnprocessed, 0);
    }

    public void CompleteRun(ProcessingRunOutcome outcome = ProcessingRunOutcome.Completed)
    {
        _isRunning = false;
        lock (_activityLock)
        {
            _currentActivity = null;
            _activityCounts.Clear();
        }
        lock (_stateLock)
        {
            _lastRunCompleted = DateTime.UtcNow;
            _lastOutcome = outcome == ProcessingRunOutcome.Completed && ErrorsThisRun > 0
                ? ProcessingRunOutcome.CompletedWithErrors : outcome;
        }
        Notify();
    }

    public void AppendLog(string line)
    {
        lock (_recentLog)
        {
            if (_recentLog.Count >= 100)
            {
                _recentLog.Dequeue();
            }

            _recentLog.Enqueue(new ProcessingLogEntry(DateTimeOffset.UtcNow, line));
        }
        Notify();
    }

    public IReadOnlyList<string> GetRecentLog()
    {
        return GetRecentLogEntries().Select(entry => entry.ToUtcString()).ToArray();
    }

    public IReadOnlyList<ProcessingLogEntry> GetRecentLogEntries()
    {
        lock (_recentLog)
        {
            return _recentLog.ToArray();
        }
    }

    private long _processedThisRun, _errorsThisRun, _skippedThisRun;
    private void Notify() => OnChanged?.Invoke();

    private void EndActivity(string activity)
    {
        lock (_activityLock)
        {
            if (_activityCounts.TryGetValue(activity, out var count))
            {
                if (count <= 1)
                {
                    _activityCounts.Remove(activity);
                }
                else
                {
                    _activityCounts[activity] = count - 1;
                }
            }

            _currentActivity = _activityCounts.Count > 0
                ? _activityCounts.Keys.Last()
                : null;
        }

        Notify();
    }

    private sealed class ActivityScope(ProcessingState state, string activity) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                state.EndActivity(activity);
            }
        }
    }
}

public sealed record ProcessingLogEntry(DateTimeOffset Timestamp, string Message)
{
    public string ToUtcString() => $"[{Timestamp.UtcDateTime:O}] {Message}";

    public bool MatchesFilter(string filter) => filter switch
    {
        "[WARN]" => Message.Contains("[WARN]") || Message.Contains("[ERROR]"),
        "[ERROR]" => Message.Contains("[ERROR]"),
        _ => true
    };
}

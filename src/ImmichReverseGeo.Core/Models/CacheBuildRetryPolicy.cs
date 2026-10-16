using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Core.Models;

/// <summary>Prevents later assets from immediately repeating a failed country build.</summary>
public sealed class CacheBuildRetryPolicy
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private sealed record Failure(DateTimeOffset RetryAt, string Message);
    private readonly ConcurrentDictionary<string, Failure> _failures = new(StringComparer.Ordinal);

    public Task? GetDeferredFailure(string country, DateTimeOffset now)
    {
        if (_failures.TryGetValue(country, out var failure) && now < failure.RetryAt)
        {
            return Task.FromException(new InvalidOperationException(
                $"Cache preparation for {country} failed recently. Retry after {failure.RetryAt:O}. Last error: {failure.Message}"));
        }
        return null;
    }

    public void RecordFailure(string country, DateTimeOffset now, Exception error) =>
        _failures[country] = new Failure(now + RetryDelay, error.Message);

    public void Clear(string country) => _failures.TryRemove(country, out _);
}

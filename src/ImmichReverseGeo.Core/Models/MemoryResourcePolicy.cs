using System;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Core.Models;

/// <summary>Shared by downloads and remote queries across all sources.</summary>
public sealed class MemoryResourcePolicy
{
    private readonly SemaphoreSlim _heavyWork = new(1, 1);
    private volatile bool _lowMemoryMode;
    private int _countryIndexIdleMinutes = 5;

    public int CountryIndexIdleMinutes
    {
        get => Volatile.Read(ref _countryIndexIdleMinutes);
        set => Volatile.Write(ref _countryIndexIdleMinutes, Math.Clamp(value, 0, 1440));
    }

    public bool LowMemoryMode
    {
        get => _lowMemoryMode;
        set
        {
            _lowMemoryMode = value;
            if (value)
            {
                LowMemoryEnabled?.Invoke();
            }
        }
    }

    public event Action? LowMemoryEnabled;

    public WorkLease Enter(CancellationToken ct = default)
    {
        if (!LowMemoryMode)
        {
            return new WorkLease(null);
        }

        _heavyWork.Wait(ct);
        return new WorkLease(_heavyWork);
    }

    public async Task<WorkLease> EnterAsync(CancellationToken ct = default)
    {
        if (!LowMemoryMode)
        {
            return new WorkLease(null);
        }

        await _heavyWork.WaitAsync(ct);
        return new WorkLease(_heavyWork);
    }

    public sealed class WorkLease : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        public bool IsLowMemoryMode { get; }

        internal WorkLease(SemaphoreSlim? semaphore)
        {
            _semaphore = semaphore;
            IsLowMemoryMode = semaphore is not null;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}

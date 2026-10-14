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
    private int _duckDbThreads;

    public int DuckDbThreads
    {
        get => Volatile.Read(ref _duckDbThreads);
        set => Volatile.Write(ref _duckDbThreads, Math.Clamp(value, 0, 32));
    }

    private volatile bool _useBoundedCountryCache;
    private int _countryCacheMiB = 256;

    public bool UseBoundedCountryCache
    {
        get => _useBoundedCountryCache;
        set
        {
            if (_useBoundedCountryCache != value)
            {
                _useBoundedCountryCache = value;
                CountryCacheSettingsChanged?.Invoke();
            }
        }
    }

    public int CountryCacheMiB
    {
        get => Volatile.Read(ref _countryCacheMiB);
        set
        {
            var clamped = Math.Clamp(value, 1, 1024);
            if (Interlocked.Exchange(ref _countryCacheMiB, clamped) != clamped)
            {
                CountryCacheSettingsChanged?.Invoke();
            }
        }
    }

    public event Action? CountryCacheSettingsChanged;

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

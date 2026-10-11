using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Services;

// Packed WKB size plus per-part overhead is an estimate, not a CLR heap or process limit.
internal sealed class CountryPartCache(Func<long> getBudget)
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Row, long Offset), LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recent = new();
    private object? _generation;
    private long _weight, _hits, _misses, _evictions, _oversized;
    private sealed record Entry((long Row, long Offset) Key, long Weight, Geometry Geometry);

    public bool HasEntries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    public object Statistics
    {
        get
        {
            lock (_gate)
            {
                return new { estimatedBudgetBytes = getBudget(), estimatedRetainedBytes = _weight,
                    entries = _entries.Count, hits = _hits, misses = _misses, evictions = _evictions, oversized = _oversized };
            }
        }
    }

    public Geometry Get(object generation, (long Row, long Offset) key, int length, Func<Geometry> read)
    {
        lock (_gate)
        {
            // Check inside the lock: an in-flight lookup must not repopulate a disabled cache.
            var budget = getBudget();
            if (budget == 0)
            {
                ClearCore();
                return read();
            }
            if (!ReferenceEquals(generation, _generation))
            {
                ClearCore();
                _generation = generation;
            }
            if (_entries.TryGetValue(key, out var hit))
            {
                _hits++;
                _recent.Remove(hit);
                _recent.AddFirst(hit);
                return hit.Value.Geometry;
            }
            _misses++;
            var weight = (long)length + 4096;
            if (weight > budget)
            {
                _oversized++;
                return read();
            }
            // Drop references before decoding the replacement. Active lookups may still own them.
            while (_weight + weight > budget && _recent.Last is { } oldest)
            {
                _weight -= oldest.Value.Weight;
                _entries.Remove(oldest.Value.Key);
                _recent.RemoveLast();
                _evictions++;
            }
            var geometry = read();
            _entries.Add(key, _recent.AddFirst(new Entry(key, weight, geometry)));
            _weight += weight;
            return geometry;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCore();
            _generation = null;
        }
    }

    private void ClearCore()
    {
        _entries.Clear();
        _recent.Clear();
        _weight = 0;
    }
}

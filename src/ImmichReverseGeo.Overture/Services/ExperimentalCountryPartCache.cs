using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace ImmichReverseGeo.Overture.Services;

// Experimental estimated budget, not a measured CLR heap or process limit.
internal sealed class ExperimentalCountryPartCache(long budget)
{
    private readonly object _gate = new();
    private readonly Dictionary<(long Row, long Offset), LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _recent = new();
    private object? _generation;
    private long _weight, _hits, _misses, _evictions, _oversized;
    internal sealed record Part(Geometry Geometry, IPreparedGeometry? Prepared);
    private sealed record Entry((long Row, long Offset) Key, long Weight, Part Part);

    public bool HasEntries { get { lock (_gate) { return _entries.Count > 0; } } }

    public object Statistics
    {
        get
        {
            lock (_gate)
            {
                return new { estimatedBudgetBytes = budget, estimatedRetainedBytes = _weight,
                    entries = _entries.Count, hits = _hits, misses = _misses, evictions = _evictions, oversized = _oversized };
            }
        }
    }

    public Part Get(object generation, (long Row, long Offset) key, int length, Func<Geometry> read)
    {
        lock (_gate)
        {
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
                return hit.Value.Part;
            }
            _misses++;
            // Conservative initial hypothesis for packed coordinates plus lazy prepared indexes.
            var weight = (long)length * 16 + 4096;
            var geometry = read();
            if (weight > budget)
            {
                _oversized++;
                return new Part(geometry, null);
            }
            while (_weight + weight > budget && _recent.Last is { } oldest)
            {
                _weight -= oldest.Value.Weight;
                _entries.Remove(oldest.Value.Key);
                _recent.RemoveLast();
                _evictions++;
            }
            var part = new Part(geometry, PreparedGeometryFactory.Prepare(geometry));
            _entries.Add(key, _recent.AddFirst(new Entry(key, weight, part)));
            _weight += weight;
            return part;
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

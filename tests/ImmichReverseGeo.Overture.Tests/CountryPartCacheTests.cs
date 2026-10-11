using System.Text.Json;
using ImmichReverseGeo.Overture.Services;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class CountryPartCacheTests
{
    [TestMethod]
    public void Cache_EvictsLeastRecentlyUsedAndBypassesOversizedParts()
    {
        var cache = new CountryPartCache(() => 8194);
        var generation = new object();
        var reads = 0;
        Geometry Read()
        {
            reads++;
            return new GeometryFactory().CreatePoint(new Coordinate(reads, 0));
        }
        var first = cache.Get(generation, (1, 0), 1, Read);
        cache.Get(generation, (2, 0), 1, Read);
        Assert.AreSame(first, cache.Get(generation, (1, 0), 1, Read));
        cache.Get(generation, (3, 0), 1, Read);
        Assert.AreSame(first, cache.Get(generation, (1, 0), 1, Read));
        cache.Get(generation, (2, 0), 1, Read);
        Assert.AreEqual(4, reads, "The second part should be evicted after the first was reused.");
        cache.Get(generation, (4, 0), 8194, Read);
        cache.Get(generation, (4, 0), 8194, Read);
        Assert.AreEqual(6, reads, "Oversized parts must be decoded again rather than retained.");
        var stats = JsonSerializer.SerializeToElement(cache.Statistics);
        Assert.AreEqual(8194L, stats.GetProperty("estimatedRetainedBytes").GetInt64());
        Assert.AreEqual(2L, stats.GetProperty("oversized").GetInt64());
    }

    [TestMethod]
    public async Task Cache_ConcurrentMissesShareGeometryAndDisabledLookupsDoNotRetainIt()
    {
        long budget = 8194;
        var cache = new CountryPartCache(() => Volatile.Read(ref budget));
        var generation = new object();
        var reads = 0;
        Geometry Read()
        {
            Interlocked.Increment(ref reads);
            return new GeometryFactory().CreatePoint(new Coordinate(1, 0));
        }
        var parts = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => cache.Get(generation, (1, 0), 1, Read))));
        Assert.AreEqual(1, reads);
        Assert.IsTrue(parts.All(part => ReferenceEquals(parts[0], part)));
        var replacement = cache.Get(new object(), (1, 0), 1, Read);
        Assert.AreNotSame(parts[0], replacement);
        Volatile.Write(ref budget, 0);
        cache.Clear();
        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => cache.Get(generation, (1, 0), 1, Read))));
        Assert.IsFalse(cache.HasEntries);
    }
}

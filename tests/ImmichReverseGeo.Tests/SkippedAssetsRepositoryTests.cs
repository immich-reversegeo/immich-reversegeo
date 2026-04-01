using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class SkippedAssetsRepositoryTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [TestCleanup]
    public void Cleanup()
    {
        // Release any pooled SQLite connections before deleting files (required on Windows)
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task AddAndGet_RoundTrips()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();

        var id = Guid.NewGuid();
        await repo.AddAsync(id);
        var all = await repo.GetAllAsync();

        Assert.IsTrue(all.Contains(id));
    }

    [TestMethod]
    public async Task Add_Duplicate_DoesNotThrow()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();
        var id = Guid.NewGuid();
        await repo.AddAsync(id);
        await repo.AddAsync(id); // INSERT OR IGNORE
        var all = await repo.GetAllAsync();
        Assert.AreEqual(1, all.Count);
    }

    /// <summary>
    /// Test 6: Persistence across instances — data written by instance A is readable by instance B
    /// pointing to the same directory, after instance B calls InitialiseAsync.
    /// </summary>
    [TestMethod]
    public async Task Persistence_AcrossInstances_DataIsReadableByNewInstance()
    {
        // Instance A: initialise and add a GUID
        var repoA = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repoA.InitialiseAsync();

        var id = Guid.NewGuid();
        await repoA.AddAsync(id);

        // Instance B: points to same directory, initialise (table already exists — no-op for CREATE IF NOT EXISTS)
        var repoB = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repoB.InitialiseAsync();

        var all = await repoB.GetAllAsync();

        Assert.IsTrue(all.Contains(id), "GUID added by instance A should be returned by instance B");
    }

    [TestMethod]
    public async Task RemoveAsync_RemovesOnlyMatchingIds()
    {
        var repo = new SkippedAssetsRepository(NullLogger<SkippedAssetsRepository>.Instance, _tempDir);
        await repo.InitialiseAsync();

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();

        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await repo.AddAsync(third);

        var removed = await repo.RemoveAsync([first, third]);
        var remaining = await repo.GetAllAsync();

        Assert.AreEqual(2L, removed);
        Assert.IsFalse(remaining.Contains(first));
        Assert.IsTrue(remaining.Contains(second));
        Assert.IsFalse(remaining.Contains(third));
    }
}

using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationHistoryFilterTests
{
    private string _directory = null!;
    private SavedLocationPreviewRepository _scans = null!;
    private LocationChangeRepository _changes = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"history-filters-{Guid.NewGuid():N}");
        _scans = new(_directory);
        _changes = new(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SavedFilters_MatchContinuationAndAttention_WithoutRestrictingDetailLookup()
    {
        var ids = new Dictionary<string, Guid>();
        foreach (var status in new[] { "Paused", "More assets available", "Cancelled", "Interrupted", "Failed", "Running", "Complete" })
        {
            ids[status] = await ScanAsync(status);
        }
        var withError = await ScanAsync("Complete", "Saved error");
        var all = await _scans.GetReportsAsync();
        var continuable = await _scans.GetReportsAsync(filter: SavedScanFilter.CanContinue);
        CollectionAssert.AreEquivalent(all.Where(scan => scan.CanResume).Select(scan => scan.Id).ToArray(), continuable.Select(scan => scan.Id).ToArray());
        var attention = await _scans.GetReportsAsync(filter: SavedScanFilter.NeedsAttention);
        CollectionAssert.AreEquivalent(new[] { ids["Failed"], ids["Interrupted"], withError }, attention.Select(scan => scan.Id).ToArray());
        Assert.AreEqual(ids["Complete"], (await _scans.GetReportsAsync(id: ids["Complete"], filter: SavedScanFilter.CanContinue)).Single().Id);
    }

    [TestMethod]
    public async Task SavedFilter_AppliesBeforePagination_AndKeepsTheNextPageSentinel()
    {
        var matches = new List<Guid>();
        for (var index = 0; index < 27; index++)
        {
            matches.Add(await ScanAsync("Paused"));
        }
        for (var index = 0; index < 30; index++)
        {
            await ScanAsync("Complete");
        }
        Assert.IsTrue((await _scans.GetReportsAsync()).All(scan => scan.Status == "Complete"));
        var first = await _scans.GetReportsAsync(filter: SavedScanFilter.CanContinue);
        var second = await _scans.GetReportsAsync(2, filter: SavedScanFilter.CanContinue);
        Assert.HasCount(26, first);
        Assert.HasCount(2, second);
        CollectionAssert.AreEquivalent(matches.ToArray(), first.Take(25).Concat(second).Select(scan => scan.Id).ToArray());
        Assert.HasCount(0, await _scans.GetReportsAsync(3, filter: SavedScanFilter.CanContinue));
    }

    [TestMethod]
    public async Task OperationFilters_IncludeMixedOutcomes_AndPendingOrUncertainUndo()
    {
        var mixed = await _changes.CreateAsync(Guid.NewGuid(), new[] { Row(), Row() });
        var mixedItem = (await _changes.GetItemsAsync(mixed))[0];
        await _changes.ClaimAsync(mixed, mixedItem.AssetId, false);
        await _changes.FinishAsync(mixedItem, false, "NeedsReview", null, "Unknown outcome");
        var writing = await OperationAsync("Pending");
        var writingItem = (await _changes.GetItemsAsync(writing)).Single();
        await _changes.ClaimAsync(writing, writingItem.AssetId, false);
        var undo = await OperationAsync("Applied");
        var undoItem = (await _changes.GetItemsAsync(undo)).Single();
        await _changes.QueueUndoAsync(undo, new[] { undoItem.AssetId });
        await _changes.ClaimAsync(undo, undoItem.AssetId, true);
        var uncertainUndo = await OperationAsync("Applied");
        var uncertainItem = (await _changes.GetItemsAsync(uncertainUndo)).Single();
        await _changes.QueueUndoAsync(uncertainUndo, new[] { uncertainItem.AssetId });
        await _changes.ClaimAsync(uncertainUndo, uncertainItem.AssetId, true);
        await _changes.FinishAsync(uncertainItem, true, "NeedsReview", null, "Unknown undo outcome");
        var completed = await OperationAsync("Applied");
        CollectionAssert.AreEquivalent(new[] { mixed, writing, undo }, (await _changes.GetOperationsAsync(filter: LocationOperationFilter.Pending)).Select(operation => operation.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { mixed, uncertainUndo }, (await _changes.GetOperationsAsync(filter: LocationOperationFilter.NeedsReview)).Select(operation => operation.Id).ToArray());
        Assert.AreEqual(completed, (await _changes.GetOperationsAsync(id: completed, filter: LocationOperationFilter.Pending)).Single().Id);
    }

    [TestMethod]
    public async Task OperationFilter_AppliesBeforePagination_AndKeepsTheNextPageSentinel()
    {
        var matches = new List<Guid>();
        for (var index = 0; index < 27; index++)
        {
            matches.Add(await OperationAsync("Pending"));
        }
        for (var index = 0; index < 30; index++)
        {
            await OperationAsync("Applied");
        }
        Assert.IsTrue((await _changes.GetOperationsAsync()).All(operation => operation.Pending == 0));
        var first = await _changes.GetOperationsAsync(filter: LocationOperationFilter.Pending);
        var second = await _changes.GetOperationsAsync(2, filter: LocationOperationFilter.Pending);
        Assert.HasCount(26, first);
        Assert.HasCount(2, second);
        CollectionAssert.AreEquivalent(matches.ToArray(), first.Take(25).Concat(second).Select(operation => operation.Id).ToArray());
        Assert.HasCount(0, await _changes.GetOperationsAsync(3, filter: LocationOperationFilter.Pending));
    }

    private async Task<Guid> ScanAsync(string status, string? error = null)
    {
        var id = await _scans.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        await _scans.SetStatusAsync(id, status, error);
        return id;
    }

    private async Task<Guid> OperationAsync(string status)
    {
        var id = await _changes.CreateAsync(Guid.NewGuid(), new[] { Row() });
        if (status != "Pending")
        {
            var item = (await _changes.GetItemsAsync(id)).Single();
            await _changes.ClaimAsync(id, item.AssetId, false);
            await _changes.FinishAsync(item, false, status, "revision", null);
        }
        return id;
    }

    private static LocationPreviewRow Row() => new(new LocationPreviewAsset(Guid.NewGuid(), 0, 0, new GeoResult("Country", "State", "Old city")), new GeoResult("Country", "State", "New city"));
}

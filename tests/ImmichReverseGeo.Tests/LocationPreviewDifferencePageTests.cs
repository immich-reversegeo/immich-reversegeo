using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationPreviewDifferencePageTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Setup() => _directory = Path.Combine(Path.GetTempPath(), $"preview-differences-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(LocationPreviewScanMode.ManualPages)]
    [DataRow(LocationPreviewScanMode.Continuous)]
    public async Task Review_RetainsEarlierDifferencesAcrossScanBatchesWithBoundedPages(LocationPreviewScanMode mode)
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var report = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var existing = new GeoResult("France", "Region", "Old city");
        var assets = Enumerable.Range(0, 205).Select(_ => new LocationPreviewAsset(Guid.NewGuid(), 48, 2, existing)).ToList();
        var scanner = new LocationPreviewScanner(mode, 0);
        var review = new LocationPreviewDifferencePage();
        Task<IReadOnlyList<LocationPreviewRow>> Load(int page) => repository.GetRowsAsync(report, page, onlyChanges: true);
        var selected = new LocationPreviewReview();
        do
        {
            await scanner.RunAsync(
                (cursor, _) => Task.FromResult<IReadOnlyList<LocationPreviewAsset>>(assets
                    .Skip(cursor is null ? 0 : assets.FindIndex(a => a.Id == cursor.Id) + 1).Take(101).ToArray()),
                (asset, _) => Task.FromResult(new LocationPreviewRow(asset, existing with { City = "New city" })),
                row => repository.AppendAsync(report, row),
                async () =>
                {
                    await review.RefreshAsync(scanner.DifferentCount, Load);
                    Assert.IsTrue(review.Rows.Count() <= 100);
                    if (review.Rows.Any())
                    {
                        Assert.AreEqual(assets[0].Id, review.Rows.First().Asset.Id);
                        selected.Select(review.Rows.First(), true);
                    }
                });
        }
        while (scanner.HasMore);

        Assert.AreEqual(5, scanner.Rows.Count);
        Assert.AreEqual(205L, scanner.DifferentCount);
        CollectionAssert.AreEqual(assets.Take(100).Select(a => a.Id).ToArray(), review.Rows.Select(r => r.Asset.Id).ToArray());
        Assert.IsTrue(review.HasNext);
        var reviewed = review.Rows.ToList();
        await review.MoveAsync(1, scanner.DifferentCount, Load);
        Assert.AreEqual(2, review.Page);
        reviewed.AddRange(review.Rows);
        await review.MoveAsync(1, scanner.DifferentCount, Load);
        Assert.AreEqual(3, review.Page);
        reviewed.AddRange(review.Rows);
        Assert.IsFalse(review.HasNext);
        CollectionAssert.AreEqual(assets.Select(a => a.Id).ToArray(), reviewed.Select(r => r.Asset.Id).ToArray());
        Assert.IsTrue(selected.IsSelected(assets[0].Id));
        await review.MoveAsync(-1, scanner.DifferentCount, Load);
        Assert.AreEqual(assets[100].Id, review.Rows.First().Asset.Id);
    }

    [TestMethod]
    public async Task Review_SparseDifferencesStayWhenBatchIsClearedAndScanIsCancelled()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var report = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var old = new GeoResult("France", "Region", "Old city");
        var changed = new LocationPreviewRow(new(Guid.NewGuid(), 48, 2, old), old with { City = "New city" });
        await repository.AppendAsync(report, changed);
        var review = new LocationPreviewDifferencePage();
        Task<IReadOnlyList<LocationPreviewRow>> Load(int page) => repository.GetRowsAsync(report, page, onlyChanges: true);
        await review.RefreshAsync(1, Load);
        for (var i = 0; i < 100; i++)
        {
            await repository.AppendAsync(report, new(new(Guid.NewGuid(), 48, 2, old), old));
        }
        await repository.SetStatusAsync(report, "Cancelled");
        await review.RefreshAsync(1, Load, force: true);
        Assert.AreEqual(changed, review.Rows.Single());
        Assert.AreEqual(1, review.Page);
        Assert.IsFalse(review.HasNext);
        await review.MoveAsync(1, 1, Load);
        Assert.AreEqual(1, review.Page);
        // Starting a new preview uses a new review page with no stale differences.
        var newReview = new LocationPreviewDifferencePage();
        Assert.IsFalse(newReview.Rows.Any());
    }

    [TestMethod]
    public async Task Review_FailedPageLoadPreservesTheVisiblePage()
    {
        var old = new GeoResult("France", "Region", "Old city");
        var rows = Enumerable.Range(0, 101).Select(_ => new LocationPreviewRow(
            new(Guid.NewGuid(), 48, 2, old), old with { City = "New city" })).ToArray();
        var review = new LocationPreviewDifferencePage();
        await review.RefreshAsync(101, _ => Task.FromResult<IReadOnlyList<LocationPreviewRow>>(rows));
        await Assert.ThrowsAsync<IOException>(() => review.MoveAsync(1, 101, _ => throw new IOException("Read failed")));
        Assert.AreEqual(1, review.Page);
        Assert.AreEqual(rows[0], review.Rows.First());
    }
}

using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationPreviewScannerTests
{
    private static readonly GeoResult Existing = new("France", "Region", "City");
    private static readonly GeoResult Different = new("France", "Region", "Other");

    [TestMethod]
    public async Task Continuous_SavesAllAssetsWithBoundedLiveRows()
    {
        var fixture = new Fixture(205);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 0);
        var outcome = await fixture.Run(scanner);
        Assert.IsFalse(outcome.Paused);
        Assert.IsFalse(scanner.HasMore);
        Assert.AreEqual(205L, scanner.CheckedCount);
        Assert.AreEqual(205L, scanner.DifferentCount);
        Assert.AreEqual(3, scanner.BatchNumber);
        Assert.AreEqual(5, scanner.Rows.Count);
        Assert.AreEqual(205, fixture.Saved.Distinct().Count());
        Assert.AreEqual(100, fixture.MaxLiveRows);
    }

    [TestMethod]
    public async Task Pause_ResumesAtNextAssetAndCountsNewDifferences()
    {
        var fixture = new Fixture(20);
        fixture.Resolve = asset => new(asset, fixture.Assets.IndexOf(asset) % 2 == 0 ? Different : Existing);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 3);
        Assert.IsTrue((await fixture.Run(scanner)).Paused);
        Assert.AreEqual(5L, scanner.CheckedCount);
        Assert.IsTrue((await fixture.Run(scanner)).Paused);
        Assert.AreEqual(11L, scanner.CheckedCount);
        Assert.AreEqual(6L, scanner.DifferentCount);
        CollectionAssert.AreEqual(fixture.Assets.Take(11).Select(a => a.Id).ToArray(), fixture.Saved.ToArray());
    }

    [TestMethod]
    public async Task FinalDifference_CompletesInsteadOfPausing()
    {
        var fixture = new Fixture(3);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 3);
        Assert.IsFalse((await fixture.Run(scanner)).Paused);
        Assert.IsFalse(scanner.HasMore);
    }

    [TestMethod]
    public async Task Manual_StopsAtOnePage()
    {
        var fixture = new Fixture(205);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.ManualPages, 3);
        Assert.IsFalse((await fixture.Run(scanner)).Paused);
        Assert.AreEqual(100L, scanner.CheckedCount);
        Assert.IsTrue(scanner.HasMore);
    }

    [TestMethod]
    public async Task CancellationDuringNextLoad_ResumesWithoutLosingCheckpoint()
    {
        var fixture = new Fixture(205);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 0);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => scanner.RunAsync(
            (cursor, token) =>
            {
                if (cursor is not null)
                {
                    cts.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return fixture.Load(cursor, token);
            }, (asset, _) => Task.FromResult(fixture.Resolve(asset)), fixture.Save,
            () => Task.CompletedTask, cts.Token));
        Assert.AreEqual(100L, scanner.CheckedCount);
        Assert.AreEqual(0, scanner.Rows.Count);
        Assert.IsTrue(scanner.HasMore);
        await fixture.Run(scanner);
        CollectionAssert.AreEqual(fixture.Assets.Select(a => a.Id).ToArray(), fixture.Saved.ToArray());
    }

    [TestMethod]
    public async Task SaveFailure_DoesNotAdvanceCursorAndRetryIncludesFailedAsset()
    {
        var fixture = new Fixture(10);
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 0);
        await Assert.ThrowsAsync<IOException>(() => scanner.RunAsync(fixture.Load,
            (asset, _) => Task.FromResult(fixture.Resolve(asset)),
            row => fixture.Saved.Count == 3 ? throw new IOException("Save failed") : fixture.Save(row),
            () => Task.CompletedTask));
        Assert.AreEqual(3L, scanner.CheckedCount);
        Assert.AreEqual(fixture.Assets[2].Id, scanner.Cursor!.Id);
        await fixture.Run(scanner);
        CollectionAssert.AreEqual(fixture.Assets.Select(a => a.Id).ToArray(), fixture.Saved.ToArray());
    }

    [TestMethod]
    public async Task UnresolvedAndFailedResults_DoNotCountTowardPause()
    {
        var fixture = new Fixture(10);
        fixture.Resolve = asset => fixture.Assets.IndexOf(asset) switch
        {
            0 => new(asset, null),
            1 => new(asset, Different, "Failed"),
            2 => new(asset, Existing),
            _ => new(asset, Different)
        };
        var scanner = new LocationPreviewScanner(LocationPreviewScanMode.Continuous, 2);
        Assert.IsTrue((await fixture.Run(scanner)).Paused);
        Assert.AreEqual(5L, scanner.CheckedCount);
        Assert.AreEqual(2L, scanner.DifferentCount);
    }

    [TestMethod]
    public async Task BrowserDisconnect_CancelsPageOwnedWork()
    {
        var lifetime = new PreviewCircuitLifetime();
        using var cts = new CancellationTokenSource();
        lifetime.Disconnected += cts.Cancel;
        await lifetime.OnConnectionDownAsync(null!, CancellationToken.None);
        Assert.IsTrue(cts.IsCancellationRequested);
        lifetime.Disconnected -= cts.Cancel;
    }

    [TestMethod]
    public void SavedSelection_PreservesScanOptionsAndOldReportsDefaultToManual()
    {
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null) with
        {
            ScanMode = LocationPreviewScanMode.Continuous,
            PauseAfterDifferences = 75
        };
        var saved = System.Text.Json.JsonSerializer.Deserialize<LocationPreviewSelection>(
            System.Text.Json.JsonSerializer.Serialize(selection))!;
        Assert.AreEqual(LocationPreviewScanMode.Continuous, saved.ScanMode);
        Assert.AreEqual(75, saved.PauseAfterDifferences);
        var old = System.Text.Json.JsonSerializer.Deserialize<LocationPreviewSelection>(
            "{\"Scope\":4,\"AssetIds\":[],\"Value\":null}")!;
        Assert.AreEqual(LocationPreviewScanMode.ManualPages, old.ScanMode);
        Assert.AreEqual(0, old.PauseAfterDifferences);
    }

    [TestMethod]
    public void InvalidOptions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LocationPreviewScanner((LocationPreviewScanMode)99, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocationPreviewScanner(LocationPreviewScanMode.Continuous, -1));
    }

    private sealed class Fixture(int count)
    {
        public List<LocationPreviewAsset> Assets { get; } = Enumerable.Range(0, count)
            .Select(_ => new LocationPreviewAsset(Guid.NewGuid(), 48, 2, Existing)).ToList();
        public List<Guid> Saved { get; } = [];
        public int MaxLiveRows { get; private set; }
        public Func<LocationPreviewAsset, LocationPreviewRow> Resolve { get; set; } = asset => new(asset, Different);
        public Task<IReadOnlyList<LocationPreviewAsset>> Load(LocationPreviewCursor? cursor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = cursor is null ? 0 : Assets.FindIndex(a => a.Id == cursor.Id) + 1;
            return Task.FromResult<IReadOnlyList<LocationPreviewAsset>>(Assets.Skip(start).Take(101).ToArray());
        }
        public Task Save(LocationPreviewRow row)
        {
            Saved.Add(row.Asset.Id);
            return Task.CompletedTask;
        }
        public Task<LocationPreviewScanOutcome> Run(LocationPreviewScanner scanner) => scanner.RunAsync(
            Load, (asset, _) => Task.FromResult(Resolve(asset)), Save,
            () =>
            {
                MaxLiveRows = Math.Max(MaxLiveRows, scanner.Rows.Count);
                return Task.CompletedTask;
            });
    }
}

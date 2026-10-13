using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationManagementTests
{
    private string _directory = null!;
    private SavedLocationPreviewRepository _scans = null!;
    private LocationChangeRepository _journal = null!;
    private FakeWriter _writer = null!;
    private LocationManagementService _service = null!;

    [TestInitialize]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"location-management-{Guid.NewGuid():N}");
        _scans = new(_directory);
        _journal = new(_directory);
        _writer = new();
        _service = Service(_journal);
    }
    private LocationManagementService Service(LocationChangeRepository journal) => new(_scans, journal, _writer, NullLogger<LocationManagementService>.Instance);
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
    private async Task<(Guid Scan, LocationPreviewRow[] Rows)> ScanAsync(int count = 2)
    {
        var scan = await _scans.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var rows = Enumerable.Range(0, count).Select(index => new LocationPreviewRow(
            new(Guid.NewGuid(), 48.1, 2.1, new GeoResult(null, null, "Old " + index)),
            new GeoResult("France", "Region", "Exact reviewed " + index))).ToArray();
        foreach (var row in rows)
        {
            await _scans.AppendAsync(scan, row);
            _writer.Current[row.Asset.Id] = (row.Asset, "original");
        }
        await _scans.SetStatusAsync(scan, "Complete");
        return (scan, rows);
    }

    [TestMethod]
    public async Task Apply_UsesExactSelectedSnapshotsAndPersistsResultsAcrossRestart()
    {
        var (scan, rows) = await ScanAsync();
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id, rows[0].Asset.Id]);
        Assert.AreEqual(0, _writer.Calls);
        await _service.RunAsync(id, false);
        Assert.AreEqual(rows[0].Proposed, _writer.Current[rows[0].Asset.Id].Asset.Existing);
        Assert.AreEqual(rows[1].Asset.Existing, _writer.Current[rows[1].Asset.Id].Asset.Existing);
        var restarted = new LocationChangeRepository(_directory);
        var item = (await restarted.GetItemsAsync(id)).Single();
        Assert.AreEqual(rows[0], item.Snapshot);
        Assert.AreEqual(LocationChangeStatus.Applied, item.ApplyStatus);
        Assert.IsNotNull(item.AppliedRevision);
        Assert.IsTrue(item.CanUndo);
        await _service.RunAsync(id, false);
        Assert.AreEqual(1, _writer.Calls);
        Assert.IsTrue(await _scans.DeleteAsync(scan));
        Assert.AreEqual(rows[0], (await restarted.GetItemsAsync(id)).Single().Snapshot);
    }

    [TestMethod]
    public async Task Undo_RestoresNullsAndOriginalNamesExactlyAfterRestart()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(id, false);
        var restarted = new LocationChangeRepository(_directory);
        await restarted.QueueUndoAsync(id, [rows[0].Asset.Id]);
        await Service(restarted).RunAsync(id, true);
        Assert.AreEqual(rows[0].Asset, _writer.Current[rows[0].Asset.Id].Asset);
        Assert.AreEqual(LocationUndoStatus.Undone, (await restarted.GetItemsAsync(id)).Single().UndoStatus);
        Assert.IsFalse((await restarted.GetItemsAsync(id)).Single().CanUndo);
        await Service(restarted).RunAsync(id, true);
        Assert.AreEqual(2, _writer.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.QueueUndoAsync(id, [rows[0].Asset.Id]));
    }

    [TestMethod]
    public async Task Apply_ChangedCoordinatesAndNamesAreSkipped()
    {
        var (scan, rows) = await ScanAsync();
        _writer.Current[rows[0].Asset.Id] = (rows[0].Asset with
        {
            Latitude = 49
        }, "new-gps");
        _writer.Current[rows[1].Asset.Id] = (rows[1].Asset with
        {
            Existing = new("Other country", null, "Edited")
        }, "new-name");
        var id = await _service.CreateApplyAsync(scan, rows.Select(row => row.Asset.Id).ToArray());
        await _service.RunAsync(id, false);
        Assert.IsTrue((await _journal.GetItemsAsync(id)).All(item => item.ApplyStatus == LocationChangeStatus.Skipped));
        Assert.AreEqual(49d, _writer.Current[rows[0].Asset.Id].Asset.Latitude);
        Assert.AreEqual("Edited", _writer.Current[rows[1].Asset.Id].Asset.Existing.City);
    }

    [TestMethod]
    public async Task Undo_RevisionGuardRejectsLaterEditEvenWhenNamesAreIdentical()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(id, false);
        var after = _writer.Current[rows[0].Asset.Id];
        _writer.Current[rows[0].Asset.Id] = (after.Asset, "edited-later");
        await _journal.QueueUndoAsync(id, [rows[0].Asset.Id]);
        await _service.RunAsync(id, true);
        Assert.AreEqual(LocationUndoStatus.Skipped, (await _journal.GetItemsAsync(id)).Single().UndoStatus);
        Assert.AreEqual(rows[0].Proposed, _writer.Current[rows[0].Asset.Id].Asset.Existing);
    }

    [TestMethod]
    public async Task InterruptedAfterCommit_IsUncertainAndNeverRetriedOrUndoneAutomatically()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        _writer.FailAfterWrite = true;
        await _service.RunAsync(id, false);
        var item = (await _journal.GetItemsAsync(id)).Single();
        Assert.AreEqual(LocationChangeStatus.NeedsReview, item.ApplyStatus);
        Assert.AreEqual(rows[0].Proposed, _writer.Current[rows[0].Asset.Id].Asset.Existing);
        Assert.IsFalse(item.CanUndo);
        _writer.FailAfterWrite = false;
        await _service.RunAsync(id, false);
        Assert.AreEqual(1, _writer.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => _journal.QueueUndoAsync(id, [item.AssetId]));
    }

    [TestMethod]
    public async Task InterruptedJournalClaim_IsRecoveredOnRestartWithoutWriting()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        Assert.IsTrue(await _journal.ClaimAsync(id, rows[0].Asset.Id, false));
        var restarted = new LocationChangeRepository(_directory);
        await Service(restarted).RunAsync(id, false);
        Assert.AreEqual(0, _writer.Calls);
        Assert.AreEqual(LocationChangeStatus.NeedsReview, (await restarted.GetItemsAsync(id)).Single().ApplyStatus);
    }

    [TestMethod]
    public async Task CancelBetweenWrites_LeavesPendingRowsResumableWithoutDuplicates()
    {
        var (scan, rows) = await ScanAsync(3);
        var id = await _service.CreateApplyAsync(scan, rows.Select(row => row.Asset.Id).ToArray());
        using var cts = new CancellationTokenSource();
        await _service.RunAsync(id, false, () => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);
        Assert.AreEqual(1, _writer.Calls);
        var summary = (await _journal.GetOperationsAsync(id: id)).Single();
        Assert.AreEqual(1, summary.Applied);
        Assert.AreEqual(2, summary.Pending);
        await _service.RunAsync(id, false);
        Assert.AreEqual(3, _writer.Calls);
        Assert.AreEqual(3, (await _journal.GetOperationsAsync(id: id)).Single().Applied);
    }

    [TestMethod]
    public async Task UnresolvableMissingAndWrongScanSelectionsRejectWholeOperation()
    {
        var (scan, rows) = await ScanAsync(1);
        var failed = new LocationPreviewRow(new(Guid.NewGuid(), 48, 2, rows[0].Asset.Existing), null, "Lookup failed");
        await _scans.AppendAsync(scan, failed);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(scan, [rows[0].Asset.Id, failed.Asset.Id]));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(scan, [rows[0].Asset.Id, Guid.NewGuid()]));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(Guid.NewGuid(), [rows[0].Asset.Id]));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(scan, []));
        Assert.AreEqual(0, (await _journal.GetOperationsAsync()).Count);
        Assert.AreEqual(0, _writer.Calls);
    }

    [TestMethod]
    public async Task HandledSelection_RequiresFreshScanAndRetainsOriginalOutcome()
    {
        var (scan, rows) = await ScanAsync(1);
        var first = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(first, false);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(scan, [rows[0].Asset.Id]));
        await Assert.ThrowsAsync<ArgumentException>(() => _journal.CreateAsync(scan, rows));
        var results = await _journal.GetScanResultsAsync(scan, [rows[0].Asset.Id]);
        Assert.AreEqual(first, results[rows[0].Asset.Id].OperationId);
        Assert.AreEqual(LocationChangeStatus.Applied, results[rows[0].Asset.Id].ApplyStatus);
        Assert.AreEqual(0, (await _journal.GetScanResultsAsync(Guid.NewGuid(), [rows[0].Asset.Id])).Count);
        var freshScan = await _scans.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.AssetIds, rows[0].Asset.Id.ToString()), new());
        var freshRow = new LocationPreviewRow(_writer.Current[rows[0].Asset.Id].Asset, new GeoResult("France", "Region", "Newly reviewed city"));
        await _scans.AppendAsync(freshScan, freshRow);
        var freshOperation = await _service.CreateApplyAsync(freshScan, [freshRow.Asset.Id]);
        await _service.RunAsync(freshOperation, false);
        Assert.AreEqual(freshRow.Proposed, _writer.Current[freshRow.Asset.Id].Asset.Existing);
    }

    [TestMethod]
    public async Task UndoInterruptedAfterCommit_RemainsUncertainAndIsNotRepeated()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(id, false);
        await _journal.QueueUndoAsync(id, [rows[0].Asset.Id]);
        _writer.FailAfterWrite = true;
        await _service.RunAsync(id, true);
        var item = (await _journal.GetItemsAsync(id)).Single();
        Assert.AreEqual(LocationUndoStatus.NeedsReview, item.UndoStatus);
        Assert.AreEqual(rows[0].Asset, _writer.Current[rows[0].Asset.Id].Asset);
        Assert.IsFalse(item.CanUndo);
        _writer.FailAfterWrite = false;
        await _service.RunAsync(id, true);
        Assert.AreEqual(2, _writer.Calls);
    }

    [TestMethod]
    public async Task UndoInterruptedClaim_IsRecoveredWithoutAttemptingAnotherWrite()
    {
        var (scan, rows) = await ScanAsync(1);
        var id = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(id, false);
        await _journal.QueueUndoAsync(id, [rows[0].Asset.Id]);
        Assert.IsTrue(await _journal.ClaimAsync(id, rows[0].Asset.Id, true));
        var restarted = new LocationChangeRepository(_directory);
        await Service(restarted).RunAsync(id, true);
        Assert.AreEqual(1, _writer.Calls);
        Assert.AreEqual(LocationUndoStatus.NeedsReview, (await restarted.GetItemsAsync(id)).Single().UndoStatus);
    }

    [TestMethod]
    public async Task ConcurrentOperation_IsLeftPendingUntilExecutionLockIsAvailable()
    {
        var (scan, rows) = await ScanAsync();
        var first = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        var second = await _service.CreateApplyAsync(scan, [rows[1].Asset.Id]);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _service.RunAsync(first, false, () => { reached.SetResult(); return release.Task; });
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RunAsync(second, false));
            Assert.AreEqual(1, (await _journal.GetOperationsAsync(id: second)).Single().Pending);
        }
        finally
        {
            release.TrySetResult();
            await running;
        }
        await _service.RunAsync(second, false);
        Assert.AreEqual(LocationChangeStatus.Applied, (await _journal.GetItemsAsync(second)).Single().ApplyStatus);
        Assert.AreEqual(2, _writer.Calls);
    }


    [TestMethod]
    public async Task UndoQueue_RejectsEntireSelectionWhenAnyItemIsNoLongerEligible()
    {
        var (scan, rows) = await ScanAsync();
        var id = await _service.CreateApplyAsync(scan, rows.Select(row => row.Asset.Id).ToArray());
        await _service.RunAsync(id, false);
        await _journal.QueueUndoAsync(id, [rows[1].Asset.Id]);
        await Assert.ThrowsAsync<ArgumentException>(() => _journal.QueueUndoAsync(id,
            [rows[0].Asset.Id, rows[1].Asset.Id]));
        var items = (await _journal.GetItemsAsync(id)).ToDictionary(item => item.AssetId);
        Assert.AreEqual(LocationUndoStatus.NotRequested, items[rows[0].Asset.Id].UndoStatus);
        Assert.AreEqual(LocationUndoStatus.Pending, items[rows[1].Asset.Id].UndoStatus);
    }

    [TestMethod]
    public async Task UndoQueue_StorageFailureRollsBackEntireSelection()
    {
        var (scan, rows) = await ScanAsync();
        var id = await _service.CreateApplyAsync(scan, rows.Select(row => row.Asset.Id).ToArray());
        await _service.RunAsync(id, false);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "location-changes.db"), Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Fail the second row after the first update succeeded inside the transaction.
            command.CommandText = $"""
                CREATE TRIGGER fail_undo BEFORE UPDATE OF undo_status ON changes
                WHEN NEW.asset_id='{rows[1].Asset.Id}' AND NEW.undo_status='Pending'
                BEGIN SELECT RAISE(ABORT, 'simulated storage failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => _journal.QueueUndoAsync(id,
            [rows[0].Asset.Id, rows[1].Asset.Id]));
        Assert.IsTrue((await _journal.GetItemsAsync(id)).All(item => item.UndoStatus == LocationUndoStatus.NotRequested));
        Assert.AreEqual(2, _writer.Calls);
    }

    [TestMethod]
    public async Task ReadyResults_AdvanceAfterEachHundredAndSurviveReload()
    {
        var (scan, rows) = await ScanAsync(201);
        var results = new LocationReviewResults(_scans, _journal);
        var page = new LocationPreviewDifferencePage();
        Task<IReadOnlyList<LocationPreviewRow>> Load(int number) => results.GetRowsAsync(scan, number, LocationReviewFilter.Ready);
        await page.RefreshAsync(201, Load);
        Assert.AreEqual(100, page.Rows.Count());
        Assert.IsTrue(page.HasNext);
        var first = await _service.CreateApplyAsync(scan, page.Rows.Select(row => row.Asset.Id).ToArray());
        await _service.RunAsync(first, false);
        await page.RefreshAsync(201, Load, force: true);
        CollectionAssert.AreEqual(rows.Skip(100).Take(100).Select(row => row.Asset.Id).ToArray(), page.Rows.Select(row => row.Asset.Id).ToArray());
        var second = await _service.CreateApplyAsync(scan, page.Rows.Select(row => row.Asset.Id).ToArray());
        await _service.RunAsync(second, false);
        await page.RefreshAsync(201, Load, force: true);
        Assert.AreEqual(rows[200].Asset.Id, page.Rows.Single().Asset.Id);
        var restarted = new LocationReviewResults(new(_directory), new(_directory));
        Assert.AreEqual(rows[200].Asset.Id, (await restarted.GetRowsAsync(scan, 1, LocationReviewFilter.Ready)).Single().Asset.Id);
        Assert.AreEqual(101, (await restarted.GetRowsAsync(scan, 1, LocationReviewFilter.Applied)).Count);
        Assert.AreEqual(100, (await restarted.GetRowsAsync(scan, 2, LocationReviewFilter.Applied)).Count);
        Assert.AreEqual(101, (await restarted.GetRowsAsync(scan, 1, LocationReviewFilter.All)).Count);
        Assert.AreEqual(0, (await restarted.GetRowsAsync(scan, 1, LocationReviewFilter.Attention)).Count);
        Assert.AreEqual(200, _writer.Calls);
        await page.MoveAsync(1, 201, Load);
        Assert.AreEqual(1, page.Page);
    }

    [TestMethod]
    public async Task AttentionResults_KeepSkippedPendingUncertainAndUndoneOutOfReady()
    {
        var (scan, rows) = await ScanAsync(4);
        var results = new LocationReviewResults(_scans, _journal);
        _writer.Current.Remove(rows[0].Asset.Id);
        var skipped = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(skipped, false);
        await _service.CreateApplyAsync(scan, [rows[1].Asset.Id]);
        var uncertain = await _service.CreateApplyAsync(scan, [rows[2].Asset.Id]);
        _writer.FailAfterWrite = true;
        await _service.RunAsync(uncertain, false);
        _writer.FailAfterWrite = false;
        var undone = await _service.CreateApplyAsync(scan, [rows[3].Asset.Id]);
        await _service.RunAsync(undone, false);
        await _journal.QueueUndoAsync(undone, [rows[3].Asset.Id]);
        await _service.RunAsync(undone, true);
        Assert.AreEqual(0, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.Ready)).Count);
        Assert.AreEqual(0, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.Applied)).Count);
        Assert.AreEqual(3, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.Attention)).Count);
        Assert.AreEqual(4, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.All)).Count);
        var failure = new LocationPreviewRow(new(Guid.NewGuid(), 48, 2, new(null,null,null)), null, "Lookup failed");
        await _scans.AppendAsync(scan, failure);
        Assert.AreEqual(4, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.Attention)).Count);
        var unresolved = new LocationPreviewRow(new(Guid.NewGuid(), 48, 2, new(null,null,null)), new GeoResult(null,null,null));
        await _scans.AppendAsync(scan, unresolved);
        Assert.AreEqual(5, (await results.GetRowsAsync(scan, 1, LocationReviewFilter.Attention)).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateApplyAsync(scan, [rows[3].Asset.Id]));
    }

    [TestMethod]
    public async Task FilteredPage_ReturnsToEarlierPageWhenLastPageIsHandled()
    {
        var (scan, rows) = await ScanAsync(101);
        var results = new LocationReviewResults(_scans, _journal);
        var page = new LocationPreviewDifferencePage();
        Task<IReadOnlyList<LocationPreviewRow>> Load(int number) => results.GetRowsAsync(scan, number, LocationReviewFilter.Ready);
        await page.RefreshAsync(101, Load);
        await page.MoveAsync(1, 101, Load);
        Assert.AreEqual(2, page.Page);
        var operation = await _service.CreateApplyAsync(scan, [rows[100].Asset.Id]);
        await _service.RunAsync(operation, false);
        await page.RefreshAsync(101, Load, force: true);
        Assert.AreEqual(1, page.Page);
        Assert.AreEqual(100, page.Rows.Count());
        Assert.IsFalse(page.HasNext);
    }

    [TestMethod]
    public async Task DifferencesOnly_FilterRunsBeforePagingAndCombinesWithStatus()
    {
        var (scan, rows) = await ScanAsync(1);
        // Interleave more than one page of unchanged snapshots before the next difference.
        for (var index = 0; index < 101; index++)
        {
            var names = new GeoResult("France", "Region", "Unchanged city");
            await _scans.AppendAsync(scan, new(new(Guid.NewGuid(),48,2,names),names));
        }
        var later = new LocationPreviewRow(new(Guid.NewGuid(),48,2,new("France",null,"Before")),new("France",null,"After"));
        await _scans.AppendAsync(scan, later);
        var failed = new LocationPreviewRow(new(Guid.NewGuid(),48,2,new(null,null,null)),null,"Lookup failed");
        await _scans.AppendAsync(scan, failed);
        var results = new LocationReviewResults(_scans,_journal);
        var different = await results.GetRowsAsync(scan,1,LocationReviewFilter.All,onlyChanges:true);
        CollectionAssert.AreEqual(new[] { rows[0].Asset.Id,later.Asset.Id },different.Select(row=>row.Asset.Id).ToArray());
        Assert.AreEqual(101,(await results.GetRowsAsync(scan,1,LocationReviewFilter.All,onlyChanges:false)).Count);
        Assert.AreEqual(0,(await results.GetRowsAsync(scan,1,LocationReviewFilter.Attention,onlyChanges:true)).Count);
        Assert.AreEqual(failed.Asset.Id,(await results.GetRowsAsync(scan,1,LocationReviewFilter.Attention,onlyChanges:false)).Single().Asset.Id);
        var operation = await _service.CreateApplyAsync(scan,[rows[0].Asset.Id]);
        await _service.RunAsync(operation,false);
        Assert.AreEqual(rows[0].Asset.Id,(await results.GetRowsAsync(scan,1,LocationReviewFilter.Applied,onlyChanges:true)).Single().Asset.Id);
        Assert.AreEqual(later.Asset.Id,(await results.GetRowsAsync(scan,1,LocationReviewFilter.Ready,onlyChanges:true)).Single().Asset.Id);
    }

    private sealed class FakeWriter : ILocationChangeWriter
    {
        public Dictionary<Guid, (LocationPreviewAsset Asset, string Revision)> Current { get; } = [];
        public int Calls
        {
            get; private set;
        }
        public bool FailAfterWrite
        {
            get; set;
        }
        public Task<LocationWriteResult> TryReplaceLocationAsync(LocationPreviewAsset expected,
            GeoResult replacement, string? expectedRevision = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            if (!Current.TryGetValue(expected.Id, out var current) || current.Asset != expected
                || (expectedRevision is not null && expectedRevision != current.Revision))
            {
                return Task.FromResult(LocationWriteResult.Skip("Stale snapshot."));
            }
            var revision = "revision-" + Calls;
            Current[expected.Id] = (expected with
            {
                Existing = replacement
            }, revision);
            if (FailAfterWrite)
            {
                throw new IOException("Connection lost after commit.");
            }
            return Task.FromResult(new LocationWriteResult(true, revision, null));
        }
    }
}

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
    public async Task Results_AreLinkedToTheirScanAndLatestOperation()
    {
        var (scan, rows) = await ScanAsync(1);
        var first = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(first, false);
        var second = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        await _service.RunAsync(second, false);
        var results = await _journal.GetScanResultsAsync(scan, [rows[0].Asset.Id]);
        Assert.AreEqual(second, results[rows[0].Asset.Id].OperationId);
        Assert.AreEqual(LocationChangeStatus.Skipped, results[rows[0].Asset.Id].ApplyStatus);
        Assert.AreEqual(0, (await _journal.GetScanResultsAsync(Guid.NewGuid(), [rows[0].Asset.Id])).Count);
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
        var (scan, rows) = await ScanAsync(1);
        var first = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
        var second = await _service.CreateApplyAsync(scan, [rows[0].Asset.Id]);
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
        Assert.AreEqual(LocationChangeStatus.Skipped, (await _journal.GetItemsAsync(second)).Single().ApplyStatus);
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

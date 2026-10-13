using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class SavedScanResumeTests
{
    private string _directory = null!;
    [TestInitialize]
    public void Setup() => _directory = Path.Combine(Path.GetTempPath(), "scan-resume-" + Guid.NewGuid().ToString("N"));
    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
    private static LocationPreviewRow Row(int index) => new(
        new(Guid.Parse($"00000000-0000-0000-0000-{index:000000000000}"), 48.1, 2.1, new("France", null, "Old")), new("France", null, "New"));

    [TestMethod]
    public async Task PausedScan_RestoresOriginalSettingsCursorCountsAndProgressAfterRestart()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null) with
        {
            Order = LocationPreviewOrder.NearbyCoordinates,
            ScanMode = LocationPreviewScanMode.Continuous,
            PauseAfterDifferences = 2
        };
        var owner = Guid.NewGuid();
        var id = await repository.CreateAsync(selection, new ProcessingConfig { UseGadmAdministrativeAreas = true }, runToken: owner);
        var scanner = new LocationPreviewScanner(selection.ScanMode, selection.PauseAfterDifferences);
        var rows = Enumerable.Range(1, 5).Select(Row).ToArray();
        Task<IReadOnlyList<LocationPreviewAsset>> Load(LocationPreviewCursor? cursor, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LocationPreviewAsset>>(rows.Where(row => cursor is null || row.Asset.Id.CompareTo(cursor.Id) > 0).Select(row => row.Asset).ToArray());
        var checkpoint = new LocationPreviewCheckpoint(5, TimeSpan.FromSeconds(4).Ticks, 1, null);
        var outcome = await scanner.RunAsync(Load, (asset, _) => Task.FromResult(rows.Single(row => row.Asset.Id == asset.Id)),
            row => repository.AppendAsync(id, row, runToken: owner, checkpoint: checkpoint), () => Task.CompletedTask);
        Assert.IsTrue(outcome.Paused);
        await repository.FinishRunAsync(id, owner, "Paused", null, checkpoint with { PausedDifferences = outcome.NewDifferences });
        var restarted = new SavedLocationPreviewRepository(_directory);
        var saved = await restarted.GetResumeStateAsync(id);
        Assert.IsTrue(saved.Report.CanResume);
        Assert.AreEqual("Paused for review", saved.Report.StatusLabel);
        Assert.IsTrue(saved.Report.Settings.UseGadmAdministrativeAreas);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(selection), System.Text.Json.JsonSerializer.Serialize(saved.Report.Selection));
        Assert.AreEqual(LocationPreviewCursor.FromAsset(rows[1].Asset), saved.Cursor);
        Assert.AreEqual(2L, saved.Report.CheckedCount);
        Assert.AreEqual(2L, saved.Report.DifferentCount);
        Assert.AreEqual(2L, saved.Report.Checkpoint!.PausedDifferences);
        var estimate = new LocationPreviewScanEstimate(saved.Report.Checkpoint.TotalCount!.Value, saved.Report.CheckedCount, TimeSpan.FromTicks(saved.Report.Checkpoint.ActiveTicks));
        Assert.AreEqual(40d, estimate.Percentage);
        Assert.AreEqual(TimeSpan.FromSeconds(4), estimate.ActiveElapsed);
        var nextOwner = Guid.NewGuid();
        var claimed = await restarted.ClaimResumeAsync(id, nextOwner);
        scanner = LocationPreviewScanner.FromSaved(claimed);
        outcome = await scanner.RunAsync((cursor, ct) => restarted.LoadUnseenBatchAsync(id, cursor, Load, ct),
            (asset, _) => Task.FromResult(rows.Single(row => row.Asset.Id == asset.Id)),
            row => restarted.AppendAsync(id, row, runToken: nextOwner, checkpoint: checkpoint), () => Task.CompletedTask);
        Assert.IsTrue(outcome.Paused);
        Assert.AreEqual(2L, outcome.NewDifferences);
        Assert.AreEqual(4L, scanner.CheckedCount);
        Assert.AreEqual(rows[3].Asset.Id, scanner.Cursor!.Id);
        await restarted.FinishRunAsync(id, nextOwner, "Paused", null, checkpoint with { BatchNumber = 2, PausedDifferences = 2 });
        CollectionAssert.AreEqual(rows.Take(4).ToArray(), (await restarted.GetRowsAsync(id)).ToArray());
    }

    [TestMethod]
    public async Task Ownership_BlocksCompetingResumeAndRejectsOldWriterAfterRestart()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var firstOwner = Guid.NewGuid();
        var id = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new(), runToken: firstOwner);
        var checkpoint = new LocationPreviewCheckpoint(3, 1234, 1, null);
        await repository.AppendAsync(id, Row(1), runToken: firstOwner, checkpoint: checkpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ClaimResumeAsync(id, Guid.NewGuid()));
        Assert.IsFalse(await repository.DeleteAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SetStatusAsync(id, "Complete"));
        var restarted = new SavedLocationPreviewRepository(_directory);
        var saved = await restarted.GetResumeStateAsync(id);
        Assert.AreEqual("Interrupted", saved.Report.Status);
        Assert.AreEqual(checkpoint, saved.Report.Checkpoint);
        var secondOwner = Guid.NewGuid();
        await restarted.ClaimResumeAsync(id, secondOwner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AppendAsync(id, Row(2), runToken: firstOwner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.FinishRunAsync(id, firstOwner, "Complete", null, checkpoint));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ClaimResumeAsync(id, Guid.NewGuid()));
        await restarted.AppendAsync(id, Row(2), runToken: secondOwner, checkpoint: checkpoint);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.AppendAsync(id, Row(2), runToken: secondOwner));
        await restarted.FinishRunAsync(id, secondOwner, "Complete", null, checkpoint);
        Assert.IsFalse((await restarted.GetResumeStateAsync(id)).Report.CanResume);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ClaimResumeAsync(id, Guid.NewGuid()));
        Assert.AreEqual(2L, (await restarted.GetResumeStateAsync(id)).Report.CheckedCount);
    }

    [TestMethod]
    public async Task UnseenBatch_FillsLookaheadPastSavedAssetsThatMovedInCoordinateOrder()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var id = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var rows = Enumerable.Range(1, 205).Select(Row).ToArray();
        foreach (var row in rows.Take(104))
        {
            await repository.AppendAsync(id, row);
        }
        await repository.SetStatusAsync(id, "Cancelled");
        // Previously checked assets can move after the saved tile cursor. The loader must
        // skip an entire raw batch without incorrectly reporting the scan complete.
        var calls = 0;
        Task<IReadOnlyList<LocationPreviewAsset>> Load(LocationPreviewCursor? cursor, CancellationToken ct)
        {
            calls++;
            return Task.FromResult<IReadOnlyList<LocationPreviewAsset>>(rows.Where(row => cursor is null || cursor.LatitudeTile < 196 || row.Asset.Id.CompareTo(cursor.Id) > 0).Take(101).Select(row => row.Asset with { Latitude = 49 }).ToArray());
        }
        var remaining = await repository.LoadUnseenBatchAsync(id, LocationPreviewCursor.FromAsset(rows[103].Asset), Load, CancellationToken.None);
        Assert.AreEqual(101, remaining.Count);
        Assert.AreEqual(3, calls);
        CollectionAssert.AreEqual(rows.Skip(104).Select(row => row.Asset.Id).ToArray(), remaining.Select(row => row.Id).ToArray());
    }

    [TestMethod]
    public async Task LegacyDatabase_MigratesAndRecoversLastSavedRowWithoutCheckpoint()
    {
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "location-previews.db")};Pooling=false"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE previews(id TEXT PRIMARY KEY,created_at TEXT,updated_at TEXT,selection_json TEXT,settings_json TEXT,status TEXT,error TEXT);
                CREATE TABLE preview_rows(row_id INTEGER PRIMARY KEY AUTOINCREMENT,preview_id TEXT,asset_id TEXT,row_json TEXT,has_change INTEGER,UNIQUE(preview_id,asset_id));
                INSERT INTO previews VALUES ($id,$at,$at,$selection,$settings,'Running',NULL);
                INSERT INTO preview_rows(preview_id,asset_id,row_json,has_change) VALUES ($id,$asset,$row,1);
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$selection", System.Text.Json.JsonSerializer.Serialize(LocationPreviewSelection.Create(LocationPreviewScope.All, null)));
            command.Parameters.AddWithValue("$settings", "{}");
            command.Parameters.AddWithValue("$asset", Row(1).Asset.Id.ToString());
            command.Parameters.AddWithValue("$row", System.Text.Json.JsonSerializer.Serialize(Row(1)));
            await command.ExecuteNonQueryAsync();
        }
        var repository = new SavedLocationPreviewRepository(_directory);
        var saved = await repository.GetResumeStateAsync(id);
        Assert.AreEqual("Interrupted", saved.Report.Status);
        Assert.IsNull(saved.Report.Checkpoint);
        Assert.AreEqual(LocationPreviewCursor.FromAsset(Row(1).Asset), saved.Cursor);
        var owner = Guid.NewGuid();
        await repository.ClaimResumeAsync(id, owner);
        await repository.AppendAsync(id, Row(2), runToken: owner, checkpoint: new(3, 0, 1, null));
        Assert.AreEqual(2L, (await repository.GetResumeStateAsync(id)).Report.CheckedCount);
    }

    [TestMethod]
    public async Task SaveFailure_DoesNotAdvanceCursorAndCancelledEmptyScanCanResume()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var owner = Guid.NewGuid();
        var id = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new(), runToken: owner);
        var checkpoint = new LocationPreviewCheckpoint(3, 0, 1, null);
        await repository.AppendAsync(id, Row(1), runToken: owner, checkpoint: checkpoint);
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "location-previews.db")};Pooling=false"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_save BEFORE INSERT ON preview_rows BEGIN SELECT RAISE(ABORT,'simulated save failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => repository.AppendAsync(id, Row(2), runToken: owner, checkpoint: checkpoint with { ActiveTicks = 99 }));
        await repository.FinishRunAsync(id, owner, "Failed", "Could not save", checkpoint);
        var saved = await repository.GetResumeStateAsync(id);
        Assert.AreEqual(Row(1).Asset.Id, saved.Cursor!.Id);
        Assert.AreEqual(1L, saved.Report.CheckedCount);
        var emptyOwner = Guid.NewGuid();
        var empty = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new(), runToken: emptyOwner);
        await repository.FinishRunAsync(empty, emptyOwner, "Cancelled", null, new(null, 0, 0, null));
        var emptyState = await repository.ClaimResumeAsync(empty, Guid.NewGuid());
        Assert.IsNull(emptyState.Cursor);
        Assert.AreEqual(0L, emptyState.Report.CheckedCount);
    }
}

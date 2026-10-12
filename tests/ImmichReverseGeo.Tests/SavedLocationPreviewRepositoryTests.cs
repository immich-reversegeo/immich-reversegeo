using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class SavedLocationPreviewRepositoryTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Setup() => _directory = Path.Combine(Path.GetTempPath(), $"saved-previews-{Guid.NewGuid():N}");

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task CompletedReport_RetainsSettingsNamesCoordinatesAndErrorsAcrossRestart()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var settings = new ProcessingConfig { UseGadmAdministrativeAreas = true };
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.Country, "France") with { Order = LocationPreviewOrder.NearbyCoordinates };
        var id = await repository.CreateAsync(selection, settings);
        var changed = Row(changed: true);
        var failed = new LocationPreviewRow(Row(false).Asset, null, "Lookup failed");
        await repository.AppendAsync(id, changed);
        await repository.AppendAsync(id, failed);
        await repository.SetStatusAsync(id, "Complete");
        settings.UseGadmAdministrativeAreas = false;

        var reopened = new SavedLocationPreviewRepository(_directory);
        var report = (await reopened.GetReportsAsync(id: id)).Single();
        Assert.AreEqual("Complete", report.Status);
        Assert.AreEqual(selection.Scope, report.Selection.Scope);
        Assert.AreEqual("France", report.Selection.Value);
        Assert.AreEqual(LocationPreviewOrder.NearbyCoordinates, report.Selection.Order);
        Assert.IsTrue(report.Settings.UseGadmAdministrativeAreas);
        Assert.AreEqual(2L, report.CheckedCount);
        Assert.AreEqual(1L, report.DifferentCount);
        var rows = await reopened.GetRowsAsync(id);
        Assert.AreEqual(changed, rows[0]);
        Assert.AreEqual(failed, rows[1]);
        Assert.AreEqual(changed, (await reopened.GetRowsAsync(id, onlyChanges: true)).Single());
    }

    [TestMethod]
    public async Task PagesAndChanges_AreBoundedAndDoNotDuplicateAssets()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var id = await repository.CreateAsync(LocationPreviewSelection.Create(LocationPreviewScope.All, null), new());
        var rows = Enumerable.Range(0, 205).Select(index => Row(index % 2 == 0)).ToArray();
        foreach (var row in rows)
        {
            await repository.AppendAsync(id, row);
        }
        await repository.AppendAsync(id, rows[0]);
        var first = await repository.GetRowsAsync(id);
        var second = await repository.GetRowsAsync(id, 2);
        var third = await repository.GetRowsAsync(id, 3);
        Assert.HasCount(101, first);
        Assert.HasCount(101, second);
        Assert.HasCount(5, third);
        CollectionAssert.AreEqual(rows, first.Take(100).Concat(second.Take(100)).Concat(third).ToArray());
        var changesFirst = await repository.GetRowsAsync(id, onlyChanges: true);
        var changesSecond = await repository.GetRowsAsync(id, 2, onlyChanges: true);
        Assert.HasCount(101, changesFirst);
        Assert.HasCount(3, changesSecond);
        CollectionAssert.AreEqual(rows.Where(row => row.HasChange).ToArray(), changesFirst.Take(100).Concat(changesSecond).ToArray());
        Assert.AreEqual(205L, (await repository.GetReportsAsync(id: id)).Single().CheckedCount);
    }

    [TestMethod]
    public async Task InterruptedAndCancelledReports_KeepCompletedRows()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null);
        var interrupted = await repository.CreateAsync(selection, new());
        await repository.AppendAsync(interrupted, Row(true));
        var cancelled = await repository.CreateAsync(selection, new());
        await repository.AppendAsync(cancelled, Row(false));
        await repository.SetStatusAsync(cancelled, "Cancelled");

        var reopened = new SavedLocationPreviewRepository(_directory);
        Assert.AreEqual("Interrupted", (await reopened.GetReportsAsync(id: interrupted)).Single().Status);
        Assert.AreEqual("Cancelled", (await reopened.GetReportsAsync(id: cancelled)).Single().Status);
        Assert.HasCount(1, await reopened.GetRowsAsync(interrupted));
        Assert.HasCount(1, await reopened.GetRowsAsync(cancelled));
    }

    [TestMethod]
    public async Task Delete_RemovesOnlyInactiveReportAndItsRows()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null);
        var first = await repository.CreateAsync(selection, new());
        var second = await repository.CreateAsync(selection, new());
        await repository.AppendAsync(first, Row(true));
        await repository.AppendAsync(second, Row(true));
        Assert.IsFalse(await repository.DeleteAsync(first));
        await repository.SetStatusAsync(first, "Cancelled");
        Assert.IsTrue(await repository.DeleteAsync(first));
        Assert.HasCount(0, await repository.GetReportsAsync(id: first));
        Assert.HasCount(0, await repository.GetRowsAsync(first));
        Assert.HasCount(1, await repository.GetRowsAsync(second));
    }

    [TestMethod]
    public async Task ReportList_PaginatesWithoutMixingResults()
    {
        var repository = new SavedLocationPreviewRepository(_directory);
        var selection = LocationPreviewSelection.Create(LocationPreviewScope.All, null);
        for (var index = 0; index < 30; index++)
        {
            var id = await repository.CreateAsync(selection, new());
            await repository.AppendAsync(id, Row(index % 2 == 0));
            await repository.SetStatusAsync(id, "Complete");
        }
        var first = await repository.GetReportsAsync();
        var second = await repository.GetReportsAsync(2);
        Assert.HasCount(26, first);
        Assert.HasCount(5, second);
        Assert.HasCount(30, first.Take(25).Concat(second).Select(report => report.Id).Distinct().ToArray());
        Assert.IsTrue(first.Concat(second).All(report => report.CheckedCount == 1));
    }

    private static LocationPreviewRow Row(bool changed)
    {
        var existing = new GeoResult("France", "Region", "Old city");
        return new(new(Guid.NewGuid(), 48.1, 2.1, existing), changed ? existing with { City = "New city" } : existing);
    }
}

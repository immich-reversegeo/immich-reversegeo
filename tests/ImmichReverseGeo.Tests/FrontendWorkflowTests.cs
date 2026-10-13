using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class FrontendWorkflowTests
{
    [TestMethod]
    public async Task Maintenance_PreventsOverlappingWritesAndManualRuns_AndReleasesOnce()
    {
        var state = new ProcessingState();
        // Unused dependencies are deliberate: a rejected manual run must not touch them.
        using var processor = new ProcessingBackgroundService(NullLogger<ProcessingBackgroundService>.Instance,
            null!, null!, state, null!, null!);
        var first = processor.TryBeginMaintenance();
        Assert.IsNotNull(first);
        Assert.IsTrue(state.IsMaintaining);
        Assert.IsNull(processor.TryBeginMaintenance());
        var management = new LocationManagementService(null!, null!, null!, NullLogger<LocationManagementService>.Instance, processor);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => management.RunAsync(Guid.NewGuid(), false));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => management.RunAsync(Guid.NewGuid(), true));
        await processor.TriggerRunAsync();
        Assert.IsFalse(state.IsRunning);
        first.Dispose();
        first.Dispose();
        Assert.IsFalse(state.IsMaintaining);
        using var next = processor.TryBeginMaintenance();
        Assert.IsNotNull(next);
    }

    [TestMethod]
    public void ScheduleEditorValidation_DoesNotSilentlyClampInvalidInput()
    {
        var editor = new ScheduleEditorState { Mode = ScheduleEditorState.ModeHourly, Minute = 60 };
        Assert.IsNotNull(SettingsValidation.GetScheduleEditorError(editor));
        editor.Minute = 59;
        Assert.IsNull(SettingsValidation.GetScheduleEditorError(editor));
        editor.Mode = ScheduleEditorState.ModeDaily;
        editor.Time = "";
        Assert.IsNotNull(SettingsValidation.GetScheduleEditorError(editor));
        editor.Time = "99:00";
        Assert.IsNotNull(SettingsValidation.GetScheduleEditorError(editor));
        editor.Time = "02:00";
        Assert.IsNull(SettingsValidation.GetScheduleEditorError(editor));
    }

    [TestMethod]
    public void ResetHandoff_IsExplicitAndIsolatedBetweenBrowserSessions()
    {
        var first = new LocationPreviewSession();
        var second = new LocationPreviewSession();
        var id = Guid.NewGuid();
        first.Review.Select(new LocationPreviewRow(new LocationPreviewAsset(id, 48, 2,
            new GeoResult("France", "Region", "Old city")), new GeoResult("France", "Region", "New city")), true);
        Assert.HasCount(0, first.TakeResetIds());
        first.PrepareReset();
        Assert.HasCount(0, second.TakeResetIds());
        CollectionAssert.AreEqual(new[] { id }, first.TakeResetIds());
        Assert.HasCount(0, first.TakeResetIds());
        Assert.IsTrue(first.Review.IsSelected(id));
        first.RemoveResetAssets([id]);
        Assert.IsFalse(first.Review.IsSelected(id));
        Assert.IsTrue(first.ResetAssets.Contains(id));
    }

    [TestMethod]
    public void SettingsValidation_RejectsInvalidOrImpossibleSchedulesBeforeSaving()
    {
        var config = new AppConfig();
        var now = new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
        config.Schedule.Cron = "invalid cron";
        Assert.IsNotNull(SettingsValidation.GetError(config, now));
        config.Schedule.Cron = "0 2 31 2 *";
        Assert.IsNotNull(SettingsValidation.GetError(config, now));
        config.Schedule.Cron = "0 2 * * *";
        Assert.IsNull(SettingsValidation.GetError(config, now));
        Assert.AreEqual(new DateTime(2026, 10, 14, 2, 0, 0, DateTimeKind.Utc), SettingsValidation.NextRun(config.Schedule.Cron, now));
        config.Processing.BatchSize = 0;
        Assert.IsNotNull(SettingsValidation.GetError(config, now));
        config.Processing.BatchSize = 25;
        config.Processing.BatchDelayMs = -1;
        Assert.IsNotNull(SettingsValidation.GetError(config, now));
        config.Processing.BatchDelayMs = 0;
        config.Processing.MaxDegreeOfParallelism = 33;
        Assert.IsNotNull(SettingsValidation.GetError(config, now));
    }
}

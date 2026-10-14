using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ScheduleEditorStateTests
{
    [TestMethod]
    public void FromCron_DailyCron_ParsesDailyMode()
    {
        var state = ScheduleEditorState.FromCron("0 2 * * *");

        Assert.AreEqual(ScheduleEditorState.ModeDaily, state.Mode);
        Assert.AreEqual("02:00", state.Time);
        Assert.AreEqual("0 2 * * *", state.ToCron());
    }

    [TestMethod]
    public void FromCron_EveryMinutesCron_ParsesIntervalMode()
    {
        var state = ScheduleEditorState.FromCron("*/15 * * * *");

        Assert.AreEqual(ScheduleEditorState.ModeEveryMinutes, state.Mode);
        Assert.AreEqual(15, state.MinuteInterval);
        Assert.AreEqual("*/15 * * * *", state.ToCron());
    }

    [TestMethod]
    public void ToCron_WeeklyMode_BuildsWeeklyCron()
    {
        var state = new ScheduleEditorState
        {
            Mode = ScheduleEditorState.ModeWeekly,
            Time = "06:30",
            WeeklyDay = "FRI"
        };

        Assert.AreEqual("30 6 * * FRI", state.ToCron());
        Assert.AreEqual("Every Friday at 06:30", state.Describe());
    }

    // Invalid persisted schedules must not be silently converted to valid presets on save.
    [TestMethod]
    [DataRow("99 * * * *")]
    [DataRow("60 * * * *")]
    [DataRow("*/0 * * * *")]
    [DataRow("*/60 * * * *")]
    [DataRow("0 */0 * * *")]
    [DataRow("0 */24 * * *")]
    [DataRow("60 */6 * * *")]
    [DataRow("60 2 * * *")]
    [DataRow("0 24 * * *")]
    [DataRow("60 2 * * MON")]
    [DataRow("0 24 * * MON")]
    [DataRow("٠ * * * *")]
    [DataRow("*/١ * * * *")]
    [DataRow("٠ ٢ * * *")]
    public void InvalidPresetFields_RemainCustom(string cron)
    {
        Assert.AreEqual(ScheduleEditorState.ModeCustom, ScheduleEditorState.FromCron(cron).Mode);
    }

    [TestMethod]
    [DataRow("59 * * * *")]
    [DataRow("*/59 * * * *")]
    [DataRow("59 */23 * * *")]
    [DataRow("59 23 * * *")]
    [DataRow("59 23 * * SUN")]
    public void ValidPresetBoundaries_RoundTrip(string cron)
    {
        var editor = ScheduleEditorState.FromCron(cron);
        Assert.AreNotEqual(ScheduleEditorState.ModeCustom, editor.Mode);
        Assert.AreEqual(cron, editor.ToCron());
    }

}

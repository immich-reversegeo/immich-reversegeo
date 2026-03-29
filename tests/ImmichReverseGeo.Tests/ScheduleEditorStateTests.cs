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
}

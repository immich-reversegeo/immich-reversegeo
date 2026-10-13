using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingStateTests
{
    [TestMethod]
    public void CheckedProgress_IncludesUpdatedSkippedFailedAndPreviouslySkippedAssets()
    {
        var state = new ProcessingState();
        state.StartRun(4);
        state.IncrementProcessed();
        state.RecordChecked();
        state.IncrementSkipped();
        state.RecordChecked();
        state.IncrementError("Asset write failed");
        state.RecordChecked();
        state.RecordChecked(previouslySkipped: true);
        state.CompleteRun();

        Assert.AreEqual(4L, state.CheckedThisRun);
        Assert.AreEqual(1L, state.PreviouslySkippedThisRun);
        Assert.AreEqual(1L, state.ProcessedThisRun);
        Assert.AreEqual(ProcessingRunOutcome.CompletedWithErrors, state.LastOutcome);
        Assert.IsFalse(state.IsRunning);
    }

    [TestMethod]
    public void Outcome_PreservesCancellationAndFatalFailure()
    {
        var state = new ProcessingState();
        state.StartRun(100);
        state.IncrementProcessed();
        state.RecordChecked();
        state.CompleteRun(ProcessingRunOutcome.Cancelled);
        Assert.AreEqual(ProcessingRunOutcome.Cancelled, state.LastOutcome);
        Assert.AreEqual(1L, state.ProcessedThisRun);

        state.MarkPending();
        Assert.AreEqual(0L, state.CheckedThisRun);
        Assert.AreEqual(0L, state.ProcessedThisRun);
        Assert.IsNull(state.LastOutcome);
        state.IncrementError("Database unavailable before the run could start");
        state.CompleteRun(ProcessingRunOutcome.Failed);
        Assert.AreEqual(ProcessingRunOutcome.Failed, state.LastOutcome);
    }

    [TestMethod]
    public void WarningsFilter_IncludesErrorsButExcludesInformation()
    {
        Assert.IsTrue(new ProcessingLogEntry(DateTimeOffset.UtcNow, "[WARN] skipped").MatchesFilter("[WARN]"));
        Assert.IsTrue(new ProcessingLogEntry(DateTimeOffset.UtcNow, "[ERROR] failed").MatchesFilter("[WARN]"));
        Assert.IsFalse(new ProcessingLogEntry(DateTimeOffset.UtcNow, "Run started").MatchesFilter("[WARN]"));
        Assert.IsFalse(new ProcessingLogEntry(DateTimeOffset.UtcNow, "[WARN] skipped").MatchesFilter("[ERROR]"));
    }

    [TestMethod]
    public void RecentLog_KeepsUtcTimestampsAndReturnsIndependentSnapshots()
    {
        var state = new ProcessingState();
        state.AppendLog("First message");
        var entries = state.GetRecentLogEntries();
        state.AppendLog("Second message");

        Assert.HasCount(1, entries);
        Assert.AreEqual(TimeSpan.Zero, entries[0].Timestamp.Offset);
        Assert.AreEqual("First message", entries[0].Message);
        Assert.AreEqual($"[{entries[0].Timestamp.UtcDateTime:O}] First message", state.GetRecentLog()[0]);
    }

    [TestMethod]
    public void RecentLog_RemainsBoundedAndOrdered()
    {
        var state = new ProcessingState();
        for (var i = 0; i < 105; i++)
        {
            state.AppendLog($"Message {i}");
        }

        var entries = state.GetRecentLogEntries();
        Assert.HasCount(100, entries);
        Assert.AreEqual("Message 5", entries[0].Message);
        Assert.AreEqual("Message 104", entries[^1].Message);
    }

    [TestMethod]
    public void BeginActivity_KeepsActivityVisibleUntilLastScopeEnds()
    {
        var state = new ProcessingState();

        var scope1 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");
        var scope2 = state.BeginActivity("Downloading Overture divisions for Spain (ESP)...");

        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope1.Dispose();
        Assert.AreEqual("Downloading Overture divisions for Spain (ESP)...", state.CurrentActivity);

        scope2.Dispose();
        Assert.IsNull(state.CurrentActivity);
    }
}

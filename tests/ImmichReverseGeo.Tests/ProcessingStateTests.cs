using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class ProcessingStateTests
{
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

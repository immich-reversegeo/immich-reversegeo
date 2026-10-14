using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class DuckDbWorkScopeTests
{
    [TestMethod]
    [DataRow(8, false, 8)]
    [DataRow(8, true, 1)]
    [DataRow(100, false, 32)]
    public void ThreadOverride_AppliesToNewConnections_AndRespectsLowMemory(int requested, bool lowMemory, int expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"duckdb-threads-{Guid.NewGuid():N}");
        try
        {
            var policy = new MemoryResourcePolicy { DuckDbThreads = requested, LowMemoryMode = lowMemory };
            using var work = new DuckDbWorkScope(policy, directory);
            using var command = work.Connection.CreateCommand();
            command.CommandText = "SELECT current_setting('threads')";
            Assert.AreEqual(expected, Convert.ToInt32(command.ExecuteScalar()));
            policy.DuckDbThreads = 2;
            Assert.AreEqual(expected, Convert.ToInt32(command.ExecuteScalar()), "Active work keeps its current limit.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                var spillRoot = Path.Combine(directory, "duckdb-tmp");
                if (Directory.Exists(spillRoot))
                {
                    Directory.Delete(spillRoot);
                }
                Directory.Delete(directory);
            }
        }
    }
}

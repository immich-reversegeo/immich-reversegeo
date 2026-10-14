using System.Collections.Concurrent;
using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class DuckDbQueryProgressTests
{
    [TestMethod]
    public async Task IdleConnection_ReportsUnavailableInsteadOfANegativePercentage()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        var reported = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var monitor = new DuckDbQueryProgressMonitor(connection,
            value => reported.TrySetResult(value.Percentage), NullLogger.Instance);
        Assert.IsNull(await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task ParquetQuery_ReportsNativePercentage_AndStopsBeforeConnectionCloses()
    {
        var path = Path.Combine(Path.GetTempPath(), $"query-progress-{Guid.NewGuid():N}.parquet");
        var samples = new ConcurrentQueue<DuckDbQueryProgressSnapshot>();
        try
        {
            using (var connection = new DuckDBConnection("Data Source=:memory:"))
            {
                connection.Open();
                using var setup = connection.CreateCommand();
                setup.CommandText = $"SET threads=1; COPY (SELECT i FROM range(1000000) t(i)) TO '{path.Replace("'", "''")}' (FORMAT PARQUET, ROW_GROUP_SIZE 2048);";
                setup.ExecuteNonQuery();
                using var command = connection.CreateCommand();
                var expression = "CAST(i AS DOUBLE)";
                for (var i = 0; i < 50; i++)
                {
                    expression = $"sin({expression})";
                }
                command.CommandText = $"SELECT sum({expression}) FROM read_parquet('{path.Replace("'", "''")}')";
                command.CommandTimeout = 15;
                using (var monitor = new DuckDbQueryProgressMonitor(connection, samples.Enqueue, NullLogger.Instance))
                {
                    await Task.Run(() => command.ExecuteScalar());
                }
            }
            Assert.IsTrue(samples.Any(value => value.Percentage is > 0 and < 100),
                "Expected an in-flight percentage from a real local Parquet scan.");
            var countAfterDispose = samples.Count;
            await Task.Delay(300);
            Assert.AreEqual(countAfterDispose, samples.Count, "Polling must stop before the connection closes.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Overture.Services;

/// <summary>Polls native progress without issuing SQL on the busy connection.</summary>
public sealed class DuckDbQueryProgressMonitor : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _polling;

    public DuckDbQueryProgressMonitor(DuckDBConnection connection, Action<DuckDbQueryProgressSnapshot> report, ILogger logger)
    {
        using var settings = connection.CreateCommand();
        settings.CommandText = "SET enable_progress_bar = true; SET enable_progress_bar_print = false; SET progress_bar_time = 0;";
        settings.ExecuteNonQuery();
        _polling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    var native = connection.GetQueryProgress();
                    var percentage = native.Percentage;
                    report(new DuckDbQueryProgressSnapshot(
                        double.IsFinite(percentage) && percentage >= 0 && percentage <= 100 ? percentage : null,
                        native.RowsProcessed, native.TotalRowsToProcess));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                // A telemetry failure must not fail a cache download.
                logger.LogDebug(ex, "DuckDB query progress unavailable");
                report(new DuckDbQueryProgressSnapshot(null, 0, 0));
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        // Join before the owning query/connection can be disposed or another stage reported.
        _polling.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}

public sealed record DuckDbQueryProgressSnapshot(double? Percentage, ulong RowsProcessed, ulong TotalRows);

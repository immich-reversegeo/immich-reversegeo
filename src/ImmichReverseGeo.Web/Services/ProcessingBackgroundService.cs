using System.IO;
using System.Text.Json;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using ImmichReverseGeo.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ProcessingBackgroundService(
    ILogger<ProcessingBackgroundService> logger,
    ConfigService config,
    AssetLocationResolverService locationResolver,
    ProcessingState state,
    ImmichDbRepository db,
    SkippedAssetsRepository skipped) : BackgroundService
{
    private CancellationTokenSource? _runCts;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sw = Stopwatch.StartNew();
        logger.LogInformation("ProcessingBackgroundService: initialising skipped-assets db");
        await skipped.InitialiseAsync();
        logger.LogInformation("ProcessingBackgroundService: skipped-assets db ready in {Elapsed}ms", sw.ElapsedMilliseconds);
        state.AppendLog("Service started. Waiting for next scheduled run.");

        while (!stoppingToken.IsCancellationRequested)
        {
            AppConfig cfg;
            try
            {
                cfg = await config.GetConfigAsync();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                logger.LogError(ex, "Settings could not be read; scheduled processing will retry in one minute");
                state.AppendLog("Settings could not be read. Scheduled processing is paused; restore settings.json or check its permissions.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }
            if (!cfg.Schedule.Enabled)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            var next = GetNextOccurrence(cfg.Schedule.Cron);
            if (next is null)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            var delay = next.Value - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                state.AppendLog($"Next run scheduled at {next.Value:u}");
                await ScheduledRunWaiter.WaitUntilAsync(next.Value, TimeProvider.System, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                if (await _runLock.WaitAsync(0, stoppingToken))
                {
                    state.MarkPending();
                    _runCts?.Dispose();
                    _runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    try { await RunOnceAsync(_runCts.Token); }
                    finally { _runLock.Release(); }
                }
                else
                {
                    state.AppendLog("Scheduled run skipped because processing or maintenance is already in progress.");
                }
            }
        }
    }

    /// <summary>Starts a background pass if processing and maintenance are idle.</summary>
    public bool TryTriggerRun()
    {
        // Acquire the lock synchronously (non-blocking). If it's already held by a running
        // or recently-triggered run, bail out immediately — no silent race possible.
        if (!_runLock.Wait(0))
        {
            state.AppendLog("Manual run was not started because processing or maintenance is already in progress.");
            return false;
        }

        // Mark as pending right now so the UI disables the button on this render cycle,
        // before the background Task.Run has had a chance to call state.StartRun().
        state.MarkPending();

        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        _ = Task.Run(async () =>
        {
            try { await RunOnceAsync(token); }
            finally { _runLock.Release(); }
        }).ContinueWith(t => logger.LogError(t.Exception, "Manual processing run faulted"),
                        TaskContinuationOptions.OnlyOnFaulted);

        return true;
    }

    public void CancelRun() => _runCts?.Cancel();

    /// <summary>Resets and cache changes share the run lock, preventing a run starting mid-write.</summary>
    public IDisposable? TryBeginMaintenance()
    {
        if (!_runLock.Wait(0))
        {
            return null;
        }
        state.SetMaintenance(true);
        return new MaintenanceScope(() =>
        {
            state.SetMaintenance(false);
            _runLock.Release();
        });
    }

    private sealed class MaintenanceScope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var outcome = ProcessingRunOutcome.Completed;
        try
        {
            using var releaseScope = locationResolver.BeginOvertureReleaseScope();
            var total = await db.GetUnprocessedCountAsync(ct);
            state.StartRun(total);

            if (total == 0)
            {
                state.AppendLog("Nothing to process: no active GPS assets have both city and country missing. Use Preview Location Corrections to check existing names.");
                return;
            }

            state.AppendLog($"Run started. {total} assets to process.");

            var skippedCount = await skipped.GetCountAsync();
            if (skippedCount > 0)
            {
                state.AppendLog($"Skipping {skippedCount} previously unresolvable assets.");
            }

            var cursor = AssetCursor.Initial;
            var cfg = await config.GetConfigAsync();
            int batchNum = 0;

            while (!ct.IsCancellationRequested)
            {
                var batch = await db.GetUnprocessedBatchAsync(cursor, cfg.Processing.EffectiveBatchSize, ct);
                if (batch.Count == 0)
                {
                    break;
                }

                batchNum++;
                state.AppendLog($"Batch {batchNum}: fetched {batch.Count} assets " +
                                $"(total processed so far: {state.ProcessedThisRun}).");

                cursor = new AssetCursor(batch[^1].CreatedAt, batch[^1].Id);

                var skippedIds = await skipped.GetMatchingAsync(batch.Select(asset => asset.Id), ct);
                var maxParallelism = cfg.Processing.EffectiveParallelism;
                await Parallel.ForEachAsync(
                    batch,
                    new ParallelOptions
                    {
                        CancellationToken = ct,
                        MaxDegreeOfParallelism = maxParallelism
                    },
                    async (asset, token) =>
                    {
                        if (skippedIds.Contains(asset.Id))
                        {
                            state.RecordChecked(previouslySkipped: true);
                            return;
                        }

                        if (await ProcessAssetAsync(asset, cfg, token))
                        {
                            state.RecordChecked();
                        }
                    });

                if (cfg.Processing.BatchDelayMs > 0)
                {
                    await Task.Delay(cfg.Processing.BatchDelayMs, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = ProcessingRunOutcome.Cancelled;
            state.AppendLog("Run cancelled. Location names already written are kept.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error during processing run");
            state.IncrementError($"Fatal: {ex.Message}");
            outcome = ProcessingRunOutcome.Failed;
        }
        finally
        {
            state.CompleteRun(outcome);
            state.AppendLog($"Run ended: {state.LastOutcome}. Checked={state.CheckedThisRun} Updated={state.ProcessedThisRun} " +
                            $"Skipped={state.SkippedThisRun} Errors={state.ErrorsThisRun}");
        }
    }

    private async Task<bool> ProcessAssetAsync(AssetRecord asset, AppConfig cfg, CancellationToken ct)
    {
        var step = "ResolveLocation";
        try
        {
            // 1. Country detection — bundled Overture country divisions only.
            var geoResult = await locationResolver.ResolveAsync(
                asset.Latitude,
                asset.Longitude,
                cfg.Processing,
                new ProcessingResolutionProgress(state),
                ct);

            if (geoResult is null)
            {
                state.AppendLog($"[WARN] Asset {asset.Id}: no country found at ({asset.Latitude:F4}, {asset.Longitude:F4}), skipping.");
                await skipped.AddAsync(asset.Id);
                state.IncrementSkipped();
                return true;
            }

            // 4. Write back (only if we have country AND city)
            step = "WriteLocation";
            if (geoResult.HasMatch)
            {
                if (geoResult.City is null)
                {
                    // Don't write partial data to Immich — a write with city=NULL would
                    // satisfy the "country IS NULL" filter and prevent the asset from being
                    // reprocessed, permanently losing the city. Log for investigation and
                    // leave the asset unprocessed so it is retried on the next run.
                    logger.LogWarning(
                        "Asset {AssetId}: country={Country} state={State} resolved but no city — skipping write (lat={Lat:F4}, lon={Lon:F4})",
                        asset.Id, geoResult.Country, geoResult.State, asset.Latitude, asset.Longitude);
                    state.IncrementSkipped();
                    return true;
                }

                if (cfg.Processing.VerboseLogging)
                {
                    state.AppendLog($"Asset {asset.Id}: {geoResult.City}, {geoResult.State}, {geoResult.Country}");
                }
                else
                {
                    logger.LogDebug("Asset {AssetId}: {City}, {State}, {Country}",
                        asset.Id, geoResult.City, geoResult.State, geoResult.Country);
                }
                if (await db.WriteUnprocessedLocationAsync(asset, geoResult, ct))
                {
                    state.IncrementProcessed();
                }
                else
                {
                    state.AppendLog($"Asset {asset.Id}: location or coordinates changed after the batch was loaded; skipped write.");
                    state.IncrementSkipped();
                }
            }
            else
            {
                state.AppendLog($"[WARN] Asset {asset.Id}: country={geoResult.Country} but no admin match, skipping.");
                await skipped.AddAsync(asset.Id);
                state.IncrementSkipped();
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(
                "Processing cancelled at step={Step} for asset {AssetId}",
                step,
                asset.Id);
            return false;
        }
        catch (Exception ex)
        {
            // Log full exception including type and stack trace so we can pinpoint the source
            logger.LogError(ex, "Error at step={Step} for asset {AssetId} [{ExType}]",
                step, asset.Id, ex.GetType().Name);
            state.IncrementError($"Asset {asset.Id} [{step}]: {ex.Message}");
            return true;
        }
    }

    private static DateTime? GetNextOccurrence(string cron)
    {
        try
        {
            var expr = CronExpression.Parse(cron, CronFormat.Standard);
            return expr.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }
        catch { return null; }
    }

    private sealed class ProcessingResolutionProgress(ProcessingState state) : IAdministrativeAreaResolutionProgress
    {
        public IDisposable BeginActivity(string activity)
        {
            return state.BeginActivity(activity);
        }

        public void Report(string message)
        {
            state.AppendLog(message);
        }
    }
}

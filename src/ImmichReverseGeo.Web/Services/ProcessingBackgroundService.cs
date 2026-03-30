using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cronos;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public class ProcessingBackgroundService(
    ILogger<ProcessingBackgroundService> logger,
    ConfigService config,
    ProcessingState state,
    ImmichDbRepository db,
    OverturePlacesService overturePlaces,
    OvertureDivisionsService overtureDivisions,
    DataCacheService cache,
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
            var cfg = await config.GetConfigAsync();
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
                await Task.Delay(delay, stoppingToken);
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                if (await _runLock.WaitAsync(0, stoppingToken))
                {
                    state.MarkPending();
                    try { await RunOnceAsync(stoppingToken); }
                    finally { _runLock.Release(); }
                }
                else
                {
                    state.AppendLog("Scheduled run skipped because a processing pass is already in progress.");
                }
            }
        }
    }

    /// <summary>Triggered by "Run Now" button in the Blazor UI.</summary>
    public Task TriggerRunAsync()
    {
        // Acquire the lock synchronously (non-blocking). If it's already held by a running
        // or recently-triggered run, bail out immediately — no silent race possible.
        if (!_runLock.Wait(0))
        {
            return Task.CompletedTask;
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
        }).ContinueWith(t => logger.LogError(t.Exception, "TriggerRunAsync faulted"),
                        TaskContinuationOptions.OnlyOnFaulted);

        return Task.CompletedTask;
    }

    public void CancelRun() => _runCts?.Cancel();

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var total = await db.GetUnprocessedCountAsync(ct);
            state.StartRun(total);

            if (total == 0)
            {
                state.AppendLog("Run started — nothing to process, all assets already have location data.");
                return;
            }

            state.AppendLog($"Run started. {total} assets to process.");

            var skippedIds = await skipped.GetAllAsync();
            if (skippedIds.Count > 0)
            {
                state.AppendLog($"Skipping {skippedIds.Count} previously unresolvable assets.");
            }

            var cursor = AssetCursor.Initial;
            var cfg = await config.GetConfigAsync();
            int batchNum = 0;

            while (!ct.IsCancellationRequested)
            {
                var batch = await db.GetUnprocessedBatchAsync(cursor, cfg.Processing.BatchSize, ct);
                if (batch.Count == 0)
                {
                    break;
                }

                batchNum++;
                state.AppendLog($"Batch {batchNum}: fetched {batch.Count} assets " +
                                $"(total processed so far: {state.ProcessedThisRun}).");

                cursor = new AssetCursor(batch[^1].CreatedAt, batch[^1].Id);

                var maxParallelism = Math.Clamp(cfg.Processing.MaxDegreeOfParallelism, 1, 32);
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
                            return;
                        }

                        await ProcessAssetAsync(asset, cfg, token);
                    });

                if (cfg.Processing.BatchDelayMs > 0)
                {
                    await Task.Delay(cfg.Processing.BatchDelayMs, ct);
                }
            }
        }
        catch (OperationCanceledException) { state.AppendLog("Run cancelled."); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error during processing run");
            state.IncrementError($"Fatal: {ex.Message}");
        }
        finally
        {
            state.CompleteRun();
            state.AppendLog($"Run complete. Processed={state.ProcessedThisRun} " +
                            $"Skipped={state.SkippedThisRun} Errors={state.ErrorsThisRun}");
        }
    }

    private async Task ProcessAssetAsync(AssetRecord asset, AppConfig cfg, CancellationToken ct)
    {
        var step = "FindCountry";
        try
        {
            // 1. Country detection — bundled Overture country divisions only.
            var (iso3, countryName, alpha2) = await overtureDivisions.FindBundledCountryAsync(
                asset.Latitude,
                asset.Longitude,
                ct);

            if (iso3 is null)
            {
                state.AppendLog($"[WARN] Asset {asset.Id}: no country found at ({asset.Latitude:F4}, {asset.Longitude:F4}), skipping.");
                await skipped.AddAsync(asset.Id);
                state.IncrementSkipped();
                return;
            }

            // 2. On-demand Overture data fetch
            step = "EnsureOvertureDivisionData";
            var (divisionEnsureTask, ensureResult) = cache.GetOrStartOvertureDivisionDownload(iso3, ct);
            using var divisionDownloadActivity = ensureResult == OvertureDivisionEnsureResult.StartedDownload
                ? state.BeginActivity($"Downloading Overture divisions for {countryName} ({iso3})…")
                : null;
            if (ensureResult == OvertureDivisionEnsureResult.StartedDownload)
            {
                state.AppendLog($"Preparing Overture divisions cache for {countryName} ({iso3}).");
            }
            await divisionEnsureTask.WaitAsync(ct);

            // 3. Admin lookup — Overture divisions only.
            step = "FindAdminLevels";
            var adminResult = await overtureDivisions.ResolveAdministrativeGeoAsync(
                                asset.Latitude,
                                asset.Longitude,
                                alpha2,
                                iso3,
                                ct);
            var geoResult = new GeoResult(countryName!, adminResult?.State, adminResult?.City);

            if (cfg.Processing.UseAirportInfrastructure)
            {
                // 4. Transport lookup — bundled Overture airport infrastructure.
                step = "FindNearestInfrastructure";
                var infrastructure = await overturePlaces.FindNearestInfrastructureWithDiagnosticsAsync(
                    asset.Latitude,
                    asset.Longitude,
                    iso3,
                    ct);

                if (infrastructure.BestMatch?.GeometryContainsPoint == true)
                {
                    geoResult = geoResult with { City = infrastructure.BestMatch.Name };
                }
                else if (geoResult.City is null && infrastructure.BestMatch is not null)
                {
                    geoResult = geoResult with { City = infrastructure.BestMatch.Name };
                }
            }

            // 5. City fallback — promote state → city when divisions resolve only a broad area.
            if (geoResult.City is null && geoResult.State is not null)
            {
                geoResult = geoResult with { City = geoResult.State };
            }

            // 6. Write back (only if we have country AND city)
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
                    return;
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
                await db.WriteLocationAsync(asset.Id, geoResult, ct);
                state.IncrementProcessed();
            }
            else
            {
                state.AppendLog($"[WARN] Asset {asset.Id}: country={countryName} but no admin match, skipping.");
                await skipped.AddAsync(asset.Id);
                state.IncrementSkipped();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug(
                "Processing cancelled at step={Step} for asset {AssetId}",
                step,
                asset.Id);
        }
        catch (Exception ex)
        {
            // Log full exception including type and stack trace so we can pinpoint the source
            logger.LogError(ex, "Error at step={Step} for asset {AssetId} [{ExType}]",
                step, asset.Id, ex.GetType().Name);
            state.IncrementError($"Asset {asset.Id} [{step}]: {ex.Message}");
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
}

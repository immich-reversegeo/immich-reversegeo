using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ImmichReverseGeo.Web.Services;

public sealed class LocationManagementService(SavedLocationPreviewRepository scans,
    LocationChangeRepository changes, ILocationChangeWriter writer, ILogger<LocationManagementService> logger)
{
    private readonly SemaphoreSlim _execution = new(1, 1);

    public async Task<IReadOnlyList<LocationPreviewRow>> ReviewAsync(Guid scanId, IReadOnlyCollection<Guid> assetIds)
    {
        var ids = assetIds.Distinct().Take(1001).ToArray();
        if (ids.Length is < 1 or > 1000)
        {
            throw new ArgumentException("Select between 1 and 1,000 differences per operation.");
        }
        var rows = await scans.GetSelectedRowsAsync(scanId, ids);
        if (rows.Count != ids.Length || rows.Any(row => !row.HasChange))
        {
            throw new ArgumentException("One or more selected assets are missing from this scan or are not resolvable differences.");
        }
        if ((await changes.GetScanResultsAsync(scanId, ids)).Count > 0)
        {
            throw new ArgumentException("One or more selected differences have already been attempted. Use Change History for pending work or run a new scan before applying them again.");
        }
        return rows;
    }

    public async Task<Guid> CreateApplyAsync(Guid scanId, IReadOnlyCollection<Guid> assetIds) =>
        await changes.CreateAsync(scanId, await ReviewAsync(scanId, assetIds));

    public async Task RunAsync(Guid operationId, bool undo, Func<Task>? updated = null, CancellationToken ct = default)
    {
        if (!await _execution.WaitAsync(0, ct))
        {
            throw new InvalidOperationException("Another location change operation is running. Resume this operation from Change History when it finishes.");
        }
        try
        {
            await changes.RecoverAsync(operationId);
            logger.LogInformation("Location change operation {OperationId}: {Action} started", operationId, undo ? "undo" : "apply");
            foreach (var item in await changes.GetItemsAsync(operationId))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                var pending = undo ? item.UndoStatus == LocationUndoStatus.Pending : item.ApplyStatus == LocationChangeStatus.Pending;
                if (!pending || !await changes.ClaimAsync(operationId, item.AssetId, undo))
                {
                    continue;
                }
                LocationWriteResult result;
                try
                {
                    if (undo && string.IsNullOrWhiteSpace(item.AppliedRevision))
                    {
                        throw new InvalidOperationException("The applied metadata revision is missing; undo cannot proceed safely.");
                    }
                    var snapshot = item.Snapshot;
                    var expected = undo ? snapshot.Asset with
                    {
                        Existing = snapshot.Proposed!
                    } : snapshot.Asset;
                    result = await writer.TryReplaceLocationAsync(expected,
                        undo ? snapshot.Asset.Existing : snapshot.Proposed!, undo ? item.AppliedRevision : null, ct);
                    if (result.Applied && string.IsNullOrWhiteSpace(result.Revision))
                    {
                        throw new InvalidOperationException("The database did not return a revision for the applied change.");
                    }
                }
                catch (Exception ex)
                {
                    // The server may have committed even if the connection failed afterwards.
                    await changes.FinishAsync(item, undo, "NeedsReview", null,
                        "Write outcome uncertain. Check Immich before making another change. " + ex.Message);
                    logger.LogWarning(ex, "Location change operation {OperationId}: uncertain outcome for {AssetId}", operationId, item.AssetId);
                    if (updated is not null)
                    {
                        await updated();
                    }
                    break;
                }

                // A failure here leaves Writing, so the next run recovers it as uncertain.
                await changes.FinishAsync(item, undo, result.Applied ? undo ? "Undone" : "Applied" : "Skipped",
                    result.Revision, result.Reason);
                if (updated is not null)
                {
                    await updated();
                }
            }
            logger.LogInformation("Location change operation {OperationId}: {Action} stopped", operationId, undo ? "undo" : "apply");
        }
        finally
        {
            _execution.Release();
        }
    }
}

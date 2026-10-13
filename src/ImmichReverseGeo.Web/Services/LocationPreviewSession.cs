using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

/// <summary>One circuit's live review; durable results still belong to SavedLocationPreviewRepository.</summary>
public sealed class LocationPreviewSession
{
    public LocationPreviewScope Scope { get; set; } = LocationPreviewScope.AssetIds;
    public string? Input { get; set; }
    public LocationPreviewOrder Order { get; set; }
    public LocationPreviewScanMode Mode { get; set; }
    public int PauseAfterDifferences { get; set; } = 100;
    public LocationPreviewSelection? Selection { get; set; }
    public ProcessingConfig? Settings { get; set; }
    public LocationPreviewScanner? Scanner { get; set; }
    public LocationPreviewScanEstimate? Estimate { get; set; }
    public Guid? SavedPreviewId { get; set; }
    public Guid? ResumeScanId { get; set; }
    public TimeSpan ElapsedBeforeResume { get; set; }
    public long? PausedDifferences { get; set; }
    public string PreviewLogId { get; set; } = string.Empty;
    public LocationPreviewDifferencePage Differences { get; set; } = new();
    public LocationReviewFilter Filter { get; set; } = LocationReviewFilter.Ready;
    public bool OnlyChanges { get; set; } = true;
    public LocationPreviewReview Review { get; } = new();
    public Stopwatch ScanElapsed { get; } = new();
    public Task? RunningScan { get; set; }
    public HashSet<Guid> ResetAssets { get; } = [];
    public bool ResetAll { get; set; }
    public Guid[] ResetIds { get; private set; } = [];

    public void PrepareReset() => ResetIds = Review.SelectedIds.ToArray();

    public Guid[] TakeResetIds()
    {
        var ids = ResetIds;
        ResetIds = [];
        return ids;
    }

    public void RemoveResetAssets(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            Review.Deselect(id);
            ResetAssets.Add(id);
        }
    }
}

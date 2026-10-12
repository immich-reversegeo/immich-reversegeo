using System;

namespace ImmichReverseGeo.Web.Services;

/// <summary>Progress across pages, using only active processing time for the estimate.</summary>
public sealed class LocationPreviewScanEstimate(long total)
{
    public long Total { get; } = Math.Max(0, total);
    public long Checked { get; private set; }
    public TimeSpan ActiveElapsed { get; private set; }
    public double Percentage => Total == 0 ? 100 : Math.Min(100, 100.0 * Checked / Total);

    // Wait for a small sample rather than extrapolating the first lookup alone.
    public TimeSpan? EstimatedRemaining => Checked >= Total ? TimeSpan.Zero
        : Checked < 5 || ActiveElapsed <= TimeSpan.Zero ? null
        : TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds / 2,
            ActiveElapsed.TotalSeconds / Checked * (Total - Checked)));

    public void RecordChecked(TimeSpan activeElapsed)
    {
        Checked++;
        UpdateElapsed(activeElapsed);
    }

    public void UpdateElapsed(TimeSpan activeElapsed) => ActiveElapsed = activeElapsed;
}

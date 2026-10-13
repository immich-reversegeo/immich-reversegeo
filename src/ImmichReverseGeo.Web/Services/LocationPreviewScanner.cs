using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

public sealed record LocationPreviewScanOutcome(bool Paused, long NewDifferences);

/// <summary>A page-owned scan with bounded live rows and a cursor advanced only after saving.</summary>
public sealed class LocationPreviewScanner
{
    private readonly LocationPreviewScanMode _mode;
    private readonly int _pauseAfterDifferences;
    private readonly List<LocationPreviewRow> _rows = [];
    public IReadOnlyList<LocationPreviewRow> Rows => _rows;
    public LocationPreviewCursor? Cursor { get; private set; }
    public long CheckedCount { get; private set; }
    public long DifferentCount { get; private set; }
    public int BatchNumber { get; private set; }
    public int BatchSize { get; private set; }
    public bool HasMore { get; private set; }

    public LocationPreviewScanner(LocationPreviewScanMode mode, int pauseAfterDifferences)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("Choose a valid scan mode.", nameof(mode));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(pauseAfterDifferences);
        _mode = mode;
        _pauseAfterDifferences = pauseAfterDifferences;
    }

    public static LocationPreviewScanner FromSaved(LocationPreviewResumeState saved)
    {
        var selection = saved.Report.Selection;
        return new(selection.ScanMode, selection.PauseAfterDifferences)
        {
            Cursor = saved.Cursor,
            CheckedCount = saved.Report.CheckedCount,
            DifferentCount = saved.Report.DifferentCount,
            BatchNumber = saved.Report.Checkpoint?.BatchNumber ?? (int)Math.Min(int.MaxValue, (saved.Report.CheckedCount + 99) / 100),
            HasMore = true
        };
    }

    public async Task<LocationPreviewScanOutcome> RunAsync(
        Func<LocationPreviewCursor?, CancellationToken, Task<IReadOnlyList<LocationPreviewAsset>>> load,
        Func<LocationPreviewAsset, CancellationToken, Task<LocationPreviewRow>> resolve,
        Func<LocationPreviewRow, Task> save,
        Func<Task> changed,
        CancellationToken ct = default)
    {
        long newDifferences = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            _rows.Clear();
            BatchSize = 0;
            BatchNumber++;
            await changed();
            var assets = await load(Cursor, ct);
            BatchSize = Math.Min(assets.Count, 100);
            HasMore = assets.Count > 0;
            await changed();
            foreach (var asset in assets.Take(100))
            {
                ct.ThrowIfCancellationRequested();
                var row = await resolve(asset, ct);
                ct.ThrowIfCancellationRequested();
                await save(row);
                // Publishing after the write also makes thumbnails immediately eligible.
                Cursor = LocationPreviewCursor.FromAsset(row.Asset);
                _rows.Add(row);
                CheckedCount++;
                if (row.HasChange)
                {
                    DifferentCount++;
                    newDifferences++;
                }
                HasMore = assets.Count > _rows.Count;
                await changed();
                if (_mode == LocationPreviewScanMode.Continuous && _pauseAfterDifferences > 0
                    && newDifferences >= _pauseAfterDifferences && HasMore)
                {
                    return new(true, newDifferences);
                }
            }

            HasMore = assets.Count > _rows.Count;
        }
        while (_mode == LocationPreviewScanMode.Continuous && HasMore);

        return new(false, newDifferences);
    }
}

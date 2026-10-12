using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

/// <summary>One bounded review page from the saved differences, independent of the scan batch.</summary>
public sealed class LocationPreviewDifferencePage
{
    private IReadOnlyList<LocationPreviewRow> _rows = Array.Empty<LocationPreviewRow>();
    private long _loadedCount = -1;
    public int Page { get; private set; } = 1;
    public IEnumerable<LocationPreviewRow> Rows => _rows.Take(100);
    public bool HasNext => _rows.Count > 100;

    public async Task RefreshAsync(long differentCount,
        Func<int, Task<IReadOnlyList<LocationPreviewRow>>> load, bool force = false)
    {
        // Once this page has its look-ahead row, later appends cannot change it.
        if (!force && (_loadedCount == differentCount || HasNext))
        {
            return;
        }

        _rows = await load(Page);
        while (Page > 1 && _rows.Count == 0)
        {
            Page--;
            _rows = await load(Page);
        }
        _loadedCount = differentCount;
    }

    public async Task MoveAsync(int direction, long differentCount,
        Func<int, Task<IReadOnlyList<LocationPreviewRow>>> load)
    {
        if (direction is not (-1 or 1) || (direction == -1 && Page == 1) || (direction == 1 && !HasNext))
        {
            return;
        }

        var page = Page + direction;
        var rows = await load(page);
        Page = page;
        _rows = rows;
        _loadedCount = differentCount;
    }
}

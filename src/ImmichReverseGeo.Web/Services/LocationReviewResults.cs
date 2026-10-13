using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ImmichReverseGeo.Web.Services;

public enum LocationReviewFilter
{
    Ready, Applied, Attention, All
}

/// <summary>Pages saved snapshots by their recorded write outcomes, without new lookups.</summary>
public sealed class LocationReviewResults(SavedLocationPreviewRepository scans, LocationChangeRepository changes)
{
    public async Task<IReadOnlyList<LocationPreviewRow>> GetRowsAsync(Guid scanId, int page, LocationReviewFilter filter)
    {
        // Initialize the journal before attaching it to the snapshot query.
        await changes.GetOperationsAsync(id: Guid.Empty);
        return await scans.GetReviewRowsAsync(scanId, page, filter);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ImmichReverseGeo.Web.Services;

/// <summary>A confirmation belongs to one scan, result view, and exact selection.</summary>
public sealed class LocationReviewSnapshot(Guid scanId, IEnumerable<Guid> assetIds, long version)
{
    private readonly Guid[] _ids = assetIds.Distinct().Order().ToArray();

    public bool Matches(Guid? currentScanId, IEnumerable<Guid> currentIds, long currentVersion) =>
        scanId == currentScanId && version == currentVersion
        && _ids.SequenceEqual(currentIds.Distinct().Order());
}

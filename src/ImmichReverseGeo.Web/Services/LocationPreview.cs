using System;
using System.Collections.Generic;
using System.Linq;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public enum LocationPreviewScope
{
    AssetIds,
    City,
    State,
    Country,
    All
}

public enum LocationPreviewOrder
{
    AssetId,
    NearbyCoordinates
}

public enum LocationPreviewScanMode
{
    ManualPages,
    Continuous
}

public sealed record LocationPreviewSelection(
    LocationPreviewScope Scope,
    Guid[] AssetIds,
    string? Value)
{
    public LocationPreviewOrder Order { get; init; }
    public LocationPreviewScanMode ScanMode { get; init; }
    public int PauseAfterDifferences { get; init; }

    public static LocationPreviewSelection Create(LocationPreviewScope scope, string? input)
    {
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentException("Choose a valid preview scope.");
        }

        if (scope == LocationPreviewScope.All)
        {
            return new(scope, [], null);
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException(scope == LocationPreviewScope.AssetIds
                ? "Paste at least one asset GUID."
                : "Enter the existing location name exactly as it appears in Immich.");
        }

        if (scope != LocationPreviewScope.AssetIds)
        {
            return new(scope, [], input);
        }

        var ids = new HashSet<Guid>();
        foreach (var token in input.Split(['\r', '\n', ',', ';', ' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(token, out var id))
            {
                throw new ArgumentException($"Invalid asset GUID: {token}. No preview was started.");
            }

            ids.Add(id);
        }

        if (ids.Count == 0 || ids.Count > 1000)
        {
            throw new ArgumentException("Paste between 1 and 1,000 asset GUIDs.");
        }

        return new(scope, [.. ids], null);
    }
}

public sealed record LocationPreviewAsset(
    Guid Id, double? Latitude, double? Longitude, GeoResult Existing);

/// <summary>Stable seek key for GUID order or quarter-degree map tiles.</summary>
public sealed record LocationPreviewCursor(Guid Id, double LatitudeTile, double LongitudeTile)
{
    public static LocationPreviewCursor FromAsset(LocationPreviewAsset asset)
    {
        if (asset.Latitude is null || asset.Longitude is null)
        {
            throw new ArgumentException("A preview cursor requires both GPS coordinates.", nameof(asset));
        }

        return new(asset.Id, Math.Floor(asset.Latitude.Value * 4), Math.Floor(asset.Longitude.Value * 4));
    }
}

public sealed record LocationPreviewRow(LocationPreviewAsset Asset, GeoResult? Proposed, string? Error = null)
{
    public bool HasChange => Asset.Latitude is not null && Asset.Longitude is not null
        && Error is null && Proposed?.HasMatch == true
        && Proposed.City is not null && Proposed != Asset.Existing;

    public string Status => Error is not null ? "Lookup failed"
        : Asset.Latitude is null || Asset.Longitude is null ? "No GPS coordinates"
        : Proposed?.HasMatch != true || Proposed.City is null ? "No resolvable location"
        : HasChange ? "Different" : "Unchanged";
}

/// <summary>Only explicitly reviewed, resolvable changes are carried across preview pages.</summary>
public sealed class LocationPreviewReview
{
    private readonly HashSet<Guid> _selectedIds = [];

    public IReadOnlyList<Guid> SelectedIds => _selectedIds.Order().ToArray();
    public int Count => _selectedIds.Count;

    public bool IsSelected(Guid id) => _selectedIds.Contains(id);

    public void Select(LocationPreviewRow row, bool selected)
    {
        if (selected && row.HasChange)
        {
            _selectedIds.Add(row.Asset.Id);
        }
        else
        {
            _selectedIds.Remove(row.Asset.Id);
        }
    }

    public void Clear() => _selectedIds.Clear();
}

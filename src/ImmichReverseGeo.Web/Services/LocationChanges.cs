using System;
using System.Threading;
using System.Threading.Tasks;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Web.Services;

public enum LocationChangeStatus
{
    Pending, Writing, Applied, Skipped, NeedsReview
}
public enum LocationUndoStatus
{
    NotRequested, Pending, Writing, Undone, Skipped, NeedsReview
}

public sealed record LocationChangeOperation(Guid Id, Guid ScanId, DateTimeOffset CreatedAt,
    int Total, int Applied, int Skipped, int NeedsReview, int Pending, int Undone, int UndoPending);

public sealed record LocationChangeItem(Guid OperationId, LocationPreviewRow Snapshot,
    LocationChangeStatus ApplyStatus, LocationUndoStatus UndoStatus, string? AppliedRevision,
    string? Message, DateTimeOffset UpdatedAt)
{
    public Guid AssetId => Snapshot.Asset.Id;
    public bool CanUndo => !string.IsNullOrWhiteSpace(AppliedRevision) && ApplyStatus == LocationChangeStatus.Applied
        && UndoStatus is LocationUndoStatus.NotRequested or LocationUndoStatus.Skipped;
    public string Label => (UndoStatus, ApplyStatus) switch
    {
        (LocationUndoStatus.NotRequested, LocationChangeStatus.NeedsReview) => "Needs review",
        (LocationUndoStatus.NotRequested, _) => ApplyStatus.ToString(),
        (LocationUndoStatus.NeedsReview, _) => "Undo needs review",
        (LocationUndoStatus.Undone, _) => "Undone",
        _ => $"Undo: {UndoStatus}"
    };
}

public sealed record LocationWriteResult(bool Applied, string? Revision, string? Reason)
{
    public static LocationWriteResult Skip(string reason) => new(false, null, reason);
}

public interface ILocationChangeWriter
{
    Task<LocationWriteResult> TryReplaceLocationAsync(LocationPreviewAsset expected,
        GeoResult replacement, string? expectedRevision = null, CancellationToken ct = default);
}

using System;

namespace ImmichReverseGeo.Core.Models;

public record AssetRecord(Guid Id, double Latitude, double Longitude, DateTime CreatedAt);

public record AssetCursor(DateTime CreatedAt, Guid Id)
{
    public static readonly AssetCursor Initial =
        new(DateTime.UnixEpoch, Guid.Empty);
}

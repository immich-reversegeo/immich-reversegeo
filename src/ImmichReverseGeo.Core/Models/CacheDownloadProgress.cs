using System;

namespace ImmichReverseGeo.Core.Models;

/// <summary>A snapshot of active cache work. Byte totals describe the transfer only.</summary>
public sealed record CacheDownloadProgress(
    string Source,
    string Iso3,
    DateTimeOffset StartedAt,
    string Stage,
    long BytesReceived = 0,
    long? TotalBytes = null,
    long RowsWritten = 0,
    double? QueryPercentage = null)
{
    public double? TransferPercentage => TotalBytes is > 0
        ? Math.Clamp(100d * BytesReceived / TotalBytes.Value, 0, 100)
        : null;
}

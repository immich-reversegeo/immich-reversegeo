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
    double? QueryPercentage = null,
    int Phase = 0,
    DateTimeOffset? PhaseStartedAt = null,
    DateTimeOffset? LastProgressAt = null,
    ulong QueryRowsProcessed = 0,
    ulong QueryRowsTotal = 0,
    long? TotalRows = null)
{
    public const int OverturePhaseCount = 5;

    public CacheDownloadProgress Observe(string stage, int phase = 0, double? percentage = null,
        ulong queryRows = 0, ulong queryTotal = 0, long rows = 0, long? totalRows = null,
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        var stageChanged = Stage != stage || Phase != phase;
        var changed = stageChanged || QueryPercentage != percentage || QueryRowsProcessed != queryRows
            || QueryRowsTotal != queryTotal || RowsWritten != rows || TotalRows != totalRows;
        return this with
        {
            Stage = stage, Phase = phase, QueryPercentage = percentage,
            QueryRowsProcessed = queryRows, QueryRowsTotal = queryTotal,
            RowsWritten = rows, TotalRows = totalRows,
            PhaseStartedAt = stageChanged || PhaseStartedAt is null ? now : PhaseStartedAt,
            LastProgressAt = changed || LastProgressAt is null ? now : LastProgressAt
        };
    }

    public double? WritePercentage => TotalRows is > 0
        ? Math.Clamp(100d * RowsWritten / TotalRows.Value, 0, 100) : null;

    public double? TransferPercentage => TotalBytes is > 0
        ? Math.Clamp(100d * BytesReceived / TotalBytes.Value, 0, 100)
        : null;
}

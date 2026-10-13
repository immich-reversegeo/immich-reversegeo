using System;

namespace ImmichReverseGeo.Web.Services;

public sealed record LocationPreviewCheckpoint(long? TotalCount, long ActiveTicks, int BatchNumber, long? PausedDifferences);
public sealed record LocationPreviewResumeState(SavedLocationPreview Report, LocationPreviewCursor? Cursor);

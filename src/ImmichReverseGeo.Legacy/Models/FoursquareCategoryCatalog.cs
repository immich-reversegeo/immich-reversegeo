using System;
using System.Collections.Generic;

namespace ImmichReverseGeo.Legacy.Models;

public record FoursquareCategoryCatalog(
    DateTime DownloadedAt,
    List<FoursquareCategoryEntry> Categories);

public record FoursquareCategoryEntry(
    string CategoryId,
    int CategoryLevel,
    string CategoryName,
    string CategoryLabel,
    string? Level1CategoryId,
    string? Level1CategoryName,
    string? Level2CategoryId,
    string? Level2CategoryName,
    string? Level3CategoryId,
    string? Level3CategoryName,
    string? Level4CategoryId,
    string? Level4CategoryName,
    string? Level5CategoryId,
    string? Level5CategoryName,
    string? Level6CategoryId,
    string? Level6CategoryName)
{
    public IEnumerable<string> GetHierarchyIds()
    {
        if (!string.IsNullOrWhiteSpace(Level1CategoryId)) { yield return Level1CategoryId; }
        if (!string.IsNullOrWhiteSpace(Level2CategoryId)) { yield return Level2CategoryId; }
        if (!string.IsNullOrWhiteSpace(Level3CategoryId)) { yield return Level3CategoryId; }
        if (!string.IsNullOrWhiteSpace(Level4CategoryId)) { yield return Level4CategoryId; }
        if (!string.IsNullOrWhiteSpace(Level5CategoryId)) { yield return Level5CategoryId; }
        if (!string.IsNullOrWhiteSpace(Level6CategoryId)) { yield return Level6CategoryId; }
    }
}

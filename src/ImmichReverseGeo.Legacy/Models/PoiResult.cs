using System.Collections.Generic;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Legacy.Models;

public record PoiResult(
    string Name,
    CategoryTier Tier,
    double DistanceMetres,
    bool ContainsPoint = false,
    double? BoundingBoxArea = null,
    int ContainmentRank = 0);

public record PoiLookupDiagnostics(
    PoiResult? BestMatch,
    List<PoiCandidateDiagnostic> Candidates,
    int ValidAllowlistCount);

public record PoiCandidateDiagnostic(
    string Name,
    string CategoryId,
    CategoryTier Tier,
    double DistanceMetres,
    bool ContainsPoint,
    int ContainmentRank,
    bool WithinTierRadius,
    bool Selected,
    string Decision,
    double? BoundingBoxArea = null);

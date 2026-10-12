using System.Collections.Generic;

namespace ImmichReverseGeo.Overture.Models;

public record OverturePlaceResult(
    string Id,
    string Name,
    string? Category,
    string? BasicCategory,
    double Confidence,
    string? OperatingStatus,
    double DistanceMetres,
    bool BoundingBoxContainsPoint,
    IReadOnlyList<string> Sources,
    bool ConfidenceKnown = true);

public record OvertureLookupDiagnostics(
    OverturePlaceResult? BestMatch,
    List<OvertureCandidateDiagnostic> Candidates,
    string? Release,
    string? CountryFilter,
    string? Error = null);

public record OvertureCandidateDiagnostic(
    string Id,
    string Name,
    string? Category,
    string? BasicCategory,
    double Confidence,
    string? OperatingStatus,
    double DistanceMetres,
    bool BoundingBoxContainsPoint,
    IReadOnlyList<string> Sources,
    bool Selected,
    string Decision,
    bool Eligible = true,
    bool ConfidenceKnown = true);

public record OvertureInfrastructureResult(
    string Id,
    string Name,
    string? FeatureType,
    string? SubType,
    string? ClassName,
    double DistanceMetres,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    IReadOnlyList<string> Sources,
    bool HasMappedBoundary = false,
    bool IsPointOnly = false)
{
    public bool IsBoundaryMatch => HasMappedBoundary && GeometryContainsPoint;
    public string BoundaryStatus => IsPointOnly ? "point-only airport" : !HasMappedBoundary ? "boundary unavailable" : GeometryContainsPoint ? "boundary match (5 m tolerance)" : "outside boundary";
}

public record OvertureInfrastructureLookupDiagnostics(
    OvertureInfrastructureResult? BestMatch,
    List<OvertureInfrastructureCandidateDiagnostic> Candidates,
    string? Release,
    string? Error = null);

public record OvertureInfrastructureCandidateDiagnostic(
    string Id,
    string Name,
    string? FeatureType,
    string? SubType,
    string? ClassName,
    double DistanceMetres,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    IReadOnlyList<string> Sources,
    bool Selected,
    string Decision,
    bool HasMappedBoundary = false,
    bool IsPointOnly = false)
{
    public string BoundaryStatus => IsPointOnly ? "point-only airport" : !HasMappedBoundary ? "boundary unavailable" : GeometryContainsPoint ? "boundary match (5 m tolerance)" : "outside boundary";
}

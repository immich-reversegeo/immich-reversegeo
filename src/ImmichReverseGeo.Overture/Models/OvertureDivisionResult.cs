using System.Collections.Generic;

namespace ImmichReverseGeo.Overture.Models;

public record OvertureDivisionResult(
    string Id,
    string Name,
    string? SubType,
    string? ClassName,
    int? AdminLevel,
    string? Country,
    bool IsLand,
    bool IsTerritorial,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    double BoundingBoxArea);

public record OvertureDivisionLookupDiagnostics(
    OvertureDivisionResult? BestMatch,
    List<OvertureDivisionCandidateDiagnostic> Candidates,
    string? Release,
    string? Error = null);

public record OvertureDivisionCandidateDiagnostic(
    string Id,
    string Name,
    string? SubType,
    string? ClassName,
    int? AdminLevel,
    string? Country,
    bool IsLand,
    bool IsTerritorial,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    double BoundingBoxArea,
    bool Selected,
    string Decision);

public record OvertureAdministrativeResult(
    string? State,
    string? City);

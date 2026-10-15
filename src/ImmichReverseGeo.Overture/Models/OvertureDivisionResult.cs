using System.Collections.Generic;

namespace ImmichReverseGeo.Overture.Models;

public record OvertureDivisionResult(
    string Id,
    string Name,
    string? SubType,
    string? ClassName,
    int? AdminLevel,
    string? Country,
    string? Alpha3,
    bool IsLand,
    bool IsTerritorial,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    double BoundingBoxArea,
    bool? ExactGeometryContainsPoint = null)
{
    public bool IsExactGeometryMatch => GeometryContainsPoint && (ExactGeometryContainsPoint ?? true);
}

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
    string Decision,
    string? DivisionId = null,
    string? HierarchyJson = null,
    bool? ExactGeometryContainsPoint = null,
    string? NamesJson = null)
{
    public bool IsExactGeometryMatch => GeometryContainsPoint && (ExactGeometryContainsPoint ?? true);
}

public record OvertureDivisionAncestor(string DivisionId, string Name, string SubType, string? Country = null);

public record OvertureAdministrativeResult(
    string? State,
    string? City,
    string? Country = null);

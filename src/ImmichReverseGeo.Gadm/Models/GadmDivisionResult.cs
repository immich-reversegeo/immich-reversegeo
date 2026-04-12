using System.Collections.Generic;

namespace ImmichReverseGeo.Gadm.Models;

public record GadmDivisionResult(
    string Id,
    string Name,
    string? EnglishType,
    string? LocalType,
    int AdminLevel,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    double BoundingBoxArea);

public record GadmDivisionLookupDiagnostics(
    GadmDivisionResult? BestMatch,
    List<GadmDivisionCandidateDiagnostic> Candidates,
    string? Version,
    string? Error = null);

public record GadmDivisionCandidateDiagnostic(
    string Id,
    string Name,
    string? EnglishType,
    string? LocalType,
    int AdminLevel,
    bool BoundingBoxContainsPoint,
    bool GeometryContainsPoint,
    double BoundingBoxArea,
    bool Selected,
    string Decision);

public record GadmAdministrativeResult(
    string? State,
    string? City);

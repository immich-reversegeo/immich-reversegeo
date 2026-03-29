namespace ImmichReverseGeo.Core.Models;

public record GeoResult(string? Country, string? State, string? City)
{
    public bool HasMatch => Country is not null;
}

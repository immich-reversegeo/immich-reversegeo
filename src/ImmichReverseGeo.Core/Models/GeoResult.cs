namespace ImmichReverseGeo.Core.Models;

public record GeoResult(string? Country, string? State, string? City)
{
    public bool HasMatch => Country is not null;

    public GeoResult WithFallbackCity()
    {
        if (City is not null)
        {
            return this;
        }

        if (State is not null)
        {
            return this with { City = State };
        }

        if (Country is not null)
        {
            return this with { City = Country };
        }

        return this;
    }
}

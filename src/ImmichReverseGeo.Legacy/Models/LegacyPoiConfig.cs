using System.Collections.Generic;
using ImmichReverseGeo.Core.Models;

namespace ImmichReverseGeo.Legacy.Models;

public class LegacyPoiConfig
{
    public LegacyRadiusTiers RadiusTiers { get; set; } = new();
    public List<string> CategoryAllowlist { get; set; } = [];
}

public class LegacyRadiusTiers
{
    public int MajorTransport { get; set; } = 2000;
    public int LocalTransport { get; set; } = 200;
    public int LargeVenues { get; set; } = 500;
    public int Landmarks { get; set; } = 200;
    public int Nature { get; set; } = 300;
    public int Culture { get; set; } = 150;
    public int Religion { get; set; } = 100;
    public int Districts { get; set; } = 75;
    public int Default { get; set; } = 75;

    public int GetRadius(CategoryTier tier) => tier switch
    {
        CategoryTier.MajorTransport => MajorTransport,
        CategoryTier.LocalTransport => LocalTransport,
        CategoryTier.LargeVenues => LargeVenues,
        CategoryTier.Landmarks => Landmarks,
        CategoryTier.Nature => Nature,
        CategoryTier.Culture => Culture,
        CategoryTier.Religion => Religion,
        CategoryTier.Districts => Districts,
        _ => Default
    };
}

public record CategoryAllowlistEntry(string Id, string Name, string Tier);

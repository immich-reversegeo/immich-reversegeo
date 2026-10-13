using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureCompatibilityTests
{
    [TestMethod]
    public void LatestRelease_AcceptsCurrentPatch()
    {
        Assert.AreEqual("2026-09-23.1", OvertureReleaseCatalog.ValidateLatestRelease("2026-09-23.1", new DateTime(2026, 10, 11)));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("2026-02-18.0")]
    [DataRow("2026-08-12.0")]
    [DataRow("2026-10-21.0")]
    [DataRow("2026-09-31.0")]
    [DataRow("2026-09-23.1/../../other")]
    public void LatestRelease_RejectsMissingExpiredFutureAndMalformedVersions(string? release)
    {
        Assert.Throws<InvalidOperationException>(() => OvertureReleaseCatalog.ValidateLatestRelease(release, new DateTime(2026, 10, 11)));
    }

    [TestMethod]
    public void PlacesQuery_ExecutesAgainstTaxonomyOnlySchema()
    {
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var query = duck.CreateCommand();
        // Execute the actual category projection with no legacy categories column present.
        var sql = OverturePlacesLogic.BuildQuery(0, 0, -1, 1, -1, 1, null, "unused");
        var categoryProjection = sql.Split('\n').Single(line => line.Contains("AS primary_category", StringComparison.Ordinal)).Trim().TrimEnd(',');
        query.CommandText = $"SELECT {categoryProjection} FROM (SELECT {{'primary': 'museum'}} AS taxonomy, 'museum' AS basic_category)";
        Assert.AreEqual("museum", query.ExecuteScalar());
        query.CommandText = $"SELECT {categoryProjection} FROM (SELECT {{'primary': NULL::VARCHAR}} AS taxonomy, 'park' AS basic_category)";
        Assert.AreEqual("park", query.ExecuteScalar());
    }

    [TestMethod]
    [DataRow("CZ", "localadmin", "Praha")]
    [DataRow("SK", "localadmin", "Bratislava")]
    [DataRow("VN", "neighborhood", "Neighborhood")]
    public void ReclassifiedAreas_RemainEligibleUnderDefaultProfile(string country, string subtype, string name)
    {
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([Area(name, subtype, country)]);
        Assert.AreEqual(name, result.City);
    }

    [TestMethod]
    public void Hierarchy_RecoversPreferredCityAndItsStateFromContainingNeighborhood()
    {
        var neighborhood = WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state");
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([neighborhood, Area("Unrelated State", "region")]);
        Assert.AreEqual("Parent City", result.City);
        Assert.AreEqual("Parent State", result.State);
    }

    [TestMethod]
    public void Hierarchy_PrefersMatchingParentPolygonNameOverUnrelatedSmallerRegion()
    {
        var city = WithHierarchy(Area("City", "locality"), "city", null, "state");
        var parent = Area("Localized State", "region") with { DivisionId = "state", BoundingBoxArea = 10 };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([city, Area("Wrong State", "region"), parent]);
        Assert.AreEqual("Localized State", result.State);
        Assert.AreEqual("City", result.City);
    }

    [TestMethod]
    public void Hierarchy_RespectsExplicitNeighborhoodPreference()
    {
        var neighborhood = WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state");
        var profile = new CityResolverProfile { PreferredSubtypes = ["neighborhood", "locality"] };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([neighborhood], profile);
        Assert.AreEqual("Neighborhood", result.City);
        Assert.AreEqual("Parent State", result.State);
        CollectionAssert.AreEqual(new[] { "neighborhood", "locality" }, profile.PreferredSubtypes);
    }

    [TestMethod]
    public void Hierarchy_DoesNotPromoteBoundingBoxOnlyMatches()
    {
        var neighborhood = WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state") with { GeometryContainsPoint = false };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([neighborhood]);
        Assert.AreEqual("Neighborhood", result.City);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    public void Hierarchy_CanRecoverCityWhenOnlyLocalityIsAllowed()
    {
        var neighborhood = WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state");
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([neighborhood], new CityResolverProfile { PreferredSubtypes = ["locality"] });
        Assert.AreEqual("Parent City", result.City);
    }

    [TestMethod]
    public void Hierarchy_ContainingParentCityOutranksUnrelatedBoundingBoxLocality()
    {
        var neighborhood = WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state");
        var bboxOnlyCity = Area("Wrong City", "locality") with { GeometryContainsPoint = false };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([neighborhood, bboxOnlyCity]);
        Assert.AreEqual("Parent City", result.City);
        Assert.AreEqual("Parent State", result.State);
    }

    [TestMethod]
    public void Hierarchy_DoesNotUseUnrelatedStateWhenCompleteChainHasNoState()
    {
        var city = Area("City", "locality") with
        {
            DivisionId = "city",
            HierarchyJson = """[{"division_id":"country","name":"Country","subtype":"country"},{"division_id":"city","name":"City","subtype":"locality"}]"""
        };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([city, Area("Wrong State", "region")]);
        Assert.AreEqual("City", result.City);
        Assert.IsNull(result.State);
    }

    [TestMethod]
    [DataRow(CityResolverTieBreakModes.SmallestArea, "Small City", "Small State")]
    [DataRow(CityResolverTieBreakModes.LargestArea, "Large City", "Large State")]
    public void Hierarchy_PreservesConfiguredAreaTieBreakAndSelectedCityState(string tieBreak, string expectedCity, string expectedState)
    {
        var small = WithHierarchy(Area("Small City", "locality"), "small-city", null, "small-state") with
        {
            BoundingBoxArea = 1
        };
        var large = WithHierarchy(Area("Large City", "locality"), "large-city", null, "large-state") with
        {
            BoundingBoxArea = 10
        };
        var smallState = Area("Small State", "region") with { DivisionId = "small-state" };
        var largeState = Area("Large State", "region") with { DivisionId = "large-state" };
        var profile = new CityResolverProfile { TieBreakMode = tieBreak };

        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([small, large, smallState, largeState], profile);

        Assert.AreEqual(expectedCity, result.City);
        Assert.AreEqual(expectedState, result.State);
        Assert.AreEqual(tieBreak, profile.TieBreakMode);
    }

    [TestMethod]
    public void Hierarchy_CountyLikeCityUsesParentStateInsteadOfItself()
    {
        var city = WithHierarchy(Area("County City", "county"), "city", null, "state");
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([city],
            new CityResolverProfile { PreferredSubtypes = ["county", "locality"] });

        Assert.AreEqual("County City", result.City);
        Assert.AreEqual("Parent State", result.State);
    }

    [TestMethod]
    [DataRow("not-json")]
    [DataRow("{}")]
    [DataRow("[{}]")]
    [DataRow("[{\"division_id\":\"someone-else\",\"name\":\"Wrong City\",\"subtype\":\"locality\"}]")]
    public void Hierarchy_InvalidOrUnrelatedChainPreservesPolygonSelection(string json)
    {
        var city = Area("City", "locality") with { DivisionId = "city", HierarchyJson = json };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([city, Area("State", "region")]);
        Assert.AreEqual("City", result.City);
        Assert.AreEqual("State", result.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CachedLookup_ReadsLegacyAndHierarchyCaches(bool withHierarchy)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        try
        {
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(root, "overture-divisions", "CHE.db")};Pooling=false"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE division_area (
                        id TEXT, name TEXT, subtype TEXT, class_name TEXT, country TEXT,
                        is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    CREATE TABLE _meta (key TEXT, value TEXT);
                    INSERT INTO _meta VALUES ('release', '2026-02-18.0');
                    INSERT INTO division_area VALUES ('area', 'Neighborhood', 'neighborhood', NULL, 'CH', 1, 1, $geom, 0, 0, 2, 2);
                    """;
                cmd.Parameters.AddWithValue("$geom", new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))")));
                cmd.ExecuteNonQuery();
                if (withHierarchy)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = "ALTER TABLE division_area ADD COLUMN division_id TEXT; ALTER TABLE division_area ADD COLUMN hierarchy_json TEXT; UPDATE division_area SET division_id='hood', hierarchy_json=$hierarchy";
                    cmd.Parameters.AddWithValue("$hierarchy", WithHierarchy(Area("Neighborhood", "neighborhood"), "hood", "city", "state").HierarchyJson!);
                    cmd.ExecuteNonQuery();
                }
            }

            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE");
            var diagnostics = await service.FindContainingDivisionAreasAsync(1, 1, "CH", "CHE");
            Assert.IsNull(diagnostics.Error);
            var result = await service.ResolveAdministrativeGeoAsync(1, 1, "CH", "CHE");
            Assert.IsNotNull(result);
            Assert.AreEqual(withHierarchy ? "Parent City" : "Neighborhood", result.City);
            Assert.AreEqual(withHierarchy ? "Parent State" : null, result.State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OvertureDivisionCandidateDiagnostic Area(string name, string subtype, string country = "CH") =>
        new(Guid.NewGuid().ToString("N"), name, subtype, null, null, country, true, true, true, true, 1, false, "test");

    [TestMethod]
    public void DivisionJoin_PreservesAreasWithoutHierarchyAndUsesOnlyDefaultChain()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var areaDir = Path.Combine(root, "type=division_area");
        var divisionDir = Path.Combine(root, "type=division");
        Directory.CreateDirectory(areaDir);
        Directory.CreateDirectory(divisionDir);
        try
        {
            using var duck = new DuckDBConnection("Data Source=:memory:");
            duck.Open();
            using var query = duck.CreateCommand();
            var areaPath = Path.Combine(areaDir, "data.parquet").Replace('\\', '/');
            var divisionPath = Path.Combine(divisionDir, "data.parquet").Replace('\\', '/');
            query.CommandText = $$"""
                COPY (SELECT 'area' AS id, 'CH' AS country, 'city' AS division_id
                    UNION ALL SELECT 'missing', 'CH', 'missing'
                    UNION ALL SELECT 'other-country', 'FR', 'other') TO '{{areaPath}}' (FORMAT PARQUET);
                COPY (SELECT 'city' AS id, 'CH' AS country,
                    {'primary': NULL::VARCHAR, 'common': MAP {'en': NULL::VARCHAR} } AS names,
                    [[{'division_id': 'city', 'name': 'Default', 'subtype': 'locality'}],
                     [{'division_id': 'city', 'name': 'Alternative', 'subtype': 'locality'}]] AS hierarchies)
                    TO '{{divisionPath}}' (FORMAT PARQUET);
                """;
            query.ExecuteNonQuery();
            query.CommandText = $"SELECT id, hierarchy_json FROM {OvertureDivisionsLogic.BuildDivisionAreaSource(areaPath, "CH")} ORDER BY id";
            using var reader = query.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("area", reader.GetString(0));
            StringAssert.Contains(reader.GetString(1), "Default");
            Assert.IsFalse(reader.GetString(1).Contains("Alternative", StringComparison.Ordinal));
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("missing", reader.GetString(0));
            Assert.IsTrue(reader.IsDBNull(1));
            Assert.IsFalse(reader.Read());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static OvertureDivisionCandidateDiagnostic WithHierarchy(OvertureDivisionCandidateDiagnostic area, string owner, string? city, string state)
    {
        var hierarchy = new List<object>
        {
            new { division_id = "country", name = "Country", subtype = "country" },
            new { division_id = state, name = "Parent State", subtype = "region" }
        };
        if (city is not null)
        {
            hierarchy.Add(new { division_id = city, name = "Parent City", subtype = "locality" });
        }

        hierarchy.Add(new { division_id = owner, name = area.Name, subtype = area.SubType });
        return area with { DivisionId = owner, HierarchyJson = System.Text.Json.JsonSerializer.Serialize(hierarchy) };
    }

    [TestMethod]
    [DataRow("FR", "CP")]
    [DataRow("BQ", "XE")]
    [DataRow("BQ", "XS")]
    [DataRow("SJ", "XJ")]
    public void DivisionJoin_IncludesTerritoryAliasAreasAndTheirHierarchy(string country, string alias)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var areaDir = Path.Combine(root, "type=division_area");
        var divisionDir = Path.Combine(root, "type=division");
        Directory.CreateDirectory(areaDir);
        Directory.CreateDirectory(divisionDir);
        try
        {
            using var duck = new DuckDBConnection("Data Source=:memory:");
            duck.Open();
            using var query = duck.CreateCommand();
            var areaPath = Path.Combine(areaDir, "data.parquet").Replace('\\', '/');
            var divisionPath = Path.Combine(divisionDir, "data.parquet").Replace('\\', '/');
            query.CommandText = $$"""
                COPY (SELECT 'alias-area' AS id, '{{alias}}' AS country, 'alias-city' AS division_id
                    UNION ALL SELECT 'unrelated-area', 'ZZ', 'unrelated-city') TO '{{areaPath}}' (FORMAT PARQUET);
                COPY (SELECT 'alias-city' AS id, '{{alias}}' AS country,
                    {'primary': NULL::VARCHAR, 'common': MAP {'en': NULL::VARCHAR} } AS names,
                    [[{'division_id': 'alias-city', 'name': 'Alias City', 'subtype': 'locality'}]] AS hierarchies)
                    TO '{{divisionPath}}' (FORMAT PARQUET);
                """;
            query.ExecuteNonQuery();
            query.CommandText = $"SELECT id, hierarchy_json FROM {OvertureDivisionsLogic.BuildDivisionAreaSource(areaPath, country)}";
            using var reader = query.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual("alias-area", reader.GetString(0));
            Assert.IsFalse(reader.IsDBNull(1), "The owner's hierarchy must survive the alias filter on the joined division dataset.");
            StringAssert.Contains(reader.GetString(1), "Alias City");
            Assert.IsFalse(reader.Read());

            var liveSql = OvertureDivisionsLogic.BuildDivisionAreaQuery(0, 0, country,
                "az://example.test/theme=divisions/type=division_area/*.parquet");
            StringAssert.Contains(liveSql, $"AND {OvertureCountrySourceCodes.BuildCountryFilter(country)}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

using DuckDB.NET.Data;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public sealed class PlacesTaxonomyCompatibilityTests
{
    [TestMethod]
    [DataRow("anglican_or_episcopal_place_of_worship")]
    [DataRow("baptist_place_of_worship")]
    [DataRow("roman_catholic_place_of_worship")]
    [DataRow("christian_place_of_worship")]
    [DataRow("protestant_place_of_worship")]
    [DataRow("pentecostal_place_of_worship")]
    [DataRow("buddhist_place_of_worship")]
    [DataRow("hindu_place_of_worship")]
    [DataRow("muslim_place_of_worship")]
    [DataRow("sikh_place_of_worship")]
    [DataRow("jewish_place_of_worship")]
    [DataRow("place_of_worship")]
    [DataRow("performing_arts_venue")]
    [DataRow("government_office")]
    [DataRow("school_district_office")]
    public void RenamedUsefulCategories_WorkAtPrimaryAndBasicLevels(string category)
    {
        var place = Place("primary", category, category);
        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(place), category);
        Assert.IsTrue(OverturePlacesLogic.IsInterestingPhotoPlace(place with { Category = "unknown" }), category);
        var result = OverturePlacesLogic.BuildPlaceDiagnostics([place], "2026-09-23.1", "CH");
        Assert.AreEqual(category, result.BestMatch?.Category, "Filtering aliases must not replace the raw category displayed in Lookup.");
    }

    [TestMethod]
    [DataRow("shopping")]
    [DataRow("non_alcoholic_beverage_venue")]
    [DataRow("reproductive_perinatal_and_womens_care")]
    [DataRow("b2b_storage")]
    [DataRow("b2b_storage_and_warehouses")]
    [DataRow("bar")]
    [DataRow("office")]
    public void RoutineBusinesses_StillOverrideUsefulBasicCategories(string category)
    {
        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(Place("routine", category, "museum")));
    }

    [TestMethod]
    public void BroadAncestors_DoNotMakeUnrelatedCategoriesEligible()
    {
        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(Place("broad", "unknown", "arts_and_entertainment")));
        Assert.IsFalse(OverturePlacesLogic.IsInterestingPhotoPlace(Place("broad", "arts_and_entertainment", "unknown")));
    }

    [TestMethod]
    public void ProductionParquetQuery_AgreesWithClientFilteringForRenamesAndV2Sources()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var duck = new DuckDBConnection("Data Source=:memory:");
            duck.Open();
            using var command = duck.CreateCommand();
            var path = Path.Combine(root, "places.parquet").Replace('\\', '/');
            command.CommandText = $$"""
                CREATE MACRO ST_X(p) AS p.x;
                CREATE MACRO ST_Y(p) AS p.y;
                CREATE TABLE fixture(id VARCHAR, category VARCHAR, basic VARCHAR, confidence DOUBLE, status VARCHAR);
                INSERT INTO fixture VALUES
                    ('mosque', 'muslim_place_of_worship', 'muslim_place_of_worship', 0.98, 'open'),
                    ('church', 'roman_catholic_place_of_worship', 'christian_place_of_worship', 0.98, 'active'),
                    ('performance', 'performing_arts_venue', 'performing_arts_venue', 0.98, NULL),
                    ('government', 'government_office', 'government_office', 0.98, 'open'),
                    ('school', 'school_district_office', 'government_office', 0.98, 'open'),
                    ('fallback', NULL, 'muslim_place_of_worship', 0.98, 'open'),
                    ('shopping', 'shopping', 'museum', 0.98, 'open'),
                    ('clinic', 'reproductive_perinatal_and_womens_care', 'museum', 0.98, 'open'),
                    ('storage', 'b2b_storage', 'museum', 0.98, 'open'),
                    ('beverage', 'non_alcoholic_beverage_venue', 'museum', 0.98, 'open'),
                    ('bar', 'bar', 'performing_arts_venue', 0.98, 'open'),
                    ('broad', 'unknown', 'arts_and_entertainment', 0.98, 'open'),
                    ('closed', 'muslim_place_of_worship', 'muslim_place_of_worship', 0.98, 'permanently_closed'),
                    ('low', 'muslim_place_of_worship', 'muslim_place_of_worship', 0.89, 'open'),
                    ('unknown', 'muslim_place_of_worship', 'muslim_place_of_worship', NULL, 'open');
                COPY (SELECT id, {'primary': id, 'common': map(['en'], [id])} AS names,
                    {'primary': category} AS taxonomy, basic AS basic_category, confidence,
                    {'x': 0.001, 'y': 0.0} AS geometry,
                    {'xmin': 0.001, 'xmax': 0.001, 'ymin': 0.0, 'ymax': 0.0} AS bbox,
                    [{'country': 'CH'}] AS addresses, status AS operating_status,
                    [{'provider': 'fixture', 'resource': 'places', 'version': '2026-09-23.1'}] AS sources
                    FROM fixture) TO '{{path}}' (FORMAT PARQUET);
                """;
            command.ExecuteNonQuery();
            command.CommandText = OverturePlacesLogic.BuildQuery(0, 0, -1, 1, -1, 1, "CH", path);
            var count = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var place = Place(id, reader.GetString(2), reader.GetString(3)) with
                {
                    Confidence = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    ConfidenceKnown = !reader.IsDBNull(4),
                    OperatingStatus = reader.IsDBNull(7) ? null : reader.GetString(7),
                    DistanceMetres = reader.GetDouble(10),
                    Sources = OverturePlacesLogic.ParseSources(reader.GetString(9))
                };
                var expected = id is "mosque" or "church" or "performance" or "government" or "school" or "fallback";
                Assert.AreEqual(expected, OverturePlacesLogic.IsInterestingPhotoPlace(place), id);
                Assert.AreEqual(OverturePlacesLogic.GetExclusionReason(place), reader.IsDBNull(12) ? null : reader.GetString(12), id);
                CollectionAssert.AreEqual(new[] { "fixture/places" }, place.Sources.ToArray());
                if (id == "mosque")
                {
                    Assert.AreEqual("muslim_place_of_worship", place.Category);
                }
                count++;
            }
            Assert.AreEqual(15, count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InfrastructureSourceProjection_ProducesParseableJsonForProviderResourceSources()
    {
        using var duck = new DuckDBConnection("Data Source=:memory:");
        duck.Open();
        using var command = duck.CreateCommand();
        var query = OverturePlacesLogic.BuildInfrastructureQuery(0, 0, -1, 1, -1, 1, "unused");
        var projection = query.Split('\n').Single(line => line.Contains("AS sources_json", StringComparison.Ordinal)).Trim().TrimEnd(',');
        command.CommandText = $"SELECT {projection} FROM (SELECT [{{'provider': 'fixture', 'resource': 'infrastructure', 'version': '2026-09-23.1'}}] AS sources)";
        CollectionAssert.AreEqual(new[] { "fixture/infrastructure" }, OverturePlacesLogic.ParseSources((string)command.ExecuteScalar()!).ToArray());
    }

    private static OverturePlaceResult Place(string id, string? category, string? basic) =>
        new(id, id, category, basic, 0.98, "open", 100, false, []);
}

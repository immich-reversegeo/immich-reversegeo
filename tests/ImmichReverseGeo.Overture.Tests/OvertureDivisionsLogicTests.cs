using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Core.Models;
using DuckDB.NET.Data;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureDivisionsLogicTests
{
    [TestMethod]
    [DataRow("Business Bay", "الخليج التجاري", false, "Business Bay")]
    [DataRow(null, "الخليج التجاري", false, "الخليج التجاري")]
    [DataRow("  ", "الخليج التجاري", false, "الخليج التجاري")]
    [DataRow(null, null, false, "الخليج التجاري")]
    [DataRow("Business Bay", "الخليج التجاري", true, "الخليج التجاري")]
    public void BuildDivisionAreaSource_LocalizesHierarchyAndPreservesFallbacks(
        string? english, string? primary, bool missingParent, string expectedCity)
    {
        var directory = Path.Combine(Path.GetTempPath(), "reversegeo-hierarchy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "type=division_area"));
        Directory.CreateDirectory(Path.Combine(directory, "type=division"));
        try
        {
            using var connection = new DuckDBConnection("DataSource=:memory:");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE divisions AS
                SELECT 'area' AS id, 'AE' AS country,
                    {'primary': 'الخليج التجاري', 'common': MAP {'en': 'Business Bay'}} AS names,
                    [[{'division_id': 'country', 'subtype': 'country', 'name': 'الإمارات العربية المتحدة'},
                      {'division_id': 'region', 'subtype': 'region', 'name': 'دبي'},
                      {'division_id': 'city', 'subtype': 'locality', 'name': 'الخليج التجاري'},
                      {'division_id': 'area', 'subtype': 'macrohood', 'name': 'الخليج التجاري'}]] AS hierarchies;
                INSERT INTO divisions VALUES
                    ('country', 'AE', {'primary': 'الإمارات العربية المتحدة', 'common': MAP {'en': 'United Arab Emirates'}}, NULL),
                    ('region', 'AE', {'primary': 'دبي', 'common': MAP {'en': 'Dubai Emirate'}}, NULL);
                """;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO divisions VALUES ('city', 'AE', {'primary': $primary, 'common': MAP {'en': $english}}, NULL)";
            command.Parameters.Add(new DuckDBParameter("primary", (object?)primary ?? DBNull.Value));
            command.Parameters.Add(new DuckDBParameter("english", (object?)english ?? DBNull.Value));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            if (missingParent)
            {
                command.CommandText = "DELETE FROM divisions WHERE id='city'";
                command.ExecuteNonQuery();
            }

            var root = directory.Replace('\\', '/');
            command.CommandText = $"""
                COPY divisions TO '{root}/type=division/records.parquet' (FORMAT PARQUET);
                COPY (SELECT id, country, names, 'area' AS division_id FROM divisions WHERE id='area')
                    TO '{root}/type=division_area/records.parquet' (FORMAT PARQUET);
                """;
            command.ExecuteNonQuery();
            command.CommandText = $"SELECT hierarchy_json FROM {OvertureDivisionsLogic.BuildDivisionAreaSource(root + "/type=division_area/*.parquet", "AE")}";
            var hierarchy = command.ExecuteScalar()?.ToString();
            var candidate = CreateDiagnostic("macrohood", "Business Bay", 0.01) with
            {
                Country = "AE",
                DivisionId = "area",
                HierarchyJson = hierarchy
            };
            var ancestors = OvertureDivisionsLogic.ParseHierarchy(candidate);
            CollectionAssert.AreEqual(new[] { "country", "region", "city", "area" }, ancestors.Select(a => a.DivisionId).ToArray());
            var result = OvertureDivisionsLogic.SelectAdministrativeAreas([candidate]);
            Assert.AreEqual(expectedCity, result.City);
            Assert.AreEqual("Dubai Emirate", result.State);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void BuildDivisionAreaQuery_WithCountryFilter_EmbedsAlpha2AndDivisionPath()
    {
        var sql = OvertureDivisionsLogic.BuildDivisionAreaQuery(
            lat: 47.4513,
            lon: 8.5574,
            alpha2: "CH",
            releaseUrl: "az://example.test/release/theme=divisions/type=division_area/*.parquet");

        StringAssert.Contains(sql, "read_parquet('az://example.test/release/theme=divisions/type=division_area/*.parquet'");
        StringAssert.Contains(sql, "lower(country) IN ('ch')");
        StringAssert.Contains(sql, "ST_Intersects(geometry, ST_Point(8.5574, 47.4513))");
    }

    [TestMethod]
    public void BuildDivisionAreaQuery_WithoutCountryFilter_DoesNotEmbedCountryClause()
    {
        var sql = OvertureDivisionsLogic.BuildDivisionAreaQuery(
            lat: 47.4513,
            lon: 8.5574,
            alpha2: null,
            releaseUrl: "az://example.test/release/theme=divisions/type=division_area/*.parquet");

        Assert.IsFalse(sql.Contains("lower(country)", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersGeometryContainment()
    {
        var geometry = CreateCandidate("locality", geometryContainsPoint: true, bboxArea: 0.50);
        var bboxOnly = CreateCandidate("microhood", geometryContainsPoint: false, bboxArea: 0.01);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(geometry, bboxOnly));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(bboxOnly, geometry));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersMoreSpecificSubtypeWhenContainmentMatches()
    {
        var neighborhood = CreateCandidate("neighborhood", geometryContainsPoint: true, bboxArea: 0.20);
        var locality = CreateCandidate("locality", geometryContainsPoint: true, bboxArea: 0.05);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(neighborhood, locality));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(locality, neighborhood));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersTerritorialBeforeAreaTieBreak()
    {
        var nonTerritorial = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.50, isTerritorial: false);
        var territorial = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.01, isTerritorial: true);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(territorial, nonTerritorial));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(nonTerritorial, territorial));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersSmallerAreaAsFinalTieBreak()
    {
        var tighter = CreateCandidate("county", geometryContainsPoint: false, bboxArea: 0.02);
        var broader = CreateCandidate("county", geometryContainsPoint: false, bboxArea: 0.20);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(tighter, broader));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(broader, tighter));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersLowerAdminLevelWithinSameSubtypePool()
    {
        var lower = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.20, adminLevel: 1);
        var higher = CreateCandidate("region", geometryContainsPoint: true, bboxArea: 0.10, adminLevel: 2);

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(lower, higher));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(higher, lower));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_PrefersDependencyBeforeSovereignCountry()
    {
        var dependency = CreateCandidate("dependency", true, 0.50, country: "HK");
        var country = CreateCandidate("country", true, 0.01, country: "CN");

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(dependency, country));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(country, dependency));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_DoesNotGiveDependencyPriorityInAdministrativeLookup()
    {
        var region = CreateCandidate("region", true, 0.50, country: "HK");
        var dependency = CreateCandidate("dependency", true, 0.01, country: "HK");

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(region, dependency));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(dependency, region));
    }

    [TestMethod]
    [DataRow("HK", "CN")]
    [DataRow("GL", "DK")]
    [DataRow("PR", "US")]
    [DataRow("AW", "NL")]
    [DataRow("RE", "FR")]
    public void ShouldPreferDivisionCandidate_ReversedInputOrderProducesSameWinner(
        string dependencyAlpha2,
        string sovereignAlpha2)
    {
        var dependency = CreateCandidate("dependency", true, 0.50, id: "dependency", country: dependencyAlpha2);
        var country = CreateCandidate("country", true, 0.01, id: "country", country: sovereignAlpha2);

        var forward = SelectBestBundledCountry([country, dependency]);
        var reverse = SelectBestBundledCountry([dependency, country]);

        Assert.AreEqual("dependency", forward.Id);
        Assert.AreEqual(forward.Id, reverse.Id);
    }

    [TestMethod]
    public void SelectStateName_PrefersRegionOverCounty()
    {
        var result = OvertureDivisionsLogic.SelectStateName(
        [
            CreateDiagnostic("county", "Zurich District", bboxArea: 0.05),
            CreateDiagnostic("region", "Canton of Zurich", bboxArea: 0.20)
        ]);

        Assert.AreEqual("Canton of Zurich", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersLocalityOverNeighborhood()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("neighborhood", "Seefeld", bboxArea: 0.01),
            CreateDiagnostic("locality", "Zurich", bboxArea: 0.20)
        ]);

        Assert.AreEqual("Zurich", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersGeometryMatchesWithinSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Zurich", bboxArea: 0.20, geometryContainsPoint: false),
            CreateDiagnostic("locality", "Zurich-Flughafen", bboxArea: 0.05, geometryContainsPoint: true)
        ]);

        Assert.AreEqual("Zurich-Flughafen", result);
    }

    [TestMethod]
    public void SelectCityName_PrefersTerritorialWithinSameSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Water Label", bboxArea: 0.01, geometryContainsPoint: true, isTerritorial: false),
            CreateDiagnostic("locality", "Real Administrative Area", bboxArea: 0.02, geometryContainsPoint: true, isTerritorial: true)
        ]);

        Assert.AreEqual("Real Administrative Area", result);
    }

    [TestMethod]
    public void SelectCityName_UsesConfiguredSubtypeOrder()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Chassieu", bboxArea: 0.00217),
            CreateDiagnostic("localadmin", "Lyon", bboxArea: 0.39414)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["localadmin", "locality"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Lyon", result);
    }

    [TestMethod]
    public void SelectCityName_UsesLargestAreaTieBreakWithinSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Armfelt", bboxArea: 0.000239),
            CreateDiagnostic("locality", "Salo", bboxArea: 0.60364)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["locality", "localadmin"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Salo", result);
    }

    [TestMethod]
    public void SelectCityName_UsesConfiguredCountyPreference()
    {
        var result = OvertureDivisionsLogic.SelectCityName(
        [
            CreateDiagnostic("locality", "Altstadt-Lehel", bboxArea: 0.000856),
            CreateDiagnostic("county", "Munich", bboxArea: 0.067538, adminLevel: 2)
        ],
        new CityResolverProfile
        {
            PreferredSubtypes = ["county", "locality", "localadmin"],
            TieBreakMode = CityResolverTieBreakModes.LargestArea
        });

        Assert.AreEqual("Munich", result);
    }

    [TestMethod]
    public void SelectStateName_PrefersLowerAdminLevelWithinSameSubtypePool()
    {
        var result = OvertureDivisionsLogic.SelectStateName(
        [
            CreateDiagnostic("region", "Lower Priority Region", bboxArea: 0.01, adminLevel: 2),
            CreateDiagnostic("region", "Preferred Region", bboxArea: 0.20, adminLevel: 1)
        ]);

        Assert.AreEqual("Preferred Region", result);
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_UsesStableIdAsFinalTieBreak()
    {
        var first = CreateCandidate("dependency", true, 0.10, id: "a");
        var second = CreateCandidate("dependency", true, 0.10, id: "b");

        Assert.IsTrue(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(first, second));
        Assert.IsFalse(OvertureDivisionsLogic.ShouldPreferDivisionCandidate(second, first));
    }

    [TestMethod]
    public void ShouldPreferDivisionCandidate_AllNullableAdminLevelPermutationsProduceSameWinner()
    {
        var first = CreateCandidate("region", true, 100, adminLevel: 1, id: "first");
        var missing = CreateCandidate("region", true, 1, adminLevel: null, id: "missing");
        var second = CreateCandidate("region", true, 0.5, adminLevel: 2, id: "second");
        OvertureDivisionResult[][] permutations =
        [
            [first, missing, second],
            [first, second, missing],
            [missing, first, second],
            [missing, second, first],
            [second, first, missing],
            [second, missing, first]
        ];

        foreach (var permutation in permutations)
        {
            Assert.AreEqual("first", SelectBest(permutation).Id);
        }
    }

    private static OvertureDivisionResult SelectBest(IEnumerable<OvertureDivisionResult> candidates)
    {
        return candidates.Aggregate(
            (OvertureDivisionResult?)null,
            (current, candidate) => current is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, current)
                ? candidate
                : current)!;
    }

    private static OvertureDivisionResult SelectBestBundledCountry(IEnumerable<OvertureDivisionResult> candidates)
    {
        return candidates.Aggregate(
            (OvertureDivisionResult?)null,
            (current, candidate) => current is null || OvertureDivisionsLogic.ShouldPreferDivisionCandidate(candidate, current)
                ? candidate
                : current)!;
    }

    private static OvertureDivisionResult CreateCandidate(
        string subtype,
        bool geometryContainsPoint,
        double bboxArea,
        bool isTerritorial = false,
        int? adminLevel = null,
        string? id = null,
        string country = "CH") =>
        new(
            Id: id ?? Guid.NewGuid().ToString("N"),
            Name: "Candidate",
            SubType: subtype,
            ClassName: "land",
            AdminLevel: adminLevel,
            Country: country,
            Alpha3: null,
            IsLand: true,
            IsTerritorial: isTerritorial,
            BoundingBoxContainsPoint: true,
            GeometryContainsPoint: geometryContainsPoint,
            BoundingBoxArea: bboxArea);

    private static OvertureDivisionCandidateDiagnostic CreateDiagnostic(
        string subtype,
        string name,
        double bboxArea,
        bool geometryContainsPoint = true,
        bool isTerritorial = false,
        int? adminLevel = null) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Name: name,
            SubType: subtype,
            ClassName: "land",
            AdminLevel: adminLevel,
            Country: "CH",
            IsLand: true,
            IsTerritorial: isTerritorial,
            BoundingBoxContainsPoint: true,
            GeometryContainsPoint: geometryContainsPoint,
            BoundingBoxArea: bboxArea,
            Selected: false,
            Decision: "test");
}

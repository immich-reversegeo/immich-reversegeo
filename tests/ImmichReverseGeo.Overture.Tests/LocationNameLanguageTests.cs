using System.Text.Json;
using DuckDB.NET.Data;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class LocationNameLanguageTests
{
    private const string Names = """{"primary":"München","common":{"en":"Munich","de":"München","fr":"Munich français","zh-Hant":"慕尼黑"}}""";

    [TestMethod]
    [DataRow("english", "en", "Munich")]
    [DataRow("local", "en", "München")]
    [DataRow("preferred", "de", "München")]
    [DataRow("preferred", "FR-ca", "Munich français")]
    [DataRow("preferred", "zh-Hant-TW", "慕尼黑")]
    [DataRow("preferred", "ja", "München")]
    public void SelectName_UsesRequestedLanguageThenExplicitFallbacks(string mode, string language, string expected)
    {
        Assert.AreEqual(expected, OvertureNameLocalizer.SelectName(Names, "old label",
            new LocationNameOptions { Mode = mode, PreferredLanguage = language }));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("invalid")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("42")]
    [DataRow("{}")]
    [DataRow("{\"primary\":\"  \",\"common\":{\"en\":null}}")]
    public void SelectName_MissingOrMalformedNamesRetainExistingLabel(string? json)
    {
        Assert.AreEqual("old label", OvertureNameLocalizer.SelectName(json, "old label"));
    }

    [TestMethod]
    public void SelectName_BlankTranslationFallsBackToPrimary_AndMissingPrimaryToEnglish()
    {
        var options = new LocationNameOptions { Mode = LocationNameOptions.Preferred, PreferredLanguage = "de" };
        Assert.AreEqual("Local", OvertureNameLocalizer.SelectName(
            """{"primary":"Local","common":{"de":"  ","en":"English"}}""", "old", options));
        Assert.AreEqual("English", OvertureNameLocalizer.SelectName(
            """{"primary":null,"common":{"en":"English"}}""", "old", options));
    }

    [TestMethod]
    public void CountryNames_PreserveTerritoryIdentityInsteadOfBorrowingSovereign()
    {
        var candidate = Area("hood", "Neighborhood", "neighborhood") with
        {
            Country = "HK", DivisionId = "hood",
            HierarchyJson = """[{"division_id":"cn","subtype":"country","name":"China","country":"CN"},{"division_id":"hk","subtype":"dependency","name":"香港","country":"HK"},{"division_id":"hood","subtype":"neighborhood","name":"Neighborhood","country":"HK"}]"""
        };
        var diagnostics = new OvertureDivisionLookupDiagnostics(null, [candidate], "test");
        var options = new LocationNameOptions { Mode = LocationNameOptions.Local };
        Assert.AreEqual("香港", OvertureNameLocalizer.SelectCountryName(diagnostics, "HK", "Hong Kong", options));
        Assert.AreEqual("Macao", OvertureNameLocalizer.SelectCountryName(diagnostics, "MO", "Macao", options));
        Assert.AreEqual("Hong Kong", OvertureNameLocalizer.SelectCountryName(diagnostics, "HK", "Hong Kong", new()));
        Assert.AreEqual("Hong Kong", OvertureNameLocalizer.SelectCountryName(diagnostics, "HK", "Hong Kong",
            new() { Mode = "unknown" }));
    }

    [TestMethod]
    public async Task CachedLookup_SwitchesLanguagesWithoutChangingCacheOrGeometry_AndLocalizesPromotedParents()
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-language-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        var path = Path.Combine(root, "overture-divisions", "DEU.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=false"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE division_area (id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                        country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL,
                        division_id TEXT, hierarchy_json TEXT, names_json TEXT);
                    CREATE TABLE _meta (key TEXT, value TEXT);
                    INSERT INTO _meta VALUES ('release','test');
                    INSERT INTO division_area VALUES ('hood-area','English neighborhood','neighborhood',NULL,NULL,
                        'DE',1,1,$geom,0,0,2,2,'hood',$hierarchy,$names);
                    """;
                command.Parameters.AddWithValue("$geom", new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0,2 0,2 2,0 2,0 0))")));
                command.Parameters.AddWithValue("$names", """{"primary":"Viertel","common":{"en":"English neighborhood","fr":"Quartier"}}""");
                command.Parameters.AddWithValue("$hierarchy", JsonSerializer.Serialize(new[]
                {
                    Ancestor("country", "country", "Germany", "Deutschland", "Allemagne"),
                    Ancestor("state", "region", "Bavaria", "Bayern", "Bavière"),
                    Ancestor("city", "locality", "Munich", "München", "Munich français"),
                    Ancestor("hood", "neighborhood", "English neighborhood", "Viertel", "Quartier")
                }));
                command.ExecuteNonQuery();
            }
            var before = File.ReadAllBytes(path);
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "DEU");
            foreach (var (options, state, city, country, neighborhood) in new[]
            {
                (new LocationNameOptions(), "Bavaria", "Munich", (string?)null, "English neighborhood"),
                (new LocationNameOptions { Mode = "local" }, "Bayern", "München", "Deutschland", "Viertel"),
                (new LocationNameOptions { Mode = "preferred", PreferredLanguage = "fr" }, "Bavière", "Munich français", "Allemagne", "Quartier")
            })
            {
                var diagnostics = await service.FindContainingDivisionAreasAsync(1, 1, "DE", "DEU", locationNames: options);
                Assert.IsNull(diagnostics.Error);
                Assert.AreEqual(neighborhood, diagnostics.BestMatch!.Name);
                Assert.IsTrue(diagnostics.Candidates.Single().IsExactGeometryMatch);
                var result = await service.ResolveAdministrativeGeoAsync(1, 1, "DE", "DEU", locationNames: options);
                Assert.AreEqual(state, result!.State);
                Assert.AreEqual(city, result.City);
                Assert.AreEqual(country, result.Country);
            }
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DivisionSource_RetainsAllCommonNamesAndCountryIdentityForHierarchy()
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-names-parquet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "type=division"));
        Directory.CreateDirectory(Path.Combine(root, "type=division_area"));
        try
        {
            using var duck = new DuckDBConnection("DataSource=:memory:");
            duck.Open();
            using var command = duck.CreateCommand();
            var directory = root.Replace('\\', '/');
            command.CommandText = $$$"""
                CREATE TABLE divisions AS SELECT 'city' AS id, 'DE' AS country,
                    {'primary':'München','common': MAP {'en':'Munich','de':'München','fr':'Munich français'}} AS names,
                    [[{'division_id':'city','subtype':'locality','name':'München'}]] AS hierarchies;
                COPY divisions TO '{{{directory}}}/type=division/rows.parquet' (FORMAT PARQUET);
                COPY (SELECT id, country, names, id AS division_id FROM divisions)
                    TO '{{{directory}}}/type=division_area/rows.parquet' (FORMAT PARQUET);
                """;
            command.ExecuteNonQuery();
            command.CommandText = $"SELECT hierarchy_json FROM {OvertureDivisionsLogic.BuildDivisionAreaSource(directory + "/type=division_area/*.parquet", "DE")}";
            using var hierarchy = JsonDocument.Parse((string)command.ExecuteScalar()!);
            var ancestor = hierarchy.RootElement[0];
            Assert.AreEqual("DE", ancestor.GetProperty("country").GetString());
            Assert.AreEqual("München", ancestor.GetProperty("names").GetProperty("primary").GetString());
            Assert.AreEqual("Munich français", ancestor.GetProperty("names").GetProperty("common").GetProperty("fr").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static object Ancestor(string id, string subtype, string english, string primary, string french) => new
    {
        division_id = id, subtype, name = english, country = "DE",
        names = new { primary, common = new Dictionary<string, string> { ["en"] = english, ["fr"] = french } }
    };

    private static OvertureDivisionCandidateDiagnostic Area(string id, string name, string subtype) =>
        new(id, name, subtype, null, null, "DE", true, true, true, true, 1, true, "test");
}

using System.Text.Json;
using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Overture.Models;
using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class OvertureBoundaryTests
{
    [TestMethod]
    [DataRow(0, CityResolverTieBreakModes.SmallestArea, false)]
    [DataRow(0, CityResolverTieBreakModes.SmallestArea, true)]
    [DataRow(0, CityResolverTieBreakModes.LargestArea, false)]
    [DataRow(1, CityResolverTieBreakModes.SmallestArea, false)]
    [DataRow(1, CityResolverTieBreakModes.SmallestArea, true)]
    [DataRow(1, CityResolverTieBreakModes.LargestArea, false)]
    public async Task CachedSwissCoordinates_PreferExactMunicipalityAndItsState(int index, string tieBreak, bool reverse)
    {
        var fixture = LoadFixture(index);
        await WithCacheAsync(fixture, reverse ? fixture.Areas.Reverse() : fixture.Areas, async service =>
        {
            var diagnostics = await service.FindContainingDivisionAreasAsync(fixture.Lat, fixture.Lon, "CH", "CHE");
            Assert.IsNull(diagnostics.Error);
            foreach (var area in fixture.Areas)
            {
                var candidate = diagnostics.Candidates.Single(c => c.Id == area.Id);
                Assert.AreEqual(area.Exact, candidate.IsExactGeometryMatch, area.Name);
                Assert.AreEqual(area.Tolerant, candidate.GeometryContainsPoint, area.Name);
            }

            Assert.AreEqual(fixture.Expected, diagnostics.BestMatch?.Name);
            var result = await service.ResolveAdministrativeGeoAsync(fixture.Lat, fixture.Lon, "CH", "CHE",
                new CityResolverProfile { TieBreakMode = tieBreak });
            Assert.AreEqual(fixture.Expected, result?.City);
            Assert.AreEqual(fixture.State, result?.State);
        });
    }

    [TestMethod]
    [DataRow(0, "Eppenberg-Wöschnau")]
    [DataRow(1, "Fislisbach")]
    public async Task CachedSwissCoordinates_KeepToleranceFallbackWhenExactCityIsAbsent(int index, string expected)
    {
        var fixture = LoadFixture(index);
        await WithCacheAsync(fixture, fixture.Areas.Where(a => !(a.Subtype == "localadmin" && a.Exact)), async service =>
        {
            var result = await service.ResolveAdministrativeGeoAsync(fixture.Lat, fixture.Lon, "CH", "CHE");
            Assert.AreEqual(expected, result?.City);
        });
    }

    [TestMethod]
    [DataRow(false, "Exact municipality")]
    [DataRow(true, "Exact parent city")]
    public void HierarchyPromotion_PrefersExactMatchOverToleranceOnlyPreferredSubtype(bool exactParent, string expected)
    {
        var municipality = Area("Exact municipality", "localadmin", !exactParent);
        var neighborhood = Area("Neighborhood", "neighborhood", exactParent) with
        {
            DivisionId = "hood",
            HierarchyJson = """[{"division_id":"country","name":"Country","subtype":"country"},{"division_id":"city","name":"Exact parent city","subtype":"locality"},{"division_id":"hood","name":"Neighborhood","subtype":"neighborhood"}]"""
        };
        var result = OvertureDivisionsLogic.SelectAdministrativeAreas([municipality, neighborhood]);
        Assert.AreEqual(expected, result.City);
    }

    private static OvertureDivisionCandidateDiagnostic Area(string name, string subtype, bool exact) =>
        new(name, name, subtype, "land", null, "CH", true, true, true, true, 1, false, "test",
            ExactGeometryContainsPoint: exact);

    private static Fixture LoadFixture(int index)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SwissBoundaryFixtures.json")));
        return json.RootElement.GetProperty("cases")[index].Deserialize<Fixture>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static async Task WithCacheAsync(Fixture fixture, IEnumerable<FixtureArea> areas, Func<OvertureDivisionsService, Task> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        try
        {
            using (var db = new SqliteConnection($"Data Source={Path.Combine(root, "overture-divisions", "CHE.db")};Pooling=false"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE division_area (id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                        country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL, division_id TEXT, hierarchy_json TEXT);
                    CREATE TABLE _meta (key TEXT, value TEXT);
                    INSERT INTO _meta VALUES ('release', '2026-09-23.1');
                    """;
                cmd.ExecuteNonQuery();
                foreach (var area in areas)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = "INSERT INTO division_area VALUES ($id,$name,$subtype,$class,$admin,'CH',$land,$territorial,$wkb,$xmin,$ymin,$xmax,$ymax,$division,$hierarchy)";
                    cmd.Parameters.AddWithValue("$id", area.Id);
                    cmd.Parameters.AddWithValue("$name", area.Name);
                    cmd.Parameters.AddWithValue("$subtype", area.Subtype);
                    cmd.Parameters.AddWithValue("$class", (object?)area.ClassName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$admin", (object?)area.AdminLevel ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$land", area.IsLand);
                    cmd.Parameters.AddWithValue("$territorial", area.IsTerritorial);
                    cmd.Parameters.AddWithValue("$wkb", Convert.FromBase64String(area.Wkb));
                    cmd.Parameters.AddWithValue("$xmin", area.Bbox[0]);
                    cmd.Parameters.AddWithValue("$ymin", area.Bbox[1]);
                    cmd.Parameters.AddWithValue("$xmax", area.Bbox[2]);
                    cmd.Parameters.AddWithValue("$ymax", area.Bbox[3]);
                    cmd.Parameters.AddWithValue("$division", (object?)area.DivisionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$hierarchy", (object?)area.HierarchyJson ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var service = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "CHE");
            await test(service);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(double Lat, double Lon, string Expected, string State, FixtureArea[] Areas);
    private sealed record FixtureArea(string Id, string Name, string Subtype, string? ClassName, int? AdminLevel,
        int IsLand, int IsTerritorial, double[] Bbox, string Wkb, string? DivisionId, string? HierarchyJson, bool Exact, bool Tolerant);
}

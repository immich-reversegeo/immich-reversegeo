using ImmichReverseGeo.Core.Models;
using ImmichReverseGeo.Gadm.Services;
using ImmichReverseGeo.Overture.Services;
using ImmichReverseGeo.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationNameResolverTests
{
    [TestMethod]
    public async Task SharedProcessingAndPreviewResolver_AppliesLanguageToCityStateAndCountry()
    {
        var root = Path.Combine(Path.GetTempPath(), "reversegeo-language-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        Directory.CreateDirectory(Path.Combine(root, "overture-divisions"));
        try
        {
            Seed(Path.Combine(root, "defaults", "overture-country-divisions.db"), bundled: true);
            Seed(Path.Combine(root, "overture-divisions", "DEU.db"), bundled: false);
            var places = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var divisions = new OvertureDivisionsService(NullLogger<OvertureDivisionsService>.Instance, places, root, root, _ => "DEU");
            var admin = new AdministrativeAreaResolverService(NullLogger<AdministrativeAreaResolverService>.Instance,
                new CityResolverProfileCatalogService(NullLogger<CityResolverProfileCatalogService>.Instance, new(root, root)),
                divisions, new FixtureCache(root),
                new GadmDivisionsService(NullLogger<GadmDivisionsService>.Instance, root),
                new GadmDivisionCacheService(NullLogger<GadmDivisionCacheService>.Instance, root));
            var resolver = new AssetLocationResolverService(admin, places);
            var config = new ProcessingConfig { UseAirportInfrastructure = false };
            var english = await resolver.ResolveAsync(1, 1, config);
            Assert.AreEqual(new GeoResult("Germany", "Bavaria", "Munich"), english);
            config.LocationNames.Mode = LocationNameOptions.Local;
            var local = await resolver.ResolveAsync(1, 1, config);
            Assert.AreEqual(new GeoResult("Deutschland", "Bayern", "München"), local);
            var preview = new LocationPreviewRow(new(Guid.NewGuid(), 1, 1, english!), local);
            Assert.IsTrue(preview.HasChange);
            config.LocationNames.Mode = LocationNameOptions.Preferred;
            config.LocationNames.PreferredLanguage = "fr";
            Assert.AreEqual(new GeoResult("Allemagne", "Bavière", "Munich français"), await resolver.ResolveAsync(1, 1, config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Seed(string path, bool bundled)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE division_area (id TEXT, name TEXT, subtype TEXT, class_name TEXT, admin_level INTEGER,
                country TEXT, is_land INTEGER, is_territorial INTEGER, geom_wkb BLOB,
                bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL,
                division_id TEXT, hierarchy_json TEXT, names_json TEXT, alpha3 TEXT);
            CREATE TABLE _meta (key TEXT, value TEXT);
            INSERT INTO _meta VALUES ('release','test');
            INSERT INTO _meta VALUES ('cacheFormatVersion',$version);
            INSERT INTO _meta VALUES ('downloadedAt','2026-10-15T00:00:00Z');
            INSERT INTO division_area VALUES ($id,$name,$subtype,'land',1,'DE',1,1,$geom,0,0,2,2,$id,$hierarchy,$names,'DEU');
            """;
        command.Parameters.AddWithValue("$version", OvertureDivisionCacheService.CacheFormatVersion);
        command.Parameters.AddWithValue("$id", bundled ? "country" : "city");
        command.Parameters.AddWithValue("$name", bundled ? "Germany" : "Munich");
        command.Parameters.AddWithValue("$subtype", bundled ? "country" : "locality");
        command.Parameters.AddWithValue("$geom", new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0,2 0,2 2,0 2,0 0))")));
        command.Parameters.AddWithValue("$names", """{"primary":"München","common":{"en":"Munich","fr":"Munich français"}}""");
        command.Parameters.AddWithValue("$hierarchy", """
            [{"division_id":"country","subtype":"country","country":"DE","name":"Germany","names":{"primary":"Deutschland","common":{"en":"Germany","fr":"Allemagne"}}},
             {"division_id":"state","subtype":"region","country":"DE","name":"Bavaria","names":{"primary":"Bayern","common":{"en":"Bavaria","fr":"Bavière"}}},
             {"division_id":"city","subtype":"locality","country":"DE","name":"Munich","names":{"primary":"München","common":{"en":"Munich","fr":"Munich français"}}}]
            """);
        command.ExecuteNonQuery();
    }

    private sealed class FixtureCache(string root) : OvertureDivisionCacheService(
        NullLogger<OvertureDivisionCacheService>.Instance, root, _ => "DE")
    {
        protected override string GetLatestOvertureReleaseForCache(CancellationToken ct) => "test";
    }
}

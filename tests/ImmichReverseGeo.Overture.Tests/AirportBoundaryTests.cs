using ImmichReverseGeo.Overture.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

[TestClass]
public class AirportBoundaryTests
{
    [TestMethod]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", 1, 1, true, true)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", 0, 1, true, true)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", -0.00001, 1, true, true)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", -0.00006, 1, true, false)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", -0.00003, -0.00003, true, true)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", -0.00004, -0.00004, true, false)]
    [DataRow("POLYGON ((0 74, 2 74, 2 76, 0 76, 0 74))", -0.0001, 75, true, true)]
    [DataRow("POLYGON ((0 74, 2 74, 2 76, 0 76, 0 74))", -0.0003, 75, true, false)]
    [DataRow("POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 3, 3 3, 3 1, 1 1))", 2, 2, true, false)]
    [DataRow("MULTIPOLYGON (((0 0, 2 0, 2 2, 0 2, 0 0)))", 1, 1, true, true)]
    [DataRow("POINT (1 1)", 1, 1, false, false)]
    [DataRow("LINESTRING (0 1, 2 1)", 1, 1, false, false)]
    [DataRow("POLYGON EMPTY", 1, 1, false, false)]
    [DataRow("POLYGON ((0 0, 2 2, 0 2, 2 0, 0 0))", 1, 1, false, false)]
    public void Boundary_RequiresValidAreaAndLimitedMetreTolerance(string wkt, double x, double y, bool hasBoundary, bool contains)
    {
        var bytes = new WKBWriter().Write(new WKTReader().Read(wkt));
        var result = OvertureDataAccess.ReadAirportBoundary(bytes, new Point(x, y));
        Assert.AreEqual(hasBoundary, result.HasBoundary);
        Assert.AreEqual(contains, result.ContainsPoint);
    }

    [TestMethod]
    [DataRow("POINT (1 1)", true)]
    [DataRow("POINT EMPTY", false)]
    [DataRow("LINESTRING (0 0, 1 1)", false)]
    [DataRow("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", false)]
    public void PointFallback_RequiresActualPointGeometry(string wkt, bool expected)
    {
        Assert.AreEqual(expected, OvertureDataAccess.IsAirportPointGeometry(new WKBWriter().Write(new WKTReader().Read(wkt))));
        Assert.IsFalse(OvertureDataAccess.IsAirportPointGeometry(null));
        Assert.IsFalse(OvertureDataAccess.IsAirportPointGeometry([1, 2, 3]));
    }

    [TestMethod]
    public void Boundary_MissingOrMalformedGeometryCannotMatch()
    {
        Assert.AreEqual((false, false), OvertureDataAccess.ReadAirportBoundary(null, new Point(0, 0)));
        Assert.AreEqual((false, false), OvertureDataAccess.ReadAirportBoundary([1, 2, 3], new Point(0, 0)));
    }

    [TestMethod]
    public async Task BundledLookup_ExcludesPointOnlyAndOutsideAirportsBeforeClassRanking()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "defaults"));
        try
        {
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "defaults", "overture-airports.db")};Pooling=false"))
            {
                connection.Open();
                using var ddl = connection.CreateCommand();
                ddl.CommandText = """
                    CREATE TABLE infrastructure (id TEXT, name TEXT, feature_type TEXT, subtype TEXT,
                        class_name TEXT, latitude REAL, longitude REAL, geom_wkb BLOB,
                        bbox_xmin REAL, bbox_ymin REAL, bbox_xmax REAL, bbox_ymax REAL);
                    CREATE TABLE _meta (key TEXT, value TEXT);
                    INSERT INTO _meta VALUES ('release', 'fixture');
                    """;
                ddl.ExecuteNonQuery();
                foreach (var (id, kind, wkt) in new[]
                {
                    ("inside", "private_airport", "POLYGON ((-0.01 -0.01, 0.01 -0.01, 0.01 0.01, -0.01 0.01, -0.01 -0.01))"),
                    ("outside", "international_airport", "POLYGON ((0.02 0.02, 0.03 0.02, 0.03 0.03, 0.02 0.03, 0.02 0.02))"),
                    ("point", "international_airport", "POINT (0 0)")
                })
                {
                    var geometry = new WKTReader().Read(wkt);
                    using var insert = connection.CreateCommand();
                    insert.CommandText = "INSERT INTO infrastructure VALUES ($id,$id,'infrastructure','airport',$class,0,0,$geom,$xmin,$ymin,$xmax,$ymax)";
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$class", kind);
                    insert.Parameters.AddWithValue("$geom", new WKBWriter().Write(geometry));
                    insert.Parameters.AddWithValue("$xmin", geometry.EnvelopeInternal.MinX);
                    insert.Parameters.AddWithValue("$ymin", geometry.EnvelopeInternal.MinY);
                    insert.Parameters.AddWithValue("$xmax", geometry.EnvelopeInternal.MaxX);
                    insert.Parameters.AddWithValue("$ymax", geometry.EnvelopeInternal.MaxY);
                    insert.ExecuteNonQuery();
                }
            }

            var service = new OverturePlacesService(NullLogger<OverturePlacesService>.Instance, root, root);
            var result = await service.FindNearestInfrastructureWithDiagnosticsAsync(0, 0, "USA");
            Assert.IsNull(result.Error);
            Assert.AreEqual("inside", result.BestMatch?.Id);
            Assert.AreEqual("outside boundary", result.Candidates.Single(c => c.Id == "outside").BoundaryStatus);
            Assert.AreEqual("point-only airport", result.Candidates.Single(c => c.Id == "point").BoundaryStatus);
            Assert.IsFalse(result.Candidates.Single(c => c.Id == "point").Selected);
            Assert.IsTrue(result.Candidates.Single(c => c.Id == "point").IsPointOnly);

            var miss = await service.FindNearestInfrastructureWithDiagnosticsAsync(0.04, 0.04, "USA");
            Assert.IsNull(miss.Error);
            Assert.IsNull(miss.BestMatch);
            Assert.IsTrue(miss.Candidates.Count > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

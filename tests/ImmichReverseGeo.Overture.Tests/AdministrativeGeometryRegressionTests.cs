using ImmichReverseGeo.Overture.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImmichReverseGeo.Overture.Tests;

// Adapted fixture ideas from crazz/immich-reversegeo at 31f16bbaa02513c762435af27325caa311af3c5a,
// tests/ImmichReverseGeo.Spatial.Tests/CompactAdministrativeGeometryTests.cs.
// Expected results preserve upstream's rectangular tolerance and separate exact-match flag.
[TestClass]
public sealed class AdministrativeGeometryRegressionTests
{
    private const string WithHole = "POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0), (2 2, 2 4, 4 4, 4 2, 2 2))";
    private const string Disjoint = "MULTIPOLYGON (((0 0, 2 0, 2 2, 0 2, 0 0)), ((40 40, 60 40, 60 60, 40 60, 40 40)))";

    [TestMethod]
    [DataRow(WithHole, 1d, 1d, true, true)]
    [DataRow(WithHole, 0d, 5d, true, true)]
    [DataRow(WithHole, 2d, 3d, true, true)]
    [DataRow(WithHole, 3d, 3d, false, false)]
    [DataRow(WithHole, 2.0001d, 3d, true, false)]
    [DataRow(WithHole, 2.001d, 3d, false, false)]
    [DataRow(Disjoint, 1d, 1d, true, true)]
    [DataRow(Disjoint, 50d, 50d, true, true)]
    [DataRow(Disjoint, 20d, 20d, false, false)]
    [DataRow(Disjoint, 40d, 50d, true, true)]
    [DataRow(Disjoint, 39.9999d, 50d, true, false)]
    [DataRow("POLYGON EMPTY", 0d, 0d, false, false)]
    public void HolesAndDisjointParts_PreserveContainmentAndExactness(
        string wkt, double x, double y, bool expectedMatch, bool expectedExact)
    {
        var bytes = new WKBWriter().Write(new WKTReader().Read(wkt));
        var match = OvertureDataAccess.TryGeometryContains(bytes, new Point(x, y), out var exact);
        Assert.AreEqual(expectedMatch, match, $"{wkt}: ({x}, {y}) match");
        Assert.AreEqual(expectedExact, exact, $"{wkt}: ({x}, {y}) exact");
    }

    [TestMethod]
    public void RectangularTolerance_IncludesDiagonalCornerWithoutCallingItExact()
    {
        var bytes = new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))"));
        Assert.IsTrue(OvertureDataAccess.TryGeometryContains(bytes, new Point(-0.00014, -0.00014), out var exact));
        Assert.IsFalse(exact);
        Assert.IsFalse(OvertureDataAccess.TryGeometryContains(bytes, new Point(-0.00016, -0.00014), out exact));
        Assert.IsFalse(exact);
    }

    [TestMethod]
    public void ToleranceThreshold_DistinguishesAdjacentRepresentableValues()
    {
        var bytes = new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))"));
        foreach (var distance in new[] { Math.BitDecrement(OvertureDataAccess.BoundaryToleranceDegrees), OvertureDataAccess.BoundaryToleranceDegrees })
        {
            Assert.IsTrue(OvertureDataAccess.TryGeometryContains(bytes, new Point(-distance, 5), out var exact));
            Assert.IsFalse(exact);
        }

        Assert.IsFalse(OvertureDataAccess.TryGeometryContains(bytes,
            new Point(-Math.BitIncrement(OvertureDataAccess.BoundaryToleranceDegrees), 5), out var outsideExact));
        Assert.IsFalse(outsideExact);
    }

    [TestMethod]
    [DataRow("POLYGON Z ((0 0 4, 10 0 5, 10 10 6, 0 10 7, 0 0 4))", ByteOrder.LittleEndian)]
    [DataRow("POLYGON M ((0 0 4, 10 0 5, 10 10 6, 0 10 7, 0 0 4))", ByteOrder.BigEndian)]
    [DataRow("POLYGON ZM ((0 0 4 8, 10 0 5 9, 10 10 6 10, 0 10 7 11, 0 0 4 8))", ByteOrder.LittleEndian)]
    [DataRow("POLYGON ZM ((0 0 4 8, 10 0 5 9, 10 10 6 10, 0 10 7 11, 0 0 4 8))", ByteOrder.BigEndian)]
    public void DimensionalWkbAndSrid_PreservePlanarLocationMatches(string wkt, ByteOrder order)
    {
        var geometry = new WKTReader().Read(wkt);
        geometry.SRID = 4326;
        var bytes = new WKBWriter(order, true, true, true).Write(geometry);
        Assert.IsTrue(OvertureDataAccess.TryGeometryContains(bytes, new Point(5, 5), out var insideExact));
        Assert.IsTrue(insideExact);
        Assert.IsTrue(OvertureDataAccess.TryGeometryContains(bytes, new Point(-0.0001, 5), out var nearExact));
        Assert.IsFalse(nearExact);
        Assert.IsFalse(OvertureDataAccess.TryGeometryContains(bytes, new Point(11, 11), out var outsideExact));
        Assert.IsFalse(outsideExact);
    }

    [TestMethod]
    public void MalformedWkb_DoesNotContaminateTheNextValidLookup()
    {
        Assert.IsFalse(OvertureDataAccess.TryGeometryContains([1], new Point(0, 0), out var malformedExact));
        Assert.IsFalse(malformedExact);
        var bytes = new WKBWriter().Write(new WKTReader().Read("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))"));
        Assert.IsTrue(OvertureDataAccess.TryGeometryContains(bytes, new Point(1, 1), out var validExact));
        Assert.IsTrue(validExact);
    }
}

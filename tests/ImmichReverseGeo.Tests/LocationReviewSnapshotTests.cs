using ImmichReverseGeo.Web.Services;

namespace ImmichReverseGeo.Tests;

[TestClass]
public class LocationReviewSnapshotTests
{
    [TestMethod]
    public void ClearingOrExtendingAReviewedPageInvalidatesItsConfirmation()
    {
        var scan = Guid.NewGuid();
        var first = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var next = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        var review = new LocationReviewSnapshot(scan, first, 1);
        Assert.IsTrue(review.Matches(scan, first, 1));
        Assert.IsFalse(review.Matches(scan, [], 1));
        Assert.IsFalse(review.Matches(scan, first.Concat(next), 1));
        Assert.IsFalse(review.Matches(scan, next, 1));
    }

    [TestMethod]
    public void RefreshOrDifferentScanRequiresFreshReviewEvenWithTheSameSelection()
    {
        var scan = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid() };
        var review = new LocationReviewSnapshot(scan, ids, 1);
        Assert.IsFalse(review.Matches(scan, ids, 2));
        Assert.IsFalse(review.Matches(Guid.NewGuid(), ids, 1));
        Assert.IsFalse(review.Matches(null, ids, 1));
    }

    [TestMethod]
    public void ReorderingDoesNotChangeTheReviewedSelectionButMutatingItDoes()
    {
        var scan = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var review = new LocationReviewSnapshot(scan, ids, 1);
        Assert.IsTrue(review.Matches(scan, ids.Reverse().Concat(ids), 1));
        ids[0] = Guid.NewGuid();
        Assert.IsFalse(review.Matches(scan, ids, 1));
    }
}

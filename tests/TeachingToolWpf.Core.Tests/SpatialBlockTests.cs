using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class SpatialBlockTests
{
    [Fact]
    public void LabelDensityChoosesContainingParentWithoutRemovingPreciseRois()
    {
        var editor = new EditorSession();
        var parent = editor.Add(new(new(20, 20), 25, 25, 0));
        var child = editor.AddChild(parent, new(new(20, 20), 5, 5, 0));
        var index = editor.State.Snapshot.Spatial;
        var candidate = Assert.Single(index.LabelCandidates(index.Bounds, 1, 120, 32));
        Assert.Equal(parent, candidate.Roi!.Id);
        Assert.Equal(child, index.HitTest(new(20, 20), 0)!.Id);
        Assert.Equal(2, index.Intersecting(index.Bounds).Count());
        editor.Rename(parent, "updated");
        Assert.Equal("updated", Assert.Single(editor.State.Snapshot.Spatial.LabelCandidates(index.Bounds, 1, 120, 32)).Roi!.Name);
        editor.Select([parent]);
        editor.DeleteSelection();
        Assert.Empty(editor.State.Snapshot.Spatial.LabelCandidates(index.Bounds, 1, 120, 32));
    }

    [Fact]
    public void HitPriorityMatchesExhaustiveRotatedQueriesAndPreviousSnapshot()
    {
        var random = new Random(175);
        var rois = Enumerable.Range(0, 1000).Select(i => new Roi(Guid.NewGuid(), "ROI", new(new(random.Next(1000), random.Next(1000)), random.Next(20, 300), random.Next(20, 300), random.Next(360)), null, false, i)).ToArray();
        var index = new RoiSpatialIndex(rois);
        foreach (var point in Enumerable.Range(0, 200).Select(_ => new PointD(random.Next(1000), random.Next(1000))))
        {
            var expected = rois.Where(r => r.Geometry.Contains(point, 3)).OrderBy(r => r.Geometry.Area).ThenByDescending(r => r.Order).FirstOrDefault();
            Assert.Equal(expected, index.HitTest(point, 3));
        }
        var overlapping = Enumerable.Range(0, 32940).Select(i => rois[0] with { Id = Guid.NewGuid(), Order = i }).ToArray();
        var dense = new RoiSpatialIndex(overlapping);
        var center = rois[0].Geometry.Center;
        Assert.Equal(overlapping[^1], dense.HitTest(center, 0));
        var next = dense.WithChanges([(overlapping[^1].Id, null)]);
        Assert.Equal(overlapping[^2], next.HitTest(center, 0));
        Assert.Equal(overlapping[^1], dense.HitTest(center, 0));
    }

    [Fact]
    public void UneditedBlocksKeepIdentityAndOldGeometryRemainsImmutable()
    {
        var rois = Enumerable.Range(0, 10000).Select(i => new Roi(Guid.NewGuid(), "ROI", new(new(i * 20, i % 3 * 10), 10, 5, 12), null, false, i)).ToArray();
        var before = new RoiSpatialIndex(rois);
        var bounds = before.Bounds;
        var blocks = before.Blocks(bounds).ToArray();
        var changed = rois[100] with
        {
            Geometry = rois[100].Geometry.Translate(new(1, 1))
        };
        var after = before.WithChanges([(changed.Id, changed)]);
        var next = after.Blocks(bounds).ToArray();
        Assert.Equal(rois.Length, blocks.Sum(b => b.Count));
        Assert.Equal(blocks.Length - 1, next.Count(b => blocks.Any(old => ReferenceEquals(old.Identity, b.Identity))));
        Assert.Equal(rois[100].Geometry, blocks.SelectMany(b => b.Detail(0)).Single(r => r.Roi?.Id == changed.Id).Roi!.Geometry);
        Assert.Equal(changed.Geometry, next.SelectMany(b => b.Detail(0)).Single(r => r.Roi?.Id == changed.Id).Roi!.Geometry);
        Assert.Equal(rois.Length, next.SelectMany(b => b.Detail(0)).Select(r => r.Roi!.Id).Distinct().Count());
    }

    [Fact]
    public void AnalyticalBoundsMatchRotatedCornersIncludingLargeCoordinates()
    {
        foreach (var center in new[] { new PointD(100, 100), new PointD(1e12, 1e12) })
        {
            foreach (var angle in new[] { 0d, 1, 45, 90, 180, 270, 359.9 })
            {
                var shape = new RoiGeometry(center, 123.456, 38.25, angle);
                var expected = BoundsD.FromPoints(shape.FrameCorners);
                var actual = shape.Bounds;
                Assert.InRange(Math.Abs(expected.Left - actual.Left), 0, .00025);
                Assert.InRange(Math.Abs(expected.Top - actual.Top), 0, .00025);
                Assert.InRange(Math.Abs(expected.Right - actual.Right), 0, .00025);
                Assert.InRange(Math.Abs(expected.Bottom - actual.Bottom), 0, .00025);
            }
        }
    }
}

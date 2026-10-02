using System.Collections.Immutable;
using TeachingToolWpf.Core.Editing;
using TeachingToolWpf.Core.Geometry;

namespace TeachingToolWpf.Core.Tests;

public class SpatialRefitTests
{
    [Fact] public void WholeRigidRefitMatchesRebuildAndPreservesOriginalSnapshot()
    {
        var originals = Grid(128); var before = new RoiSpatialIndex(originals.Values);
        var transform = new RigidTransform2D(new(200, 200), 37, new(1000, 500));
        var current = originals.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with
        {
            Name = $"changed {pair.Value.Order}",
            Geometry = new(transform.Apply(pair.Value.Geometry.Center), pair.Value.Geometry.Width,
                pair.Value.Geometry.Height, pair.Value.Geometry.Angle + transform.AngleDegrees)
        });
        var refitted = before.RefitGeometry(current); var rebuilt = new RoiSpatialIndex(current.Values);
        Assert.Equal(rebuilt.Bounds, refitted.Bounds);
        foreach (var roi in current.Values)
        {
            Assert.Equal(rebuilt.HitTest(roi.Geometry.Center, 0), refitted.HitTest(roi.Geometry.Center, 0));
            Assert.Equal(rebuilt.Intersecting(roi.Geometry.Bounds).OrderBy(r => r.Id), refitted.Intersecting(roi.Geometry.Bounds).OrderBy(r => r.Id));
        }
        foreach (var roi in originals.Values) Assert.Same(roi, before.HitTest(roi.Geometry.Center, 0));
        Assert.Equal(originals.Values.OrderBy(r => r.Id), before.Visible(before.Bounds, 0).Select(item => item.Roi!).OrderBy(r => r.Id));
    }

    [Fact] public void RefitRefreshesHitPriorityAreaAndLabelRepresentativeMetadata()
    {
        var originals = Enumerable.Range(0, 24).Select(index => new Roi(Guid.NewGuid(), $"old {index}",
            new(new(100, 100), 40, 40, 0), null, false, index)).ToImmutableDictionary(r => r.Id);
        var before = new RoiSpatialIndex(originals.Values);
        var current = originals.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with
        {
            Name = $"new {pair.Value.Order}", Order = -pair.Value.Order,
            Geometry = new(new(100, 100), pair.Value.Order == 5 ? 10 : pair.Value.Order == 9 ? 100 : 40, 40, 27)
        });
        var refitted = before.RefitGeometry(current);
        Assert.Equal(current.Values.Single(r => r.Order == -5), refitted.HitTest(new(100, 100), 0));
        var representative = Assert.Single(refitted.LabelCandidates(refitted.Bounds, 0, 1000, 1000));
        Assert.Equal(current.Values.Single(r => r.Order == -9), representative.Roi);
        Assert.Equal(representative.Roi, Assert.Single(refitted.Intersecting(refitted.Bounds, 2000)));
        Assert.All(refitted.Visible(refitted.Bounds, 0), item => Assert.Same(current[item.Roi!.Id], item.Roi));
        Assert.Equal(originals.Values.MaxBy(r => r.Order), before.HitTest(new(100, 100), 0));
    }

    [Fact] public void RefitPreservesPathsForFollowingPersistentEdits()
    {
        var originals = Grid(40); var before = new RoiSpatialIndex(originals.Values);
        var current = originals.ToImmutableDictionary(pair => pair.Key, pair => pair.Value with { Geometry = pair.Value.Geometry.RotateAround(new(200, 200), 72) });
        var refitted = before.RefitGeometry(current);
        var moved = current.Values.First() with { Geometry = new(new(9000, 9000), 10, 10, 0) };
        var removed = current.Values.First(r => r.Id != moved.Id);
        var added = new Roi(Guid.NewGuid(), "added", new(new(8000, 8000), 10, 10, 0), null, false, 100);
        var edited = refitted.WithChanges([(moved.Id, moved), (removed.Id, null), (added.Id, added)]);
        var expected = current.SetItem(moved.Id, moved).Remove(removed.Id).Add(added.Id, added);
        Assert.Equal(new RoiSpatialIndex(expected.Values).Bounds, edited.Bounds);
        Assert.Equal(expected.Values.OrderBy(r => r.Id), edited.Visible(edited.Bounds, 0).Select(item => item.Roi!).OrderBy(r => r.Id));
        Assert.Equal(current.Values.OrderBy(r => r.Id), refitted.Visible(refitted.Bounds, 0).Select(item => item.Roi!).OrderBy(r => r.Id));
    }

    [Fact] public void RefitRejectsMissingExtraAndMismatchedIdsWithoutChangingOriginal()
    {
        var originals = Grid(20); var before = new RoiSpatialIndex(originals.Values); var roi = originals.Values.First();
        Assert.Throws<ArgumentException>(() => before.RefitGeometry(originals.Remove(roi.Id)));
        var extra = roi with { Id = Guid.NewGuid() };
        Assert.Throws<ArgumentException>(() => before.RefitGeometry(originals.Add(extra.Id, extra)));
        Assert.Throws<ArgumentException>(() => before.RefitGeometry(originals.Remove(roi.Id).Add(extra.Id, extra)));
        Assert.Throws<ArgumentException>(() => before.RefitGeometry(originals.SetItem(roi.Id, extra)));
        Assert.Same(roi, before.HitTest(roi.Geometry.Center, 0));
    }

    [Fact] public void EmptyAndCanceledRefitsHaveExplicitContracts()
    {
        var empty = new RoiSpatialIndex([]);
        Assert.Equal(default, empty.RefitGeometry(ImmutableDictionary<Guid, Roi>.Empty).Bounds);
        var values = Grid(20); var before = new RoiSpatialIndex(values.Values);
        Assert.Throws<OperationCanceledException>(() => before.RefitGeometry(values, new CancellationToken(true)));
        Assert.Equal(values.Count, before.Visible(before.Bounds, 0).Count());
    }

    private static ImmutableDictionary<Guid, Roi> Grid(int count)
        => Enumerable.Range(0, count).Select(index => new Roi(Guid.NewGuid(), $"ROI {index}",
            new(new(50 + index % 16 * 30, 50 + index / 16 * 30), 12, 8, index % 90), null, false, index)).ToImmutableDictionary(r => r.Id);
}

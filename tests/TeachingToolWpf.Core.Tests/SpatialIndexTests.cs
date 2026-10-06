using System.Collections.Immutable;
using VisionCanvas.Core.Editing;
using VisionCanvas.Core.Geometry;
using VisionGeometry;

namespace TeachingToolWpf.Core.Tests;

public class SpatialIndexTests
{
    [Fact]
    public void MovingOutsideOldBvhBoundsUpdatesQueriesAndRetainsSnapshotMetadata()
    {
        var editor = new EditorSession();
        editor.AddRange(Enumerable.Range(0, 100).Select(i => new RoiGeometry(new(i * 100, 100), 20, 20, 0)));
        var roi = editor.State.Rois.Values.MinBy(r => r.Order)!;
        editor.Select([roi.Id]);
        var before = editor.State.Snapshot.Spatial;
        editor.TranslateSelection(new(0, 5000));
        var moved = editor.State.Snapshot.Spatial;
        Assert.DoesNotContain(roi.Id, moved.At(roi.Geometry.Center).Select(r => r.Id));
        Assert.Equal(roi.Id, Assert.Single(moved.At(new(0, 5100))).Id);
        editor.Rename(roi.Id, "새 이름");
        Assert.Equal("새 이름", Assert.Single(editor.State.Snapshot.Spatial.At(new(0, 5100))).Name);
        Assert.Equal(roi.Name, Assert.Single(moved.At(new(0, 5100))).Name);
        Assert.Equal(roi, Assert.Single(before.At(roi.Geometry.Center)));
        editor.Undo();
        editor.Undo();
        Assert.Equal(roi, Assert.Single(editor.State.Snapshot.Spatial.At(roi.Geometry.Center)));
    }

    [Fact]
    public void IncrementalEditingPreservesExplicitHierarchyAndIndexedGeometry()
    {
        var editor = new EditorSession();
        var random = new Random(53);
        editor.InitializeImage(new("test", "test", 2000, 2000), ViewportTransform.Identity);
        editor.AddRange(Enumerable.Range(0, 100).Select(_ => new RoiGeometry(new(random.Next(400, 1600), random.Next(400, 1600)), random.Next(20, 300), random.Next(20, 300), random.Next(360))));
        for (var i = 0; i < 30; i++)
        {
            var id = editor.State.Rois.Keys.ElementAt(i);
            editor.Select([id]);
            var before = editor.State;
            editor.TranslateSelection(new(random.Next(-100, 100), random.Next(-100, 100)));
            var after = editor.State;
            Assert.All(after.Rois.Values, roi => Assert.Equal(before.Rois[roi.Id].ParentId, roi.ParentId));
            editor.Undo();
            Assert.Equal(before.Rois[id], editor.State.Rois[id]);
            Assert.Contains(id, editor.State.Snapshot.Spatial.At(before.Rois[id].Geometry.Center).Select(r => r.Id));
            editor.Redo();
            Assert.Equal(after.Rois[id], editor.State.Rois[id]);
            Assert.Contains(id, before.Snapshot.Spatial.At(before.Rois[id].Geometry.Center).Select(r => r.Id));
        }
    }

    [Fact]
    public void PersistentUpdatesPreservePreviousQueriesAndMatchFreshIndex()
    {
        var random = new Random(31);
        var rois = Enumerable.Range(0, 200).Select(i => new Roi(Guid.NewGuid(), $"R{i}",
            new(new(random.Next(1000), random.Next(1000)), 30, 40, random.Next(360)), null, false, i)).ToArray();
        var initial = new RoiSpatialIndex(rois);
        var changes = rois.Take(50).Select(r => (r.Id, Value: (Roi?)(r with { Geometry = r.Geometry.Translate(new(300, 300)) })))
            .Concat(rois.Skip(50).Take(50).Select(r => (r.Id, Value: (Roi?)null))).ToList();
        var additions = rois.Take(50).Select(r => r with { Id = Guid.NewGuid() }).ToArray();
        changes.AddRange(additions.Select(r => (r.Id, (Roi?)r)));
        var updated = initial.WithChanges(changes);
        var expected = rois.ToDictionary(r => r.Id);
        foreach (var (id, roi) in changes)
        {
            if (roi is null)
            {
                expected.Remove(id);
            }
            else
            {
                expected[id] = roi;
            }
        }
        for (var i = 0; i < 100; i++)
        {
            var x = random.Next(1000);
            var y = random.Next(1000);
            var box = new BoundsD(x, y, x + 100, y + 100);
            Assert.True(expected.Values.Where(r => r.Geometry.Bounds.Intersects(box)).Select(r => r.Id).ToHashSet().SetEquals(updated.Intersecting(box).Select(r => r.Id)));
            Assert.True(rois.Where(r => r.Geometry.Bounds.Intersects(box)).Select(r => r.Id).ToHashSet().SetEquals(initial.Intersecting(box).Select(r => r.Id)));
        }
        Assert.Equal(expected.Count, updated.Visible(new(-10000, -10000, 10000, 10000), 100).Sum(item => item.Count));
    }

    [Fact]
    public void SequentialInsertionsRemainQueryableAfterRebalancing()
    {
        var index = new RoiSpatialIndex([]);
        var values = new List<Roi>();
        for (var i = 0; i < 1000; i++)
        {
            var roi = new Roi(Guid.NewGuid(), "R", new(new(i * 100, 0), 10, 10, 0), null, false, i);
            values.Add(roi);
            index = index.WithChanges([(roi.Id, roi)]);
        }
        foreach (var roi in values)
        {
            Assert.Equal(roi.Id, Assert.Single(index.At(roi.Geometry.Center)).Id);
        }
    }

    [Fact]
    public void IndexedContainmentMatchesExhaustiveGeometryWithoutChangingParents()
    {
        var random = new Random(123);
        var rois = Enumerable.Range(0, 300).Select(i => new Roi(Guid.NewGuid(), $"R{i}",
            new(new(random.Next(1000), random.Next(1000)), random.Next(10, 700), random.Next(10, 700), random.Next(360)), null, false, i)).ToImmutableDictionary(r => r.Id);
        var index = new RoiSpatialIndex(rois.Values);
        foreach (var child in rois.Values)
        {
            var expected = rois.Values.Where(candidate => candidate.Geometry.Contains(child.Geometry)).OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id).FirstOrDefault()?.Id;
            var actual = index.At(child.Geometry.ContainmentAnchor, child.Geometry.Area)
                .Where(candidate => candidate.Geometry.Contains(child.Geometry))
                .OrderBy(r => r.Geometry.Area).ThenBy(r => r.Id).FirstOrDefault()?.Id;
            Assert.Equal(expected, actual);
            Assert.Null(child.ParentId);
        }
    }

    [Fact]
    public void BroadPhaseReturnsAllOverlappingBoundsIncludingEdges()
    {
        var roi = new Roi(Guid.NewGuid(), "edge", new(new(0, 0), 20, 20, 45), null, false, 1);
        var index = new RoiSpatialIndex([roi]);
        Assert.Single(index.At(new(roi.Geometry.Bounds.Right, 0)));
        Assert.Empty(index.At(new(100, 100)));
    }
}
